using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6hTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        RunStops(Test);
        RunNames(Test);
        foreach (bool currentLocked in new[] { false, true })
            Test("N4", "unreadable log survives other error rows; current=" + currentLocked, () => PartialLogFailure(currentLocked));
        Test("N7", "unknown queue age never claims empty; known zero alone does", QueueAgeUnknown);
        Test("N3", "initial-config pause, resume, queue display and wait-stop complete", EarlyPauseResumePolling);
        Test("N1", "supervisor without callback really remounts and uploads pending data", RestartWithoutCallback);
    }

    private static void RestartWithoutCallback()
    {
        using var f = new Unit5Fixture("u6h-no-callback");
        using var cancel = new CancellationTokenSource();
        var owned = new List<RuntimeState>();
        f.Controller.StartupCheckpoint = (stage, state) => { if (stage == "mount-ready") owned.Add(state); };
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token));
        try
        {
            Unit5Fixture.Wait(() => f.Controller.Status().Ready, "Initial supervised mount missing.");
            var first = f.State.Load()!;
            f.Write("pending.txt", "pending restart content");
            Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Pending pair missing.");
            using (var child = ProcessIdentity.Open(first)!)
            { child.Kill(); TestRunner.Assert(child.WaitForExit(10000)); }
            Unit5Fixture.Wait(() => f.State.Load() is { } s && s.RclonePid != first.RclonePid && f.Controller.Status().Ready, "New mount PID missing.");
            var next = f.State.Load()!;
            TestRunner.Assert(next.RclonePid != first.RclonePid && Directory.Exists(f.MountRoot));
            Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.RemoteRoot, "pending.txt")) && f.Controller.Status().UploadsQueued == 0, "Normal pending upload did not finish.");
            TestRunner.Equal("pending restart content", File.ReadAllText(Path.Combine(f.RemoteRoot, "pending.txt")));
            f.Controller.Stop(); TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            TestRunner.Equal(0, run.Result);
            Console.WriteLine($"N1 oldPid={first.RclonePid}; newPid={next.RclonePid}; drive restored; normal upload verified.");
        }
        finally
        {
            cancel.Cancel(); f.Controller.Stop(true);
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
            f.Controller.StartupCheckpoint = null;
        }
    }
}
