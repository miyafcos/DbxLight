using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class Unit6sTests
{
    private const double Scale = 0.14;
    private const string TimeoutMessage = "rclone did not stop; process was left running.";
    internal static void Run(TestRunner runner, string? issue = null)
    {
        if (issue == "force") { runner.Test("6s T2: force keeps the scaled ten-second deadline and kills only the owned child", Force); return; }
        if (issue == "regressions") { Regressions(runner); return; }
        bool before = issue == "before";
        if (issue is null or "all" or "before" or "T1")
        {
            runner.Test("6s T1: timeout with live child retains supervision until restart-wait", () => TimeoutCase(false, before));
            runner.Test("6s T1: timeout with exited observation records stop and prevents restart", () => TimeoutCase(true, before));
        }
        if (issue is null or "all" or "before" or "T2")
        {
            runner.Test("6s T2: child ending after the old deadline stops normally without restart", GracefulAfterOldDeadline);
            if (before) return;
            runner.Test("6s T2: force keeps the scaled ten-second deadline and kills only the owned child", Force);
            runner.Test("6s T2: pending uploads return exit three before core/quit or exit wait", Pending);
            runner.Test("6s T2: status JSON stays busy during normal stop", () => BusyRead(true));
            runner.Test("6s T2: resident snapshot read stays busy during normal stop", () => BusyRead(false));
        }
        if (issue is null or "all" or "T3") NotificationPolicy(runner);
    }

    private static void Regressions(TestRunner runner)
    {
        Unit3aTests.Run(runner, "A1"); Unit3aTests.Run(runner, "A3");
        Unit3bTests.Run(runner, "B2");
        Unit6gTests.Run(runner, "J1");
        Unit6hTests.Run(runner, "N2"); Unit6hTests.Run(runner, "N3");
        Unit6iTests.Run(runner);
        Unit6iTests.RunUnit6j(runner); Unit6iTests.RunUnit6k(runner); Unit6iTests.RunUnit6l(runner); Unit6iTests.RunUnit6m(runner);
        Unit6nTests.Run(runner); Unit6oTests.Run(runner); Unit6qTests.Run(runner);
    }
    private static void NotificationPolicy(TestRunner runner)
    {
        void Check(ResidentPhase phase, Exception error, int expected)
        {
            int notices = 0;
            ResidentContext.ReportRefreshFailure(phase, error, (title, detail, icon) =>
            {
                notices++; TestRunner.Equal("状態を確認できません", title);
                TestRunner.Equal(ToolTipIcon.Warning, icon); TestRunner.Assert(detail.Length > 0);
            });
            TestRunner.Equal(expected, notices);
        }
        runner.Test("6s T3: stop phases skip busy-read notifications", () =>
        {
            foreach (var phase in new[] { ResidentPhase.Stopping, ResidentPhase.Exiting }) Check(phase, new ControlBusyException(), 0);
        });
        runner.Test("6s T3: other phases retain busy-read notifications", () =>
        {
            foreach (var phase in Enum.GetValues<ResidentPhase>().Except(new[] { ResidentPhase.Stopping, ResidentPhase.Exiting })) Check(phase, new ControlBusyException(), 1);
        });
        runner.Test("6s T3: stop phases retain notifications for other read failures", () =>
        {
            foreach (var phase in new[] { ResidentPhase.Stopping, ResidentPhase.Exiting })
            foreach (var error in new Exception[] { new IOException("fixture"), new TimeoutException("fixture"), new JsonException("fixture") }) Check(phase, error, 1);
        });
    }
    private static Unit3aTests.RcFixture Fixture()
    {
        var fixture = new Unit3aTests.RcFixture(Path.Combine(TestPaths.WorkRoot, "run-unit6s-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixture.Config.Load().CacheDir);
        return fixture;
    }
    private static void EndChild(Unit3aTests.RcFixture fixture) =>
        File.WriteAllText(Path.Combine(fixture.Root, "child-stop.txt"), "exit", new UTF8Encoding(false));
    private static readonly object ConsoleGate = new();
    private static (int Exit, string Out, string Error) Cli(MountController controller, params string[] words)
    {
        lock (ConsoleGate)
        {
            TextWriter oldOut = Console.Out, oldError = Console.Error;
            using var output = new StringWriter(); using var error = new StringWriter();
            try
            {
                Console.SetOut(output); Console.SetError(error);
                string[] arguments = words.Concat(new[] { "--config", controller.Config.PathName,
                    "--state", controller.State.PathName }).ToArray();
                var entry = typeof(DbxLight.Cli.Program).GetMethod("MainCore",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                int exit = (int)entry.Invoke(null, [arguments, controller])!;
                return (exit, output.ToString(), error.ToString());
            }
            finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        }
    }
    private static void StateFields(RuntimeState actual, RuntimeState before, bool requested, StopDisposition disposition, bool recovery)
    {
        TestRunner.Equal(requested, actual.StopRequested);
        TestRunner.Assert(!actual.StopInProgress);
        TestRunner.Equal(disposition, actual.StopDisposition);
        TestRunner.Equal(recovery, actual.RecoveryRequired);
        TestRunner.Equal(requested, actual.StoppedUtc is not null);
        TestRunner.Equal(before.StopSequence + (requested ? 1 : 0), actual.StopSequence);
    }
    private static void Duration(double actual, double expected)
    {
        TestRunner.Assert(actual >= expected && actual <= 2 * 25 * Scale,
            $"Exit wait {actual:F3}s was outside [{expected:F3}, {2 * 25 * Scale:F3}]s.");
    }

    private static void TimeoutCase(bool exitedObservation, bool before)
    {
        using var f = Fixture();
        using var quit = new ManualResetEventSlim();
        using var watchStarted = new ManualResetEventSlim();
        using var observe = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        int hideProcess = 0, restartWait = 0, starts = 0;
        var controller = new MountController(f.Config, f.State, processQuery: state =>
            Volatile.Read(ref hideProcess) != 0 ? null : ProcessIdentity.Open(state)) { StopWaitTestScale = Scale };
        f.Respond = path =>
        {
            if (path == "/core/quit")
            {
                if (exitedObservation) Volatile.Write(ref hideProcess, 1);
                quit.Set(); return (200, "{}");
            }
            return (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0}}");
        };
        controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started") watchStarted.Set();
            if (stage == "before-watch-observation")
                TestRunner.Assert(observe.Wait(TimeSpan.FromSeconds(10)), "Observation barrier expired outside the gate.");
            if (stage == "restart-wait") { Interlocked.Increment(ref restartWait); cancel.Cancel(); }
        };
        Task<int> watch = Task.Run(() => controller.Run(cancel.Token, _ => Interlocked.Increment(ref starts)));
        try
        {
            TestRunner.Assert(watchStarted.Wait(TimeSpan.FromSeconds(10)));
            RuntimeState original = f.State.Load()!;
            var timer = new Stopwatch();
            var result = ControlGate.Execute(() => { timer.Start(); return Cli(controller, "stop"); });
            double elapsed = timer.Elapsed.TotalSeconds;
            TestRunner.Assert(quit.IsSet);
            TestRunner.Equal(1, result.Exit); TestRunner.Equal(TimeoutMessage, result.Error.Trim());
            RuntimeState stopped = f.State.Load()!;
            StateFields(stopped, original, exitedObservation, StopDisposition.StopUncertain, true);
            using (Process? live = ProcessIdentity.Open(original)) TestRunner.Assert(live is not null,
                "Timeout force-terminated the owned child.");
            Duration(elapsed, (before ? 10 : 25) * Scale);
            EndChild(f);
            Unit3aTests.Wait(() => { using var live = ProcessIdentity.Open(original); return live is null; }, "Owned child did not exit.");
            observe.Set();
            TestRunner.Assert(watch.Wait(TimeSpan.FromSeconds(10)));
            TestRunner.Equal(0, watch.Result);
            TestRunner.Equal(exitedObservation ? 0 : 1, restartWait);
            TestRunner.Equal(1, starts);
            Console.WriteLine($"6s timeout exitedObservation={exitedObservation}: exit={result.Exit}; error={result.Error.Trim()}; " +
                $"StopRequested={stopped.StopRequested}; StopInProgress={stopped.StopInProgress}; StopDisposition={stopped.StopDisposition}; " +
                $"RecoveryRequired={stopped.RecoveryRequired}; StoppedUtc={(stopped.StoppedUtc is null ? "null" : "present")}; " +
                $"StopSequence={original.StopSequence}->{stopped.StopSequence}; restart-wait={restartWait}; starts={starts}; elapsed={elapsed:F3}s.");
        }
        finally
        {
            EndChild(f); observe.Set(); cancel.Cancel();
            TestRunner.Assert(watch.Wait(TimeSpan.FromSeconds(10)), "Owned watcher did not finish.");
        }
    }

    private static void GracefulAfterOldDeadline()
    {
        using var f = Fixture();
        using var quit = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var observe = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        long quitAt = 0; int restarts = 0, starts = 0;
        f.Controller.StopWaitTestScale = Scale;
        f.Respond = path =>
        {
            if (path == "/core/quit") { Interlocked.Exchange(ref quitAt, Stopwatch.GetTimestamp()); quit.Set(); return (200, "{}"); }
            return (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0}}");
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "watch-started") started.Set();
            if (stage == "before-watch-observation") TestRunner.Assert(observe.Wait(TimeSpan.FromSeconds(10)));
            if (stage == "restart-wait") { Interlocked.Increment(ref restarts); cancel.Cancel(); }
        };
        Task<int> watch = Task.Run(() => f.Controller.Run(cancel.Token, _ => Interlocked.Increment(ref starts)));
        Task<(int Exit, string Out, string Error)>? stop = null;
        try
        {
            TestRunner.Assert(started.Wait(TimeSpan.FromSeconds(10)));
            RuntimeState original = f.State.Load()!;
            stop = Task.Run(() => Cli(f.Controller, "stop"));
            TestRunner.Assert(quit.Wait(TimeSpan.FromSeconds(3)));
            // End only after observing that the old, scaled deadline plus margin has elapsed.
            while (Stopwatch.GetElapsedTime(Interlocked.Read(ref quitAt)).TotalSeconds < 10 * Scale + 0.25) Thread.Sleep(10);
            EndChild(f);
            TestRunner.Assert(stop.Wait(TimeSpan.FromSeconds(5)));
            var result = stop.Result;
            TestRunner.Equal(0, result.Exit);
            StateFields(f.State.Load()!, original, true, StopDisposition.Clean, false);
            observe.Set(); TestRunner.Assert(watch.Wait(TimeSpan.FromSeconds(10)));
            TestRunner.Equal(0, restarts); TestRunner.Equal(1, starts);
            Console.WriteLine($"6s graceful: exit=0; child ended after {10 * Scale + 0.25:F2}s; clean stop; restart-wait=0; starts={starts}.");
        }
        finally
        {
            EndChild(f); observe.Set(); cancel.Cancel();
            if (stop is not null) TestRunner.Assert(stop.Wait(TimeSpan.FromSeconds(6)));
            TestRunner.Assert(watch.Wait(TimeSpan.FromSeconds(10)));
        }
    }
    private static void Force()
    {
        using var f = Fixture(); f.Controller.StopWaitTestScale = Scale;
        var waits = new List<int>(); f.Controller.StopWaitCheckpoint = waits.Add;
        f.Respond = _ => (200, "{}");
        RuntimeState original = f.State.Load()!;
        var timer = new Stopwatch(); var result = ControlGate.Execute(() => { timer.Start(); return Cli(f.Controller, "stop", "--force"); });
        double elapsed = timer.Elapsed.TotalSeconds;
        TestRunner.Equal(0, result.Exit); Duration(elapsed, 10 * Scale);
        TestRunner.Equal(1, waits.Count);
        TestRunner.Equal(checked((int)(MountController.ForcedStopWaitMilliseconds * Scale)), waits.Single());
        StateFields(f.State.Load()!, original, true, StopDisposition.Forced, true);
        using var live = ProcessIdentity.Open(original); TestRunner.Assert(live is null);
        Console.WriteLine($"6s force: exit=0; owned child killed after {elapsed:F3}s; scaled deadline={10 * Scale:F3}s; waitMilliseconds={waits.Single()}.");
    }
    private static void Pending()
    {
        using var f = Fixture(); f.Controller.StopWaitTestScale = Scale;
        int quits = 0;
        f.Respond = path =>
        {
            if (path == "/core/quit") Interlocked.Increment(ref quits);
            return (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0}}");
        };
        var timer = new Stopwatch(); var result = ControlGate.Execute(() => { timer.Start(); return Cli(f.Controller, "stop"); });
        TestRunner.Equal(3, result.Exit); TestRunner.Equal(0, quits);
        TestRunner.Assert(timer.Elapsed.TotalSeconds < 25 * Scale, "Pending refusal entered the exit wait.");
        using var live = ProcessIdentity.Open(f.State.Load()!); TestRunner.Assert(live is not null);
        Console.WriteLine("6s pending: exit=3; core/quit calls=0; owned child alive; exit wait not entered.");
    }
    private static void BusyRead(bool cli)
    {
        using var f = Fixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int paused = 0;
        var controller = new MountController(f.Config, f.State, processQuery: state =>
        {
            // Stop holds the real gate before starting its bounded exit wait.
            if (Interlocked.Exchange(ref paused, 1) == 0) { entered.Set(); release.Wait(); }
            return ProcessIdentity.Open(state);
        }) { StopWaitTestScale = Scale };
        f.Respond = path =>
        {
            if (path == "/core/quit") { EndChild(f); return (200, "{}"); }
            return (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0}}");
        };
        Task<StopResult> stop = Task.Run(() => controller.Stop());
        try
        {
            TestRunner.Assert(entered.Wait(TimeSpan.FromSeconds(30)), "Stop gate checkpoint missing.");
            var readTimer = Stopwatch.StartNew();
            if (cli)
            {
                var result = Cli(controller, "status", "--json"); TestRunner.Equal(0, result.Exit);
                using var json = JsonDocument.Parse(result.Out); TestRunner.Equal("busy", json.RootElement.GetProperty("code").GetString());
                Console.WriteLine("6s read during stop: status --json exit=0; code=busy.");
            }
            else
            {
                TestRunner.Throws<ControlBusyException>(() => SnapshotReader.Read(controller));
                Console.WriteLine("6s read during stop: SnapshotReader.Read -> ControlBusyException.");
            }
            TestRunner.Assert(readTimer.Elapsed.TotalSeconds is >= 1.8 and < 10, $"Busy read took {readTimer.Elapsed.TotalSeconds:F3}s.");
            TestRunner.Assert(!release.IsSet && !stop.IsCompleted);
            release.Set(); TestRunner.Assert(stop.Wait(TimeSpan.FromSeconds(30))); TestRunner.Assert(stop.Result.WasRunning);
        }
        finally { release.Set(); EndChild(f); TestRunner.Assert(stop.Wait(TimeSpan.FromSeconds(30))); }
    }
}
