using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    internal static void RunUnit6l(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == "all" || issue == id) runner.Test(id + ": " + name, action); }
        Test("L2", "persistent stop-save and ownership-release failure retains CLI pending result", CancellationAndCleanupFailure);
        Test("L2", "cleanup attachment keeps the same primary exception", CleanupKeepsIdentity);
        Test("L3", "cleanup alone propagates and accepted cancellation outranks communication", CleanupPrecedence);
        Test("L3", "cancelled notification failure preserves accepted stop", CancellationNotificationFailure);
        Test("L3", "accepted cancellation retains preceding comparison cause through translation", CancellationKeepsComparisonCause);
        foreach (string kind in new[] { "recovery-read", "preparation-begin", "intent-save", "comparison-update", "ready-json", "ownership-read", "callback", "child-cleanup-save", "comparison-ownership-read" })
            Test("L3", "restart local storage or callback failure is terminal; kind=" + kind, () => RestartLocalFailure(kind));
        foreach (bool create in new[] { true, false })
            Test("L3", "native child startup failure still retries; create=" + create, () => RetryChildFailure(create));
        foreach (bool restart in new[] { false, true })
            Test("L3", "plain cancellation plus failed ownership read keeps CLI pending; restart=" + restart, () => PlainCancellationReadFailure(restart));
        foreach (bool restart in new[] { false, true })
        foreach (bool storageFailure in new[] { true, false })
            Test("L1", $"completion storage failure stops retries; restart={restart}; storageFailure={storageFailure}",
                () => CompletionStorageFailure(restart, storageFailure));
    }

    private static void CompletionStorageFailure(bool restart, bool storageFailure)
    {
        using var f = new Unit5Fixture("u6l-complete", writeBack: "5m");
        if (!restart)
        {
            f.Start(); f.Write("pending.txt", "completion failure retained content");
            Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Initial pending body missing.");
            f.Controller.Stop(true);
        }
        using var cancel = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new List<RuntimeState>();
        FileStream? saveLock = null;
        int comparisons = 0, resumes = 0, starts = 0, injections = 0, retries = 0;
        string? oldPreparation = null;
        bool accepted = false;
        f.Controller.RecoveryInfoOverride = info =>
        {
            if (Interlocked.Increment(ref comparisons) == 1)
                throw new RecoveryComparisonException("Injected remote communication failure.");
            return info;
        };
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") Interlocked.Increment(ref resumes);
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.PreparationCompletionCheckpoint = stage =>
        {
            if (!storageFailure || comparisons != 1 || stage != "before-completion-save" || injections != 0) return;
            TestRunner.Assert(StartupPreparation.Read(f.State.PathName, f.Store.PathName) is not null);
            oldPreparation = StartupPreparation.Read(f.State.PathName, f.Store.PathName)!.Id;
            saveLock = File.Open(f.State.PathName + ".preparation.json", FileMode.Open, FileAccess.Read, FileShare.Read);
            injections++;
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage is "initial-retry-wait" or "restart-comparison-wait" or "restart-wait")
            {
                if (!restart || starts > 0 && comparisons > 0) retries++;
                TestRunner.Assert(!accepted, "A retry followed the accepted stop.");
            }
            if (stage != "before-ownership-read" || saveLock is null) return;
            saveLock.Dispose(); saveLock = null;
            var stop = f.Cli("stop", "--json");
            TestRunner.Equal(0, stop.Exit);
            using var reply = JsonDocument.Parse(stop.Output);
            accepted = reply.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean();
            TestRunner.Assert(accepted);
        };
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token, _ =>
        {
            if (Interlocked.Increment(ref starts) == 1) first.TrySetResult();
            else second.TrySetResult();
        }));
        try
        {
            if (restart)
            {
                TestRunner.Assert(first.Task.Wait(TimeSpan.FromSeconds(20)));
                f.Write("pending.txt", "completion failure retained content");
                Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Restart pending body missing.");
                KillOwned(f.State.Load()!);
            }
            if (storageFailure)
            {
                RecoveryComparisonException? failure = null;
                try { run.WaitAsync(TimeSpan.FromSeconds(25)).GetAwaiter().GetResult(); }
                catch (RecoveryComparisonException error) { failure = error; }
                TestRunner.Assert(failure is not null && StartupFailure.CleanupFailures(failure).Any(e => e is PreparationPersistenceException));
                TestRunner.Assert(accepted && injections == 1);
                TestRunner.Equal(0, retries);
                TestRunner.Equal(1, comparisons);
                TestRunner.Equal(restart ? 1 : 0, resumes);
                TestRunner.Assert(!f.Controller.Status().Running);
                TestRunner.Equal(oldPreparation, StartupPreparation.Read(f.State.PathName, f.Store.PathName)!.Id);
                f.Controller.PreparationCompletionCheckpoint = null;
                f.Controller.SupervisorCheckpoint = null;
                f.Controller.RecoveryInfoOverride = null;
                f.Start(awaitRecovery: false);
                TestRunner.Assert(f.Controller.Status().Ready, "Old active preparation blocked explicit start.");
                TestRunner.Assert(StartupPreparation.Read(f.State.PathName, f.Store.PathName) is null);
                TestRunner.Assert(File.Exists(f.State.PathName + ".cancel-" + oldPreparation + ".json"));
                TestRunner.Equal((restart ? 1 : 0) + 1, resumes);
                Console.WriteLine($"Local completion failure: accepted stop=true; retries=0; explicit start=ready; restart={restart}.");
            }
            else
            {
                TestRunner.Assert((restart ? second.Task : first.Task).Wait(TimeSpan.FromSeconds(30)));
                TestRunner.Assert(comparisons > 1 && retries > 0);
                TestRunner.Equal(restart ? 2 : 1, resumes);
                TestRunner.Equal(0, injections);
                f.Controller.Stop(true);
                TestRunner.Equal(0, run.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());
                Console.WriteLine($"Communication-only failure: retry succeeded; restart={restart}.");
            }
        }
        finally
        {
            saveLock?.Dispose();
            f.Controller.PreparationCompletionCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.RecoveryInfoOverride = null;
            f.Controller.Stop(true); cancel.Cancel();
            try { run.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
            catch (RecoveryComparisonException error) when (storageFailure && StartupFailure.CleanupFailures(error).Count > 0) { }
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void CleanupKeepsIdentity()
    {
        var primary = new PendingUploadsException("accepted stop", innerException: new IOException("stop sequence save"));
        primary.Data["PreparationCancelled"] = true;
        StartupFailure.ForUser(primary, "preparation_cancel_persistence_failed", "test-state", "accepted stop", "check storage");
        Exception same = primary;
        var cleanup = new IOException("ownership release save");
        StartupFailure.AddCleanupFailure(primary, cleanup);
        TestRunner.Assert(ReferenceEquals(same, primary));
        TestRunner.Assert(ReferenceEquals(cleanup, StartupFailure.CleanupFailures(primary).Single()));
        TestRunner.Equal("preparation_cancel_persistence_failed", StartupFailure.Get(primary)!.Classification);
        TestRunner.Assert(primary.InnerException is IOException && primary.Data["PreparationCancelled"] is true);
    }

    private static void CancellationAndCleanupFailure()
    {
        using var f = new Unit5Fixture("u6l-cancel-cleanup", writeBack: "5m");
        FileStream? saveLock = null;
        var owned = new List<RuntimeState>();
        bool accepted = false;
        int comparisons = 0, resumes = 0, releaseAttempts = 0, retriesAfterStop = 0;
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") resumes++;
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.RecoveryInfoOverride = _ =>
        {
            comparisons++;
            throw new RecoveryComparisonException("Injected remote communication failure.");
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started")
            {
                f.Write("pending.txt", "same storage fault preserves accepted stop");
                Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Pending body missing.");
                KillOwned(f.State.Load()!);
            }
            if (accepted && stage is "initial-retry-wait" or "restart-comparison-wait" or "restart-wait")
                retriesAfterStop++;
            if (stage == "before-ownership-release") releaseAttempts++;
        };
        f.Controller.PreparationCompletionCheckpoint = stage =>
        {
            if (stage != "before-completion" || comparisons == 0 || accepted) return;
            var stop = f.Cli("stop", "--json");
            TestRunner.Equal(0, stop.Exit);
            using var reply = JsonDocument.Parse(stop.Output);
            accepted = reply.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean();
            TestRunner.Assert(accepted);
        };
        f.Controller.PreparationCancellationCheckpoint = stage =>
        {
            if (stage == "before-persistence")
                saveLock = File.Open(RecoveryJournal.CachePath(f.Config.CacheDir), FileMode.Open, FileAccess.Read, FileShare.Read);
            // Keep the same sharing failure through Run's ownership release.
        };
        try
        {
            var reply = Unit3cTests.Capture(() => (int)typeof(DbxLight.Cli.Program)
                .GetMethod("MainCore", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [new[] { "--config", f.Store.PathName, "--state", f.State.PathName, "run", "--json" }, f.Controller])!);
            TestRunner.Equal(3, reply.Exit);
            using var json = JsonDocument.Parse(reply.Output);
            TestRunner.Equal("pending", json.RootElement.GetProperty("code").GetString());
            TestRunner.Equal("preparation_cancel_persistence_failed", json.RootElement.GetProperty("failureClass").GetString());
            string message = json.RootElement.GetProperty("message").GetString()!;
            TestRunner.Assert(message.Contains("\u8d77\u52d5\u3057\u76f4\u3057\u306f\u884c\u3044\u307e\u305b\u3093"));
            TestRunner.Assert(message.Contains("\u5f8c\u7247\u3065\u3051\u3082"));
            TestRunner.Assert(accepted && saveLock is not null);
            TestRunner.Equal(1, releaseAttempts);
            TestRunner.Equal(0, retriesAfterStop);
            TestRunner.Equal(1, comparisons);
            TestRunner.Equal(1, resumes);
            Console.WriteLine("CLI persistent storage fault: exit=3; code=pending; class=preparation_cancel_persistence_failed; cleanup appended; retries=0.");
        }
        finally
        {
            saveLock?.Dispose();
            f.Controller.PreparationCompletionCheckpoint = null;
            f.Controller.PreparationCancellationCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.Stop(true);
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void CleanupPrecedence()
    {
        var cleanup = new IOException("cleanup alone");
        Exception? actual = null;
        try { StartupFailure.Cleanup(null, () => throw cleanup, "test-state"); }
        catch (IOException error) { actual = error; }
        TestRunner.Assert(ReferenceEquals(cleanup, actual));

        var remote = new RecoveryComparisonException("connection failed");
        var accepted = new PreparationCancelledException();
        try { StartupFailure.Cleanup(remote, () => throw accepted, "test-state"); }
        catch (PreparationCancelledException error) { actual = error; }
        TestRunner.Assert(ReferenceEquals(accepted, actual));
        TestRunner.Assert(ReferenceEquals(remote, accepted.Data["DbxLight.PriorFailure"]));
        TestRunner.Equal(0, StartupFailure.CleanupFailures(accepted).Count);

        var earlier = new PreparationCancelledException();
        var save = new IOException("completion save failed");
        StartupFailure.Cleanup(earlier, () => throw new PreparationCancelledException(save), "test-state");
        TestRunner.Assert(ReferenceEquals(save, StartupFailure.CleanupFailures(earlier).Single()));
    }

    private static void RestartLocalFailure(string kind)
    {
        using var f = new Unit5Fixture("u6l-local-" + kind, writeBack: "5m");
        var owned = new List<RuntimeState>();
        FileStream? saveLock = null;
        int resumes = 0, waits = 0, comparisons = 0, starts = 0;
        bool injected = false;
        void Lock(string path, bool denyRead = false)
        {
            TestRunner.Assert(!injected);
            saveLock = File.Open(path, FileMode.Open, FileAccess.Read, denyRead ? FileShare.None : FileShare.Read);
            injected = true;
        }
        f.Controller.RecoveryInfoOverride = info =>
        {
            comparisons++;
            if (kind == "comparison-update") Lock(f.State.PathName + ".preparation.json");
            if (kind == "comparison-ownership-read") throw new RecoveryComparisonException("Injected remote failure before read failure.");
            return info;
        };
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") resumes++;
            if (stage == "mount-ready")
            {
                owned.Add(state);
                if (kind == "ready-json" && resumes > 1)
                { injected = true; throw new JsonException("Injected local state parse failure."); }
            }
            if (waits == 0) return;
            if (stage == "after-cache-preservation" && kind == "intent-save")
                Lock(RecoveryJournal.CachePath(f.Config.CacheDir));
            if (stage == "before-child-create" && kind == "child-cleanup-save")
            {
                Lock(RecoveryJournal.CachePath(f.Config.CacheDir));
                throw new DetachedProcess.NoChildCreatedException(2);
            }
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started")
            {
                if (kind is "comparison-update" or "comparison-ownership-read")
                {
                    f.Write("pending.txt", "local failure must stop automatic retries");
                    Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Pending local failure body missing.");
                }
                KillOwned(f.State.Load()!);
            }
            if (stage == "restart-wait")
            {
                waits++;
                TestRunner.Equal(1, waits);
                if (kind == "recovery-read") Lock(RecoveryJournal.CachePath(f.Config.CacheDir), denyRead: true);
                if (kind == "preparation-begin") Lock(f.State.PathName + ".preparation.json");
            }
            if (stage == "before-ownership-read" &&
                (kind == "ownership-read" && resumes > 1 || kind == "comparison-ownership-read" && comparisons > 0))
                Lock(f.State.PathName, denyRead: true);
            if (stage is "initial-retry-wait" or "restart-comparison-wait")
                throw new InvalidOperationException("Local failure reached a communication retry.");
        };
        try
        {
            Exception? failure = null;
            try
            {
                f.Controller.Run(CancellationToken.None, _ =>
                {
                    starts++;
                    if (kind == "callback" && starts > 1)
                    { injected = true; throw new IOException("Injected startup callback local failure."); }
                });
            }
            catch (Exception error) { failure = error; }
            TestRunner.Assert(injected && failure is not null);
            TestRunner.Assert(!StartupFailure.CanRetry(failure!));
            TestRunner.Equal(1, waits);
            TestRunner.Equal(kind is "ready-json" or "ownership-read" or "callback" ? 2 : 1, resumes);
            if (kind == "ready-json") TestRunner.Assert(failure is JsonException, "Local JSON failure was polled as an RC failure.");
            if (kind is "child-cleanup-save" or "comparison-ownership-read")
                TestRunner.Assert(StartupFailure.CleanupFailures(failure!).Count > 0);
            Console.WriteLine($"Terminal local failure: kind={kind}; restart waits=1; additional retries=0; resumes={resumes}.");
        }
        finally
        {
            saveLock?.Dispose();
            f.Controller.StartupCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            // A failed no-child proof leaves a guarded launch intent; retain it without bypassing the guard.
            if (kind != "child-cleanup-save") f.Controller.Stop(true);
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void RetryChildFailure(bool create)
    {
        using var f = new Unit5Fixture("u6l-child-retry");
        var owned = new List<RuntimeState>();
        int resumes = 0, waits = 0, starts = 0;
        bool injected = false;
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "before-child-create" && waits > 0 && create && !injected)
            { injected = true; throw new DetachedProcess.NoChildCreatedException(2); }
            if (stage == "after-resume")
            {
                resumes++;
                if (waits > 0 && !create && !injected)
                { injected = true; KillOwned(state); }
            }
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started") KillOwned(f.State.Load()!);
            if (stage == "restart-wait") { waits++; TestRunner.Assert(waits <= 2); }
        };
        try
        {
            TestRunner.Equal(0, f.Controller.Run(CancellationToken.None, _ =>
            {
                if (++starts == 2) f.Controller.Stop();
            }));
            TestRunner.Assert(injected);
            TestRunner.Equal(2, starts);
            TestRunner.Equal(2, waits);
            TestRunner.Equal(create ? 2 : 3, resumes);
            Console.WriteLine($"Child startup failure retried successfully: create={create}; restart waits=2.");
        }
        finally
        {
            f.Controller.StartupCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.Stop(true);
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void PlainCancellationReadFailure(bool restart)
    {
        using var f = new Unit5Fixture("u6l-plain-stop-read", writeBack: "5m");
        if (!restart)
        {
            f.Start(); f.Write("pending.txt", "plain accepted stop with failed reread");
            Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Initial plain stop body missing.");
            f.Controller.Stop(true);
        }
        var owned = new List<RuntimeState>();
        FileStream? saveLock = null;
        bool accepted = false;
        int comparisons = 0, retries = 0, resumes = 0;
        f.Controller.RecoveryInfoOverride = _ =>
        { comparisons++; throw new RecoveryComparisonException("Injected comparison before plain stop."); };
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") resumes++;
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started")
            {
                f.Write("pending.txt", "plain accepted stop with failed reread");
                Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Restart plain stop body missing.");
                KillOwned(f.State.Load()!);
            }
            if (accepted && stage is "restart-wait" or "initial-retry-wait" or "restart-comparison-wait") retries++;
            if (accepted && stage == "before-ownership-read")
                saveLock = File.Open(f.State.PathName, FileMode.Open, FileAccess.Read, FileShare.None);
        };
        f.Controller.PreparationCompletionCheckpoint = stage =>
        {
            if (stage != "before-completion" || comparisons == 0 || accepted) return;
            var stop = f.Cli("stop", "--json");
            TestRunner.Equal(0, stop.Exit);
            using var json = JsonDocument.Parse(stop.Output);
            accepted = json.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean();
        };
        try
        {
            var reply = Unit3cTests.Capture(() => (int)typeof(DbxLight.Cli.Program)
                .GetMethod("MainCore", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [new[] { "--config", f.Store.PathName, "--state", f.State.PathName, "run", "--json" }, f.Controller])!);
            TestRunner.Equal(3, reply.Exit);
            using var json = JsonDocument.Parse(reply.Output);
            TestRunner.Equal("pending", json.RootElement.GetProperty("code").GetString());
            TestRunner.Equal("preparation_cancel_persistence_failed", json.RootElement.GetProperty("failureClass").GetString());
            TestRunner.Assert(accepted && saveLock is not null);
            TestRunner.Equal(0, retries);
            TestRunner.Equal(1, comparisons);
            TestRunner.Equal(restart ? 1 : 0, resumes);
        }
        finally
        {
            saveLock?.Dispose();
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.PreparationCompletionCheckpoint = null;
            f.Controller.Stop(true);
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void CancellationNotificationFailure()
    {
        using var f = new Unit5Fixture("u6l-cancel-notification");
        bool accepted = false;
        int resumes = 0;
        var notification = new IOException("Injected local failure in cancellation notification.");
        f.Controller.StartupCheckpoint = (stage, _) =>
        {
            if (stage == "after-resume") resumes++;
            if (stage != "after-cache-preservation") return;
            var stop = f.Cli("stop", "--json");
            TestRunner.Equal(0, stop.Exit);
            using var json = JsonDocument.Parse(stop.Output);
            accepted = json.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean();
        };
        try
        {
            PendingUploadsException? failure = null;
            try { f.Controller.Run(CancellationToken.None, onPreparationCancelled: () => throw notification); }
            catch (PendingUploadsException error) { failure = error; }
            TestRunner.Assert(accepted && failure is not null && failure.Data["PreparationCancelled"] is true);
            TestRunner.Assert(ReferenceEquals(notification, StartupFailure.CleanupFailures(failure!).Single()));
            TestRunner.Equal("preparation_cancel_persistence_failed", StartupFailure.Get(failure!)!.Classification);
            TestRunner.Equal(0, resumes);
        }
        finally { f.Controller.StartupCheckpoint = null; f.Controller.Stop(true); }
    }

    private static void CancellationKeepsComparisonCause()
    {
        using var f = new Unit5Fixture("u6l-stop-prior-cause", writeBack: "5m");
        f.Start(); f.Write("pending.txt", "accepted stop retains earlier comparison cause");
        Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Prior comparison body missing.");
        f.Controller.Stop(true);
        var prior = new RecoveryComparisonException("Injected preceding comparison failure.");
        bool accepted = false;
        f.Controller.RecoveryInfoOverride = _ => throw prior;
        f.Controller.PreparationCompletionCheckpoint = stage =>
        {
            if (stage != "before-completion" || accepted) return;
            var stop = f.Cli("stop", "--json");
            TestRunner.Equal(0, stop.Exit);
            using var json = JsonDocument.Parse(stop.Output);
            accepted = json.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean();
        };
        try
        {
            PendingUploadsException? failure = null;
            try { f.Controller.Start(); }
            catch (PendingUploadsException error) { failure = error; }
            TestRunner.Assert(accepted && failure is not null && failure.Data["PreparationCancelled"] is true);
            TestRunner.Assert(ReferenceEquals(prior, failure!.Data["DbxLight.PriorFailure"]));
            TestRunner.Assert(failure.InnerException is null, "A connection failure was misclassified as cancellation persistence failure.");
        }
        finally { f.Controller.PreparationCompletionCheckpoint = null; f.Controller.Stop(true); }
    }
}
