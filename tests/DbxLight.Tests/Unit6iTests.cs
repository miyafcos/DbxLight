using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        foreach (bool exit in new[] { false, true })
            Test("R1", "failed stop keeps starts blocked until explicit retry; exit=" + exit, () => StopFailureRetry(exit));
        Test("R5", "external CLI preparation stop shows stopped and allows menu resume", ExternalPreparationStop);
        Test("R1", "pause menu stops restart-wait and resume starts a new child", PauseRestartWait);
        Test("R1", "pause menu immediately after resume ignores stale stopped snapshot", PauseStaleResume);
        Test("R1", "exit phase rejects resume during its first await", ExitRejectsResume);
        foreach (bool stop in new[] { false, true })
            Test("R4", "initial comparison retry observes accepted CLI stop; stop=" + stop, () => InitialRetryStop(stop));
        Test("R4", "stop-only marker never grants pending cache provenance", StopMarkerKeepsCacheUnknown);
        Test("R4", "a new run accepts historical normal stops", HistoricalStop);
        Test("R4", "missing sequence defaults to zero and no-child stops advance it", StopSequenceCompatibility);
        foreach (bool preparing in new[] { false, true })
            Test("R3", "old watcher cancellation leaves replacement run intact; preparing=" + preparing, () => OldWatcherKeepsReplacement(preparing));
        foreach (bool exit in new[] { false, true })
            Test("R2", "external stop completes waiting after watcher ends; exit=" + exit, () => StopRefusalKeepsWatch(true, true, exit));
        foreach (bool wait in new[] { false, true })
            Test("R2", "refused startup stop retains the same watcher; wait=" + wait, () => StopRefusalKeepsWatch(wait));
    }

    private static ToolStripMenuItem Pause(ResidentContext c) => FormTests.Field<ToolStripMenuItem>(c, "pause");
    private static async Task PauseAsync(ResidentContext c)
    {
        Pause(c).PerformClick();
        await (c.MenuStopTask ?? throw new InvalidOperationException("Menu did not stop."));
    }
    private static async Task ResumeAsync(ResidentContext c)
    {
        Pause(c).PerformClick();
        await FormTests.Field<Task>(c, "mountStartup");
    }
    private static void KillOwned(RuntimeState state)
    {
        using var child = ProcessIdentity.Open(state) ?? throw new InvalidOperationException("Owned child missing.");
        child.Kill(); TestRunner.Assert(child.WaitForExit(10000));
    }
    internal static void RunTray(Unit5Fixture f, Func<ResidentContext, Task> scenario,
        Action<ResidentContext, Func<Task>>? configure = null, bool autoStart = true)
    {
        Exception? failure = null; bool ran = false;
        var owned = new List<RuntimeState>();
        var thread = new Thread(() =>
        {
            try
            {
                var options = new AppOptions(f.Store.PathName, f.State.PathName,
                    RegistryRoot: RegistrationService.TestHive + "\\U6i-" + Guid.NewGuid().ToString("N"));
                using var instance = new SingleInstance(options.StatePath);
                async Task Execute(ResidentContext c)
                {
                    ran = true;
                    try { await scenario(c); }
                    catch (Exception e) { failure = e; }
                    finally
                    {
                        try { await c.ShutdownOwnedAsync(); }
                        catch (Exception e) { failure ??= e; c.ExitThread(); }
                    }
                }
                using var context = new ResidentContext(options, instance, autoStart ? Execute : null);
                context.Controller.StartupCheckpoint = (stage, state) => { if (stage == "mount-ready") owned.Add(state); };
                configure?.Invoke(context, () => Execute(context));
                using var guard = new System.Windows.Forms.Timer { Interval = 60000 };
                guard.Tick += (_, _) => { failure ??= new TimeoutException("Tray checkpoint scenario hung."); context.ExitThread(); };
                guard.Start(); Application.Run(context);
            }
            catch (Exception e) { failure ??= e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try
        {
            TestRunner.Assert(thread.Join(TimeSpan.FromSeconds(75)), "Tray scenario did not exit.");
            if (failure is not null) throw new InvalidOperationException("Tray scenario failed.", failure);
            TestRunner.Assert(ran);
        }
        finally
        {
            f.Controller.Stop(true);
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void PauseRestartWait()
    {
        using var f = new Unit5Fixture("u6i-menu-restart");
        RunTray(f, async c =>
        {
            using var release = new ManualResetEventSlim();
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int resumes = 1;
            c.Controller.StartupCheckpoint = (stage, _) => { if (stage == "after-resume") Interlocked.Increment(ref resumes); };
            c.Controller.SupervisorCheckpoint = stage =>
            {
                if (stage != "restart-wait") return;
                reached.TrySetResult();
                TestRunner.Assert(release.Wait(TimeSpan.FromSeconds(30)));
            };
            try
            {
                await Task.Run(() => KillOwned(f.State.Load()!));
                await reached.Task;
                await c.RefreshAsync();
                TestRunner.Assert(!c.Snapshot.Status.Running);
                TestRunner.Equal(ResidentPhase.Watching, c.Phase);
                Pause(c).PerformClick();
                Task stopped = c.MenuStopTask!;
                TestRunner.Equal(ResidentPhase.Stopping, c.Phase);
                release.Set(); await stopped;
                TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
                TestRunner.Equal(1, resumes);
                await ResumeAsync(c);
                TestRunner.Equal(ResidentPhase.Watching, c.Phase);
                TestRunner.Assert(c.Snapshot.Status.Ready);
                TestRunner.Equal(2, resumes);
            }
            finally { release.Set(); c.Controller.SupervisorCheckpoint = null; }
        });
    }

    private static void PauseStaleResume()
    {
        using var f = new Unit5Fixture("u6i-menu-stale");
        RunTray(f, async c =>
        {
            await PauseAsync(c);
            TestRunner.Assert(!c.Snapshot.Status.Running);
            bool clicked = false;
            c.StartupAwaitCheckpoint = stage =>
            {
                if (stage == "mounted-before-refresh")
                {
                    TestRunner.Assert(!c.Snapshot.Status.Running, "Snapshot was not stale.");
                    TestRunner.Equal(ResidentPhase.Watching, c.Phase);
                    clicked = true; Pause(c).PerformClick();
                }
                return Task.CompletedTask;
            };
            await ResumeAsync(c); await c.MenuStopTask!;
            TestRunner.Assert(clicked);
            TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
            TestRunner.Assert(!c.Controller.Status().Running);
        });
    }

    private static async Task RetryFailureScenario(ResidentContext c, bool exit)
    {
        if (exit) await PauseAsync(c);
        c.StopAwaitCheckpoint = () => throw new IOException("Injected stop failure before work.");
        if (exit) FormTests.Field<ToolStripMenuItem>(c, "exit").PerformClick();
        else Pause(c).PerformClick();
        await c.MenuStopTask!;
        TestRunner.Equal(exit ? ResidentPhase.ExitFailed : ResidentPhase.StopFailed, c.Phase);
        await c.StartMountedAsync();
        TestRunner.Equal(!exit, c.Controller.Status().Running);
        c.StopAwaitCheckpoint = null;
        Pause(c).PerformClick();
        await c.MenuStopTask!;
        TestRunner.Equal(exit ? ResidentPhase.Exiting : ResidentPhase.Stopped, c.Phase);
    }
    private static void StopFailureRetry(bool exit)
    {
        using var f = new Unit5Fixture("u6i-stop-failure-" + exit);
        RunTray(f, c => RetryFailureScenario(c, exit));
    }

    private static void ExitRejectsResume()
    {
        using var f = new Unit5Fixture("u6i-exit-resume");
        RunTray(f, async c =>
        {
            await PauseAsync(c);
            int resumes = 0;
            c.Controller.StartupCheckpoint = (stage, _) => { if (stage == "after-resume") resumes++; };
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            c.StopAwaitCheckpoint = () => release.Task;
            FormTests.Field<ToolStripMenuItem>(c, "exit").PerformClick();
            Task exiting = c.MenuStopTask!;
            TestRunner.Equal(ResidentPhase.Exiting, c.Phase);
            TestRunner.Assert(!Pause(c).Enabled);
            Pause(c).PerformClick();
            await c.StartMountedAsync();
            TestRunner.Equal(0, resumes);
            release.SetResult(); await exiting;
            TestRunner.Equal(ResidentPhase.Exiting, c.Phase);
            TestRunner.Assert(!c.Controller.Status().Running);
        });
    }
}
