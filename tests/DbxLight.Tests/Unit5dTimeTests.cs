using System.Diagnostics;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5dTests
{
    private static void TimeLimitOptions()
    {
        TestRunner.Equal(45d, new ListingOptions().TimeLimitSeconds);
        using var f = new Unit5Fixture("5d-time-options");
        f.Seed("one.txt");
        TestTimeline.Mark(f.Root, "options-start");
        using var logical = new TestTraversalClock();
        foreach (string command in new[] { "ls", "find", "stat" })
        {
            string operand = command == "ls" ? "" : command == "find" ? "one" : "one.txt";
            foreach (string value in new[] { "0", "0.9", "3600.1", "NaN", "Infinity", "-1", "1e2", "1,5" })
            {
                var invalid = f.Cli(command, operand, "--time-limit", value, "--json");
                TestRunner.Equal(2, invalid.Exit);
                using var error = JsonDocument.Parse(invalid.Output);
                TestRunner.Equal("usage", error.RootElement.GetProperty("code").GetString());
            }
            TestRunner.Equal(2, f.Cli(command, operand, "--time-limit", "--json").Exit);
            TestRunner.Equal(2, f.Cli(command, operand, "--time-limit", "1", "--time-limit", "2", "--json").Exit);
            // The real CLI parser runs with a controlled traversal clock in this process.
            var valid = Unit3cTests.Capture(() => (int)typeof(DbxLight.Cli.Program)
                .GetMethod("MainCore", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [new[] { "--config", f.Store.PathName, "--state", f.State.PathName, command, operand, "--time-limit", "1.25", "--json" }, f.Controller])!);
            TestTimeline.Mark(f.Root, "cli-valid", new { command, valid.Exit, valid.Output });
            TestRunner.Equal(0, valid.Exit);
            using var json = JsonDocument.Parse(valid.Output);
            if (command == "stat") TestRunner.Assert(!json.RootElement.TryGetProperty("timeLimited", out _));
            else TestRunner.Assert(!json.RootElement.GetProperty("timeLimited").GetBoolean());
        }
        using var metadata = new AgentMetadata(f.Controller)
        {
            ChildrenReader = _ => { logical.Advance(1.1); return [new("one.txt", false, 1, null)]; }
        };
        var timer = Stopwatch.StartNew();
        AgentListing atomic = metadata.List("", new(TimeLimitSeconds: 1));
        TestRunner.Assert(logical.Seconds >= 1 && !atomic.Truncated && !atomic.TimeLimited);
        TestRunner.Equal(1, atomic.Entries.Count);
        foreach (double value in new[] { 0d, 0.99, 3600.01, double.NaN, double.PositiveInfinity })
        {
            TestRunner.Throws<ArgumentException>(() => metadata.List("", new(TimeLimitSeconds: value)));
            TestRunner.Throws<ArgumentException>(() => metadata.Find("one", timeLimitSeconds: value));
        }
        Console.WriteLine($"S1 options: default=45; decimal=1.25 accepted by ls/find/stat; invalid and duplicate/missing values rejected; one-level atomic ls={timer.Elapsed.TotalSeconds:F3}s, timeLimited=false.");
    }

    private static void EmptyTimedSearch()
    {
        using var f = new Unit5Fixture("5d-empty-time");
        using var logical = new TestTraversalClock();
        int reads = 0;
        using var metadata = new AgentMetadata(f.Controller)
        {
            ChildrenReader = _ =>
            {
                if (Interlocked.Increment(ref reads) == 1) logical.Advance(1.1);
                return Enumerable.Range(0, 30).Select(i => new MetadataItem($"file{i:D2}", false, 1, null))
                    .Append(new("needle.txt", false, 1, null)).ToArray();
            }
        };
        var timer = Stopwatch.StartNew();
        AgentFindResult first = metadata.Find("needle", timeLimitSeconds: 1);
        double elapsed = timer.Elapsed.TotalSeconds;
        TestRunner.Assert(first.Truncated && first.TimeLimited && first.Entries.Count == 0);
        TestRunner.Equal(0, first.Continuation!.Offset);
        TestRunner.Assert(first.Continuation.Hint.Contains("more", StringComparison.Ordinal) &&
            first.Continuation.Hint.Contains("empty", StringComparison.Ordinal));
        TestRunner.Equal(1, reads);
        logical.Reset();
        AgentFindResult next = metadata.Find("needle", offset: first.Continuation.Offset!.Value, timeLimitSeconds: 1);
        TestRunner.Assert(!next.Truncated && !next.TimeLimited);
        TestRunner.Equal("needle.txt", next.Entries.Single().Path);
        reads = 0; logical.Reset();
        AgentFindResult replay = metadata.Find("needle", offset: 10, timeLimitSeconds: 1);
        TestRunner.Assert(replay.TimeLimited && replay.Entries.Count == 0);
        TestRunner.Equal(10, replay.Continuation!.Offset);
        TestRunner.Assert(AgentContinuation.ForOffset(1000001, true).Offset is null);
        TestRunner.Assert(AgentContinuation.ForOffset(1000001, true).Hint.Contains("--in", StringComparison.Ordinal));
        Console.WriteLine($"S1 empty page: entries=0; truncated=true; timeLimited=true; offset=0; elapsed={elapsed:F3}s; overshoot={elapsed - 1:F3}s; reads=1; next=needle.txt; replay offset never moves backwards.");
    }

    private static void TimedReadFailure()
    {
        using var f = new Unit5Fixture("5d-time-failure");
        using var logical = new TestTraversalClock(); using var release = new ManualResetEventSlim();
        int checks = 0, secondCheckRead = 0, consumer = Environment.CurrentManagedThreadId;
        // The admission check uses its pre-release snapshot even if the reader completes immediately.
        AgentTraversalBudget.ElapsedTestClock.Value = () => Environment.CurrentManagedThreadId == consumer &&
            checks == 2 && Interlocked.Exchange(ref secondCheckRead, 1) == 0 ? 0 : logical.Seconds;
        logical.StopCheckpoint = () =>
        {
            // The first check precedes the directory yield; the second follows its consumption.
            if (++checks == 2) release.Set();
        };
        int active = 0;
        using var metadata = new AgentMetadata(f.Controller)
        {
            ChildrenReader = path =>
            {
                if (path == "") return [new("a", true, -1, null)];
                Interlocked.Increment(ref active);
                try { release.Wait(); logical.Advance(1.1); throw new IOException("Injected slow folder failure."); }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        AgentListing page = metadata.List("", new(Depth: 2, TimeLimitSeconds: 1));
        TestRunner.Assert(page.TimeLimited && page.Truncated);
        TestRunner.Equal("a", page.Entries.Single().Path);
        TestRunner.Equal(1, page.Continuation!.Offset);
        TestRunner.Equal(0, active);
        logical.Reset(); checks = 0; secondCheckRead = 0; release.Reset();
        TestRunner.Throws<IOException>(() => metadata.List("", new(Depth: 2, Offset: 1, TimeLimitSeconds: 3600)));
        TestRunner.Equal(0, active);
        Console.WriteLine("S1 slow unconsumed failure: confirmed directory retained; timeLimited=true; offset=1; activeAfter=0; consuming continuation with sufficient time still fails.");
    }

    private static void CountLimitReason()
    {
        using var f = new Unit5Fixture("5d-count-time");
        using var metadata = new AgentMetadata(f.Controller)
        {
            ChildrenReader = _ => Enumerable.Range(0, 10002)
                .Select(i => new MetadataItem($"file{i:D5}", false, 1, null)).ToArray()
        };
        AgentListing list = metadata.List("", new(Depth: 2, Max: 2, TimeLimitSeconds: 3600));
        TestRunner.Assert(list.Truncated && !list.TimeLimited); TestRunner.Equal(2, list.Continuation!.Offset);
        AgentFindResult find = metadata.Find("file", max: 2, timeLimitSeconds: 3600);
        TestRunner.Assert(find.Truncated && !find.TimeLimited); TestRunner.Equal(2, find.Continuation!.Offset);
        AgentFindResult scan = metadata.Find("absent", timeLimitSeconds: 3600);
        TestRunner.Assert(scan.Truncated && !scan.TimeLimited && scan.Entries.Count == 0);
        TestRunner.Equal(10000, scan.Continuation!.Offset);
        Console.WriteLine("S1 result cap and 10000-entry scan cap retain timeLimited=false; upper bound 3600 accepted.");
    }

    private static void TimeLimitGuide()
    {
        using var f = new Unit5Fixture("5d-time-guide");
        foreach (bool writable in new[] { false, true })
        {
            f.Store.Save(f.Config with { WriteAllowedRemotes = writable ? [f.Config.Remote] : [] });
            string text = AgentGuide.Create(f.Controller).Markdown;
            TestRunner.Assert(text.Split('\n').Length <= 40);
            TestRunner.Assert(text.Contains("timeLimited=true", StringComparison.Ordinal) &&
                text.Contains("--offset", StringComparison.Ordinal) && text.Contains("--in", StringComparison.Ordinal));
            Console.WriteLine($"S1 guide writable={writable}: lines={text.Split('\n').Length}");
        }
        foreach (string path in new[] { "integrations/AGENTS.snippet.md", "integrations/claude-code/SKILL.md" })
        {
            string text = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), path));
            TestRunner.Assert(text.TrimEnd().Split('\n').Length <= 40 &&
                text.Contains("timeLimited=true", StringComparison.Ordinal) &&
                text.Contains("--offset", StringComparison.Ordinal) && text.Contains("--in", StringComparison.Ordinal));
            Console.WriteLine($"S1 {path}: lines={text.TrimEnd().Split('\n').Length}");
        }
    }

    // Each read still uses the real mounted directory. Only its first visit receives
    // a short delay, modelling the cost of a cold directory listing without a cloud.
    private sealed class TimedReadProbe(string mount, string artifactRoot, TestTraversalClock logical)
    {
        private readonly MountMetadata native = new(mount);
        private readonly HashSet<string> warmed = new(StringComparer.Ordinal);
        private readonly object gate = new();
        private Stopwatch page = Stopwatch.StartNew();
        internal int Active, Peak, Calls, Cold;
        internal long WarmTicks;
        internal readonly List<double> Starts = [];
        internal const int ColdDelayMs = 180;

        internal void Begin()
        {
            TestRunner.Equal(0, Active);
            lock (gate) { Peak = Calls = Cold = 0; WarmTicks = 0; Starts.Clear(); logical.Reset(); page = Stopwatch.StartNew(); }
        }

        internal IReadOnlyList<MetadataItem> Read(string relative)
        {
            bool cold;
            lock (gate)
            {
                Active++; Calls++; Peak = Math.Max(Peak, Active);
                Starts.Add(logical.Seconds);
                cold = warmed.Add(relative) && relative.Length > 0;
                if (cold) Cold++;
            }
            long started = Stopwatch.GetTimestamp();
            try
            {
                var result = native.Read(relative);
                if (cold) logical.Advance(ColdDelayMs / 1000d);
                return result;
            }
            finally
            {
                lock (gate)
                {
                    TestTimeline.Mark(artifactRoot, "native-read", new { relative, cold, seconds = Stopwatch.GetElapsedTime(started).TotalSeconds, pageSeconds = page.Elapsed.TotalSeconds });
                    Active--;
                    if (!cold) WarmTicks += Stopwatch.GetTimestamp() - started;
                }
            }
        }
    }

    private static void TimedListingPages() => TimedPages(find: false);
    private static void TimedFindPages() => TimedPages(find: true);

    private static void TimedPages(bool find)
    {
        using var f = new Unit5Fixture(find ? "5d-timed-find" : "5d-timed-list", cloudNames: true);
        for (int i = 0; i < 24; i++)
        {
            f.Seed($"d{i:D2}/a/target.txt");
            f.Seed($"d{i:D2}/b/target.txt");
            f.Seed($"d{i:D2}/note.txt");
        }
        f.Start();
        using var logical = new TestTraversalClock();
        var reads = new TimedReadProbe(f.Config.MountPoint, f.Root, logical);
        TestTimeline.Mark(f.Root, "timed-pages-start", new { find });
        using var metadata = new AgentMetadata(f.Controller) { ChildrenReader = reads.Read };
        var joined = new List<AgentEntry>();
        var times = new List<double>();
        int offset = 0, pages = 0, timed = 0, stalled = 0;
        string label = find ? "find" : "ls";
        while (true)
        {
            TestRunner.Assert(++pages <= 1 + 24 * 3, "Timed pages must reach the end.");
            reads.Begin(); var clock = Stopwatch.StartNew();
            IReadOnlyList<AgentEntry> entries; bool truncated, timeLimited; AgentContinuation? continuation;
            if (find)
            {
                AgentFindResult result = metadata.Find("target", depth: 8, max: 10000, offset: offset, timeLimitSeconds: 1);
                (entries, truncated, timeLimited, continuation) = (result.Entries, result.Truncated, result.TimeLimited, result.Continuation);
            }
            else
            {
                AgentListing result = metadata.List("", new(Depth: 8, Max: 10000, Offset: offset, TimeLimitSeconds: 1));
                (entries, truncated, timeLimited, continuation) = (result.Entries, result.Truncated, result.TimeLimited, result.Continuation);
            }
            double elapsed = clock.Elapsed.TotalSeconds;
            TestTimeline.Mark(f.Root, "page", new { pages, offset, next = continuation?.Offset, count = entries.Count, elapsed, timeLimited, reads.Calls, reads.Cold, warmMs = reads.WarmTicks * 1000d / Stopwatch.Frequency });
            times.Add(elapsed); joined.AddRange(entries);
            TestRunner.Equal(0, reads.Active);
            TestRunner.Assert(reads.Peak <= 4);
            TestRunner.Assert(reads.Starts.All(t => t <= 1 + 3 * TimedReadProbe.ColdDelayMs / 1000d), "Only the already admitted readers may enter after the logical deadline.");
            if (timeLimited)
            {
                timed++;
                TestRunner.Assert(truncated && continuation?.Offset >= offset);
                if (continuation!.Offset == offset)
                {
                    TestRunner.Equal(0, entries.Count);
                    stalled++; TestRunner.Assert(reads.Cold > 0, "A logical timeout must consume a previously cold folder.");
                }
                else stalled = 0;
                TestRunner.Assert(logical.Seconds >= 1 && logical.Seconds <= 1 + 4 * TimedReadProbe.ColdDelayMs / 1000d,
                    "Only the admitted four-reader window may extend the logical limit.");
            }
            Console.WriteLine($"S1 {label} page={pages}; offset={offset}; next={continuation?.Offset}; entries={entries.Count}; timeLimited={timeLimited}; elapsed={elapsed:F4}s; overshoot={Math.Max(0, elapsed - 1):F4}s; coldReads={reads.Cold}; warmReads={reads.Calls - reads.Cold}; warmReadWorkMs={reads.WarmTicks * 1000d / Stopwatch.Frequency:F3}; logicalSeconds={logical.Seconds:F3}; peak={reads.Peak}; lastReadStart={reads.Starts.DefaultIfEmpty(0).Max():F4}s; activeAfter={reads.Active}");
            if (!truncated) break;
            TestRunner.Assert(timeLimited);
            offset = continuation!.Offset!.Value;
        }
        TestRunner.Assert(timed >= 2 && pages >= 3);
        reads.Begin(); var fullClock = Stopwatch.StartNew();
        IReadOnlyList<AgentEntry> full = find
            ? metadata.Find("target", depth: 8, max: 10000, timeLimitSeconds: 3600).Entries
            : metadata.List("", new(Depth: 8, Max: 10000, TimeLimitSeconds: 3600)).Entries;
        double warmSeconds = fullClock.Elapsed.TotalSeconds;
        TestRunner.Assert(full.SequenceEqual(joined), "Concatenated pages must exactly match one non-truncated traversal.");
        TestRunner.Equal(joined.Count, joined.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count());
        var serialNative = new MountMetadata(f.Config.MountPoint);
        IEnumerable<string> UntimedPaths(string parent)
        {
            foreach (MetadataItem item in serialNative.Read(parent).OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                string path = parent.Length == 0 ? item.Name : parent + "/" + item.Name;
                yield return path;
                if (item.IsDir) foreach (string child in UntimedPaths(path)) yield return child;
            }
        }
        IEnumerable<string> untimed = UntimedPaths("");
        if (find) untimed = untimed.Where(p => p.EndsWith("/target.txt", StringComparison.Ordinal));
        TestRunner.Assert(untimed.SequenceEqual(joined.Select(e => e.Path)), "An independent traversal without a deadline must match every page.");

        TestRunner.Assert(logical.Seconds == 0 && reads.Cold == 0);
        DbxStatus status = f.Controller.Status();
        TestRunner.Equal(0L, status.CacheFiles); TestRunner.Equal(0L, status.CacheBytes);
        TestRunner.Equal(0L, status.DownloadActivity!.Files); TestRunner.Equal(0, reads.Active);
        Console.WriteLine($"S1 {label} complete: pages={pages}; timedPages={timed}; entries={joined.Count}; duplicates=0; missing=0; identicalOrder=true; untimedSerialEqual=true; warmFull={warmSeconds:F4}s; maxOvershoot={Math.Max(0, times.Max() - 1):F4}s; logicalColdDelay={TimedReadProbe.ColdDelayMs}ms; cache=0/0; downloads=0.");
    }
}
