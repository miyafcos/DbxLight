using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class Unit3cTests
{
    public static void Run(TestRunner runner, string? issue = null, bool includeMounts = true)
    {
        void Test(string id, string name, Action action, bool mount = false)
        {
            if ((issue is null || issue == id) && (includeMounts || !mount)) runner.Test(id + ": " + name, action);
        }
        if (issue is null or "C1-review") Unit3cIdentityReviewTests.Run(runner, includeMounts);
        if (issue is null or "C4") Unit3cQueueTextTests.Run(runner);
        if (issue is null or "C3") Unit3cRecoveryTests.Run(runner);
        Test("C2", "unknown identity guidance names the PID and process and only offers possible recovery", UnknownGuidance);
        Test("C2", "unknown process name is explicitly unavailable", UnknownNameGuidance);
        Test("C2", "status retains the unknown process detail for the recovery window", UnknownStatusGuidance);
        Test("C2", "supervisor and cache-owner refusal identify their own unknown PID", UnknownOwners);
        Test("C1", "a different name proves death before any privileged query", DifferentName);
        Test("C1", "the same name with inaccessible queries remains unknown", SameNameDenied);
        Test("C1", "an unreadable name stays unknown unless exit is confirmed", UnreadableName);
        Test("C1", "case, start time, path mismatch and malformed recorded paths keep their guards", IdentityBoundaries);
        Test("C1", "a controlled reused rclone PID permits clean start and status", RealReusedRclone, true);
        Test("C1", "a controlled reused supervisor PID permits clean start", RealReusedSupervisor, true);
        Test("C1", "a controlled reused cache-owner PID permits clean start", RealReusedCacheOwner, true);
        Test("C1", "recovery clear and both CLI status forms accept a controlled reused PID", RealReusedClear);
    }

    private static ProcessIdentityUnknownException Unknown(bool noName = false)
    {
        var query = Queries("rclone");
        if (noName) query = query with { Name = _ => throw new Win32Exception(5, "name query unavailable") };
        try { using Process? process = ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, @"C:\test\rclone.EXE", query); }
        catch (ProcessIdentityUnknownException e) { return e; }
        throw new InvalidOperationException("Expected an unknown identity");
    }

    private static void AssertUnknownGuidance(string text, int pid, string? name)
    {
        TestRunner.Assert(text.Contains(pid.ToString(), StringComparison.Ordinal), "Missing PID: " + text);
        TestRunner.Assert(text.Contains(name ?? "\u540d\u524d\u3092\u53d6\u5f97\u3067\u304d\u307e\u305b\u3093", StringComparison.Ordinal), "Missing process name/unavailable explanation: " + text);
        TestRunner.Assert(text.Contains("Windows", StringComparison.Ordinal) && text.Contains("\u518d\u8d77\u52d5", StringComparison.Ordinal), text);
        TestRunner.Assert(text.Contains("\u7d42\u4e86", StringComparison.Ordinal), text);
        TestRunner.Assert(!text.Contains("recovery clear", StringComparison.Ordinal) && !text.Contains("\u540c\u3058\u56de\u5fa9\u5148\u3067\u518d\u958b", StringComparison.Ordinal), "Unavailable action still advertised: " + text);
    }

    private static string Refusal(Action action)
    {
        try { action(); }
        catch (PendingUploadsException e) { return e.Message; }
        throw new InvalidOperationException("Expected a recovery refusal");
    }

    private static void UnknownGuidance()
    {
        using var f = new LockoutMount("unknown-guidance"); RuntimeSnapshots.Save(f.State, f.Clean);
        var controller = new MountController(f.Config, f.State, processQuery: _ => throw Unknown());
        foreach (Action action in new Action[] { () => controller.Start(), () => controller.ClearRecovery(true),
            () => controller.UpdateConfig(c => c with { CacheDir = Path.Combine(f.Root, "other-cache") }) })
            AssertUnknownGuidance(Refusal(action), Environment.ProcessId, "rclone");
        AssertUnknownGuidance(RecoveryDestination.Explain(f.State.Load()!), Environment.ProcessId, "rclone");
    }

    private static void UnknownNameGuidance()
    {
        using var f = new LockoutMount("unknown-name"); RuntimeSnapshots.Save(f.State, f.Clean);
        var controller = new MountController(f.Config, f.State, processQuery: _ => throw Unknown(true));
        AssertUnknownGuidance(Refusal(() => controller.Start()), Environment.ProcessId, null);
    }

    private static void UnknownStatusGuidance()
    {
        using var f = new LockoutMount("unknown-status"); RuntimeSnapshots.Save(f.State, f.Clean);
        var controller = new MountController(f.Config, f.State, processQuery: _ => throw Unknown());
        TraySnapshot snapshot = SnapshotReader.Read(controller);
        TestRunner.Assert(snapshot.Status.ProcessUncertain, "Status bypassed the identity query.");
        AssertUnknownGuidance(snapshot.Status.RecoveryLocation!, Environment.ProcessId, "rclone");
        TestRunner.Assert(snapshot.Status.StatusError!.Contains(Environment.ProcessId.ToString(), StringComparison.Ordinal));
        FormTests.Sta(() =>
        {
            using var form = new StatusForm(); form.UpdateSnapshot(snapshot, null, f.Config.Load());
            AssertUnknownGuidance(FormTests.Field<TextBox>(form, "recovery").Text, Environment.ProcessId, "rclone");
        });
    }

    private static void UnknownOwners()
    {
        if (ControlledReusedIdentity() is not { } target) return;
        using var current = Process.GetCurrentProcess();
        DateTime actualStart = current.StartTime.ToUniversalTime();
        foreach (string owner in new[] { "supervisor", "cache" })
        {
            using var f = new LockoutMount("unknown-" + owner);
            string image = Path.Combine(@"C:\test", target.Name + ".exe");
            RuntimeSnapshots.Save(f.State, owner == "supervisor" ? f.Clean with { SupervisorPid = target.Pid,
                SupervisorStartedUtc = actualStart, SupervisorExecutablePath = image,
                StopDisposition = StopDisposition.UnexpectedExit, RecoveryRequired = true, StopRequested = false } : f.Clean);
            if (owner == "cache")
            {
                Directory.CreateDirectory(f.Config.Load().CacheDir);
                JsonFiles.Write(Path.Combine(f.Config.Load().CacheDir, "dbxl-owner.json"),
                    new { pid = target.Pid, processStartedUtc = actualStart, executablePath = image, statePath = f.State.PathName, identityVersion = 1 });
            }
            AssertUnknownGuidance(Refusal(() => f.Controller.Start()), target.Pid, target.Name);
        }
    }

    private static ProcessIdentityQueries Queries(string name, bool denied = true) => new(_ => name,
        _ => denied ? throw new Win32Exception(5, "injected access denied") : false,
        _ => throw new InvalidOperationException("Start time must not be queried"),
        _ => throw new InvalidOperationException("Image must not be queried"));

    private static void DifferentName()
    {
        using Process? result = ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, @"C:\test\rclone.EXE", Queries("svchost"));
        TestRunner.Assert(result is null, "A different name was not recognized as a reused PID.");
    }

    private static void SameNameDenied() => TestRunner.Throws<ProcessIdentityUnknownException>(() =>
        ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, @"C:\test\rclone.EXE", Queries("RCLONE")));

    private static void UnreadableName()
    {
        var query = Queries("unused") with { Name = _ => throw new Win32Exception(5, "name unavailable") };
        TestRunner.Throws<ProcessIdentityUnknownException>(() => ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, @"C:\test\rclone.EXE", query));
        using Process? exited = ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, @"C:\test\rclone.EXE",
            query with { Exited = _ => true });
        TestRunner.Assert(exited is null);
    }

    private static void IdentityBoundaries()
    {
        var query = Queries("RCLONE", false) with { StartedUtc = _ => DateTime.UnixEpoch, Image = _ => @"C:\test\rclone.EXE" };
        using (Process? alive = ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, @"c:\TEST\rclone.exe", query))
            TestRunner.Assert(alive is not null, "Case-only executable difference killed a live identity.");
        using (Process? reused = ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch.AddSeconds(1), @"C:\test\rclone.EXE", query))
            TestRunner.Assert(reused is null);
        TestRunner.Throws<ProcessIdentityUnknownException>(() => ProcessIdentity.Open(Environment.ProcessId,
            DateTime.UnixEpoch, @"C:\other\rclone.EXE", query));
        foreach (string path in new[] { "", " ", "rclone.exe", @"C:\test\", @"C:\test\*.exe", "C:\\test\\rclone.exe\0", @"C:\test\rclone.exe " })
            TestRunner.Throws<ProcessIdentityUnknownException>(() => ProcessIdentity.Open(Environment.ProcessId,
                DateTime.UnixEpoch, path, Queries("different")));
    }

    private static (int Pid, string Name)? ControlledReusedIdentity()
    {
        // The unit6e boundary forbids querying unrelated processes. The current
        // owned test process supplies a real mismatched name; injected denied queries remain above.
        using var process = Process.GetCurrentProcess();
        return (process.Id, process.ProcessName);
    }

    private static void RealReusedRclone()
    {
        if (ControlledReusedIdentity() is not { } target) return;
        using var f = new LockoutMount("rclone");
        RuntimeState clean = f.Clean with { RclonePid = target.Pid };
        RuntimeSnapshots.Save(f.State, clean);
        DbxStatus status = f.Controller.Status();
        StopDisposition beforeStart = f.State.Load()!.StopDisposition;
        f.Start();
        TestRunner.Assert(!status.ProcessUncertain && status.StatusError is null, "Status falsely reports ProcessUnknown: " + status.StatusError);
        TestRunner.Equal(StopDisposition.Clean, beforeStart);
        TestRunner.Assert(f.Controller.Status().Ready);
        TestRunner.Assert(f.State.Load()!.StopDisposition != StopDisposition.ProcessUnknown);
    }

    private static void RealReusedSupervisor()
    {
        if (ControlledReusedIdentity() is not { } target) return;
        using var f = new LockoutMount("supervisor");
        RuntimeSnapshots.Save(f.State, f.Clean with { SupervisorPid = target.Pid, SupervisorStartedUtc = DateTime.UnixEpoch });
        f.Start();
        TestRunner.Assert(f.Controller.Status().Ready);
    }

    private static void RealReusedCacheOwner()
    {
        if (ControlledReusedIdentity() is not { } target) return;
        using var f = new LockoutMount("cache-owner");
        RuntimeSnapshots.Save(f.State, f.Clean);
        Directory.CreateDirectory(f.Config.Load().CacheDir);
        JsonFiles.Write(Path.Combine(f.Config.Load().CacheDir, "dbxl-owner.json"),
            new { pid = target.Pid, processStartedUtc = DateTime.UnixEpoch, executablePath = f.Clean.ExecutablePath, statePath = f.State.PathName, identityVersion = 1 });
        f.Start();
        TestRunner.Assert(f.Controller.Status().Ready);
    }

    private static void RealReusedClear()
    {
        if (ControlledReusedIdentity() is not { } target) return;
        using var f = new LockoutMount("clear");
        RuntimeSnapshots.Save(f.State, f.Clean with { RclonePid = target.Pid });
        string[] common = ["--config", f.Config.PathName, "--state", f.State.PathName];
        var status = Capture(() => DbxLight.Cli.Program.Main([.. common, "status"]));
        TestRunner.Equal(0, status.Exit);
        TestRunner.Assert(!status.Output.Contains("error=", StringComparison.Ordinal), status.Output);
        var machine = Capture(() => DbxLight.Cli.Program.Main([.. common, "status", "--json"]));
        TestRunner.Equal(0, machine.Exit);
        using (JsonDocument doc = JsonDocument.Parse(machine.Output))
        {
            TestRunner.Assert(!doc.RootElement.GetProperty("processUncertain").GetBoolean());
            TestRunner.Equal("Clean", doc.RootElement.GetProperty("stopDisposition").GetString());
        }
        TestRunner.Equal(0, DbxLight.Cli.Program.Main([.. common, "recovery", "clear", "--i-understand-pending-may-be-lost"]));
        TestRunner.Equal(StopDisposition.ClearedByUser, f.State.Load()!.StopDisposition);
    }

    internal static (int Exit, string Output, string Error) Capture(Func<int> action)
    {
        TextWriter output = Console.Out, error = Console.Error;
        using var captured = new StringWriter(); using var errors = new StringWriter();
        try { Console.SetOut(captured); Console.SetError(errors); return (action(), captured.ToString(), errors.ToString()); }
        finally { Console.SetOut(output); Console.SetError(error); }
    }

    internal sealed class LockoutMount : IDisposable
    {
        internal string Root { get; }
        internal ConfigStore Config { get; }
        internal StateStore State { get; }
        internal MountController Controller { get; }
        internal RuntimeState Clean { get; }
        private readonly List<RuntimeState> owned = [];
        internal LockoutMount(string label)
        {
            Root = Path.Combine(TestPaths.WorkRoot, "run-u3c-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string remote = Path.Combine(Root, "remote"); Directory.CreateDirectory(remote);
            string empty = Path.Combine(Root, "empty.conf"); File.WriteAllText(empty, "", new UTF8Encoding(false));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var config = new DbxConfig { Remote = remote, WriteAllowedRemotes = [], RcloneConfigPath = empty,
                MountPoint = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable),
                CacheDir = Path.Combine(Root, "cache"), LogDir = Path.Combine(Root, "logs"), RcPort = port,
                CacheMaxSize = "8M", CacheMinFreeSpace = "16M", PollInterval = "0", DirCacheTime = "1m" };
            Config = new ConfigStore(Path.Combine(Root, "config.json")); Config.Save(config);
            State = new StateStore(Path.Combine(Root, "runtime", "state.json"));
            Controller = new MountController(Config, State);
            Clean = new RuntimeState { RclonePid = 0, ExecutablePath = config.RclonePath,
                ProcessStartedUtc = DateTime.UnixEpoch, ConfigPath = Config.PathName, ActiveConfig = config,
                StopRequested = true, StoppedUtc = DateTime.UtcNow, StopDisposition = StopDisposition.Clean, RecoveryRequired = false };
        }
        internal void Start()
        {
            StartResult result = Controller.Start();
            RuntimeState state = State.Load()!;
            TestRunner.Equal(result.Pid, state.RclonePid);
            owned.Add(state); // Only the child spawned by this exact call may ever be stopped.
        }
        public void Dispose()
        {
            foreach (RuntimeState state in owned)
            {
                State.Save(state);
                try { Controller.Stop(true); }
                finally
                {
                    using Process? process = ProcessIdentity.Open(state);
                    if (process is not null) { process.Kill(); TestRunner.Assert(process.WaitForExit(10000)); }
                    using Process? remaining = ProcessIdentity.Open(state);
                    TestRunner.Assert(remaining is null, "Owned C1 process remained alive.");
                }
            }
            long bytes = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            TestRunner.Assert(bytes < 50L * 1024 * 1024);
            Console.WriteLine($"Owned C1 PIDs={string.Join(',', owned.Select(s => s.RclonePid))}: exited; bytes={bytes}; artifacts={Root}");
        }
    }

}
