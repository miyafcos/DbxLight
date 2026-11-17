using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static class EndToEnd
{
    public static void Run(TestRunner runner)
    {
        TestContext? context = null;
        runner.Test("E2E setup: isolated local remote, empty config and free drive", () => context = new TestContext());
        if (context is null) return;
        try
        {
            if (!runner.Test("E1 read-only mount, matching listing and status JSON", context.ReadOnly)) return;
            if (!runner.Test("E6 repeated and concurrent start does not create another rclone", context.DuplicateStart)) return;
            if (!runner.Test("E2 hide, unfiltered folders and unhide", context.Hidden)) return;
            if (!runner.Test("E3 allowed local remote receives mounted writes", context.Write)) return;
            if (!runner.Test("E4 pending uploads refuse stop, restart and hide; drained stop succeeds", context.StopGuard)) return;
            if (!runner.Test("E5 supervised crash recovery delivers pending data", context.CrashRecovery)) return;
            if (!runner.Test("explicit force-stop retains pending cache for next start", context.ForceStop)) return;
            if (!runner.Test("E7 cache eviction preserves pending data through cleanup cycles", context.Retention)) return;
            if (!runner.Test("supervisor retries at most three times", context.RestartLimit)) return;
            if (!runner.Test("supervisor retries failed startups three times and returns failure", context.FailedRestarts)) return;
            if (!runner.Test("shared cache ownership across state files", context.CacheOwnership)) return;
            runner.Test("startup timeout stops its own rclone PID", context.StartupTimeout);
        }
        finally
        {
            runner.Test("E2E cleanup: every owned rclone PID stopped; artifacts retained under 50 MB", context.Cleanup);
        }
    }

    private sealed class TestContext
    {
        private readonly string root;
        private readonly string remote;
        private readonly ConfigStore config;
        private readonly StateStore state;
        private readonly MountController controller;
        private readonly string mountRoot;
        private readonly Dictionary<int, RuntimeState> owned = [];
        private readonly List<Process> supervisors = [];
        private DbxConfig baseline;

        public TestContext()
        {
            root = Path.Combine(TestPaths.WorkRoot, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(root);
            remote = Path.Combine(root, "remote");
            foreach (string folder in new[] { "alpha", "alpha/child", "beta", "\u898b\u672c\u30d5\u30a9\u30eb\u30c0" }) Directory.CreateDirectory(Path.Combine(remote, folder));
            File.WriteAllText(Path.Combine(remote, "read.txt"), new string('r', 8192), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(remote, "alpha", "small.txt"), "alpha", new UTF8Encoding(false));
            string emptyConfig = Path.Combine(root, "empty.conf");
            File.WriteAllText(emptyConfig, "", new UTF8Encoding(false));
            string drive = new[] { "U:", "V:", "W:" }.FirstOrDefault(MountPaths.IsAvailable) ?? throw new IOException("No free U/V/W drive.");
            mountRoot = MountPaths.CanonicalMount(drive);
            baseline = new DbxConfig
            {
                Remote = remote, RcloneConfigPath = emptyConfig, MountPoint = drive, NetworkMode = true,
                RcPort = FreePort(), CacheDir = Path.Combine(root, "cache"), LogDir = Path.Combine(root, "logs"),
                CacheMaxSize = "8M", CacheMinFreeSpace = "16M", PollInterval = "0", DirCacheTime = "1m",
                TestOverrides = new() { WriteBack = "1s", CachePollInterval = "1s" }
            };
            config = new ConfigStore(Path.Combine(root, "config.json"));
            state = new StateStore(Path.Combine(root, "runtime", "state.json"));
            controller = new MountController(config, state);
            config.Save(baseline);
            Console.WriteLine("E2E artifacts: " + root);
            var checks = Doctor.Check(baseline);
            foreach (var check in checks) Console.WriteLine($"doctor {check.Name}: {check.Passed} {check.Detail}");
            TestRunner.Assert(checks.All(c => c.Passed), "Doctor failed.");
        }

        public void ReadOnly()
        {
            MustCli("start");
            RuntimeState live = Track();
            var drive = new DriveInfo(baseline.MountPoint);
            Console.WriteLine($"R8 E2E mount path={baseline.MountPoint}; DriveType={drive.DriveType}; DriveFormat={drive.DriveFormat}");
            TestRunner.Assert(!TestWorkPaths.IsLocalFixedPath(mountRoot, out string workRootReason), workRootReason);
            string actual = string.Join('|', Directory.GetFileSystemEntries(mountRoot).Select(Path.GetFileName).OrderBy(p => p, StringComparer.Ordinal));
            string expected = string.Join('|', Directory.GetFileSystemEntries(remote).Select(Path.GetFileName).OrderBy(p => p, StringComparer.Ordinal));
            TestRunner.Equal(expected, actual);
            string statusJson = MustCli("status", "--json");
            File.WriteAllText(Path.Combine(root, "e1-status.json"), statusJson, new UTF8Encoding(false));
            using var json = JsonDocument.Parse(statusJson);
            foreach (string key in new[] { "running", "ready", "rclonePid", "privateMemoryBytes", "workingSetBytes", "uploadsQueued", "uploadsInProgress", "erroredFiles", "cacheFiles", "cacheBytes", "oldestQueuedAgeSeconds", "oldestQueuedAgeReason" })
                TestRunner.Assert(json.RootElement.TryGetProperty(key, out _), "Missing status field: " + key);
            TestRunner.Assert(json.RootElement.GetProperty("running").GetBoolean() && json.RootElement.GetProperty("ready").GetBoolean());
            TestRunner.Assert(json.RootElement.GetProperty("privateMemoryBytes").GetInt64() > 0 && json.RootElement.GetProperty("workingSetBytes").GetInt64() > 0);
            bool refused = false;
            try { File.WriteAllText(Path.Combine(mountRoot, "must-not-write.txt"), "no"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { refused = true; }
            TestRunner.Assert(refused && !File.Exists(Path.Combine(remote, "must-not-write.txt")), "Read-only mount accepted a write.");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = http.PostAsync($"http://127.0.0.1:{live.RcPort}/core/pid", new StringContent("{}")).GetAwaiter().GetResult();
            TestRunner.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var statsResponse = http.PostAsync($"http://127.0.0.1:{live.RcPort}/vfs/stats", new StringContent("{}")).GetAwaiter().GetResult();
            TestRunner.Equal(HttpStatusCode.Unauthorized, statsResponse.StatusCode);
            TestRunner.Assert(new FileInfo(state.PathName).GetAccessControl().AreAccessRulesProtected, "State ACL must not inherit broad access.");
            string shown = MustCli("config", "show");
            TestRunner.Assert(!shown.Contains(live.RcPassword) && !statusJson.Contains(live.RcPassword), "Authentication was exposed.");
        }

        public void DuplicateStart()
        {
            int before = Track().RclonePid;
            string log = Path.Combine(baseline.LogDir, "supervisor.log");
            int starts = File.ReadLines(log).Count(line => line.Contains("Started pid=", StringComparison.Ordinal));
            var calls = new[] { Task.Run(() => MustCli("start")), Task.Run(() => MustCli("start")) };
            Task.WaitAll(calls);
            foreach (var call in calls) TestRunner.Assert(call.Result.Contains("Already running", StringComparison.Ordinal));
            TestRunner.Equal(before, Track().RclonePid);
            TestRunner.Equal(starts, File.ReadLines(log).Count(line => line.Contains("Started pid=", StringComparison.Ordinal)));
        }

        public void Hidden()
        {
            MustCli("hide", Path.Combine(mountRoot, "alpha"));
            TestRunner.Assert(!Directory.Exists(Path.Combine(mountRoot, "alpha")) && Directory.Exists(Path.Combine(mountRoot, "beta")));
            TestRunner.Assert(MustCli("folders", "").Contains("[hidden] alpha", StringComparison.Ordinal));
            var children = FolderBrowser.List(config.Load(), "alpha");
            TestRunner.Assert(children.Any(c => c.Path == "alpha/child" && c.Hidden), "Hidden descendants must be browsable and marked.");
            MustCli("unhide", "alpha");
            TestRunner.Assert(Directory.Exists(Path.Combine(mountRoot, "alpha")));
        }

        public void Write()
        {
            MustCli("stop");
            config.Save(baseline with { WriteAllowedRemotes = [remote] });
            MustCli("start");
            WriteMounted("written.txt", "mounted write");
            Wait(() => ReadRemote("written.txt") == "mounted write", 15, "mounted write did not reach the remote");
            WaitDrained();
            MustCli("stop");
        }

        public void StopGuard()
        {
            config.Save(baseline with { WriteAllowedRemotes = [remote], TestOverrides = new() { WriteBack = "20s", CachePollInterval = "1s" } });
            MustCli("start");
            WriteMounted("guarded.txt", "queued data");
            WaitQueued();
            int before = Track().RclonePid;
            string configBefore = File.ReadAllText(config.PathName);
            string filterPath = Path.Combine(Path.GetDirectoryName(state.PathName)!, "hidden-state.filter");
            string filterBefore = File.ReadAllText(filterPath);
            DbxLight.Tray.StopAttempt trayStop = DbxLight.Tray.GuardedStop.TryStop(controller);
            TestRunner.Assert(!trayStop.Stopped && trayStop.PendingCount == 1, "Tray must not stop a pending upload.");
            TestRunner.Equal(DbxLight.Tray.TrayPresentation.StopWaiting(1), trayStop.Message);
            TestRunner.Equal(before, Track().RclonePid);
            Console.WriteLine("Tray stop refused without force: " + trayStop.Message);
            foreach (string[] command in new[] { new[] { "stop" }, new[] { "restart" }, new[] { "hide", "alpha" } })
            {
                var result = Cli(command);
                TestRunner.Equal(3, result.ExitCode);
                TestRunner.Assert(result.Error.Contains("uploadsQueued=", StringComparison.Ordinal));
                TestRunner.Equal(before, Track().RclonePid);
            }
            TestRunner.Equal(configBefore, File.ReadAllText(config.PathName));
            TestRunner.Equal(filterBefore, File.ReadAllText(filterPath));
            SaveRc("e4-pending");
            TestRunner.Assert(controller.Status().OldestQueuedAgeSeconds is null && controller.Status().OldestQueuedAgeReason.Contains("no enqueue timestamp", StringComparison.Ordinal));
            Wait(() => ReadRemote("guarded.txt") == "queued data", 35, "guarded upload did not drain");
            WaitDrained();
            MustCli("stop");
            AssertStopped(before);
        }

        public void CrashRecovery()
        {
            config.Save(baseline with { WriteAllowedRemotes = [remote], TestOverrides = new() { WriteBack = "12s", CachePollInterval = "1s" } });
            Process supervisor = StartSupervisor();
            Wait(() => TryTrack()?.SupervisorPid == supervisor.Id && controller.Status().Ready, 40, "supervisor did not start");
            WriteMounted("recovered.txt", "crash recovery data");
            WaitQueued();
            SaveRc("e5-before-crash");
            RuntimeState crashed = Track();
            Kill(crashed);
            Wait(() => TryTrack() is { } current && current.RclonePid != crashed.RclonePid && controller.Status().Ready, 40, "supervisor did not remount");
            Wait(() => ReadRemote("recovered.txt") == "crash recovery data", 35, "crash recovery did not deliver queued data");
            WaitDrained();
            TestRunner.Equal(1, Track().RestartCount);
            MustCli("stop");
            TestRunner.Assert(supervisor.WaitForExit(10000), "supervisor did not exit after stop");
            TestRunner.Equal(0, supervisor.ExitCode);
            Console.WriteLine($"E5 recovered pending file after owned pid={crashed.RclonePid} was killed.");
        }

        public void Retention()
        {
            config.Save(baseline with
            {
                WriteAllowedRemotes = [remote],
                TestOverrides = new() { WriteBack = "25s", CachePollInterval = "1s", CacheMaxAgeOverride = "2s" }
            });
            MustCli("start");
            using var rc = new RcClient(Track());
            string diskPath = rc.Call("vfs/stats").GetProperty("diskCache").GetProperty("path").GetString()!;
            string readCache = Path.Combine(diskPath, "read.txt");
            TestRunner.Equal(8192, File.ReadAllBytes(Path.Combine(mountRoot, "read.txt")).Length);
            Wait(() => File.Exists(readCache), 5, "read-only content was not cached");
            Wait(() => !File.Exists(readCache), 20, "read-only cache content did not expire");
            Console.WriteLine("E7 read-only content evicted after 2s max-age / 1s cache poll.");
            const string name = "retained-pending.txt";
            const string payload = "pending data must survive cache cleanup";
            WriteMounted(name, payload);
            WaitQueued();
            string pendingCache = Path.Combine(diskPath, name);
            TestRunner.Assert(File.Exists(pendingCache) && !File.Exists(Path.Combine(remote, name)));
            SaveRc("e7-pending-initial");
            var timer = Stopwatch.StartNew();
            int samples = 0;
            while (timer.Elapsed < TimeSpan.FromSeconds(9))
            {
                TestRunner.Assert(File.Exists(pendingCache), "Pending file was evicted by cache cleanup.");
                TestRunner.Assert(!File.Exists(Path.Combine(remote, name)), "Upload happened before the retention exposure interval.");
                TestRunner.Assert(controller.Status().UploadsQueued > 0, "Pending queue unexpectedly disappeared.");
                samples++;
                Thread.Sleep(500);
            }
            double pendingObservedSeconds = timer.Elapsed.TotalSeconds;
            SaveRc("e7-pending-after-cleanup");
            Wait(() => ReadRemote(name) == payload, 35, "retained pending data was not delivered");
            WaitDrained();
            Wait(() => !File.Exists(pendingCache), 15, "uploaded cache content did not expire");
            var evidence = new { ReadCacheEvicted = true, PendingCachePreserved = true, PendingObservedSeconds = pendingObservedSeconds, PendingObservationSamples = samples, MaxAgeSeconds = 2, PollSeconds = 1, WriteBackSeconds = 25, UploadedContentMatches = ReadRemote(name) == payload };
            JsonFiles.Write(Path.Combine(root, "e7-result.json"), evidence);
            Console.WriteLine($"E7 pending cache survived {samples} observations across 9s (> 2s retention); uploaded content matches; uploaded cache later evicted.");
            var stopTimer = Stopwatch.StartNew();
            try { MustCli("stop"); }
            finally { Console.WriteLine($"E7 final normal stop elapsed={stopTimer.Elapsed.TotalSeconds:F3}s."); }
        }

        public void RestartLimit()
        {
            config.Save(baseline);
            Process supervisor = StartSupervisor();
            Wait(() => TryTrack()?.SupervisorPid == supervisor.Id && controller.Status().Ready, 40, "retry-limit supervisor did not start");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                RuntimeState crashed = Track();
                Kill(crashed);
                int expected = attempt;
                Wait(() => TryTrack() is { } next && next.RclonePid != crashed.RclonePid && next.RestartCount == expected && controller.Status().Ready, 40, "retry " + attempt + " did not complete");
            }
            RuntimeState last = Track();
            Kill(last);
            TestRunner.Assert(supervisor.WaitForExit(15000), "Supervisor did not stop after the third retry.");
            TestRunner.Equal(1, supervisor.ExitCode);
            AssertStopped(last.RclonePid);
            TestRunner.Equal(3, state.Load()!.RestartCount);
            MustCli("stop");
        }

        public void CacheOwnership()
        {
            config.Save(baseline);
            MustCli("start");
            string secondDrive = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable);
            var secondConfig = new ConfigStore(Path.Combine(root, "second-config.json"));
            secondConfig.Save(baseline with { MountPoint = secondDrive, RcPort = FreePort() });
            var second = new MountController(secondConfig, new StateStore(Path.Combine(root, "second-runtime", "state.json")));
            TestRunner.Throws<InvalidOperationException>(() => second.Start());
            int originalPid = Track().RclonePid;
            var wrongConfig = new MountController(secondConfig, state);
            TestRunner.Throws<InvalidOperationException>(() => wrongConfig.Start());
            TestRunner.Throws<InvalidOperationException>(() => wrongConfig.Restart());
            TestRunner.Throws<InvalidOperationException>(() => wrongConfig.Hide("alpha"));
            TestRunner.Equal(originalPid, Track().RclonePid);
            TestRunner.Assert(controller.Status().Ready);
            MustCli("stop");
        }

        public void ForceStop()
        {
            config.Save(baseline with { WriteAllowedRemotes = [remote], TestOverrides = new() { WriteBack = "10s", CachePollInterval = "1s" } });
            MustCli("start");
            WriteMounted("forced-pending.txt", "recover after explicit force");
            WaitQueued();
            int pid = Track().RclonePid;
            MustCli("stop", "--force");
            AssertStopped(pid);
            MustCli("start");
            Wait(() => ReadRemote("forced-pending.txt") == "recover after explicit force", 25, "force-stop lost pending data");
            WaitDrained();
            MustCli("stop");
        }

        public void StartupTimeout()
        {
            config.Save(baseline);
            var shortStartup = new MountController(config, state, TimeSpan.Zero);
            TestRunner.Throws<TimeoutException>(() => shortStartup.Start());
            RuntimeState stopped = Track();
            AssertStopped(stopped.RclonePid);
            TestRunner.Assert(stopped.StopRequested && stopped.StoppedUtc is not null);
        }

        public void FailedRestarts()
        {
            config.Save(baseline);
            string log = Path.Combine(baseline.LogDir, "supervisor.log");
            int previousLines = File.ReadAllLines(log).Length;
            Process supervisor = StartSupervisor();
            Wait(() => TryTrack()?.SupervisorPid == supervisor.Id && controller.Status().Ready, 40, "failure-case supervisor did not start");
            // The existing test executable rejects mount arguments and exits 2; no extra binary or package is needed.
            config.Save(baseline with { RclonePath = Path.Combine(AppContext.BaseDirectory, "DbxLight.Tests.exe") });
            Kill(Track());
            Wait(() => { TryTrack(); return supervisor.HasExited; }, 40, "failed restarts did not exhaust the limit");
            TestRunner.Equal(1, supervisor.ExitCode);
            string[] lines = File.ReadAllLines(log).Skip(previousLines).ToArray();
            TestRunner.Equal(3, lines.Count(line => line.Contains("Restart attempt ", StringComparison.Ordinal) && line.Contains(" failed:", StringComparison.Ordinal)));
            TestRunner.Assert(lines.Any(line => line.Contains("Restart limit reached (3)", StringComparison.Ordinal)));
            MustCli("stop");
            config.Save(baseline);
        }

        public void Cleanup()
        {
            TryTrack();
            RuntimeState? current = state.Load();
            if (current is not null && current.ConfigPath == config.PathName && current.ActiveConfig.Remote == remote) controller.Stop(force: true);
            foreach (Process process in supervisors)
            {
                if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(10000); }
                process.Dispose();
            }
            foreach (RuntimeState record in owned.Values)
            {
                using Process? process = ProcessIdentity.Open(record);
                if (process is not null) { process.Kill(); process.WaitForExit(10000); }
                AssertStopped(record.RclonePid);
            }
            long bytes = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            TestRunner.Assert(bytes <= 50L * 1024 * 1024, "Run exceeded 50 MB: " + bytes);
            string result = $"Owned rclone PIDs stopped: {string.Join(",", owned.Keys)}\nRun bytes: {bytes}\nArtifacts retained: {root}\n";
            File.WriteAllText(Path.Combine(root, "cleanup.txt"), result, new UTF8Encoding(false));
            Console.Write(result);
        }

        private void WriteMounted(string name, string content) => File.WriteAllText(Path.Combine(mountRoot, name), content, new UTF8Encoding(false));
        private string? ReadRemote(string name)
        {
            string path = Path.Combine(remote, name);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (IOException) { return null; }
        }
        private RuntimeState Track() => TryTrack() ?? throw new InvalidOperationException("Expected running state.");
        private RuntimeState? TryTrack()
        {
            RuntimeState? current = state.Load();
            if (current is null) return null;
            TestRunner.Assert(current.ConfigPath == config.PathName && current.ActiveConfig.Remote == remote, "Unexpected runtime ownership.");
            owned[current.RclonePid] = current;
            return current;
        }
        private void WaitQueued() => Wait(() => controller.Status().UploadsQueued > 0, 10, "No pending upload was observed");
        private void WaitDrained() => Wait(() => controller.Status() is { UploadsQueued: 0, UploadsInProgress: 0 }, 15, "Upload queue did not drain");

        private void SaveRc(string name)
        {
            using var rc = new RcClient(Track());
            JsonFiles.Write(Path.Combine(root, name + "-stats.json"), rc.Call("vfs/stats"));
            JsonFiles.Write(Path.Combine(root, name + "-queue.json"), rc.Call("vfs/queue"));
        }
        private static void Kill(RuntimeState record)
        {
            using var process = ProcessIdentity.Open(record) ?? throw new InvalidOperationException("Owned process disappeared before crash test.");
            process.Kill();
            TestRunner.Assert(process.WaitForExit(10000));
        }
        private void AssertStopped(int pid)
        {
            if (owned.TryGetValue(pid, out var record))
            {
                using var process = ProcessIdentity.Open(record);
                TestRunner.Assert(process is null, "Owned rclone is still running: " + pid);
            }
        }
        private string MustCli(params string[] words)
        {
            var result = Cli(words);
            TestRunner.Equal(0, result.ExitCode);
            return result.Output;
        }
        private (int ExitCode, string Output, string Error) Cli(params string[] words)
        {
            using Process process = Process.Start(CliInfo(words))!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(70000)) { process.Kill(); process.WaitForExit(10000); throw new TimeoutException("CLI timeout: " + string.Join(' ', words)); }
            if (!Task.WhenAll(output, error).Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("CLI output did not close after process exit.");
            string stdout = output.GetAwaiter().GetResult();
            string stderr = error.GetAwaiter().GetResult();
            File.AppendAllText(Path.Combine(root, "commands.txt"), JsonSerializer.Serialize(words) + "\nexit=" + process.ExitCode + "\n" + stdout + stderr + "\n", new UTF8Encoding(false));
            TryTrack();
            if (process.ExitCode != 0) Console.WriteLine("CLI " + string.Join(' ', words) + " -> " + process.ExitCode + " " + stderr.Trim());
            return (process.ExitCode, stdout, stderr);
        }
        private Process StartSupervisor()
        {
            Process process = Process.Start(CliInfo(["run"]))!;
            string file = Path.Combine(root, "supervisor-" + process.Id + ".txt");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            _ = Task.WhenAll(output, error).ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) File.WriteAllText(file, string.Join('\n', task.Result), new UTF8Encoding(false));
            }, TaskScheduler.Default);
            supervisors.Add(process);
            return process;
        }
        private ProcessStartInfo CliInfo(IEnumerable<string> words)
        {
            // dbxl writes UTF-8; read it as UTF-8 instead of relying on the test process's console code page.
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            foreach (string word in new[] { Path.Combine(AppContext.BaseDirectory, "dbxl.dll"), "--config", config.PathName, "--state", state.PathName }.Concat(words)) info.ArgumentList.Add(word);
            info.Environment.Remove("DBXL_CONFIG");
            info.Environment.Remove("DBXL_STATE");
            return info;
        }
        private static int FreePort()
        {
            for (int port = 5601; port <= 65000; port++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                try { listener.ExclusiveAddressUse = true; listener.Start(); return port; }
                catch (SocketException) { }
                finally { listener.Stop(); }
            }
            throw new IOException("No free rc port.");
        }
        private static void Wait(Func<bool> predicate, int seconds, string failure)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                if (predicate()) return;
                Thread.Sleep(150);
            } while (timer.Elapsed < TimeSpan.FromSeconds(seconds));
            throw new TimeoutException(failure);
        }
    }
}
