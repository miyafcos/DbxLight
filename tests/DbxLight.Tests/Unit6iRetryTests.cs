using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    private static void InitialRetryStop(bool stop)
    {
        using var f = new Unit5Fixture("u6i-retry-" + stop, writeBack: "5m");
        f.Start(); f.Write("retry.txt", "retained while offline");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Pending retry body missing.");
        f.Controller.Stop(true);
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        using var cancel = new CancellationTokenSource();
        using var waiting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var mounted = new TaskCompletionSource<StartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int resumes = 0, comparisons = 0;
        var owned = new List<RuntimeState>();
        f.Controller.RecoveryInfoOverride = info =>
        {
            if (Interlocked.Increment(ref comparisons) == 1) throw new RecoveryComparisonException("Injected initial connection failure.");
            return info;
        };
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") resumes++;
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage != "initial-retry-wait") return;
            waiting.Set(); TestRunner.Assert(release.Wait(TimeSpan.FromSeconds(30)));
        };
        long before = f.State.Load()!.StopSequence;
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token, result => mounted.TrySetResult(result)));
        try
        {
            TestRunner.Assert(waiting.Wait(TimeSpan.FromSeconds(30)));
            if (stop)
            {
                var reply = f.Cli("stop");
                TestRunner.Equal(0, reply.Exit);
                TestRunner.Equal(before + 1, f.State.Load()!.StopSequence);
            }
            release.Set();
            if (stop)
            {
                TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(20)));
                TestRunner.Equal(0, run.Result);
                TestRunner.Equal(0, resumes);
                TestRunner.Equal(1, comparisons);
                TestRunner.Assert(!f.Controller.Status().Running);
            }
            else
            {
                TestRunner.Assert(mounted.Task.Wait(TimeSpan.FromSeconds(25)));
                TestRunner.Equal(1, resumes);
                TestRunner.Assert(comparisons > 1);
                Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 0 &&
                    File.Exists(Path.Combine(f.RemoteRoot, "retry.txt")), "Retry upload missing.");
                TestRunner.Equal(before, f.State.Load()!.StopSequence);
                f.Controller.Stop();
                TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            }
        }
        finally
        {
            release.Set(); cancel.Cancel(); f.Controller.Stop(true);
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void HistoricalStop()
    {
        using var f = new Unit5Fixture("u6i-historical-stop");
        f.Start(); f.Controller.Stop();
        long sequence = f.State.Load()!.StopSequence;
        TestRunner.Assert(sequence > 0);
        using var cancel = new CancellationTokenSource();
        var mounted = new TaskCompletionSource<StartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token, result => mounted.TrySetResult(result)));
        try
        {
            TestRunner.Assert(mounted.Task.Wait(TimeSpan.FromSeconds(30)));
            TestRunner.Assert(f.Controller.Status().Ready);
            TestRunner.Equal(sequence, f.State.Load()!.StopSequence);
        }
        finally
        {
            f.Controller.Stop(); cancel.Cancel();
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
        }
    }

    private static void StopMarkerKeepsCacheUnknown()
    {
        using var f = new Unit5Fixture("u6i-stop-marker-cache");
        f.Controller.Stop();
        string body = Path.Combine(f.Config.CacheDir, "vfs", "legacy", "unknown.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(body)!);
        File.WriteAllText(body, "unowned body", new System.Text.UTF8Encoding(false));
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        TestRunner.Equal("unowned body", File.ReadAllText(body));
        TestRunner.Equal(0, f.State.Load()!.RclonePid);
    }

    private static void StopSequenceCompatibility()
    {
        TestRunner.Equal(0L, JsonSerializer.Deserialize<RuntimeState>("{}", JsonFiles.Options)!.StopSequence);
        using var f = new Unit5Fixture("u6i-stop-marker");
        f.Controller.Stop(); TestRunner.Equal(1L, f.State.Load()!.StopSequence);
        f.Controller.Stop(); TestRunner.Equal(2L, f.State.Load()!.StopSequence);
        TestRunner.Equal(f.Store.PathName, f.State.Load()!.ConfigPath);
        var guide = f.Cli("agent-guide", "--json");
        TestRunner.Equal(0, guide.Exit);
        f.Start();
        TestRunner.Equal(2L, f.State.Load()!.StopSequence);
        f.Controller.Stop();
        TestRunner.Equal(3L, f.State.Load()!.StopSequence);
        using var blank = new Unit5Fixture("u6i-unconfigured-stop");
        blank.Store.Save(blank.Config with { Remote = "", WriteAllowedRemotes = [], TestOverrides = null });
        blank.Controller.Stop();
        TestRunner.Assert(blank.State.Load() is null, "An unconfigured stop created an unresolved runtime.");

    }
}
