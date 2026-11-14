using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    internal static void RunUnit6k(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        Test("K3", "automatic waiting stop persists receipt while owned child is absent", WaitingStopWithoutChild);
        foreach (bool pending in new[] { false, true })
            Test("K3", "successful launch retains ownership before a failed reread; pending=" + pending,
                () => SuccessfulLaunchOwnership(pending));
        Test("K3", "automatic waiting cleanup rechecks owner inside ControlGate", WaitingCleanupKeepsReplacement);
        Test("K3", "completion tail failure remains an accepted cancellation", CompletionTailKeepsCancellation);
        foreach (bool restart in new[] { false, true })
            Test("K3", "ownership read failure retains accepted cancellation; restart=" + restart,
                () => CancellationPersistenceFailure(restart, false, capture: true));
        foreach (bool restart in new[] { false, true })
        foreach (bool completion in new[] { true, false })
            Test("K2", $"accepted cancellation survives sharing failure; restart={restart}; completion={completion}",
                () => CancellationPersistenceFailure(restart, completion));
        foreach (bool preparing in new[] { true, false })
            Test("K1", "ended tray leaves replacement run intact; preparing=" + preparing, () => EndedTrayKeepsReplacement(preparing));
        Test("K1", "rejected tray startup leaves existing CLI owner intact", RejectedTrayKeepsOwner);
        foreach (bool pending in new[] { false, true })
            Test("K1", "ended watcher normally cleans only its own child; pending=" + pending, () => EndedWatcherOwnChild(pending));
    }



    private static void WaitingStopWithoutChild()
    {
        using var f = new Unit5Fixture("u6k-wait-no-child");
        using var cancel = new CancellationTokenSource();
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var mounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownership = new SupervisionOwnership();
        int resumed = 0;
        f.Controller.StartupCheckpoint = (stage, _) => { if (stage == "after-resume") resumed++; };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage != "restart-wait") return;
            reached.Set(); TestRunner.Assert(release.Wait(TimeSpan.FromSeconds(30)));
        };
        Task<int> run = Task.Run(() => f.Controller.RunForResident(ownership, cancel.Token, _ => mounted.TrySetResult(), default));
        try
        {
            TestRunner.Assert(mounted.Task.Wait(TimeSpan.FromSeconds(20)));
            RuntimeState before = f.State.Load()!;
            KillOwned(before);
            TestRunner.Assert(reached.Wait(TimeSpan.FromSeconds(15)));
            TestRunner.Assert(!f.Controller.ReconcileSupervision(ownership, supervisorEnded: false));
            RuntimeState stopped = f.State.Load()!;
            TestRunner.Assert(stopped.StopRequested && stopped.RecoveryRequired);
            TestRunner.Equal(before.StopSequence + 1, stopped.StopSequence);
            TestRunner.Assert(!cancel.IsCancellationRequested);
            release.Set();
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
            TestRunner.Equal(0, run.Result);
            TestRunner.Equal(1, resumed);
            TestRunner.Assert(!f.Controller.Status().Running);
        }
        finally
        {
            release.Set(); cancel.Cancel();
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.Stop(true);
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(15)));
        }
    }

    private static void SuccessfulLaunchOwnership(bool pending)
    {
        using var f = new Unit5Fixture("u6k-owned-success", writeBack: "5m");
        RuntimeState? launched = null;
        bool failed = false, scheduled = false;
        RunTray(f, async c =>
        {
            TestRunner.Assert(failed && launched is not null);
            TestRunner.Equal(pending ? ResidentPhase.StopFailed : ResidentPhase.Stopped, c.Phase);
            using (var child = ProcessIdentity.Open(launched!)) TestRunner.Equal(pending, child is not null);
            if (pending)
            {
                TestRunner.Equal(1L, f.Controller.Status().UploadsQueued);
                await Task.Run(() => DrainPending(f));
                await PauseAsync(c);
                TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
            }
        }, (c, execute) =>
        {
            c.Controller.StartupCheckpoint = (stage, state) =>
            {
                if (stage != "mount-ready") return;
                launched = state;
                if (pending)
                {
                    f.Write("pending.txt", "retained owned launch content");
                    Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Owned launch queue missing.");
                }
            };
            c.Controller.SupervisorCheckpoint = stage =>
            {
                if (stage != "before-ownership-read" || failed) return;
                failed = true;
                throw new IOException("Injected reread failure after successful launch.");
            };
            c.AutomaticStopAwaitCheckpoint = () =>
            {
                if (!scheduled)
                {
                    scheduled = true;
                    FormTests.Field<ResidentWindow>(c, "dispatcher").BeginInvoke((Action)(async () =>
                    {
                        await ResidentTests.Until(() => c.Phase is ResidentPhase.Stopped or ResidentPhase.StopFailed,
                            "Owned launch cleanup did not finish.");
                        await execute();
                    }));
                }
                return Task.CompletedTask;
            };
        }, autoStart: false);
    }

    private static void WaitingCleanupKeepsReplacement()
    {
        using var f = new Unit5Fixture("u6k-wait-handover");
        RunTray(f, async c =>
        {
            FormTests.Field<System.Windows.Forms.Timer>(c, "poll").Stop();
            if (FormTests.Field<TaskCompletionSource?>(c, "refreshCompletion") is { } refreshing) await refreshing.Task;
            Task<int> runA = ResidentTests.Watch(c)!;
            Task<(int Exit, string Output, string Error)>? runB = null;
            RuntimeState? before = null;
            bool handedOver = false;
            c.Controller.OwnedStopGateCheckpoint = () =>
            {
                TestRunner.Assert(!handedOver); handedOver = true;
                TestRunner.Equal(0, f.Cli("stop").Exit);
                TestRunner.Assert(runA.Wait(TimeSpan.FromSeconds(15)));
                runB = Task.Run(() => f.Cli("run"));
                Unit5Fixture.Wait(() => f.State.Load()?.SupervisorPid is > 0 && f.Controller.Status().Ready, "Replacement CLI not ready.");
                before = f.State.Load()!;
                TestRunner.Assert(before.SupervisorPid != Environment.ProcessId);
            };
            try
            {
                // The existing dialog tests cover selecting Wait. Inject its drained-queue phase
                // here so the replacement occurs exactly between the read and ControlGate.
                typeof(ResidentContext).GetMethod("SetPhase", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(c, [ResidentPhase.WaitingToPause]);
                await c.RefreshAsync();
                TestRunner.Assert(handedOver && before is not null);
                TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
                AssertSameOwner(before!, f.State.Load()!);
                TestRunner.Assert(!runB!.IsCompleted && f.Controller.Status().Ready);
                TestRunner.Assert(c.Snapshot.Error?.Contains("\u307b\u304b\u306e\u5165\u53e3") == true);
            }
            finally
            {
                c.Controller.OwnedStopGateCheckpoint = null;
                await Task.Run(() => f.Controller.Stop());
                if (runB is not null) TestRunner.Equal(0, (await runB).Exit);
            }
        });
    }

    private static void CompletionTailKeepsCancellation()
    {
        using var f = new Unit5Fixture("u6k-completion-tail");
        using var preparing = StartupPreparation.Begin(f.State.PathName, f.Store.PathName);
        TestRunner.Assert(StartupPreparation.RequestStop(f.State.PathName, f.Store.PathName));
        preparing.CompletionCheckpoint = stage =>
        {
            if (stage == "after-completion") throw new IOException("Injected failure after cancellation completion save.");
        };
        PreparationCancelledException? failure = null;
        try { preparing.Complete(); }
        catch (PreparationCancelledException error) { failure = error; }
        TestRunner.Assert(failure?.InnerException is IOException);
        TestRunner.Assert(StartupPreparation.Read(f.State.PathName, f.Store.PathName) is null);
    }

    private static void CancellationPersistenceFailure(bool restart, bool completion, bool capture = false)
    {
        using var f = new Unit5Fixture("u6k-cancel-save", writeBack: "5m");
        if (!restart)
        {
            f.Start(); f.Write("pending.txt", "accepted cancellation retained content");
            Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Initial pending body missing.");
            f.Controller.Stop(true);
        }
        using var cancel = new CancellationTokenSource();
        var mounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new List<RuntimeState>();
        FileStream? saveLock = null;
        int comparisons = 0, resumed = 0, injected = 0, captureInjected = 0, retriesAfterAcceptance = 0;
        bool accepted = false;
        f.Controller.RecoveryInfoOverride = info =>
        {
            if (Interlocked.Increment(ref comparisons) == 1)
                throw new RecoveryComparisonException("Injected comparison failure before accepted stop.");
            return info;
        };
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") Interlocked.Increment(ref resumed);
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (accepted && stage is "restart-wait" or "initial-retry-wait" or "restart-comparison-wait")
                Interlocked.Increment(ref retriesAfterAcceptance);
            if (accepted && capture && stage == "before-ownership-read" && captureInjected++ == 0)
                throw new IOException("Injected ownership read failure after accepted cancellation.");
        };
        f.Controller.PreparationCompletionCheckpoint = stage =>
        {
            if (Volatile.Read(ref comparisons) == 0) return;
            if (stage == "before-completion" && !accepted)
            {
                var stop = f.Cli("stop", "--json");
                TestRunner.Equal(0, stop.Exit);
                using var reply = System.Text.Json.JsonDocument.Parse(stop.Output);
                TestRunner.Assert(reply.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean());
                accepted = true;
            }
            if (stage == "before-completion-save" && completion && injected == 0)
            {
                saveLock = File.Open(f.State.PathName + ".preparation.json", FileMode.Open, FileAccess.Read, FileShare.Read);
                injected++;
            }
        };
        f.Controller.PreparationCancellationCheckpoint = stage =>
        {
            if (stage == "before-persistence")
            {
                saveLock?.Dispose(); saveLock = null;
                if (!completion && injected == 0)
                {
                    saveLock = File.Open(RecoveryJournal.CachePath(f.Config.CacheDir), FileMode.Open, FileAccess.Read, FileShare.Read);
                    injected++;
                }
            }
            else { saveLock?.Dispose(); saveLock = null; }
        };
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token, _ => mounted.TrySetResult()));
        try
        {
            if (restart)
            {
                TestRunner.Assert(mounted.Task.Wait(TimeSpan.FromSeconds(20)));
                f.Write("pending.txt", "accepted cancellation retained content");
                Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Restart pending body missing.");
                KillOwned(f.State.Load()!);
            }
            PendingUploadsException? failure = null;
            try { _ = run.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); }
            catch (PendingUploadsException error) { failure = error; }
            TestRunner.Assert(accepted && injected == 1);
            TestRunner.Assert(failure is not null && failure.Data["PreparationCancelled"] is true &&
                failure.InnerException is IOException or UnauthorizedAccessException &&
                StartupFailure.CleanupFailures(failure).Count == (capture ? 1 : 0),
                "Persistence failure did not remain an accepted cancellation.");
            TestRunner.Equal(capture ? 1 : 0, captureInjected);
            TestRunner.Equal("preparation_cancel_persistence_failed", StartupFailure.Get(failure!)?.Classification);
            TestRunner.Equal(0, retriesAfterAcceptance);
            TestRunner.Equal(1, comparisons);
            TestRunner.Equal(restart ? 1 : 0, resumed);
            TestRunner.Assert(!f.Controller.Status().Running);
            TestRunner.Assert(f.State.Load()!.RecoveryRequired);
            TestRunner.Assert(PrivateJsonFile.Read<RuntimeState>(RecoveryJournal.CachePath(f.Config.CacheDir))!.RecoveryRequired);
            TestRunner.Assert(PrivateJsonFile.Read<RuntimeState>(RecoveryJournal.ConfigPath(f.Store.PathName))!.RecoveryRequired);
            var body = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
            TestRunner.Equal("accepted cancellation retained content", File.ReadAllText(body.DataPath));
            Console.WriteLine($"Accepted stop retained; actual sharing failure=1; restart={restart}; completion={completion}; capture={capture}; retries=0; resumed={resumed}.");
        }
        finally
        {
            saveLock?.Dispose();
            cancel.Cancel();
            f.Controller.PreparationCompletionCheckpoint = null;
            f.Controller.PreparationCancellationCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.Stop(true);
            try { run.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
            catch (PendingUploadsException error) when (error.Data["PreparationCancelled"] is true) { }
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void AssertSameOwner(RuntimeState before, RuntimeState after)
    {
        TestRunner.Equal(before.GenerationId, after.GenerationId);
        TestRunner.Equal(before.GenerationNumber, after.GenerationNumber);
        TestRunner.Equal(before.SupervisorPid, after.SupervisorPid);
        TestRunner.Equal(before.SupervisorStartedUtc, after.SupervisorStartedUtc);
        TestRunner.Equal(before.RclonePid, after.RclonePid);
        TestRunner.Equal(before.ProcessStartedUtc, after.ProcessStartedUtc);
        TestRunner.Equal(before.StopSequence, after.StopSequence);
        TestRunner.Assert(!after.StopRequested);
    }

    private static void EndedTrayKeepsReplacement(bool preparing)
    {
        using var f = new Unit5Fixture("u6k-replacement");
        RunTray(f, async c =>
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var bReached = new ManualResetEventSlim();
            using var bRelease = new ManualResetEventSlim();
            using var cancelB = new CancellationTokenSource();
            var bMounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int>? runB = null;
            c.AutomaticStopAwaitCheckpoint = () => { reached.TrySetResult(); return release.Task; };
            try
            {
                Task<int> runA = ResidentTests.Watch(c)!;
                TestRunner.Equal(0, (await Task.Run(() => f.Cli("stop"))).Exit);
                TestRunner.Equal(0, await runA);
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await FormTests.Field<Task>(c, "supervisorObservation");
                f.Controller.StartupCheckpoint = (stage, _) =>
                {
                    if (!preparing || stage != "after-cache-preservation") return;
                    bReached.Set(); TestRunner.Assert(bRelease.Wait(TimeSpan.FromSeconds(4)));
                };
                runB = Task.Run(() => f.Controller.Run(cancelB.Token, _ => bMounted.TrySetResult()));
                if (preparing) TestRunner.Assert(await Task.Run(() => bReached.Wait(TimeSpan.FromSeconds(15))));
                else await bMounted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                RuntimeState? before = preparing ? null : f.State.Load();
                string? preparationId = StartupPreparation.Read(f.State.PathName, f.Store.PathName)?.Id;
                release.TrySetResult();
                await ResidentTests.Until(() => c.Phase == ResidentPhase.Stopped, "Automatic reconciliation did not stop its own tray.");
                TestRunner.Equal("\u518d\u958b", Pause(c).Text);
                TestRunner.Assert(c.Snapshot.Error?.Contains("\u307b\u304b\u306e\u5165\u53e3") == true);
                if (preparing)
                {
                    var progress = StartupPreparation.Read(f.State.PathName, f.Store.PathName);
                    TestRunner.Equal(preparationId, progress?.Id);
                    TestRunner.Assert(progress is { Active: true, Progress.Stopping: false });
                    bRelease.Set();
                    await bMounted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                else AssertSameOwner(before!, f.State.Load()!);
                TestRunner.Assert(!runB.IsCompleted);
                TestRunner.Assert(f.Controller.Status().Ready);
            }
            finally
            {
                release.TrySetResult(); bRelease.Set();
                c.AutomaticStopAwaitCheckpoint = null;
                f.Controller.StartupCheckpoint = null;
                await Task.Run(() => f.Controller.Stop());
                cancelB.Cancel();
                if (runB is not null) TestRunner.Equal(0, await runB.WaitAsync(TimeSpan.FromSeconds(15)));
            }
        });
    }

    private static void RejectedTrayKeepsOwner()
    {
        using var f = new Unit5Fixture("u6k-rejected");
        Task<(int Exit, string Output, string Error)> runB = Task.Run(() => f.Cli("run"));
        try
        {
            Unit5Fixture.Wait(() => f.State.Load()?.SupervisorPid is > 0 && f.Controller.Status().Ready, "CLI owner not ready.");
            RuntimeState before = f.State.Load()!;
            TestRunner.Assert(before.SupervisorPid != Environment.ProcessId);
            RunTray(f, async c =>
            {
                TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
                TestRunner.Equal("\u518d\u958b", Pause(c).Text);
                AssertSameOwner(before, f.State.Load()!);
                TestRunner.Assert(f.Controller.Status().Ready && !runB.IsCompleted);
                TestRunner.Assert(c.Snapshot.Error?.Contains("\u307b\u304b\u306e\u5165\u53e3") == true);
                TestRunner.Equal(0, (await Task.Run(() => f.Cli("stop"))).Exit);
                TestRunner.Equal(0, (await runB).Exit);
            }, (c, execute) =>
            {
                bool scheduled = false;
                c.AutomaticStopAwaitCheckpoint = () =>
                {
                    if (!scheduled)
                    {
                        scheduled = true;
                        FormTests.Field<ResidentWindow>(c, "dispatcher").BeginInvoke((Action)(async () =>
                        {
                            await ResidentTests.Until(() => c.Phase == ResidentPhase.Stopped, "Rejected startup did not become stopped.");
                            await execute();
                        }));
                    }
                    return Task.CompletedTask;
                };
            }, autoStart: false);
        }
        finally
        {
            f.Controller.Stop();
            TestRunner.Assert(runB.Wait(TimeSpan.FromSeconds(15)));
            TestRunner.Equal(0, runB.Result.Exit);
        }
    }

    private static void EndedWatcherOwnChild(bool pending)
    {
        using var f = new Unit5Fixture("u6k-own-cleanup", writeBack: "5m");
        RunTray(f, async c =>
        {
            if (pending)
            {
                f.Write("pending.txt", "own cleanup retained content");
                await Task.Run(() => Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Own queue missing."));
            }
            RuntimeState before = f.State.Load()!;
            bool failed = false;
            c.Controller.SupervisorCheckpoint = stage =>
            {
                if (stage != "before-watch-observation" || failed) return;
                failed = true;
                throw new IOException("Injected ended watcher with live owned child.");
            };
            try { await ResidentTests.Watch(c)!; }
            catch (IOException) { }
            await ResidentTests.Until(() => c.Phase is ResidentPhase.Stopped or ResidentPhase.StopFailed, "Own cleanup not completed.");
            TestRunner.Assert(failed);
            TestRunner.Equal(pending ? ResidentPhase.StopFailed : ResidentPhase.Stopped, c.Phase);
            using (var child = ProcessIdentity.Open(before)) TestRunner.Equal(pending, child is not null);
            if (pending)
            {
                TestRunner.Equal("\u505c\u6b62\u3092\u3084\u308a\u76f4\u3059", Pause(c).Text);
                TestRunner.Equal(before.StopSequence, f.State.Load()!.StopSequence);
                TestRunner.Assert(f.Controller.Status().UploadsQueued == 1);
                await Task.Run(() => DrainPending(f));
                await PauseAsync(c);
                TestRunner.Equal(ResidentPhase.Stopped, c.Phase);
            }
            c.Controller.SupervisorCheckpoint = null;
        });
    }
}
