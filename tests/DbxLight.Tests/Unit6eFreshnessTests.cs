using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private static void RunFreshness(Action<string, string, Action> test)
    {
        test("E2", "the oldest mixed comparison timestamp determines AgeSeconds", OldestComparisonAge);
        test("E2", "real batch age refuses stale timestamps without an age override", RealComparisonAge);
        test("E2", "ordinary pending files use one listing per parent and launch within the 30-second bound", FreshOrdinary);
        test("E2", "repeated slow comparisons preserve pending bodies instead of ever resuming them", AlwaysSlow);
        test("E2", "a first-parent update during a slow later listing is found on re-comparison", ChangedFirstParent);
        test("E2", "ownership-write and pre-resume latency are included in the freshness bound", SlowOwnership);
    }

    private static void OldestComparisonAge()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long oldest = now - 600 * System.Diagnostics.Stopwatch.Frequency;
        var entry = new CacheEntry("file.txt", "data", "meta", true, true, true, true, 7, null, "", true, null);
        var newer = new RecoveryComparedItem(entry, "new.txt", DateTimeOffset.UtcNow, now);
        var older = new RecoveryComparedItem(entry, "old.txt", newer.ComparedUtc.AddMinutes(-10), oldest);
        var batch = new RecoveryLaunchBatch([], null, null, [newer, older]);
        long before = System.Diagnostics.Stopwatch.GetTimestamp();
        double actual = batch.AgeSeconds;
        long after = System.Diagnostics.Stopwatch.GetTimestamp();
        double lower = System.Diagnostics.Stopwatch.GetElapsedTime(oldest, before).TotalSeconds;
        double upper = System.Diagnostics.Stopwatch.GetElapsedTime(oldest, after).TotalSeconds;
        TestRunner.Assert(actual >= lower && actual <= upper,
            $"Oldest comparison age {actual:F6}s must be in [{lower:F6}, {upper:F6}]s.");
        TestRunner.Equal(older.ComparedUtc, batch.OldestUtc);
        TestRunner.Equal(0d, new RecoveryLaunchBatch([], null, null, []).AgeSeconds);
        TestRunner.Equal(TimeSpan.FromSeconds(30), MountController.RecoveryFreshnessLimit);
        Console.WriteLine($"Q3 age={actual:F6}s; oldest lower={lower:F6}s; upper={upper:F6}s; default Stopwatch clock.");
    }


    private static void RealComparisonAge()
    {
        using var f = new Unit5Fixture("6s7rc-real-age", writeBack: "5m");
        PrepareFiles(f, "saved.txt");
        using var peer = new ControlledComparison(f);
        f.Controller.PreparationElapsedTestOverride = () => TimeSpan.Zero;
        f.Controller.RecoveryFreshnessTestLimit = TimeSpan.FromSeconds(1);
        int comparisons = 0, resumedPending = 0;
        f.Controller.RecoveryFolderTestOverride = (parent, info) =>
        {
            comparisons++;
            return new RecoveryRemoteProbe(f.Store.Load()).List(parent, info.Hashes.FirstOrDefault(), info) with
            {
                Timestamp = System.Diagnostics.Stopwatch.GetTimestamp() - 600 * System.Diagnostics.Stopwatch.Frequency,
                ComparedUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            };
        };
        f.Controller.StartupCheckpoint = (phase, state) =>
        {
            if (phase == "after-resume" && state.RecoveryComparedFiles > 0) resumedPending++;
        };
        TestRunner.Assert(f.Controller.RecoveryComparisonAgeTestOverride is null);
        f.Start();
        TestRunner.Equal(4, comparisons);
        TestRunner.Equal(0, resumedPending);
        RescueRecord record = f.Controller.RescueList().Single();
        TestRunner.Assert(record.MoveCompleted && record.CompleteData && !record.Conflict);
        TestRunner.Equal("saved saved.txt", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal("original saved.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "saved.txt")));
        TestRunner.Equal(0, f.State.Load()!.RecoveryComparedFiles);
        Console.WriteLine("R3 real batch.AgeSeconds: 600-second timestamps, one-second limit; four stale launches refused; body preserved.");
    }

    private static void PrepareFiles(Unit5Fixture f, params string[] names)
    {
        TestTimeline.Mark(f.Root, "prepare-start");
        foreach (string name in names) f.Seed(name, "original " + name);
        f.Start();
        foreach (string name in names)
        {
            _ = File.ReadAllText(Path.Combine(f.MountRoot, name.Replace('/', '\\')));
            f.Write(name, "saved " + name);
        }
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == names.Length, "Pending batch missing.");
        f.Controller.Stop(true);
        TestTimeline.Mark(f.Root, "pending-stopped");
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
    }

    private static void TraceFreshness(Unit5Fixture f)
    {
        f.Controller.PreparationElapsedTestOverride = () => TimeSpan.Zero;
        f.Controller.RecoveryComparisonAgeTestOverride = () => 0;
        f.Controller.RecoveryInfoOverride = info => { TestTimeline.Mark(f.Root, "info-read"); return info; };
        f.Controller.RecoveryNameCheckpoint = (stage, path) => TestTimeline.Mark(f.Root, "name-" + stage, new { path });
        f.Controller.StartupCheckpoint = (phase, state) =>
        {
            if (phase == "before-resume") TestTimeline.Mark(f.Root, "before-resume", new
            {
                files = state.RecoveryComparedFiles, oldestUtc = state.RecoveryComparedUtc,
                wallAgeSeconds = state.RecoveryComparedUtc is { } oldest ? (DateTimeOffset.UtcNow - oldest).TotalSeconds : (double?)null,
                logicalAgeSeconds = f.Controller.RecoveryComparisonAgeTestOverride?.Invoke(),
                limitSeconds = (f.Controller.RecoveryFreshnessTestLimit ?? MountController.RecoveryFreshnessLimit).TotalSeconds
            });
        };
    }

    private static void FreshOrdinary()
    {
        using var f = new Unit5Fixture("u6e-fresh", writeBack: "5m");
        string[] names = ["a/one.txt", "a/two.txt", "a/three.txt", "z/one.txt", "z/two.txt", "z/three.txt"];
        PrepareFiles(f, names); TraceFreshness(f);
        using var peer = new ControlledComparison(f);
        var groups = new List<string>();
        f.Controller.RecoveryComparisonCheckpoint = (parent, attempt) => { TestTimeline.Mark(f.Root, "group", new { parent, attempt }); TestRunner.Equal(0, attempt); groups.Add(parent); };
        TestTimeline.Mark(f.Root, "comparison-start"); f.Start(); TestTimeline.Mark(f.Root, "comparison-end");
        TestRunner.Equal(2, groups.Count);
        var state = f.State.Load()!;
        TestRunner.Equal(6, state.RecoveryComparedFiles);
        TestRunner.Assert(state.RecoveryComparedUtc <= state.ChildResumedUtc && state.RecoveryComparisonAgeSeconds <= 30);
        foreach (string name in names) TestRunner.Equal("saved " + name, File.ReadAllText(Path.Combine(f.RemoteRoot, name.Replace('/', '\\'))));
        TestRunner.Equal(0, f.Controller.RescueList().Count);
        Console.WriteLine($"E2 batch: files={state.RecoveryComparedFiles}; parentLists={groups.Count}; ageSeconds={state.RecoveryComparisonAgeSeconds:F3}; bound=30.");
    }

    private static void AlwaysSlow()
    {
        using var f = new Unit5Fixture("u6e-slow", writeBack: "5m");
        PrepareFiles(f, "saved.txt"); TraceFreshness(f);
        using var peer = new ControlledComparison(f);
        int calls = 0;
        double logicalAge = 0;
        f.Controller.RecoveryComparisonAgeTestOverride = () => logicalAge;
        f.Controller.RecoveryComparisonCheckpoint = (_, _) => { calls++; TestTimeline.Mark(f.Root, "slow-before", new { calls }); logicalAge = MountController.RecoveryFreshnessLimit.TotalSeconds + 1; TestTimeline.Mark(f.Root, "slow-after", new { calls, logicalAge }); };
        TestTimeline.Mark(f.Root, "comparison-start"); f.Start(); TestTimeline.Mark(f.Root, "comparison-end");
        TestRunner.Equal(4, calls); // Initial comparison plus at most three retries.
        RescueRecord record = f.Controller.RescueList().Single();
        TestRunner.Equal("saved saved.txt", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal("original saved.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "saved.txt")));
        TestRunner.Equal(1, Directory.GetFiles(f.RemoteRoot).Length);
        TestRunner.Equal(0, f.State.Load()!.RecoveryComparedFiles);
        TestRunner.Assert(record.Reason.Contains("自動ではアップロードしません", StringComparison.Ordinal));
        TestRunner.Equal(TimeSpan.FromSeconds(30), MountController.RecoveryFreshnessLimit);
    }

    private static void ChangedFirstParent()
    {
        using var f = new Unit5Fixture("u6e-stale-first", writeBack: "5m");
        PrepareFiles(f, "a/first.txt", "z/last.txt"); TraceFreshness(f);
        using var peer = new ControlledComparison(f);
        bool changed = false;
        double logicalAge = 0;
        f.Controller.RecoveryComparisonAgeTestOverride = () => logicalAge;
        f.Controller.RecoveryComparisonCheckpoint = (parent, attempt) =>
        {
            TestTimeline.Mark(f.Root, "group", new { parent, attempt });
            if (attempt != 0) { logicalAge = 0; return; }
            if (parent == "a" && !changed) { changed = true; f.Seed("a/first.txt", "colleague updated first"); }
            if (parent == "z") { TestTimeline.Mark(f.Root, "later-before"); logicalAge = MountController.RecoveryFreshnessLimit.TotalSeconds + 1; TestTimeline.Mark(f.Root, "later-after", new { logicalAge }); }
        };
        TestTimeline.Mark(f.Root, "comparison-start"); f.Start(); TestTimeline.Mark(f.Root, "comparison-end");
        var record = f.Controller.RescueList().Single();
        TestRunner.Assert(changed && record.Conflict && record.CloudPath == "a/first.txt");
        TestRunner.Equal("saved a/first.txt", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal("colleague updated first", File.ReadAllText(Path.Combine(f.RemoteRoot, "a", "first.txt")));
        TestRunner.Equal("saved z/last.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "z", "last.txt")));
        TestRunner.Equal(1, f.State.Load()!.RecoveryComparedFiles);
    }

    private static void SlowOwnership()
    {
        using var f = new Unit5Fixture("u6e-owner-latency", writeBack: "5m");
        PrepareFiles(f, "saved.txt"); TraceFreshness(f);
        using var peer = new ControlledComparison(f);
        var refused = new List<RuntimeState>();
        double logicalAge = 0;
        f.Controller.RecoveryComparisonAgeTestOverride = () => logicalAge;
        f.Controller.StartupCheckpoint = (phase, state) =>
        {
            if (phase == "before-resume") TestTimeline.Mark(f.Root, "owner-before-resume", new { state.RecoveryComparedFiles, wallAgeSeconds = state.RecoveryComparedUtc is { } oldest ? (DateTimeOffset.UtcNow - oldest).TotalSeconds : (double?)null });
            if (phase == "before-resume" && state.RecoveryComparedFiles > 0)
            { refused.Add(state); logicalAge = MountController.RecoveryFreshnessLimit.TotalSeconds + 1; TestTimeline.Mark(f.Root, "owner-after-delay", new { refused = refused.Count, logicalAge }); }
        };
        TestTimeline.Mark(f.Root, "comparison-start"); f.Start(); TestTimeline.Mark(f.Root, "comparison-end");
        TestRunner.Equal(4, refused.Count);
        foreach (var state in refused) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        TestRunner.Equal("original saved.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "saved.txt")));
        TestRunner.Equal("saved saved.txt", File.ReadAllText(f.Controller.RescueList().Single().DestinationPath));
        Console.WriteLine("E2 pre-resume delays: four suspended children refused and exited; pending body preserved before the clean launch.");
    }
}
