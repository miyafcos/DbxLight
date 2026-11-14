using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

// Both holder modes release after four seconds without depending on the test caller.
internal sealed class TestControlGate : IDisposable
{
    private readonly string signalName = @"Local\DbxLight.TestGate." + Guid.NewGuid().ToString("N");
    private readonly EventWaitHandle release;
    private readonly ManualResetEventSlim acquired = new();
    private readonly bool childProcess;
    private Thread? thread;
    private Exception? threadFailure;
    private Process? worker;
    private bool crashed, disposed;

    internal TestControlGate(bool childProcess = false)
    {
        this.childProcess = childProcess;
        release = new(false, EventResetMode.ManualReset, signalName);
    }

    internal void Acquire()
    {
        TestRunner.Assert(worker is null && thread is null, "Control checkpoint reached twice.");
        if (childProcess)
        {
            var info = RcloneArguments.ProcessInfo(Environment.ProcessPath!, ["--unit6q-gate-child", signalName]);
            worker = Process.Start(info) ?? throw new IOException("Gate worker did not start.");
            string? ready = worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            TestRunner.Equal("held", ready);
            return;
        }
        thread = new Thread(() =>
        {
            try { TestRunner.Equal(0, Hold(release, acquired.Set)); }
            catch (Exception error) { threadFailure = error; }
            finally { acquired.Set(); }
        }) { IsBackground = true };
        thread.Start();
        TestRunner.Assert(acquired.Wait(TimeSpan.FromSeconds(10)), "Control gate acquisition did not finish.");
        if (threadFailure is not null) throw new InvalidOperationException("Control gate acquisition failed.", threadFailure);
    }

    internal static T WhileHeld<T>(Func<T> action)
    {
        using var gate = new TestControlGate();
        gate.Acquire();
        return action();
    }

    internal void Crash()
    {
        TestRunner.Assert(worker is not null);
        worker!.Kill();
        TestRunner.Assert(worker.WaitForExit(5000));
        crashed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            release.Set();
            if (thread is not null)
            {
                TestRunner.Assert(thread.Join(5000), "Bounded gate holder did not finish.");
                if (threadFailure is not null) throw new InvalidOperationException("Bounded gate holder failed.", threadFailure);
            }
            if (worker is not null)
            {
                if (!worker.WaitForExit(5000)) { worker.Kill(); TestRunner.Assert(worker.WaitForExit(5000)); }
                if (!crashed) TestRunner.Equal(0, worker.ExitCode);
            }
        }
        finally { worker?.Dispose(); release.Dispose(); acquired.Dispose(); }
    }

    internal static int Child(string signalName)
    {
        using var release = EventWaitHandle.OpenExisting(signalName);
        return Hold(release, () => { Console.WriteLine("held"); Console.Out.Flush(); });
    }

    private static int Hold(EventWaitHandle release, Action ready)
    {
        using var mutex = new Mutex(false, @"Local\DbxLight.Control.v1");
        bool held;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) return 2;
        bool requested;
        try { ready(); requested = release.WaitOne(TimeSpan.FromSeconds(4)); }
        finally { mutex.ReleaseMutex(); }
        return requested ? 0 : 1;
    }
}
