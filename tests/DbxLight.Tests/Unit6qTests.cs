using DbxLight.Core;

namespace DbxLight.Tests;

internal static class Unit6qTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        if (issue == "T3") { runner.Test("6q T3: OS releases control gate when its bounded worker exits unexpectedly", GateCrashReleases); return; }
        if (issue is null or "all" or "T1")
        {
            runner.Test("6q T1: busy observations preserve child and state, then resume and restart", () => BusyWatch(false));
            runner.Test("6q T1: cancellation during busy observations stops without another child", () => BusyWatch(true));
            runner.Test("6q T1: an unrelated observation timeout still fails", OtherTimeout);
            runner.Test("6q T3: OS releases control gate when its bounded worker exits unexpectedly", GateCrashReleases);
        }
    }

    internal static void GateCrashReleases()
    {
        using var gate = new TestControlGate(childProcess: true);
        gate.Acquire();
        TestRunner.Assert(!ControlGate.TryExecute(() => true));
        gate.Crash();
        var retry = System.Diagnostics.Stopwatch.StartNew();
        bool acquired;
        do
        {
            acquired = ControlGate.TryExecute(() => true);
            if (!acquired) Thread.Sleep(25);
        } while (!acquired && retry.Elapsed < TimeSpan.FromSeconds(5));
        TestRunner.Assert(acquired, "OS did not release the crashed worker's mutex.");
        Console.WriteLine("6q gate worker: owned PID exited; abandoned control mutex reacquired; holding guard=4s.");
    }

    private static int Events(Unit5Fixture f, string message)
    {
        string path = Path.Combine(f.Config.LogDir, "supervisor.log");
        return File.Exists(path) ? File.ReadAllLines(path).Count(line => line.EndsWith(message, StringComparison.Ordinal)) : 0;
    }

    private static void BusyWatch(bool cancelWhileBusy)
    {
        using var f = new Unit5Fixture("u6q-watch-" + cancelWhileBusy, writable: false);
        using var cancel = new CancellationTokenSource();
        using var releaseBusy = new ManualResetEventSlim();
        using var releaseResumed = new ManualResetEventSlim();
        using var busy = new ManualResetEventSlim();
        using var resumed = new ManualResetEventSlim();
        using var restarted = new ManualResetEventSlim();
        var owned = new List<RuntimeState>();
        int observations = 0, starts = 0;
        byte[]? before = null;
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage == "mount-ready") owned.Add(state);
        };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage != "before-watch-observation") return;
            int count = Interlocked.Increment(ref observations);
            if (count <= 3)
            {
                before ??= File.ReadAllBytes(f.State.PathName);
                TestRunner.Assert(before.SequenceEqual(File.ReadAllBytes(f.State.PathName)), "Skipped observation changed state.");
                if (count == 3)
                {
                    busy.Set();
                    TestRunner.Assert(releaseBusy.Wait(TimeSpan.FromSeconds(30)));
                }
                throw new ControlBusyException();
            }
            if (count == 5)
            {
                resumed.Set();
                TestRunner.Assert(releaseResumed.Wait(TimeSpan.FromSeconds(30)));
            }
        };
        Task<int> run = Task.Run(() => f.Controller.Run(cancel.Token, _ =>
        {
            if (Interlocked.Increment(ref starts) == 2) restarted.Set();
        }));
        try
        {
            TestRunner.Assert(busy.Wait(TimeSpan.FromSeconds(20)), "Busy checkpoint missing.");
            TestRunner.Assert(!run.IsCompleted);
            TestRunner.Assert(before!.SequenceEqual(File.ReadAllBytes(f.State.PathName)));
            using (var child = ProcessIdentity.Open(owned[0])) TestRunner.Assert(child is not null);
            TestRunner.Equal(1, Events(f, "Watch observation skipped: control gate busy."));
            TestRunner.Equal(0, Events(f, "Watch observation resumed."));
            if (cancelWhileBusy) cancel.Cancel();
            releaseBusy.Set();
            if (!cancelWhileBusy)
            {
                TestRunner.Assert(resumed.Wait(TimeSpan.FromSeconds(20)), "Resumed checkpoint missing.");
                TestRunner.Assert(!run.IsCompleted);
                TestRunner.Equal(1, Events(f, "Watch observation skipped: control gate busy."));
                TestRunner.Equal(1, Events(f, "Watch observation resumed."));
                using (var child = ProcessIdentity.Open(owned[0]) ?? throw new InvalidOperationException("Owned child missing."))
                { child.Kill(); TestRunner.Assert(child.WaitForExit(10000)); }
                releaseResumed.Set();
                TestRunner.Assert(restarted.Wait(TimeSpan.FromSeconds(20)), "Restart checkpoint missing.");
                TestRunner.Assert(!run.IsCompleted);
                TestRunner.Equal(2, starts);
                TestRunner.Equal(1, f.State.Load()!.RestartCount);
                using var replacement = ProcessIdentity.Open(owned[1]);
                TestRunner.Assert(replacement is not null);
                cancel.Cancel();
            }
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(20)));
            TestRunner.Equal(0, run.Result);
            TestRunner.Equal(cancelWhileBusy ? 1 : 2, starts);
            TestRunner.Equal(cancelWhileBusy ? 0 : 1, Events(f, "Watch observation resumed."));
            Console.WriteLine($"6q watch: skipped=3; skip log=1; resumed log={(cancelWhileBusy ? 0 : 1)}; starts={starts}; cancelWhileBusy={cancelWhileBusy}; run=0.");
        }
        finally
        {
            cancel.Cancel(); releaseBusy.Set(); releaseResumed.Set();
            TestRunner.Assert(run.Wait(TimeSpan.FromSeconds(20)));
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.Stop();
            foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
        }
    }

    private static void OtherTimeout()
    {
        using var f = new Unit5Fixture("u6q-other-timeout", writable: false);
        RuntimeState? owned = null;
        var expected = new TimeoutException("Unrelated observation timeout.");
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage == "before-watch-observation") throw expected;
        };
        try
        {
            Exception? actual = null;
            try { f.Controller.Run(CancellationToken.None, _ => owned = f.State.Load()); }
            catch (Exception error) { actual = error; }
            TestRunner.Assert(ReferenceEquals(expected, actual));
            TestRunner.Equal(0, Events(f, "Watch observation skipped: control gate busy."));
        }
        finally
        {
            f.Controller.SupervisorCheckpoint = null;
            f.Controller.Stop();
            if (owned is not null) { using var child = ProcessIdentity.Open(owned); TestRunner.Assert(child is null); }
        }
    }
}
