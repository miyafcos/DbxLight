using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5cTests
{
    private sealed class ReadProbe(string mount)
    {
        private readonly MountMetadata native = new(mount);
        private readonly object gate = new();
        internal int Active, Peak, Calls;
        internal IReadOnlyList<MetadataItem> Read(string relative)
        {
            lock (gate) { Active++; Peak = Math.Max(Peak, Active); Calls++; }
            try
            {
                var result = native.Read(relative);
                if (relative.Length > 0) Thread.Sleep(12 + relative.Length % 11);
                return result;
            }
            finally { lock (gate) Active--; }
        }
    }

    private static void ParallelOrder()
    {
        using var f = new Unit5Fixture("5c-parallel", cloudNames: true);
        for (int i = 0; i < 12; i++)
        {
            f.Seed($"d{i:D2}/a/leaf.txt"); f.Seed($"d{i:D2}/a.txt");
            f.Seed($"d{i:D2}/b/sub/leaf.txt");
        }
        f.Start();
        string[] serial = WindowsPaths(f.MountRoot).ToArray();
        var reads = new ReadProbe(f.Config.MountPoint);
        using var metadata = new AgentMetadata(f.Controller) { ChildrenReader = reads.Read };
        AgentListing listed = metadata.List("", new(Depth: 8));
        TestRunner.Assert(serial.SequenceEqual(listed.Entries.Select(e => e.Path)));
        TestRunner.Equal(0, reads.Active);
        TestRunner.Assert(reads.Peak is > 1 and <= 4, "Scheduling must overlap reads without exceeding four.");
        var paged = new List<string>(); int offset = 0;
        do
        {
            AgentFindResult page = metadata.Find("leaf", max: 3, offset: offset);
            paged.AddRange(page.Entries.Select(e => e.Path)); TestRunner.Equal(0, reads.Active);
            if (!page.Truncated) break;
            offset = page.Continuation!.Offset!.Value;
        } while (true);
        TestRunner.Assert(serial.Where(p => p.EndsWith("leaf.txt", StringComparison.Ordinal)).SequenceEqual(paged));
        DbxStatus status = f.Controller.Status();
        TestRunner.Equal(0L, status.CacheFiles); TestRunner.Equal(0L, status.CacheBytes); TestRunner.Equal(0L, status.DownloadActivity!.Files);
        Console.WriteLine($"N5 serial DFS equals parallel output and offset pages: entries={listed.Entries.Count}; peak readers={reads.Peak}; active after return={reads.Active}; cache=0/0.");
    }

    private static void ParallelLimits()
    {
        using var f = new Unit5Fixture("5c-limits");
        for (int folder = 0; folder < 170; folder++)
            for (int file = 0; file < 80; file++) f.Seed($"d{folder:D3}/file{file:D3}.txt", "x");
        f.Start();
        var reads = new ReadProbe(f.Config.MountPoint);
        using var metadata = new AgentMetadata(f.Controller) { ChildrenReader = reads.Read };
        AgentFindResult limit = metadata.Find("absent");
        TestRunner.Assert(limit.Truncated && limit.Entries.Count == 0);
        TestRunner.Equal(10000, limit.Continuation!.Offset); TestRunner.Equal(0, reads.Active);
        TestRunner.Assert(reads.Peak <= 4 && reads.Calls < 135, "Scan cap must stop scheduling the remaining tree.");
        int scanCalls = reads.Calls; reads.Calls = 0;
        AgentFindResult small = metadata.Find("file", max: 5);
        TestRunner.Assert(small.Truncated); TestRunner.Equal(5, small.Entries.Count);
        TestRunner.Equal(6, small.Continuation!.Offset);
        TestRunner.Assert(reads.Calls <= 6, "Only root, consumed child and at most four prefetched folders may be read.");
        TestRunner.Equal(0, reads.Active);
        AgentFindResult next = metadata.Find("file", max: 5, offset: small.Continuation.Offset!.Value);
        TestRunner.Equal("d000/file005.txt", next.Entries[0].Path);
        TestRunner.Equal(0L, f.Controller.Status().CacheFiles);
        Console.WriteLine($"N5 scan cap offset=10000; folders read={scanCalls}/171 including at most 4 prefetched folders; max=5 continuation=6; peak readers={reads.Peak}; active after return={reads.Active}.");
    }

    private static void PrefetchFailures()
    {
        using var f = new Unit5Fixture("5c-prefetch-failure");
        using var metadata = new AgentMetadata(f.Controller)
        {
            ChildrenReader = relative => relative switch
            {
                "" => [new("a", true, -1, null), new("b", true, -1, null)],
                "a" => [],
                _ => throw new IOException("Injected unconsumed folder failure.")
            }
        };
        TestRunner.Equal("a", metadata.List("", new(Depth: 8, Max: 1)).Entries.Single().Path);
        TestRunner.Throws<IOException>(() => metadata.List("", new(Depth: 8)));
        Console.WriteLine("N5 unconsumed prefetch failure preserves the requested page; consuming that folder still fails.");
    }
}
