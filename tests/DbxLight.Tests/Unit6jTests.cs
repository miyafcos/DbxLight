using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

// Reuse the resident menu harness and owned-PID checks from unit 6i.
internal static partial class Unit6iTests
{
    internal static void RunUnit6j(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        foreach (string action in new[] { "apply", "logout", "write", "setup" })
            Test("T6", "config stop failure never restores an ended watch; action=" + action, () => ConfigStopFailure(action));
        Test("T6", "setup close keeps resume blocked until config is read", SetupCloseBlocksResume);
        Test("T6", "watch fault before startup commit confirms the normal stop", WatchFaultBeforeStartupCommit);
        foreach (bool exit in new[] { false, true })
        foreach (bool unknown in new[] { false, true })
            Test("T2", $"stop dialog uses refusal counts and reason; exit={exit}; unknown={unknown}", () => RefusalCounts(exit, unknown));
        foreach (bool restart in new[] { false, true })
        foreach (bool after in new[] { false, true })
            Test("T3", $"accepted stop crosses preparation completion; restart={restart}; after={after}",
                () => PreparationCompletionStop(restart, after));
        Test("T4", "old startup cleanup blocks resume until its own completion", StartupCleanupBlocksResume);
        foreach (string stage in new[] { "watching", "initial-config", "before-doctor" })
            Test("T5", "session state read fails after confirmed stop; stage=" + stage, () => SessionReadFailure(stage));
        foreach (bool external in new[] { false, true })
        foreach (bool wait in new[] { false, true })
        foreach (bool exit in new[] { false, true })
            Test("T1", $"watch ends inside stop dialog; external={external}; wait={wait}; exit={exit}",
                () => WatchEndsInDialog(external, wait, exit));
    }






    private static void WatchFaultBeforeStartupCommit()
    {
        using var f = new Unit5Fixture("u6j-before-startup-commit");
        bool held = false, failed = false;
        RunTray(f, async c =>
        {
            TestRunner.Assert(held && failed);
            TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
            TestRunner.Assert(!f.Controller.Status().Running);
            await ResumeAsync(c);
            TestRunner.Equal(ResidentPhase.Watching, c.Phase);
            TestRunner.Assert(f.Controller.Status().Ready);
        }, (c, _) =>
        {
            c.Controller.SupervisorCheckpoint = stage =>
            {
                if (stage != "watch-started" || failed) return;
                failed = true;
                throw new IOException("Injected watcher failure before startup commit.");
            };
            c.StartupAwaitCheckpoint = async stage =>
            {
                if (stage != "startup-finalizing" || held) return;
                held = true;
                try { await ResidentTests.Watch(c)!; }
                catch (IOException) { }
                TestRunner.Assert(f.Controller.Status().Running, "The pre-commit fault did not leave its child alive.");
            };
        });
    }

    private static void ConfigStopFailure(string action)
    {
        using var f = new Unit5Fixture("u6j-config-stop-failure");
        RunTray(f, async c =>
        {
            Task<int> watch = ResidentTests.Watch(c)!;
            c.MountStopCompletedCheckpoint = () => throw new IOException("Injected failure after accepted normal stop.");
            bool failed = false;
            try
            {
                if (action == "apply") await c.ApplyConfigAsync(config => config);
                else
                {
                    string name = action == "logout" ? "LogoutAsync" : action == "write" ? "SetWriteAccessAsync" : "ShowSetupAsync";
                    object?[] args = action == "logout" ? [] : action == "write" ? [c.Config, true, true] : [false];
                    var method = typeof(ResidentContext).GetMethod(name,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    await (Task)method.Invoke(c, args)!;
                }
            }
            catch (IOException) { failed = true; }
            finally { c.MountStopCompletedCheckpoint = null; }
            TestRunner.Assert(failed);
            TestRunner.Equal(ResidentPhase.StopFailed, c.Phase);
            await watch;
            await FormTests.Field<Task>(c, "supervisorObservation");
            TestRunner.Equal(ResidentPhase.StopFailed, c.Phase);
            await PauseAsync(c);
            TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
            await ResumeAsync(c);
            TestRunner.Equal(ResidentPhase.Watching, c.Phase);
            TestRunner.Assert(f.Controller.Status().Ready);
        });
    }

    private static void SetupCloseBlocksResume()
    {
        using var f = new Unit5Fixture("u6j-setup-close");
        RunTray(f, async c =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool held = false;
            c.SetupCloseCheckpoint = () => { held = true; return release.Task; };
            try
            {
                var method = typeof(ResidentContext).GetMethod("ShowSetupAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                await (Task)method.Invoke(c, [false])!;
                TestRunner.Equal(ResidentPhase.Changing, c.Phase);
                FormTests.Field<SetupForm>(c, "setupForm").Close();
                TestRunner.Assert(held);
                await c.StartMountedAsync();
                TestRunner.Equal(ResidentPhase.Changing, c.Phase);
                TestRunner.Assert(!f.Controller.Status().Running);
                release.TrySetResult();
                await ResidentTests.Until(() => c.Phase == ResidentPhase.Watching, "Setup close did not finish before restarting.");
                await FormTests.Field<Task>(c, "mountStartup");
                TestRunner.Assert(ResidentTests.Watch(c) is { IsCompleted: false });
                TestRunner.Assert(f.Controller.Status().Ready);
            }
            finally { release.TrySetResult(); c.SetupCloseCheckpoint = null; }
        });
    }

    private static void RefusalCounts(bool exit, bool unknown)
    {
        using var f = new Unit5Fixture("u6j-refusal-count", writeBack: "5m");
        RunTray(f, async c =>
        {
            await c.RefreshAsync();
            TestRunner.Equal(0L, c.Snapshot.Status.UploadsQueued);
            FormTests.Field<System.Windows.Forms.Timer>(c, "poll").Interval = 60000;
            f.Write("pending.txt", "refusal count content");
            await Task.Run(() => Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1,
                "Refusal queue missing."));
            TestRunner.Equal(0L, c.Snapshot.Status.UploadsQueued);
            int port = f.State.Load()!.RcPort;
            if (unknown) RuntimeSnapshots.Save(f.State, f.State.Load()! with { RcPort = 0 });
            bool chose = false; Exception? failure = null;
            IEnumerable<Control> Controls(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    yield return child;
                    foreach (Control nested in Controls(child)) yield return nested;
                }
            }
            EventHandler choose = (_, _) =>
            {
                if (chose || Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is not { } dialog) return;
                chose = true;
                try
                {
                    string text = string.Join("\n", Controls(dialog).OfType<Label>().Select(l => l.Text));
                    TestRunner.Assert(!text.Contains("\u30a2\u30c3\u30d7\u30ed\u30fc\u30c9\u5f85\u3061\u304c 0 \u4ef6"));
                    TestRunner.Assert(text.Contains(unknown ? "\u4ef6\u6570\u306f\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093" : "\u30a2\u30c3\u30d7\u30ed\u30fc\u30c9\u5f85\u3061\u304c 1 \u4ef6"));
                    TestRunner.Assert(text.Contains(unknown ? "upload state is unknown" : "uploadsQueued=1, uploadsInProgress=0"));
                }
                catch (Exception e) { failure = e; }
                finally
                {
                    if (unknown) RuntimeSnapshots.Save(f.State, f.State.Load()! with { RcPort = port });
                    ((Button)dialog.CancelButton!).PerformClick();
                }
            };
            Application.Idle += choose;
            try
            {
                if (exit) FormTests.Field<ToolStripMenuItem>(c, "exit").PerformClick();
                else Pause(c).PerformClick();
                await c.MenuStopTask!;
                if (failure is not null) throw failure;
                TestRunner.Assert(chose);
                TestRunner.Equal(ResidentPhase.Watching, c.Phase);
                TestRunner.Assert(ResidentTests.Watch(c) is { IsCompleted: false });
                await Task.Run(() => DrainPending(f));
                await PauseAsync(c);
            }
            finally
            {
                Application.Idle -= choose;
                if (unknown) RuntimeSnapshots.Save(f.State, f.State.Load()! with { RcPort = port });
            }
        });
    }

    private static void PreparationCompletionStop(bool restart, bool after)
    {
        using var f = new Unit5Fixture("u6j-preparation-finish", writeBack: "5m");
        if (!restart)
        {
            f.Start(); f.Write("pending.txt", "retained completion content");
            Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData),
                "Pending completion body missing.");
            f.Controller.Stop(true);
        }
        using var cancel = new CancellationTokenSource();
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var mounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new List<RuntimeState>();
        int comparisons = 0, resumed = 0;
        f.Controller.RecoveryInfoOverride = _ =>
        {
            Interlocked.Increment(ref comparisons);
            throw new RecoveryComparisonException("Injected failure after the probe cancellation check.");
        };
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") Interlocked.Increment(ref resumed);
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.PreparationCompletionCheckpoint = stage =>
        {
            if (after || stage != "before-completion" || Volatile.Read(ref comparisons) == 0) return;
            reached.Set(); TestRunner.Assert(release.Wait(TimeSpan.FromSeconds(4)));
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (!after || stage != (restart ? "restart-comparison-wait" : "initial-retry-wait")) return;
            reached.Set(); TestRunner.Assert(release.Wait(TimeSpan.FromSeconds(30)));
        };
        long before = f.State.Load()?.StopSequence ?? 0;
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token, _ => mounted.TrySetResult()));
        try
        {
            if (restart)
            {
                TestRunner.Assert(mounted.Task.Wait(TimeSpan.FromSeconds(30)));
                f.Write("pending.txt", "retained completion content");
                Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Restart pending body missing.");
                KillOwned(f.State.Load()!);
            }
            TestRunner.Assert(reached.Wait(TimeSpan.FromSeconds(30)), "Preparation completion checkpoint not reached.");
            var stop = f.Cli("stop", "--json");
            TestRunner.Equal(0, stop.Exit);
            using (var reply = System.Text.Json.JsonDocument.Parse(stop.Output))
                TestRunner.Equal(!after, reply.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean());
            release.Set();
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            TestRunner.Equal(0, run.Result);
            TestRunner.Equal(before + 1, f.State.Load()!.StopSequence);
            TestRunner.Assert(f.State.Load()!.StopRequested);
            TestRunner.Equal(1, comparisons);
            TestRunner.Equal(restart ? 1 : 0, resumed);
            TestRunner.Assert(!f.Controller.Status().Running);
            var body = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
            TestRunner.Equal("retained completion content", File.ReadAllText(body.DataPath));
        }
        finally
        {
            release.Set(); cancel.Cancel();
            f.Controller.PreparationCompletionCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.Stop(true);
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void StartupCleanupBlocksResume()
    {
        using var f = new Unit5Fixture("u6j-old-cleanup");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int resumed = 0; bool cancelled = false, held = false;
        RunTray(f, async c =>
        {
            try
            {
                await ResidentTests.Watch(c)!;
                await FormTests.Field<Task>(c, "supervisorObservation");
                TestRunner.Equal(ResidentPhase.Starting, c.Phase);
                TestRunner.Assert(Pause(c).Text != "\u518d\u958b");
                await c.StartMountedAsync();
                TestRunner.Equal(0, resumed);
                Pause(c).PerformClick();
                Task stop = c.MenuStopTask!;
                TestRunner.Equal(ResidentPhase.Stopping, c.Phase);
                release.TrySetResult();
                await stop;
                TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
                await ResumeAsync(c);
                TestRunner.Equal(1, resumed);
                TestRunner.Equal(ResidentPhase.Watching, c.Phase);
                TestRunner.Assert(ResidentTests.Watch(c) is { IsCompleted: false });
                TestRunner.Assert(f.Controller.Status().Ready);
                TestRunner.Equal("\u4e00\u6642\u505c\u6b62", Pause(c).Text);
            }
            finally { release.TrySetResult(); }
        }, (c, execute) =>
        {
            var dispatcher = FormTests.Field<ResidentWindow>(c, "dispatcher");
            c.Controller.StartupCheckpoint = (stage, _) =>
            {
                if (stage == "after-resume") Interlocked.Increment(ref resumed);
                if (stage != "after-cache-preservation" || cancelled) return;
                TestRunner.Equal(0, f.Cli("stop").Exit);
                cancelled = true;
            };
            c.StartupAwaitCheckpoint = stage =>
            {
                if (stage != "startup-finalizing" || held) return Task.CompletedTask;
                held = true;
                dispatcher.BeginInvoke((Action)(() => { _ = execute(); }));
                return release.Task;
            };
        }, autoStart: false);
    }

    private static void SessionReadFailure(string stage)
    {
        using var f = new Unit5Fixture("u6j-session-read");
        bool reached = false;
        RunTray(f, async c =>
        {
            RuntimeState? stopped = null;
            c.SessionStateReadCheckpoint = () =>
            {
                stopped = f.State.Load();
                TestRunner.Assert(stopped?.StopRequested == true);
                File.WriteAllText(f.State.PathName, "{", new System.Text.UTF8Encoding(false));
            };
            try
            {
                var method = typeof(ResidentContext).GetMethod("PrepareSessionStopAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                await (Task)method.Invoke(c, null)!;
            }
            finally
            {
                c.SessionStateReadCheckpoint = null;
                if (stopped is not null) RuntimeSnapshots.Save(f.State, stopped);
            }
            TestRunner.Equal(ResidentPhase.StopFailed, c.Phase);
            TestRunner.Equal("\u505c\u6b62\u3092\u3084\u308a\u76f4\u3059", Pause(c).Text);
            TestRunner.Assert(ResidentTests.Watch(c) is null);
            TestRunner.Assert(!f.Controller.Status().Running);
            await PauseAsync(c);
            TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
            await ResumeAsync(c);
            TestRunner.Equal(ResidentPhase.Watching, c.Phase);
            TestRunner.Assert(f.Controller.Status().Ready);
        }, (c, execute) =>
        {
            if (stage == "watching") return;
            c.StartupAwaitCheckpoint = async boundary =>
            {
                if (boundary != stage || reached) return;
                reached = true;
                await Task.Yield();
                _ = execute();
            };
        }, autoStart: stage == "watching");
    }

    private static void DrainPending(Unit5Fixture f)
    {
        using var rc = new RcClient(f.State.Load()!);
        foreach (var item in rc.Call("vfs/queue").GetProperty("queue").EnumerateArray())
            rc.Call("vfs/queue-set-expiry", new { id = item.GetProperty("id").GetInt64(), expiry = -1 });
        Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 0 &&
            f.Controller.Status().UploadsInProgress == 0 &&
            File.Exists(Path.Combine(f.RemoteRoot, "pending.txt")), "Pending upload did not drain.");
    }

    private static void WatchEndsInDialog(bool external, bool wait, bool exit)
    {
        using var f = new Unit5Fixture("u6j-dialog", writeBack: "5m");
        RunTray(f, async c =>
        {
            Task<int> watch = ResidentTests.Watch(c)!;
            f.Write("pending.txt", "retained dialog content");
            await Task.Run(() => Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1,
                "Dialog queue missing."));
            string body = PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Single(e => e.Dirty == true).DataPath;
            int failures = 0; bool chose = false; Exception? injection = null;
            var failedChildren = new List<RuntimeState>();
            EventHandler? choose = null;
            choose = async (_, _) =>
            {
                if (chose || Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is not { } dialog) return;
                chose = true;
                // Keep modal input out until the asynchronous watch-end injection has finished.
                IEnumerable<Control> Descendants(Control parent)
                {
                    foreach (Control child in parent.Controls)
                    { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
                }
                foreach (var button in Descendants(dialog).OfType<Button>()) button.Enabled = false;
                dialog.ControlBox = false;
                try
                {
                    TestTimeline.Dialog(f.Root, dialog, "dialog-injection-held");
                    ((Button)dialog.AcceptButton!).PerformClick();
                    TestRunner.Assert(dialog.Visible && dialog.Choice == StopChoice.Cancel, "Premature modal choice was accepted.");
                    if (external)
                    {
                        await Task.Run(() => { DrainPending(f); f.Controller.Stop(); });
                    }
                    else
                    {
                        c.Controller.StartupCheckpoint = (stage, state) =>
                        {
                            if (stage != "before-resume") return;
                            failedChildren.Add(state);
                            Interlocked.Increment(ref failures);
                            throw StartupFailure.RetryableChild(new System.ComponentModel.Win32Exception(5));
                        };
                        await Task.Run(() => KillOwned(f.State.Load()!));
                    }
                    await watch;
                    await FormTests.Field<Task>(c, "supervisorObservation");
                    TestRunner.Equal(exit ? ResidentPhase.Exiting : ResidentPhase.Stopping, c.Phase);
                    if (!external)
                    {
                        TestRunner.Equal(3, failures);
                        foreach (var state in failedChildren) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
                        TestRunner.Equal("retained dialog content", File.ReadAllText(body));
                        TestRunner.Assert(!File.Exists(Path.Combine(f.RemoteRoot, "pending.txt")));
                    }
                }
                catch (Exception e) { injection = e; }
                finally
                {
                    TestTimeline.Dialog(f.Root, dialog, "dialog-test-choice", new { external, wait, exit });
                    var selected = (Button)(wait ? dialog.AcceptButton! : dialog.CancelButton!);
                    selected.Enabled = true;
                    selected.PerformClick();
                }
            };
            Application.Idle += choose;
            try
            {
                if (exit) FormTests.Field<ToolStripMenuItem>(c, "exit").PerformClick();
                else Pause(c).PerformClick();
                await c.MenuStopTask!;
                if (injection is not null) throw injection;
                TestRunner.Assert(chose);
                TestRunner.Equal(exit ? ResidentPhase.Exiting : ResidentPhase.Stopped, c.Phase);
                TestRunner.Assert(!c.Controller.Status().Running);
                if (!exit)
                {
                    TestRunner.Equal("\u518d\u958b", Pause(c).Text);
                    c.Controller.StartupCheckpoint = null;
                    await ResumeAsync(c);
                    TestRunner.Equal(ResidentPhase.Watching, c.Phase);
                    if (!external) await Task.Run(() => DrainPending(f));
                    TestRunner.Equal("retained dialog content", File.ReadAllText(Path.Combine(f.RemoteRoot, "pending.txt")));
                    await PauseAsync(c);
                }
            }
            finally { Application.Idle -= choose; c.Controller.StartupCheckpoint = null; }
        });
    }
}
