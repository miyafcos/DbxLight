using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private static void RunFreshness(Action<string, string, Action> test)
    {
        test("E2", "ordinary pending files use one listing per parent and launch within the 30-second bound", FreshOrdinary);
        test("E2", "repeated slow comparisons preserve pending bodies instead of ever resuming them", AlwaysSlow);
        test("E2", "a first-parent update during a slow later listing is found on re-comparison", ChangedFirstParent);
        test("E2", "ownership-write and pre-resume latency are included in the freshness bound", SlowOwnership);
    }

    private static void PrepareFiles(Unit5Fixture f, params string[] names)
    {
        foreach (string name in names) f.Seed(name, "original " + name);
        f.Start();
        foreach (string name in names)
        {
            _ = File.ReadAllText(Path.Combine(f.MountRoot, name.Replace('/', '\\')));
            f.Write(name, "saved " + name);
        }
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == names.Length, "Pending batch missing.");
        f.Controller.Stop(true);
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
    }

    private static void FreshOrdinary()
    {
        using var f = new Unit5Fixture("u6e-fresh", writeBack: "5m");
        string[] names = ["a/one.txt", "a/two.txt", "a/three.txt", "z/one.txt", "z/two.txt", "z/three.txt"];
        PrepareFiles(f, names);
        var groups = new List<string>();
        f.Controller.RecoveryComparisonCheckpoint = (parent, attempt) => { TestRunner.Equal(0, attempt); groups.Add(parent); };
        f.Start();
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
        PrepareFiles(f, "saved.txt");
        int calls = 0;
        f.Controller.RecoveryFreshnessTestLimit = TimeSpan.FromSeconds(1);
        f.Controller.RecoveryComparisonCheckpoint = (_, _) => { calls++; Thread.Sleep(1100); };
        f.Start();
        TestRunner.Equal(4, calls); // Initial comparison plus at most three retries.
        RescueRecord record = f.Controller.RescueList().Single();
        TestRunner.Equal("saved saved.txt", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal("original saved.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "saved.txt")));
        TestRunner.Equal(1, Directory.GetFiles(f.RemoteRoot).Length);
        TestRunner.Equal(0, f.State.Load()!.RecoveryComparedFiles);
        TestRunner.Assert(record.Reason.Contains("自動では上げない", StringComparison.Ordinal));
        TestRunner.Equal(TimeSpan.FromSeconds(30), MountController.RecoveryFreshnessLimit);
    }

    private static void ChangedFirstParent()
    {
        using var f = new Unit5Fixture("u6e-stale-first", writeBack: "5m");
        PrepareFiles(f, "a/first.txt", "z/last.txt");
        bool changed = false;
        f.Controller.RecoveryFreshnessTestLimit = TimeSpan.FromSeconds(1);
        f.Controller.RecoveryComparisonCheckpoint = (parent, attempt) =>
        {
            if (attempt != 0) return;
            if (parent == "a" && !changed) { changed = true; f.Seed("a/first.txt", "colleague updated first"); }
            if (parent == "z") Thread.Sleep(1100);
        };
        f.Start();
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
        PrepareFiles(f, "saved.txt");
        var refused = new List<RuntimeState>();
        f.Controller.RecoveryFreshnessTestLimit = TimeSpan.FromSeconds(1);
        f.Controller.StartupCheckpoint = (phase, state) =>
        {
            if (phase == "before-resume" && state.RecoveryComparedFiles > 0)
            { refused.Add(state); Thread.Sleep(1100); }
        };
        f.Start();
        TestRunner.Equal(4, refused.Count);
        foreach (var state in refused) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        TestRunner.Equal("original saved.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "saved.txt")));
        TestRunner.Equal("saved saved.txt", File.ReadAllText(f.Controller.RescueList().Single().DestinationPath));
        Console.WriteLine("E2 pre-resume delays: four suspended children refused and exited; pending body preserved before the clean launch.");
    }
}
