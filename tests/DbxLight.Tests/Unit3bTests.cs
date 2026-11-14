using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;
using Microsoft.Win32;

namespace DbxLight.Tests;

internal static class Unit3bTests
{
    public static void MeasureGap(string label)
    {
        using var f = new Unit3aTests.RcFixture();
        long stats = 0;
        var milliseconds = new List<double>();
        f.Respond = path =>
        {
            if (path == "/vfs/stats") { stats = Stopwatch.GetTimestamp(); return (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0}}"); }
            if (path == "/core/quit") { milliseconds.Add(Stopwatch.GetElapsedTime(stats).TotalMilliseconds); return (503, "{}"); }
            return (200, "{}");
        };
        for (int n = 0; n < 105; n++) TestRunner.Throws<IOException>(() => f.Controller.Stop());
        double[] samples = milliseconds.Skip(5).ToArray();
        double[] sorted = samples.Order().ToArray();
        var result = new { label, samples = samples.Length, minMs = sorted[0], medianMs = sorted[49],
            p95Ms = sorted[94], p99Ms = sorted[98], maxMs = sorted[^1], meanMs = sorted.Average(), rawMs = samples,
            method = "loopback fixture: stats response creation to core/quit request arrival; 5 warmups excluded; unchanged live owned child" };
        string path = Path.Combine(f.Root, "gap-" + label + ".json");
        JsonFiles.Write(path, result);
        Console.WriteLine("B2 gap " + System.Text.Json.JsonSerializer.Serialize(result));
    }
    public static void Run(TestRunner runner, string? issue = null, bool includeMounts = true)
    {
        if (issue == "B3-image") { runner.Test("B3: startup image identity never accepts a loader DLL", IdentityStartupTests.SuspendedImage); return; }
        if (issue == "B1-paths") { runner.Test("B1: recovery destinations cannot depend on the caller working directory", AbsoluteRecoveryPaths); return; }
        void Test(string id, string name, Action action, bool mount = false)
        {
            if ((issue is null || issue == id) && (includeMounts || !mount)) runner.Test(id + ": " + name, action);
        }
        Test("B1", "forced pending recovery pins all three destinations until verified drained", RecoveryDestination, true);
        Test("B1", "watcher refuses a saved destination changed during crash backoff", WatcherDestination, true);
        Test("B1", "unknown process identity refuses destination changes", UnknownProcess);
        Test("B1", "recovery clear requires the explicit loss acknowledgement", ClearRecovery);
        Test("B1", "GUI retains recovery location and conditions and discards an older recovery snapshot", RecoveryPresentation);
        Test("B1", "recovery destinations cannot depend on the caller working directory", AbsoluteRecoveryPaths);
        Test("B2", "a write after the final stats snapshot survives stop and uploads on restart", LateWrite, true);
        Test("B2", "unknown cache metadata prevents declaring a clean stop", UnknownMetadata);
        Test("B3", "startup cleanup preserves StartupFailed and releases a verified clean cache", FailedStartup, true);
        Test("B3", "startup image identity never accepts a loader DLL", IdentityStartupTests.SuspendedImage);
        Test("B4", "session ending guards pending and unknown mounts in both permission modes", SessionEndDecisions);
        Test("B5", "hide confirmation refuses a changed remote, mount or rclone config", ChangedHideTarget);
        Test("B6", "state is private at creation before its first content write", PrivateAtCreation);
        Test("B6", "state save refuses a parent readable by another user", UnsafeStateParent);
        Test("B6", "startup secures a state parent before creating cache and logs beneath it", SharedStateParentStartup, true);
        Test("B7", "shell hide and web reject relative, single-root and slash UNC paths", ShellAbsolutePaths);
        Test("B8", "registered commands round-trip folder separators, Japanese spaces, files and drive roots in child argv", ShellArguments);
        Test("B9", "settings show stale shell registration and explicit re-registration steps without writing", StaleRegistrationNotice);
    }

    private static bool Recovery(RuntimeState state) =>
        typeof(RuntimeState).GetProperty("RecoveryRequired")?.GetValue(state) is true;
    private static string Disposition(RuntimeState state) =>
        typeof(RuntimeState).GetProperty("StopDisposition")?.GetValue(state)?.ToString() ?? "missing";

    private static void AbsoluteRecoveryPaths()
    {
        TestRunner.Throws<ArgumentException>(() => new DbxConfig { CacheDir = "relative-cache" }.Validate());
        TestRunner.Throws<ArgumentException>(() => new DbxConfig { RcloneConfigPath = "relative.conf" }.Validate());
        TestRunner.Throws<ArgumentException>(() => new DbxConfig { Remote = "relative-remote" }.Validate(forMount: true));
        TestRunner.Throws<ArgumentException>(() => new DbxConfig { Remote = @"D:relative" }.Validate(forMount: true));
        _ = new DbxConfig { Remote = "sample:", CacheDir = @"D:\dbxl-test\absolute-cache", RcloneConfigPath = @"D:\dbxl-test\absolute.conf" }.Validate(forMount: true);
        using var f = new Unit3aTests.RcFixture();
        RuntimeState before = f.State.Load()!;
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, before.ActiveConfig.CacheDir);
        TestRunner.Assert(!Path.IsPathFullyQualified(relative));
        RuntimeSnapshots.Save(f.State, before with { RecoveryRequired = true, StopDisposition = StopDisposition.Forced,
            ActiveConfig = before.ActiveConfig with { CacheDir = relative } });
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        Console.WriteLine("B1 paths: relative cache/config and relative local remotes refused; named remote with absolute paths accepted without connecting.");
    }

    private static void RecoveryPresentation()
    {
        using var f = new LocalMount("recovery-gui");
        var state = new RuntimeState { ActiveConfig = f.Baseline, ConfigPath = f.Config.PathName,
            RecoveryRequired = true, StopDisposition = StopDisposition.Forced };
        RuntimeSnapshots.Save(f.State, state);
        TraySnapshot snapshot = SnapshotReader.Read(f.Controller);
        string explanation = snapshot.Status.RecoveryLocation!;
        string failure = Ui.Failure(new PendingUploadsException(explanation));
        TestRunner.Assert(failure.Contains(explanation, StringComparison.Ordinal), "GUI replaced the recovery location and exit conditions with a generic queue message.");
        FormTests.Sta(() =>
        {
            using var form = new StatusForm(); form.UpdateSnapshot(snapshot, null, f.Baseline);
            TestRunner.Assert(FormTests.Field<TextBox>(form, "recovery").Text.Contains(f.Baseline.CacheDir, StringComparison.Ordinal));
            TestRunner.Assert(!FormTests.Field<Label>(form, "oldest").Text.Contains("\u4e0a\u3052\u5f85\u3061\u306f\u3042\u308a\u307e\u305b\u3093", StringComparison.Ordinal), "Recovery status incorrectly says there are no pending files.");
        });
        RuntimeSnapshots.Save(f.State, state with { RecoveryRequired = false, StopDisposition = StopDisposition.ClearedByUser });
        TestRunner.Assert(!SnapshotReader.IsCurrent(f.Controller, snapshot), "An older recovery state with the same PID was still accepted for display.");
        Console.WriteLine("B1 GUI: full location and exit conditions retained; no false empty queue; older recovery snapshot rejected.");
    }

    private static void SharedStateParentStartup()
    {
        using var f = new LocalMount("shared-state-parent");
        string parent = Path.GetDirectoryName(f.State.PathName)!;
        f.Config.Save(f.Baseline with { CacheDir = Path.Combine(parent, "cache"), LogDir = Path.Combine(parent, "logs") });
        f.Start();
        TestRunner.Assert(new DirectoryInfo(parent).GetAccessControl().AreAccessRulesProtected);
        TestRunner.Assert(f.Controller.Status().Ready);
        f.Controller.Stop();
        Console.WriteLine("B6 shared parent: private state directory created first; nested cache/log directories and real local mount succeed.");
    }

    private static void StaleRegistrationNotice()
    {
        string hive = RegistrationService.TestHive + "\\Notice-" + Guid.NewGuid().ToString("N");
        var registration = new RegistrationService(hive);
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "b9-" + Guid.NewGuid().ToString("N"));
        var options = new AppOptions(Path.Combine(root, "config.json"), Path.Combine(root, "state.json"), RegistryRoot: hive);
        var config = new DbxConfig { MountPoint = "T:" };
        string keyPath = hive + @"\Software\Classes\Directory\shell\DbxLight.Hide";
        try
        {
            registration.InstallShell(config, Environment.ProcessPath!, options);
            string Before()
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath)!;
                return (string)key.GetValue("AppliesTo")!;
            }
            string before = Before();
            FormTests.Sta(() =>
            {
                using var form = new SettingsForm(config with { MountPoint = "U:" }, registration,
                    Environment.ProcessPath!, options, _ => Task.CompletedTask);
                form.UpdateSnapshot(new(new(), [], [], false, null), config with { MountPoint = "U:" });
                string Texts(Control control) => control.Text + "\n" + string.Join('\n', control.Controls.Cast<Control>().Select(Texts));
                string text = Texts(form);
                TestRunner.Assert(text.Contains(@"T:\", StringComparison.Ordinal) && text.Contains(@"U:\", StringComparison.Ordinal) &&
                    text.Contains("--register", StringComparison.Ordinal), "Settings omit the old/current mount and re-registration command.");
            });
            TestRunner.Equal(before, Before());
            TestRunner.Assert(registration.AccessedPaths.All(p => p.StartsWith(hive + "\\", StringComparison.Ordinal)));
            Console.WriteLine("B9: settings name registered T:\\ and current U:\\ plus --register; AppliesTo unchanged; all IO confined to TestHive.");
        }
        finally { registration.UninstallShell(); }
    }

    public static int ArgvChild(string output, string[] args)
    {
        AppOptions parsed = AppOptions.Parse(args);
        JsonFiles.Write(output, new { raw = args, path = parsed.HidePath ?? parsed.WebPath });
        return 0;
    }

    private static void ShellArguments()
    {
        using var f = new LocalMount("argv");
        string hive = RegistrationService.TestHive + "\\Argv-" + Guid.NewGuid().ToString("N");
        var registration = new RegistrationService(hive);
        var options = new AppOptions(f.Config.PathName, f.State.PathName, RegistryRoot: hive);
        string exe = Environment.ProcessPath!;
        var rows = new List<(string Expected, string? Actual)>();
        try
        {
            registration.InstallShell(f.Baseline, exe, options);
            string folder = Path.Combine(f.MountRoot, "\u65e5\u672c\u8a9e \u7a7a\u767d", "folder") + "\\";
            foreach (var entry in new[] { (Type: "Directory", Verb: "Hide", Path: folder),
                (Type: "Directory", Verb: "Web", Path: folder),
                (Type: "*", Verb: "Web", Path: Path.Combine(f.MountRoot, "\u8cc7\u6599 \u7a7a\u767d.txt")),
                (Type: "Directory", Verb: "Web", Path: f.MountRoot) })
            {
                string keyPath = hive + @"\Software\Classes\" + entry.Type + @"\shell\DbxLight." + entry.Verb + @"\command";
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)!;
                string command = (string)key.GetValue("")!;
                string prefix = ShellPaths.QuoteArgument(exe) + " ";
                TestRunner.Assert(command.StartsWith(prefix, StringComparison.Ordinal));
                string output = Path.Combine(f.Root, "argv-" + rows.Count + ".json");
                var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
                    Arguments = "--unit3b-argv " + ShellPaths.QuoteArgument(output) + " " + command[prefix.Length..].Replace("%1", entry.Path, StringComparison.Ordinal),
                    RedirectStandardOutput = true, RedirectStandardError = true };
                using Process child = Process.Start(info)!;
                Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
                try { TestRunner.Assert(child.WaitForExit(15000), "Owned argv child did not exit."); }
                finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                string? actual = null;
                if (child.ExitCode == 0)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(output));
                    actual = doc.RootElement.GetProperty("path").GetString();
                }
                rows.Add((entry.Path, actual));
                Console.WriteLine($"B8 argv child PID={child.Id}, exit={child.ExitCode}: expected=[{entry.Path}] actual=[{actual}]");
            }
            TestRunner.Assert(rows.All(r => r.Expected == r.Actual), "The shell command changed a selected path during actual Windows argv parsing.");
        }
        finally { registration.UninstallShell(); }
    }

    private static void ShellAbsolutePaths()
    {
        var config = new DbxConfig { MountPoint = "T:" };
        foreach (string path in new[] { "alpha", @"\alpha", "/alpha", @"T:alpha", "//server/share", @"U:\outside" })
        {
            TestRunner.Throws<ArgumentException>(() => ShellHideConfirmation.Create(config, path));
            TestRunner.Throws<ArgumentException>(() => ShellPaths.WebUrl(path, config.MountPoint));
        }
        TestRunner.Equal("alpha", ShellHideConfirmation.Create(config, @"T:\alpha").RelativePath);
        TestRunner.Equal("https://www.dropbox.com/home/", ShellPaths.WebUrl(@"T:\", config.MountPoint));
        TestRunner.Equal("alpha", MountPaths.ToRelative("alpha", config.MountPoint)); // CLI relative paths stay supported.
        Console.WriteLine("B7: six invalid shell forms refused; fully qualified mounted child/root accepted as appropriate; CLI relative paths retained.");
    }

    private static void PrivateAtCreation()
    {
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "b6-create-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "private", "state.json");
        var ctor = typeof(StateStore).GetConstructors().SingleOrDefault(c => c.GetParameters().Length == 2);
        TestRunner.Assert(ctor is not null, "State creation cannot be observed before writing; it creates an inherited file and changes ACL later.");
        int observations = 0;
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        Action<string> created = pending =>
        {
            FileSecurity acl = new FileInfo(pending).GetAccessControl();
            TestRunner.Assert(acl.AreAccessRulesProtected);
            var allows = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow).ToArray();
            TestRunner.Assert(allows.Length == 1 && allows[0].IdentityReference == sid, "A different principal can read the newly created state.");
            TestRunner.Equal(0L, new FileInfo(pending).Length);
            observations++;
        };
        var store = (StateStore)ctor!.Invoke([path, created]);
        store.Save(new RuntimeState { RcPassword = "test-only-state-content" });
        TestRunner.Equal(1, observations);
        TestRunner.Equal("test-only-state-content", store.Load()!.RcPassword);
        DirectorySecurity parent = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
        TestRunner.Assert(parent.AreAccessRulesProtected);
        Console.WriteLine("B6: at create (length zero), protected DACL allows current SID only; same handle writes content; parent is private.");
    }

    private static void UnsafeStateParent()
    {
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "b6-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
        var state = new StateStore(Path.Combine(root, "state.json"));
        TestRunner.Throws<UnauthorizedAccessException>(() => state.Save(new RuntimeState()));
        TestRunner.Assert(!Directory.EnumerateFiles(root).Any(), "Unsafe parent received a temporary state file.");
        Console.WriteLine("B6: Everyone-readable parent refused before any file was created; ACL left unchanged.");
    }

    private static void ChangedHideTarget()
    {
        using var f = new LocalMount("hide-confirmation");
        Type? type = typeof(StatusForm).Assembly.GetType("DbxLight.Tray.ShellHideConfirmation");
        TestRunner.Assert(type is not null, "Hide confirmation does not capture a target to recheck inside UpdateConfig.");
        string path = Path.Combine(f.MountRoot, "alpha");
        object request = type!.GetMethod("Create")!.Invoke(null, [f.Baseline, path])!;
        string message = (string)type.GetProperty("Message")!.GetValue(request)!;
        TestRunner.Assert(message.Contains(f.Baseline.Remote, StringComparison.Ordinal) && message.Contains(path, StringComparison.Ordinal), "Confirmation omits the actual remote or absolute path.");
        foreach (DbxConfig changed in new[] { f.Baseline with { Remote = Path.Combine(f.Root, "other") },
            f.Baseline with { MountPoint = f.Baseline.MountPoint == "W:" ? "V:" : "W:" }, f.Baseline with { RcloneConfigPath = Path.Combine(f.Root, "other.conf") } })
        {
            f.Config.Save(changed);
            string before = File.ReadAllText(f.Config.PathName);
            bool refused = false;
            try { f.Controller.UpdateConfig(c => (DbxConfig)type.GetMethod("Apply")!.Invoke(request, [c])!); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { refused = true; }
            TestRunner.Assert(refused, "Changed target was accepted.");
            TestRunner.Equal(before, File.ReadAllText(f.Config.PathName));
        }
        f.Config.Save(f.Baseline);
        f.Controller.UpdateConfig(c => (DbxConfig)type.GetMethod("Apply")!.Invoke(request, [c])!);
        TestRunner.Assert(f.Config.Load().HiddenPaths.SequenceEqual(["alpha"]));
        Console.WriteLine("B5: remote/mount/config changes refused before mutation; same target accepted; confirmation names remote and absolute path.");
    }

    private static void SessionEndDecisions()
    {
        Type? type = typeof(StatusForm).Assembly.GetType("DbxLight.Tray.SessionEndPolicy");
        TestRunner.Assert(type is not null, "No session-ending decision is connected to the stop guard.");
        MethodInfo decide = type!.GetMethod("Decide")!;
        var writable = new DbxConfig { Remote = "local-test:", WriteAllowedRemotes = ["local-test:"] };
        bool Block(DbxConfig c, DbxStatus status, bool stopped = false) =>
            (bool)decide.Invoke(null, [c, status, stopped, false, true])!.GetType().GetProperty("Block")!.GetValue(decide.Invoke(null, [c, status, stopped, false, true]))!;
        foreach (var status in new[] { new DbxStatus { Running = true, UploadsQueued = 1, UploadsInProgress = 0 },
            new DbxStatus { Running = true, UploadsQueued = 0, UploadsInProgress = 1 },
            new DbxStatus { Running = true }, new DbxStatus { ProcessUncertain = true } })
        {
            TestRunner.Assert(Block(writable, status), "Writable pending/unknown mount allowed session end.");
            TestRunner.Assert(Block(writable with { WriteAllowedRemotes = [] }, status), "Read-only pending/unknown mount skipped guarded stop.");
        }
        TestRunner.Assert(Block(writable, new DbxStatus { Running = true, UploadsQueued = 0, UploadsInProgress = 0 }), "Fresh guarded stop must complete first.");
        TestRunner.Assert(!Block(writable, new DbxStatus(), true));
        Console.WriteLine("B4 revised one existing test: queued, uploading, RC unknown and process unknown block both permissions; verified stopped allows.");
    }

    private static void FailedStartup()
    {
        using var f = new LocalMount("startup-failure");
        var controller = new MountController(f.Config, f.State, TimeSpan.Zero);
        try { TestRunner.Throws<TimeoutException>(() => controller.Start()); }
        finally { f.Track(); }
        RuntimeState state = f.State.Load()!;
        TestRunner.Assert(Recovery(state) && Disposition(state) == "StartupFailed" && state.StoppedUtc is not null,
            "Startup cleanup did not retain the specific failed-start recovery state: " + Disposition(state));
        using var process = ProcessIdentity.Open(state); TestRunner.Assert(process is null);
        TestRunner.Assert(PendingCacheInspector.InspectStopped(state.ActiveConfig.CacheDir).Clean, "Failed startup unexpectedly left pending or unknown cache data.");
        string nextRemote = Path.Combine(f.Root, "other-remote");
        DbxConfig changed = controller.UpdateConfig(c => c with { Remote = nextRemote });
        TestRunner.Equal(nextRemote, changed.Remote);
        TestRunner.Assert(!controller.State.Load()!.RecoveryRequired, "A verified clean cache retained a notification-only recovery gate.");
        Console.WriteLine("B3 startup timeout: owned child exited; StartupFailed retained until clean cache/owner verification; destination change allowed.");
    }

    private static void LateWrite()
    {
        using var f = new LocalMount("late-write"); f.Start();
        RuntimeState actual = f.State.Load()!;
        bool injected = false;
        using (var proxy = new RcProxy(path =>
        {
            using var rc = new RcClient(actual);
            string body = rc.Call(path.TrimStart('/')).GetRawText();
            if (path == "/vfs/stats" && !injected) { injected = true; f.Write("last-moment.txt", "last moment survives"); }
            return (200, body);
        }))
        {
            RuntimeSnapshots.Save(f.State, actual with { RcPort = proxy.Port });
            f.Controller.Stop();
        }
        TestRunner.Assert(injected && Recovery(f.State.Load()!), "The last-moment pending file was declared safely drained.");
        TestRunner.Equal("StopUncertain", Disposition(f.State.Load()!));
        TestRunner.Assert(!File.Exists(Path.Combine(f.Baseline.Remote, "last-moment.txt")));
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.UpdateConfig(c => c with { CacheDir = Path.Combine(f.Root, "new-cache") }));
        f.Start(); f.WaitUploaded("last-moment.txt", "last moment survives"); f.WaitDrained(); f.Controller.Stop();
        Console.WriteLine("B2 late write: stats zero -> mounted write -> quit -> recovery required -> same destination uploaded.");
    }

    private static void UnknownMetadata()
    {
        using var f = new Unit3aTests.RcFixture();
        string meta = Path.Combine(f.Config.Load().CacheDir, "vfsMeta", "unknown-format");
        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(meta, "{\"Dirty\":false,\"newShape\":true}", new UTF8Encoding(false));
        f.Respond = path =>
        {
            if (path == "/core/quit") { File.WriteAllText(Path.Combine(f.Root, "child-stop.txt"), "exit"); return (200, "{}"); }
            return (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0}}");
        };
        f.Controller.Stop();
        TestRunner.Assert(Recovery(f.State.Load()!) && Disposition(f.State.Load()!) == "StopUncertain",
            "Unknown cache schema was treated as a clean cache.");
    }

    public static void ProbeCache()
    {
        using var f = new LocalMount("cache-probe", "30s");
        using (var version = Process.Start(RcloneArguments.ProcessInfo(f.Baseline.RclonePath, ["version"])))
        {
            TestRunner.Assert(version is not null);
            string text = version!.StandardOutput.ReadToEnd(); version.WaitForExit();
            TestRunner.Assert(version.ExitCode == 0 && text.Contains("v1.74.3", StringComparison.Ordinal), "Probe requires rclone 1.74.3.");
            Console.WriteLine(text.Trim());
        }
        f.Start(); f.Write("probe-pending.txt", "readonly recovery probe"); f.WaitQueued();
        using var rc = new RcClient(f.State.Load()!);
        var stats = rc.Call("vfs/stats");
        string metaRoot = stats.GetProperty("diskCache").GetProperty("pathMeta").GetString()!;
        string dataRoot = stats.GetProperty("diskCache").GetProperty("path").GetString()!;
        f.Kill();
        string[] metadata = Directory.GetFiles(metaRoot, "*", SearchOption.AllDirectories);
        TestRunner.Equal(1, metadata.Length);
        string raw = File.ReadAllText(metadata[0]);
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        TestRunner.Assert(doc.RootElement.GetProperty("Dirty").GetBoolean());
        f.Config.Save(f.Baseline with { WriteAllowedRemotes = [], TestOverrides = new() { WriteBack = "1s", CachePollInterval = "1s" } });
        f.Start();
        Thread.Sleep(5000);
        bool uploaded = File.Exists(Path.Combine(f.Baseline.Remote, "probe-pending.txt"));
        string? content = uploaded ? File.ReadAllText(Path.Combine(f.Baseline.Remote, "probe-pending.txt")) : null;
        string after = File.Exists(metadata[0]) ? File.ReadAllText(metadata[0]) : "(absent)";
        var evidence = new { rclone = "1.74.3", root = f.Root, metaRoot, dataRoot, forcedKillBeforeUpload = true,
            rawBefore = raw, rawAfterReadonly = after, readonlyMount = true, observedSeconds = 5,
            uploaded, content, cachedDataStillExists = File.Exists(Path.Combine(dataRoot, "probe-pending.txt")), stats = f.Controller.Status() };
        JsonFiles.Write(Path.Combine(f.Root, "cache-probe.json"), evidence);
        Console.WriteLine("B2 cache probe: " + System.Text.Json.JsonSerializer.Serialize(evidence));
    }

    internal sealed class RcProxy : IDisposable
    {
        public int Port { get; }
        private readonly HttpListener listener = new();
        private readonly Task worker;
        private bool stopped;
        public RcProxy(Func<string, (int Code, string Body)> respond)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/"); listener.Start();
            worker = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var request = await listener.GetContextAsync();
                        var answer = respond(request.Request.Url!.AbsolutePath);
                        request.Response.StatusCode = answer.Code;
                        byte[] bytes = Encoding.UTF8.GetBytes(answer.Body); request.Response.ContentLength64 = bytes.Length;
                        await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
                    }
                }
                catch (Exception e) when (stopped && e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
            });
        }
        public void Dispose() { stopped = true; listener.Close(); worker.GetAwaiter().GetResult(); }
    }

    private static void RecoveryDestination()
    {
        using var f = new LocalMount("recovery");
        f.Start(); f.Write("pending.txt", "preserve pending B1"); f.WaitQueued();
        f.Controller.Stop(true);
        foreach (Func<DbxConfig, DbxConfig> change in new Func<DbxConfig, DbxConfig>[] {
            c => c with { CacheDir = Path.Combine(f.Root, "other-cache") },
            c => c with { Remote = Path.Combine(f.Root, "other-remote") },
            c => c with { RcloneConfigPath = Path.Combine(f.Root, "other.conf") } })
        {
            string before = File.ReadAllText(f.Config.PathName);
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.UpdateConfig(change));
            TestRunner.Equal(before, File.ReadAllText(f.Config.PathName));
        }
        TestRunner.Assert(Recovery(f.State.Load()!), "Forced stop was not recorded as recovery required.");
        f.Start(); f.WaitUploaded("pending.txt", "preserve pending B1"); f.WaitDrained();
        TestRunner.Assert(!Recovery(f.State.Load()!), "Verified recovery did not clear the destination pin.");
        f.Controller.Stop();
        string next = Path.Combine(f.Root, "other-cache");
        f.Controller.UpdateConfig(c => c with { CacheDir = next });
        TestRunner.Equal(next, f.Config.Load().CacheDir);
        Console.WriteLine("B1 force -> pinned remote/config/cache -> same destination uploaded -> zero -> cache change allowed.");
    }

    private static void WatcherDestination()
    {
        using var f = new LocalMount("watcher", writeBack: "5m");
        string other = Path.Combine(f.Root, "other-remote"); Directory.CreateDirectory(other);
        int attempts = 0, resumed = 0;
        f.Controller.StartupCheckpoint = (stage, _) => { if (stage == "after-resume") resumed++; };
        f.Controller.SupervisorCheckpoint = stage =>
        {
            if (stage != "restart-wait") return;
            TestRunner.Equal(1, ++attempts);
            f.Config.Save(f.Baseline with { Remote = other });
        };
        Task<int> watch = Task.Run(() => f.Controller.Run(CancellationToken.None));
        try
        {
            LocalMount.Wait(() => f.Track()?.SupervisorPid == Environment.ProcessId && f.Controller.Status().Ready,
                "Watcher did not start.");
            f.Write("watcher-pending.txt", "watcher pending"); f.WaitQueued();
            f.Kill();
            TestRunner.Equal(1, watch.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult());
            TestRunner.Equal(1, attempts);
            TestRunner.Equal(1, resumed);
            TestRunner.Assert(f.State.Load()!.ActiveConfig.Remote == f.Baseline.Remote && Recovery(f.State.Load()!),
                "Watcher abandoned the recovery destination and mounted the rewritten remote.");
            TestRunner.Assert(!File.Exists(Path.Combine(other, "watcher-pending.txt")));
            Console.WriteLine("B1 watcher: changed destination refused once; original ActiveConfig and CLI failure=1 retained; no later retry.");
        }
        finally
        {
            f.Controller.StartupCheckpoint = null;
            f.Controller.SupervisorCheckpoint = null;
            f.Config.Save(f.Baseline);
            f.Controller.Stop(true);
            TestRunner.Assert(watch.Wait(15000), "Owned watcher did not exit.");
        }
    }

    private static void UnknownProcess()
    {
        using var f = new Unit3aTests.RcFixture();
        var ctor = typeof(MountController).GetConstructors().SingleOrDefault(c => c.GetParameters().Length == 4);
        TestRunner.Assert(ctor is not null, "The lifecycle API cannot distinguish an inaccessible process from a dead one.");
        Func<RuntimeState, Process?> inaccessible = _ => throw new Win32Exception(5, "injected query access denied");
        var controller = (MountController)ctor!.Invoke([f.Config, f.State, null, inaccessible]);
        string original = File.ReadAllText(f.Config.PathName);
        TestRunner.Throws<PendingUploadsException>(() => controller.UpdateConfig(c => c with { CacheDir = Path.Combine(f.Root, "other-cache") }));
        TestRunner.Equal(original, File.ReadAllText(f.Config.PathName));
        TestRunner.Assert(Recovery(f.State.Load()!) && Disposition(f.State.Load()!) == "ProcessUnknown");
        Console.WriteLine("B1 process access denied: destination refused; ProcessUnknown/recovery persisted.");
    }

    private static void ClearRecovery()
    {
        using var f = new Unit3aTests.RcFixture();
        f.Controller.Stop(true);
        string before = File.ReadAllText(f.State.PathName);
        string[] options = ["--config", f.Config.PathName, "--state", f.State.PathName, "recovery", "clear"];
        TestRunner.Assert(DbxLight.Cli.Program.Main(options) != 0);
        TestRunner.Equal(before, File.ReadAllText(f.State.PathName));
        TestRunner.Equal(0, DbxLight.Cli.Program.Main([.. options, "--i-understand-pending-may-be-lost"]));
        TestRunner.Assert(!Recovery(f.State.Load()!) && Disposition(f.State.Load()!) == "ClearedByUser");
        f.Controller.UpdateConfig(c => c with { CacheDir = Path.Combine(f.Root, "after-clear") });
        RuntimeState stopped = f.State.Load()!;
        RuntimeSnapshots.Save(f.State, stopped with { RecoveryRequired = true, StopDisposition = StopDisposition.UnexpectedExit,
            StopRequested = false, SupervisorPid = Environment.ProcessId });
        TestRunner.Equal(0, DbxLight.Cli.Program.Main([.. options, "--i-understand-pending-may-be-lost"]));
        TestRunner.Assert(f.State.Load()!.StopRequested, "Explicit clear left a watcher in backoff able to restart.");
        Console.WriteLine("B1 clear: absent acknowledgement refused unchanged; explicit acknowledgement clears and permits destination update.");
    }

    internal sealed class LocalMount : IDisposable
    {
        public string Root { get; }
        public ConfigStore Config { get; }
        public StateStore State { get; }
        public MountController Controller { get; }
        public DbxConfig Baseline { get; }
        public string MountRoot => MountPaths.CanonicalMount(Baseline.MountPoint);
        private readonly Dictionary<int, RuntimeState> owned = [];
        public LocalMount(string label, string writeBack = "4s")
        {
            Root = Path.Combine(@"D:\dbxl-test", "run-u3b-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string remote = Path.Combine(Root, "remote"); Directory.CreateDirectory(remote);
            string empty = Path.Combine(Root, "empty.conf"); File.WriteAllText(empty, "", new UTF8Encoding(false));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string drive = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable);
            Baseline = new DbxConfig { Remote = remote, WriteAllowedRemotes = [remote], RcloneConfigPath = empty,
                MountPoint = drive, CacheDir = Path.Combine(Root, "cache"), LogDir = Path.Combine(Root, "logs"),
                RcPort = port, CacheMaxSize = "8M", CacheMinFreeSpace = "16M", PollInterval = "0", DirCacheTime = "1m",
                TestOverrides = new() { WriteBack = writeBack, CachePollInterval = "1s" } };
            Config = new ConfigStore(Path.Combine(Root, "config.json"));
            State = new StateStore(Path.Combine(Root, "runtime", "state.json"));
            Config.Save(Baseline); Controller = new MountController(Config, State);
        }
        public RuntimeState? Track()
        {
            RuntimeState? state = State.Load();
            if (state is not null)
            {
                TestRunner.Equal(Config.PathName, state.ConfigPath);
                TestRunner.Assert(MountPaths.IsSameOrBelow(state.ActiveConfig.CacheDir, Root));
                owned[state.RclonePid] = state;
            }
            return state;
        }
        public void Start() { try { Controller.Start(); } finally { Track(); } }
        public void Write(string name, string text) => File.WriteAllText(Path.Combine(MountRoot, name), text, new UTF8Encoding(false));
        public void WaitQueued() => Wait(() => Controller.Status().UploadsQueued > 0, "Queue not observed.");
        public void WaitDrained() => Wait(() => Controller.Status() is { UploadsQueued: 0, UploadsInProgress: 0 }, "Queue not drained.");
        public void WaitUploaded(string name, string text) => Wait(() => File.Exists(Path.Combine(Baseline.Remote, name)) &&
            File.ReadAllText(Path.Combine(Baseline.Remote, name)) == text, "Recovered content was not uploaded.", 25);
        public void Kill()
        {
            RuntimeState state = Track()!;
            using var process = ProcessIdentity.Open(state) ?? throw new InvalidOperationException("Owned process unavailable.");
            process.Kill(); TestRunner.Assert(process.WaitForExit(10000));
        }
        public static void Wait(Func<bool> condition, string message, int seconds = 15)
        {
            var timer = Stopwatch.StartNew();
            do { if (condition()) return; Thread.Sleep(50); } while (timer.Elapsed < TimeSpan.FromSeconds(seconds));
            throw new TimeoutException(message);
        }
        public void Dispose()
        {
            Track();
            try { Controller.Stop(true); }
            finally
            {
                foreach (RuntimeState record in owned.Values)
                {
                    using var alive = ProcessIdentity.Open(record);
                    if (alive is not null) { alive.Kill(); TestRunner.Assert(alive.WaitForExit(10000)); }
                    using var remaining = ProcessIdentity.Open(record);
                    TestRunner.Assert(remaining is null, "Owned rclone remained alive.");
                }
                long bytes = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
                TestRunner.Assert(bytes < 50L * 1024 * 1024);
                Console.WriteLine($"Owned B1 test PIDs={string.Join(',', owned.Keys)}: exited; bytes={bytes}; artifacts={Root}");
            }
        }
    }
}
