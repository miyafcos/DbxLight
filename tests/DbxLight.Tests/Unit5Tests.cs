using System.Diagnostics;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    public static void Run(TestRunner runner, string? only = null, bool includeMounts = true)
    {
        void Test(string group, string name, Action action, bool mount = false)
        {
            if ((only is null || only == group) && (!mount || includeMounts)) runner.Test("G" + group + ": " + name, action);
        }
        Test("1", "live RC and stopped lsjson list metadata without downloading; bounded and hidden-aware", Listing, true);
        Test("1", "JSON errors are one object and recovery and process uncertainty never claim success", ListingFailures, true);
        Test("4", "wait verifies uploads and remote metadata for files absent from the queue", WaitForUploads, true);
        Test("4", "wait timeout, read-only, stopped and usage exits are structured", WaitFailures, true);
        Test("4", "a real failed local upload returns four and keeps the mount running", WaitFailedUpload, true);
        Test("5", "refresh exposes external arrivals without discarding pending uploads", RefreshListing, true);
        Test("7", "agent guide follows active permission without disclosing setting markers", GuidePrivacy);
        Test("3", "stat reads metadata and queue flags without creating cache entries", StatMetadata, true);
        Test("2", "name search folds case, width and combining marks without downloading", FindNames, true);
        Test("6", "local, relative and Web paths round-trip and outside paths are refused", ResolvePaths);
        Test("flow", "scripted agent lists, finds, opens one file, saves and verifies upload", AgentFlow, true);
        Test("contract", "new and existing JSON errors each emit one object with stable exit values", JsonContracts);
        Test("contract", "all agent commands refuse incomplete recovery without clearing it", RecoveryContracts, true);
        Test("10", "download windows count direction and notify once per episode", DownloadWindows);
        Test("10", "real RC downloads notify once without stopping and exclude uploads", DownloadNotice, true);
        Test("review", "stopped wait and refresh return six even before remote configuration", StoppedWithoutRemote);
        Test("review", "unidentified cache prevents an empty wait-all from claiming success", UnidentifiedDirtyCache, true);
        Test("review", "malformed runtime failures remain one JSON object", MalformedStateJson, true);
    }

    private static void Listing()
    {
        using var f = new Unit5Fixture("listing", hidden: ["secret"]);
        f.Seed("alpha.txt"); f.Seed("beta.txt"); f.Seed("folder/child.txt"); f.Seed("secret/private.txt");
        f.Seed("\u65e5\u672c\u8a9e \u7a7a\u767d/\u8cc7\u6599.txt");
        string deep = string.Join('/', Enumerable.Repeat("long folder 123456789", 13)) + "/end.txt"; f.Seed(deep);
        var offline = new MountController(f.Store, new StateStore(Path.Combine(f.Root, "offline", "state.json")));
        using (var listing = new AgentMetadata(offline))
        {
            AgentListing result = listing.List("", new(Recursive: true));
            TestRunner.Assert(result.Entries.Any(e => e.Path == deep));
            TestRunner.Assert(result.Entries.All(e => !e.Hidden));
        }
        f.Start();
        DbxStatus before = f.Controller.Status();
        using (var listing = new AgentMetadata(f.Controller))
        {
            AgentListing first = listing.List("", new(Max: 2));
            TestRunner.Equal("mount", first.Source); TestRunner.Equal(2, first.Entries.Count);
            TestRunner.Assert(first.Truncated && first.Continuation?.Offset == 2);
            AgentListing next = listing.List("", new(Offset: 2));
            TestRunner.Assert(!first.Entries.Select(e => e.Path).Intersect(next.Entries.Select(e => e.Path)).Any());
            AgentListing recursive = listing.List("", new(Recursive: true));
            TestRunner.Assert(recursive.Entries.Any(e => e.Path == "folder/child.txt"));
            TestRunner.Assert(recursive.Entries.All(e => !e.Hidden));
            TestRunner.Assert(!listing.List("", new(Depth: 1)).Entries.Any(e => e.Path == "folder/child.txt"));
            TestRunner.Assert(listing.List("", new(IncludeHidden: true)).Entries.Any(e => e.Path == "secret" && e.Hidden));
            AgentListing japanese = listing.List(Path.Combine(f.MountRoot, "\u65e5\u672c\u8a9e \u7a7a\u767d"), new());
            TestRunner.Equal("\u8cc7\u6599.txt", japanese.Entries.Single().Name);
        }
        double[] Measure(MountController controller)
        {
            var samples = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                using var listing = new AgentMetadata(controller); _ = listing.List("", new());
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            return samples.ToArray();
        }
        var timings = new Dictionary<string, double[]> { ["rc"] = Measure(f.Controller) };
        TestRunner.Throws<AgentCommandException>(() => { using var refused = new AgentMetadata(offline); });
        DbxStatus after = f.Controller.Status();
        TestRunner.Equal(before.CacheFiles, after.CacheFiles); TestRunner.Equal(before.CacheBytes, after.CacheBytes);
        var cli = f.Cli("ls", "", "--max", "1", "--json");
        TestRunner.Equal(0, cli.Exit); using var doc = JsonDocument.Parse(cli.Output);
        TestRunner.Assert(doc.RootElement.GetProperty("truncated").GetBoolean());
        f.Controller.Stop();
        using var stopped = new AgentMetadata(f.Controller);
        TestRunner.Equal("lsjson", stopped.List("", new()).Source);
        timings["lsjson"] = Measure(offline);
        Console.WriteLine("G1 measurements=" + JsonSerializer.Serialize(new { timingsMs = timings, before = new { before.CacheFiles, before.CacheBytes }, after = new { after.CacheFiles, after.CacheBytes } }));
    }

    private static void ListingFailures()
    {
        using var f = new Unit5Fixture("errors");
        var usage = f.Cli("ls", "", "--max", "0", "--json");
        TestRunner.Equal(2, usage.Exit); AssertFailure(usage.Output, "usage");
        f.Start();
        RuntimeState state = f.State.Load()!;
        RuntimeSnapshots.Save(f.State, state with { RecoveryRequired = true, StopDisposition = StopDisposition.Forced });
        // Nonempty dirty metadata prevents the existing recovery verifier from resolving it.
        Directory.CreateDirectory(Path.Combine(f.Config.CacheDir, "vfsMeta", "unknown"));
        File.WriteAllText(Path.Combine(f.Config.CacheDir, "vfsMeta", "unknown", "broken"), "{}");
        var recovery = f.Cli("ls", "", "--json");
        TestRunner.Equal(3, recovery.Exit); AssertFailure(recovery.Output, "recovery_required");
        using var json = JsonDocument.Parse(recovery.Output);
        TestRunner.Assert(json.RootElement.GetProperty("status").GetProperty("recoveryRequired").GetBoolean());
        var uncertain = new MountController(f.Store, f.State, processQuery: _ => throw new ProcessIdentityUnknownException("fixture", identity: new(state.RclonePid, "fixture")));
        try { using var refused = new AgentMetadata(uncertain); throw new Exception("Expected refusal."); }
        catch (AgentCommandException e) { TestRunner.Equal(3, e.ExitCode); TestRunner.Equal("process_uncertain", e.Code); }
    }

    private static void AssertFailure(string output, string code)
    {
        using var doc = JsonDocument.Parse(output);
        TestRunner.Assert(!doc.RootElement.GetProperty("ok").GetBoolean());
        TestRunner.Equal(code, doc.RootElement.GetProperty("code").GetString());
        TestRunner.Assert(doc.RootElement.GetProperty("message").GetString()!.Length > 0);
    }
}
