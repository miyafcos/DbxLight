using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    internal static void RunUnit6m(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == "all" || issue == id) runner.Test(id + ": " + name, action); }
        foreach (bool restart in new[] { false, true })
        foreach (bool localFailure in new[] { true, false })
            Test("M1", $"prior Update failure followed by accepted stop; restart={restart}; localFailure={localFailure}",
                () => PriorFailureThenStop(restart, localFailure));
        foreach (string kind in new[] { "local", "comparison", "child" })
        foreach (bool additional in new[] { false, true })
            Test("M1", $"cleanup translation retains local and additional failures; kind={kind}; additional={additional}",
                () => CleanupTranslation(kind, additional));
    }

    private static void PriorFailureThenStop(bool restart, bool localFailure)
    {
        using var f = new Unit5Fixture("u6m-prior-stop", writeBack: "5m");
        if (!restart)
        {
            f.Start(); f.Write("pending.txt", "prior local failure must remain visible");
            Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Initial pending body missing.");
            f.Controller.Stop(true);
        }
        FileStream? saveLock = null;
        var owned = new List<RuntimeState>();
        int comparisons = 0, resumes = 0, retriesAfterStop = 0, completionSaves = 0, stopSaves = 0;
        long previousSequence = 0;
        bool injected = false, accepted = false, completed = false;
        f.Controller.RecoveryInfoOverride = info =>
        {
            comparisons++;
            TestRunner.Equal(1, comparisons);
            injected = true;
            if (!localFailure) throw new RecoveryComparisonException("Injected communication-only failure.");
            saveLock = File.Open(f.State.PathName + ".preparation.json", FileMode.Open, FileAccess.Read, FileShare.Read);
            return info;
        };
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "after-resume") resumes++;
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started")
            {
                TestRunner.Assert(restart);
                f.Write("pending.txt", "prior local failure must remain visible");
                Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Restart pending body missing.");
                KillOwned(f.State.Load()!);
            }
            if (accepted && stage is "initial-retry-wait" or "restart-comparison-wait" or "restart-wait")
            {
                retriesAfterStop++;
                throw new InvalidOperationException("An accepted cancellation reached another attempt.");
            }
        };
        f.Controller.PreparationCompletionCheckpoint = stage =>
        {
            if (!injected) return;
            if (stage == "before-completion" && !accepted)
            {
                // Update has already failed. Recover storage before accepting the external stop.
                TestRunner.Equal(localFailure, saveLock is not null);
                saveLock?.Dispose(); saveLock = null;
                previousSequence = f.State.Load()?.StopSequence ?? 0;
                var stop = f.Cli("stop", "--json");
                TestRunner.Equal(0, stop.Exit);
                using var reply = JsonDocument.Parse(stop.Output);
                accepted = reply.RootElement.GetProperty("result").GetProperty("preparationCancellationRequested").GetBoolean();
                TestRunner.Assert(accepted);
            }
            if (stage == "before-completion-save") completionSaves++;
            if (stage == "after-completion") completed = true;
        };
        f.Controller.PreparationCancellationCheckpoint = stage =>
        {
            TestRunner.Assert(accepted && completed);
            if (stage != "after-persistence") return;
            var stopped = f.State.Load()!;
            TestRunner.Assert(stopped.StopRequested && stopped.SupervisorPid is null);
            TestRunner.Equal(previousSequence + 1, stopped.StopSequence);
            stopSaves++;
        };
        try
        {
            var reply = Unit3cTests.Capture(() => (int)typeof(DbxLight.Cli.Program)
                .GetMethod("MainCore", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [new[] { "--config", f.Store.PathName, "--state", f.State.PathName, "run", "--json" }, f.Controller])!);
            TestRunner.Equal(localFailure ? 3 : 0, reply.Exit);
            using var json = JsonDocument.Parse(reply.Output);
            TestRunner.Equal(!localFailure, json.RootElement.GetProperty("ok").GetBoolean());
            TestRunner.Equal(localFailure ? "pending" : "cancelled", json.RootElement.GetProperty("code").GetString());
            if (localFailure)
            {
                TestRunner.Equal("preparation_cancel_persistence_failed", json.RootElement.GetProperty("failureClass").GetString());
                TestRunner.Assert(json.RootElement.GetProperty("message").GetString()!.Contains("\u8a18\u9332\u3092\u4fdd\u5b58\u3067\u304d\u307e\u305b\u3093"));
            }
            TestRunner.Assert(accepted && completed && injected);
            TestRunner.Equal(1, completionSaves);
            TestRunner.Equal(1, stopSaves);
            TestRunner.Equal(1, comparisons);
            TestRunner.Equal(restart ? 1 : 0, resumes);
            TestRunner.Equal(0, retriesAfterStop);
            TestRunner.Assert(StartupPreparation.Read(f.State.PathName, f.Store.PathName) is null);
            TestRunner.Assert(!f.Controller.Status().Running);
            Console.WriteLine($"Prior failure then stop: restart={restart}; local={localFailure}; exit={reply.Exit}; " +
                $"code={(localFailure ? "pending" : "cancelled")}; completion saves=1; stop saves=1; next attempts=0.");
        }
        finally
        {
            saveLock?.Dispose();
            f.Controller.RecoveryInfoOverride = null;
            f.Controller.StartupCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.PreparationCompletionCheckpoint = null;
            f.Controller.PreparationCancellationCheckpoint = null;
            f.Controller.Stop(true);
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void CleanupTranslation(string kind, bool additional)
    {
        Exception prior = kind switch
        {
            "comparison" => new RecoveryComparisonException("comparison failure"),
            "child" => StartupFailure.RetryableChild(new IOException("child launch failure")),
            _ => new IOException("Update save failure")
        };
        var extra = new IOException("earlier cleanup failure");
        if (additional) StartupFailure.AddCleanupFailure(prior, extra, "test-state");
        var accepted = new PreparationCancelledException();
        PreparationCancelledException? actual = null;
        try { StartupFailure.Cleanup(prior, () => throw accepted, "test-state"); }
        catch (PreparationCancelledException error) { actual = error; }
        TestRunner.Assert(ReferenceEquals(accepted, actual));
        TestRunner.Assert(ReferenceEquals(prior, accepted.Data["DbxLight.PriorFailure"]));
        var failures = StartupFailure.CleanupFailures(accepted);
        bool retainPrimary = kind == "local" || additional;
        TestRunner.Equal((retainPrimary ? 1 : 0) + (additional ? 1 : 0), failures.Count);
        if (retainPrimary) TestRunner.Assert(ReferenceEquals(prior, failures[0]));
        if (additional) TestRunner.Assert(ReferenceEquals(extra, failures[^1]));
    }
}
