using System.Diagnostics;
using DbxLight.Core;
using System.Reflection;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6hTests
{
    private static void RunStops(Action<string, string, Action> test)
    {
        foreach (bool exit in new[] { false, true })
            test("N2", "menu cancels delayed first supervisor; exit=" + exit, () => DelayedMenuStop(exit));
        test("N2", "cancellation while waiting for lifecycle gate creates no child", CancelAtGate);
        test("N2", "CLI run preparation receives Ctrl+C cancellation token", CancelCliPreparation);
        foreach (bool cancel in new[] { false, true })
            test("N2", "restart wait stops without a new PID; token=" + cancel, () => StopRestartWait(cancel));
    }

    private static void DelayedMenuStop(bool exit)
    {
        using var f = new Unit5Fixture("u6h-delayed-menu-" + exit);
        Exception? failure = null; int resumed = 0; bool delayed = false;
        var owned = new List<RuntimeState>();
        var thread = new Thread(() =>
        {
            try
            {
                var options = new AppOptions(f.Store.PathName, f.State.PathName, RegistryRoot: RegistrationService.TestHive + "\\U6h-" + Guid.NewGuid().ToString("N"));
                using var instance = new SingleInstance(options.StatePath);
                using var context = new ResidentContext(options, instance);
                var dispatcher = FormTests.Field<ResidentWindow>(context, "dispatcher");
                context.Controller.StartupCheckpoint = (stage, state) =>
                {
                    if (stage == "after-resume") Interlocked.Increment(ref resumed);
                    if (stage == "mount-ready") owned.Add(state);
                };
                context.Controller.SupervisorCheckpoint = stage =>
                {
                    if (stage != "before-initial-gate" || delayed) return;
                    delayed = true;
                    using var delivered = new ManualResetEventSlim();
                    dispatcher.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (exit) FormTests.Field<NotifyIcon>(context, "tray").ContextMenuStrip!.Items[^1].PerformClick();
                            else FormTests.Field<ToolStripMenuItem>(context, "pause").PerformClick();
                            _ = Finish();
                        }
                        catch (Exception e) { failure = e; context.ExitThread(); }
                        finally { delivered.Set(); }
                    }));
                    TestRunner.Assert(delivered.Wait(TimeSpan.FromSeconds(30)));
                };
                async Task Finish()
                {
                    try
                    {
                        await (context.MenuStopTask ?? throw new InvalidOperationException("Menu did not stop."));
                        TestRunner.Equal(0, resumed);
                        TestRunner.Assert(context.Controller.State.Load() is { StopRequested: true, StopSequence: > 0 });
                        if (!exit)
                        {
                            await context.StartMountedAsync();
                            TestRunner.Assert(context.Snapshot.Status.Ready);
                            TestRunner.Equal(1, resumed);
                            await context.ShutdownOwnedAsync();
                        }
                    }
                    catch (Exception e) { failure = e; context.ExitThread(); }
                }
                using var guard = new System.Windows.Forms.Timer { Interval = 45000 };
                guard.Tick += (_, _) => { failure ??= new TimeoutException("Delayed menu stop hung."); context.ExitThread(); };
                guard.Start(); Application.Run(context);
                TestRunner.Assert(delayed && context.MenuStopTask is { IsCompletedSuccessfully: true });
            }
            catch (Exception e) { failure ??= e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try
        {
            TestRunner.Assert(thread.Join(TimeSpan.FromSeconds(60)));
            if (failure is not null) throw new InvalidOperationException("Delayed menu failed.", failure);
            TestRunner.Equal(exit ? 0 : 1, resumed);
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
        finally { f.Controller.Stop(true); }
    }

    private static void CancelAtGate()
    {
        using var f = new Unit5Fixture("u6h-cancel-at-gate");
        using var cancel = new CancellationTokenSource();
        using var reached = new ManualResetEventSlim();
        int resumed = 0;
        f.Controller.SupervisorCheckpoint = _ => reached.Set();
        f.Controller.StartupCheckpoint = (stage, _) => { if (stage == "after-resume") resumed++; };
        Task<int>? run = null;
        ControlGate.Execute(() =>
        {
            run = Task.Run(() => f.Controller.Run(cancel.Token));
            TestRunner.Assert(reached.Wait(TimeSpan.FromSeconds(15)));
            cancel.Cancel();
            return 0;
        });
        TestRunner.Assert(run!.Wait(TimeSpan.FromSeconds(15))); TestRunner.Equal(0, run.Result);
        TestRunner.Equal(0, resumed); TestRunner.Assert(f.State.Load() is null);
    }

    private static void CancelCliPreparation()
    {
        using var f = new Unit5Fixture("u6h-cli-control-c");
        using var cancel = new CancellationTokenSource();
        int resumed = 0;
        f.Controller.StartupCheckpoint = (stage, _) =>
        {
            if (stage == "after-cache-preservation") cancel.Cancel();
            if (stage == "after-resume") resumed++;
        };
        var reply = Unit3cTests.Capture(() => (int)typeof(DbxLight.Cli.Program)
            .GetMethod("RunResident", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [f.Controller, true, cancel.Token])!);
        TestRunner.Equal(0, reply.Exit); TestRunner.Equal(0, resumed);
        TestRunner.Assert(reply.Output.Contains("cancelled", StringComparison.Ordinal));
        TestRunner.Assert(!f.Controller.Status().Running);
    }

    private static void StopRestartWait(bool cancelToken)
    {
        using var f = new Unit5Fixture("u6h-stop-restart-" + cancelToken);
        using var cancel = new CancellationTokenSource();
        var mounted = new TaskCompletionSource<RuntimeState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observeExit = new ManualResetEventSlim();
        var owned = new List<RuntimeState>(); bool stopped = false; int resumes = 0;
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") Interlocked.Increment(ref resumes);
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started")
            {
                mounted.TrySetResult(owned.Single());
                if (!observeExit.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Restart-wait test did not release the started watcher.");
                return;
            }
            if (stage != "restart-wait") return;
            if (cancelToken) cancel.Cancel(); else f.Controller.Stop();
            stopped = true;
        };
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token));
        try
        {
            // This checkpoint is after startup releases ControlGate. Do not poll Status
            // while startup can legitimately report busy after its two-second read budget.
            RuntimeState initial = mounted.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            using (var child = ProcessIdentity.Open(initial)!)
            { child.Kill(); TestRunner.Assert(child.WaitForExit(10000)); }
            observeExit.Set();
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            TestRunner.Equal(0, run.Result); TestRunner.Assert(stopped); TestRunner.Equal(1, resumes);
            TestRunner.Assert(!f.Controller.Status().Running);
        }
        finally
        {
            observeExit.Set(); cancel.Cancel(); f.Controller.Stop(true); TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }
}
