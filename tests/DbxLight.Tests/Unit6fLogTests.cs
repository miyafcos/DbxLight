using System.Diagnostics;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    private static void RunLogs(Action<string, string, Action> test)
    {
        test("G6", "old corrupt session files are ignored and retained without application cleanup", LegacyLogsRetained);
        foreach (string kind in new[] { "disk", "denied", "unexpected" })
            test("G6", "logging unavailable keeps mount usable: " + kind, () => LoggingUnavailable(kind));
        foreach (string kind in new[] { "file", "public", "reparse", "unwritable" })
            test("G6", "unsafe log storage never blocks start or normal stop: " + kind, () => UnsafeLogs(kind));
        test("G6", "native INFO log uses fixed rotation arguments and no capture child", NativeLogs);
        test("G6", "real rclone rotation retains four private backups and removes aged rotations", NativeRotation);
        test("G6", "synthetic credentials stay in raw logs and never enter UI CLI JSON or application events", PrivateLogOutput);
        test("G6", "startup failures return fixed classes and the log location", StartupClasses);
        test("G6", "a locked log is unknown in status and not an absence of errors", UnreadableLog);
        test("G6", "only current and immediately previous rclone logs are read with delete sharing", ReadRotatedLogs);
    }
    private static void LegacyLogsRetained()
    {
        using var f = new Unit5Fixture("u6g-old-logs");
        var log = MountLogStore.Prepare(f.Store.PathName, f.Config);
        TestRunner.Assert(log.Captured);
        string folder = log.Directory!, stem = "mount-" + Guid.NewGuid().ToString("N");
        string[] paths = [Path.Combine(folder, "files.json"), Path.Combine(folder, stem + ".capture.json"), Path.Combine(folder, stem + ".log")];
        foreach (string path in paths) File.WriteAllText(path, "{ broken ERROR " + Unit4Tests.Marker);
        var stamps = paths.Select(Stamp).ToArray();
        f.Start(); TestRunner.Assert(f.Controller.Status().Ready);
        TestRunner.Assert(ErrorLogTail.ReadDetails(folder).Errors.Count <= 1);
        f.Controller.Stop();
        for (int i = 0; i < paths.Length; i++) TestRunner.Equal(stamps[i], Stamp(paths[i]));
    }
    private static void LoggingUnavailable(string kind)
    {
        using var f = new Unit5Fixture("u6g-no-log-" + kind);
        MountLogStore.BeforeCreate = () => throw kind switch { "denied" => new UnauthorizedAccessException("injected"),
            "disk" => new IOException("injected"), _ => new FormatException("injected") };
        try { f.Start(); }
        finally { MountLogStore.BeforeCreate = null; }
        var status = f.Controller.Status();
        TestRunner.Assert(status.Ready && !status.LogCapture.Captured && status.LogCapture.Reason.Contains("\u53d6\u308c\u3066\u3044\u307e\u305b\u3093", StringComparison.Ordinal));
        f.Controller.Stop();
    }
    private static void UnsafeLogs(string kind)
    {
        using var f = new Unit5Fixture("u6g-unsafe-log-" + kind);
        var config = f.Config;
        if (kind == "file")
        {
            config = config with { LogDir = Path.Combine(f.Root, "log-file") };
            File.WriteAllText(config.LogDir, "retained");
        }
        string folder = MountLogStore.Folder(f.Store.PathName, config);
        string? acl = null;
        FileStream? locked = null;
        if (kind == "public")
        {
            Directory.CreateDirectory(folder);
            acl = new DirectoryInfo(folder).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        }
        if (kind == "reparse")
        {
            Directory.CreateDirectory(config.LogDir);
            string target = Path.Combine(f.Root, "junction-target"); Directory.CreateDirectory(target);
            using var helper = Process.Start(RcloneArguments.ProcessInfo("cmd.exe", ["/d", "/c", "mklink", "/J", folder, target]))!;
            TestRunner.Assert(helper.WaitForExit(10000) && helper.ExitCode == 0);
        }
        if (kind == "unwritable")
        {
            var prepared = MountLogStore.Prepare(f.Store.PathName, config); TestRunner.Assert(prepared.Captured);
            locked = new FileStream(MountLogStore.FilePath(prepared), FileMode.Open, FileAccess.Read, FileShare.None);
        }
        f.Store.Save(config);
        try { f.Start(); TestRunner.Assert(f.Controller.Status() is { Ready: true, LogCapture.Captured: false }); f.Controller.Stop(); }
        finally { locked?.Dispose(); }
        if (acl is not null) TestRunner.Equal(acl, new DirectoryInfo(folder).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        if (kind == "file") TestRunner.Equal("retained", File.ReadAllText(config.LogDir));
        TestRunner.Assert(!MountLogStore.Prepare(f.Store.PathName, config with { LogDir = @"\\sample.invalid\dbxl\logs" }).Captured);
    }
    private static void NativeLogs()
    {
        using var f = new Unit5Fixture("u6g-native-log");
        f.Start(); var first = f.State.Load()!;
        TestRunner.Assert(f.Controller.Status().LogCapture.Captured);
        var reply = f.Cli("start", "--json"); TestRunner.Equal(0, reply.Exit);
        using var parsed = JsonDocument.Parse(reply.Output);
        TestRunner.Assert(parsed.RootElement.GetProperty("result").GetProperty("logCapture").GetProperty("captured").GetBoolean());
        string path = MountLogStore.FilePath(first.LogCapture);
        Unit5Fixture.Wait(() => new FileInfo(path).Length > 0, "Native INFO log is empty.");
        TestRunner.Assert(MountLogStore.OpenFolder(f.Controller.Status().LogCapture) is not null);
        f.Controller.Restart(); f.Start();
        TestRunner.Equal(path, MountLogStore.FilePath(f.State.Load()!.LogCapture));
        f.Controller.Stop();
        var args = RcloneArguments.Mount(f.Config, Path.Combine(f.Root, "sample.filter"), path).ToArray();
        foreach (var pair in new[] { ("--log-file", path), ("--log-level", "INFO"), ("--log-file-max-size", "10M"),
            ("--log-file-max-backups", "4"), ("--log-file-max-age", "30d") })
            TestRunner.Equal(pair.Item2, args[Array.IndexOf(args, pair.Item1) + 1]);
        TestRunner.Assert(!args.Any(a => a is "DEBUG" or "-vv" || a.StartsWith("--dump", StringComparison.Ordinal) || a.Contains("compress", StringComparison.Ordinal)));
        TestRunner.Assert(!RcloneArguments.Mount(f.Config, "filter").Any(a => a.StartsWith("--log-", StringComparison.Ordinal)));
        Console.WriteLine("J2 fixed args: --log-file mount.log --log-level INFO --log-file-max-size 10M --log-file-max-backups 4 --log-file-max-age 30d");
    }
    private static void NativeRotation()
    {
        using var f = new Unit5Fixture("u6g-real-rotation");
        var log = MountLogStore.Prepare(f.Store.PathName, f.Config);
        string path = MountLogStore.FilePath(log), folder = log.Directory!;
        string source = Path.Combine(f.Root, "rotation-source"), target = Path.Combine(f.Root, "rotation-target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        for (int i = 0; i < 20000; i++) File.WriteAllText(Path.Combine(source, $"missing-{i:00000}-" + new string('x', 120) + ".txt"), "x");
        string aged = Path.Combine(folder, "mount-2000-01-01T00-00-00.000.log"); File.WriteAllText(aged, "old sample");
        string[] args = ["check", source, target, "--config", f.Config.RcloneConfigPath!, "--log-file", path, "--log-level", "INFO",
            "--log-file-max-size", "1M", "--log-file-max-backups", "4", "--log-file-max-age", "30d"];
        using (var process = JobProcess.Start(RcloneArguments.ProcessInfo(f.Config.RclonePath, args)))
        {
            Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null), error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            TestRunner.Assert(process.WaitForExit(30000)); Task.WhenAll(output, error).GetAwaiter().GetResult();
            TestRunner.Assert(process.ExitCode != 0); // Missing samples deliberately generate INFO/ERROR records.
        }
        Unit5Fixture.Wait(() => !File.Exists(aged) && Directory.GetFiles(folder, "*.log").Length <= 5, "Rotation cleanup not complete.");
        string[] files = Directory.GetFiles(folder, "*.log"); TestRunner.Assert(files.Length > 1 && files.Length <= 5);
        foreach (string file in files) PrivateStateFiles.ValidateExisting(file);
        var evidence = files.OrderBy(Path.GetFileName, StringComparer.Ordinal).Select(file => new { name = Path.GetFileName(file),
            bytes = new FileInfo(file).Length, acl = new FileInfo(file).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            currentUserOnly = true }).ToArray();
        JsonFiles.Write(Path.Combine(f.Root, "J2-rotation.json"), new {
            version = "v1.74.3", limit = "1M", backups = 4, age = "30d", agedRemovedByRclone = !File.Exists(aged), files = evidence, artifacts = f.Root });
        Console.WriteLine("J2 real rclone rotation: " + string.Join(", ", evidence.Select(e => e.name + "=" + e.bytes)) + "; all ACLs current-user-only; old timestamp removed by rclone.");
    }
    private static void UnreadableLog()
    {
        using var f = new Unit5Fixture("u6g-log-locked");
        f.Start(); f.Controller.Stop();
        var info = f.State.Load()!.LogCapture;
        using var locked = new FileStream(MountLogStore.FilePath(info), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var snapshot = SnapshotReader.Read(f.Controller);
        TestRunner.Assert(snapshot.Status.LogCapture.Captured && !snapshot.LogReadSucceeded);
        FormTests.Sta(() =>
        {
            using var form = new StatusForm(); form.UpdateSnapshot(snapshot, null, f.Config);
            string text = FormTests.Field<TextBox>(form, "errors").Text;
            TestRunner.Assert(text.Contains("\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093", StringComparison.Ordinal) && !text.Contains("\u3042\u308a\u307e\u305b\u3093", StringComparison.Ordinal));
        });
    }
    private static void ReadRotatedLogs()
    {
        using var f = new Unit5Fixture("u6g-log-read");
        var log = MountLogStore.Prepare(f.Store.PathName, f.Config); string folder = log.Directory!;
        string path = MountLogStore.FilePath(log);
        File.WriteAllText(path, "INFO current\n");
        string previous = Path.Combine(folder, "mount-2026-01-02T03-04-05.000.log");
        string older = Path.Combine(folder, "mount-2026-01-01T03-04-05.000.log");
        File.WriteAllText(previous, "ERROR " + Unit4Tests.Marker + "\n"); File.WriteAllText(older, "ERROR older\n");
        var tail = ErrorLogTail.ReadDetails(folder);
        TestRunner.Assert(tail.ReadSucceeded); TestRunner.Equal(1, tail.Errors.Count); TestRunner.Equal(ErrorLogTail.ProviderError, tail.Errors[0]);
        using var shared = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        TestRunner.Assert(ErrorLogTail.ReadDetails(folder).ReadSucceeded);
    }
    private static void PrivateLogOutput()
    {
        using var f = new LoginFixture("mount-poison"); f.Seed("sample-existing:", f.Conf);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        f.Controller.Config.Save(f.Controller.Config.Load() with { MountPoint = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable), RcPort = port });
        RuntimeState? state = null;
        try
        {
            var started = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(["--config", f.Controller.Config.PathName, "--state", f.Controller.State.PathName, "start", "--json"]));
            state = f.Controller.State.Load()!;
            Unit5Fixture.Wait(() => new FileInfo(MountLogStore.FilePath(state.LogCapture)).Length > 0, "Raw synthetic log missing.");
            using (var raw = new StreamReader(new FileStream(MountLogStore.FilePath(state.LogCapture), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                TestRunner.Assert(raw.ReadToEnd().Contains(Unit4Tests.Marker, StringComparison.Ordinal));
            void Clean(string text) => TestRunner.Assert(!text.Contains(Unit4Tests.Marker, StringComparison.Ordinal));
            Clean(started.Output); Clean(started.Error);
            Clean(JsonSerializer.Serialize(f.Controller.Status(), JsonFiles.Options));
            foreach (string path in Directory.GetFiles(state.ActiveConfig.LogDir, "*.log")) Clean(File.ReadAllText(path));
            var snapshot = SnapshotReader.Read(f.Controller);
            foreach (string line in snapshot.Errors) Clean(line);
            FormTests.Sta(() => { using var form = new StatusForm(); form.UpdateSnapshot(snapshot, null, state.ActiveConfig); foreach (var control in ControlsOf(form)) Clean(control.Text); });
        }
        finally { if (state is not null) f.Controller.Stop(true); }
    }
    private static void StartupClasses()
    {
        using var f = new Unit5Fixture("u6f-startup-class");
        var missing = new System.ComponentModel.Win32Exception(2);
        StartupFailure.Annotate(missing, f.Config with { RclonePath = "missing-on-path.exe" }, f.Store.PathName);
        TestRunner.Equal("rclone_not_found", StartupFailure.Get(missing)!.Classification);
        var timedOut = new TimeoutException("synthetic");
        StartupFailure.Annotate(timedOut, f.Config, f.Store.PathName);
        TestRunner.Equal("timeout", StartupFailure.Get(timedOut)!.Classification);
        var occupied = new IOException("synthetic");
        StartupFailure.Annotate(occupied, f.Config with { MountPoint = f.Root }, f.Store.PathName);
        TestRunner.Equal("mount_point_in_use", StartupFailure.Get(occupied)!.Classification);
        foreach (string kind in new[] { "rclone_not_found", "remote_not_configured" })
        {
            var config = kind == "rclone_not_found" ? f.Config with { RclonePath = Path.Combine(f.Root, "missing.exe") } : f.Config with { Remote = "", TestOverrides = null };
            f.Store.Save(config);
            var reply = f.Cli("start", "--json");
            using var json = JsonDocument.Parse(reply.Output);
            TestRunner.Assert(reply.Exit != 0);
            TestRunner.Equal(kind, json.RootElement.GetProperty("failureClass").GetString());
            TestRunner.Assert(json.RootElement.GetProperty("logDirectory").GetString()!.Contains("rclone-", StringComparison.Ordinal));
        }
    }
}
