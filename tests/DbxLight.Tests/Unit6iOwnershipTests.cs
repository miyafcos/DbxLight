using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    private static void OldWatcherKeepsReplacement(bool preparing)
    {
        using var f = new Unit5Fixture("u6i-owner-" + preparing);
        var b = new MountController(f.Store, f.State);
        using var aCancel = new CancellationTokenSource();
        using var bCancel = new CancellationTokenSource();
        using var aWatching = new ManualResetEventSlim();
        using var releaseA = new ManualResetEventSlim();
        using var bPreparing = new ManualResetEventSlim();
        using var releaseB = new ManualResetEventSlim();
        using var aCleanup = new ManualResetEventSlim();
        var aMounted = new TaskCompletionSource<StartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bMounted = new TaskCompletionSource<StartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new List<RuntimeState>();
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started") { aWatching.Set(); TestRunner.Assert(releaseA.Wait(TimeSpan.FromSeconds(30))); }
            if (stage == "before-cancellation-stop") aCleanup.Set();
        };
        f.Controller.StartupCheckpoint = (stage, state) => { if (stage == "mount-ready") owned.Add(state); };
        b.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-cache-preservation" && preparing)
            { bPreparing.Set(); TestRunner.Assert(releaseB.Wait(TimeSpan.FromSeconds(4))); }
            if (stage == "mount-ready") owned.Add(state);
        };
        Task<int> aRun = Task.Run(() => f.Controller.Run(aCancel.Token, started => aMounted.TrySetResult(started)));
        Task<int>? bRun = null;
        try
        {
            TestRunner.Assert(aMounted.Task.Wait(TimeSpan.FromSeconds(30)) && aWatching.Wait(TimeSpan.FromSeconds(15)));
            RuntimeState a = f.State.Load()!;
            f.Controller.Stop();
            TestRunner.Equal(StopDisposition.Clean, f.State.Load()!.StopDisposition);
            bRun = Task.Run(() => b.Run(bCancel.Token, started => bMounted.TrySetResult(started)));
            if (preparing) TestRunner.Assert(bPreparing.Wait(TimeSpan.FromSeconds(30)));
            else TestRunner.Assert(bMounted.Task.Wait(TimeSpan.FromSeconds(30)));
            aCancel.Cancel(); releaseA.Set();
            TestRunner.Assert(aCleanup.Wait(TimeSpan.FromSeconds(15)));
            releaseB.Set();
            TestRunner.Assert(bMounted.Task.Wait(TimeSpan.FromSeconds(30)));
            TestRunner.Assert(aRun.Wait(TimeSpan.FromSeconds(15)));
            TestRunner.Equal(0, aRun.Result);
            RuntimeState current = f.State.Load()!;
            TestRunner.Assert(current.GenerationId != a.GenerationId);
            TestRunner.Assert(current.SupervisorPid == Environment.ProcessId && !current.StopRequested);
            TestRunner.Assert(!bRun.IsCompleted && !bCancel.IsCancellationRequested);
            TestRunner.Assert(b.Status().Ready);
            using var child = ProcessIdentity.Open(current);
            TestRunner.Assert(child is not null, "Old watcher stopped replacement child.");
        }
        finally
        {
            releaseA.Set(); releaseB.Set(); aCancel.Cancel(); bCancel.Cancel();
            f.Controller.Stop(true);
            TestRunner.Assert(aRun.Wait(TimeSpan.FromSeconds(15)));
            if (bRun is not null) TestRunner.Assert(bRun.Wait(TimeSpan.FromSeconds(15)));
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }
}
