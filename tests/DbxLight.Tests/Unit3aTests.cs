using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class Unit3aTests
{
    public static void Run(TestRunner runner, string? issue = null, bool includeMounts = true)
    {
        void Test(string id, string name, Action action)
        {
            if ((issue is null || issue == id) && (includeMounts || id != "A5")) runner.Test(id + ": " + name, action);
        }
        Test("A1", "failed core/quit keeps the live target supervised", FailedQuit);
        Test("A1", "explicit resume clears an old stop marker only for the same process", ResumeMarker);
        Test("A2", "resume replaces a faulted resident watcher and records its failure once", ResidentTests.FaultRecovery);
        Test("A3", "stop rejects a state belonging to another config before RC or mutation", () => WrongConfigStop(false));
        Test("A3", "forced stop rejects a state belonging to another config before RC or mutation", () => WrongConfigStop(true));
        Test("A4", "mount rejects unsafe overrides and accepts an explicit local config", () => { InvalidOverrides(); ValidOverrides(); });
        Test("A4", "typed set cannot introduce or preserve unsafe test overrides", SetOverrides);
        Test("A5", "a delayed old read cannot overwrite a reconnected mount", ReconnectTests.DelayedRead);
        Test("A6", "status headline shows the overall state and counts appear only below", FormTests.StatusHeadline);
        Test("A7", "both forms show active permissions and confirmation without granting edits", FormTests.Permissions);
        Test("A8", "only the exact root symlink capability probe is separated", StartupProbe);
        Test("A8", "startup probes do not displace the last twenty real errors", ErrorWindow);
        Test("A8", "status separates the harmless probe and retains the original log", ProbePresentation);
    }

    private static void FailedQuit()
    {
        using var fixture = new RcFixture();
        Task<int> watch = Task.Run(() => fixture.Controller.Run(CancellationToken.None));
        try
        {
            Wait(() => fixture.State.Load()?.SupervisorPid == Environment.ProcessId, "Supervisor did not claim the target.");
            TestRunner.Throws<IOException>(() => fixture.Controller.Stop());
            Thread.Sleep(600);
            RuntimeState state = fixture.State.Load()!;
            TestRunner.Assert(!state.StopRequested && state.StoppedUtc is null && !watch.IsCompleted,
                "Failed core/quit left a live process with a stop marker or lost its supervisor.");
            Console.WriteLine($"Failed quit: owned target PID={state.RclonePid} alive; stopRequested={state.StopRequested}; watcherCompleted={watch.IsCompleted}.");
        }
        finally { fixture.EndWatch(watch); }
    }

    private static void ResumeMarker()
    {
        using var fixture = new RcFixture();
        RuntimeState original = fixture.State.Load()!;
        RuntimeSnapshots.Save(fixture.State, original with { StopRequested = true, StoppedUtc = DateTime.UtcNow });
        StartResult resumed = fixture.Controller.Start();
        TestRunner.Equal(original.RclonePid, resumed.Pid);
        TestRunner.Assert(resumed.AlreadyRunning);
        RuntimeState state = fixture.State.Load()!;
        TestRunner.Assert(!state.StopRequested && state.StoppedUtc is null, "Explicit resume retained the old stop marker.");
        Task<int> watch = Task.Run(() => fixture.Controller.Run(CancellationToken.None));
        try
        {
            Wait(() => fixture.State.Load()?.SupervisorPid == Environment.ProcessId, "Resume did not claim supervision.");
            Thread.Sleep(600);
            TestRunner.Assert(!watch.IsCompleted, "Resumed watcher immediately exited.");
        }
        finally { fixture.EndWatch(watch); }
        // A stale PID/start-time tuple must never authorize resuming the live child.
        RuntimeSnapshots.Save(fixture.State, original with { ProcessStartedUtc = original.ProcessStartedUtc.AddSeconds(-1), StopRequested = true });
        TestRunner.Throws<SocketException>(() => fixture.Controller.Start());
        TestRunner.Assert(fixture.State.Load()!.StopRequested, "A mismatched process identity was treated as the same target.");
    }

    private static void WrongConfigStop(bool force)
    {
        using var fixture = new RcFixture();
        int calls = 0;
        fixture.Respond = path =>
        {
            Interlocked.Increment(ref calls);
            if (path == "/core/quit")
            {
                File.WriteAllText(Path.Combine(fixture.Root, "child-stop.txt"), "exit", new UTF8Encoding(false));
                return (200, "{}");
            }
            return (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0}}");
        };
        var another = new ConfigStore(Path.Combine(fixture.Root, "another-config.json"));
        another.Save(fixture.Config.Load());
        var wrong = new MountController(another, fixture.State);
        byte[] before = File.ReadAllBytes(fixture.State.PathName);
        TestRunner.Throws<InvalidOperationException>(() => wrong.Stop(force));
        TestRunner.Equal(0, calls);
        TestRunner.Assert(before.SequenceEqual(File.ReadAllBytes(fixture.State.PathName)), "Wrong-config stop changed the state.");
        using var alive = ProcessIdentity.Open(fixture.State.Load()!);
        TestRunner.Assert(alive is not null, "Wrong-config stop terminated the owned test target.");
        Console.WriteLine($"Wrong-config stop force={force}: refused; RC calls=0; state unchanged; owned target alive.");
    }

    private static void InvalidOverrides()
    {
        using var fixture = new RcFixture();
        DbxConfig local = fixture.Config.Load() with { TestOverrides = new() { WriteBack = "1s" } };
        foreach (var bad in new[]
        {
            local with { Remote = "company:" }, local with { Remote = "relative/folder" },
            local with { Remote = @"D:relative" }, local with { Remote = @"\rooted" },
            local with { Remote = @"\\server\share" }, local with { RcloneConfigPath = null },
            local with { RcloneConfigPath = " " }
        })
        {
            try
            {
                _ = RcloneArguments.Mount(bad, Path.Combine(fixture.Root, "hidden.filter"));
                throw new InvalidOperationException("Unsafe testOverrides reached mount arguments: " + bad.Remote);
            }
            catch (ArgumentException e)
            {
                TestRunner.Assert(e.Message.Contains("testOverrides", StringComparison.Ordinal) ||
                    e.Message.Contains("RcloneConfigPath", StringComparison.Ordinal), "Refusal omitted the reason.");
            }
        }
    }

    private static void ValidOverrides()
    {
        using var fixture = new RcFixture();
        DbxConfig local = fixture.Config.Load() with { TestOverrides = new() { WriteBack = "2s", CachePollInterval = "1s", CacheMaxAgeOverride = "3s" } };
        var args = RcloneArguments.Mount(local, Path.Combine(fixture.Root, "hidden.filter"));
        TestRunner.Assert(args.Contains("2s") && args.Contains("3s") && args.Contains("--read-only"));
        fixture.Config.Save(local);
        TestRunner.Assert(fixture.Controller.Start().AlreadyRunning);
    }

    private static void SetOverrides()
    {
        TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(new DbxConfig { Remote = "company:" }, "testOverrides.writeBack", "1s"));
        using var fixture = new RcFixture();
        DbxConfig safe = ConfigStore.Set(fixture.Config.Load(), "testOverrides.writeBack", "2s");
        TestRunner.Equal("2s", safe.TestOverrides!.WriteBack);
        TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(safe, "remote", "company:"));
        TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(safe, "rcloneConfigPath", "null"));
    }

    private const string Probe = "ERROR : symlinks not supported without the --links flag: /";
    private static string LogRoot()
    {
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "probe-" + Guid.NewGuid().ToString("N"));
        PrivateStateFiles.EnsureParent(Path.Combine(root, "mount.log")); return root;
    }
    private static void StartupProbe()
    {
        string root = LogRoot();
        string[] real = [Probe + "file", Probe + "/", Probe + " (extra)", Probe + " ",
            "ERROR : Failed to copy: symlinks not supported without the --links flag: /", "ERROR : actual upload failed"];
        File.WriteAllText(Path.Combine(root, "mount.log"), string.Join('\n', new[] { Probe, "2026/11/12 01:02:03 " + Probe,
            "2026/11/12 01:02:03.123456 " + Probe }.Concat(real)) + "\n", new UTF8Encoding(false));
        IReadOnlyList<string> safe = ErrorLogTail.Read(root);
        TestRunner.Equal(real.Length, safe.Count);
        TestRunner.Assert(safe.All(line => line == ErrorLogTail.ProviderError), "Provider diagnostics were forwarded.");
    }
    private static void ErrorWindow()
    {
        string root = LogRoot();
        File.WriteAllText(Path.Combine(root, "mount.log"), string.Join('\n', Enumerable.Range(0, 24)
            .SelectMany(n => new[] { "ERROR actual-" + n, Probe })) + "\n", new UTF8Encoding(false));
        IReadOnlyList<string> errors = ErrorLogTail.Read(root);
        TestRunner.Equal(20, errors.Count); TestRunner.Equal(ErrorLogTail.ProviderError, errors[0]); TestRunner.Equal(ErrorLogTail.ProviderError, errors[^1]);
    }
    private static void ProbePresentation()
    {
        using var fixture = new RcFixture();
        var log = MountLogStore.Prepare(fixture.Config.PathName, fixture.Config.Load());
        fixture.State.Save(fixture.State.Load()! with { LogCapture = log });
        string path = MountLogStore.FilePath(log);
        string content = "2026/11/12 01:02:03 " + Probe + "\nERROR : upload failed\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        TraySnapshot snapshot = SnapshotReader.Read(fixture.Controller);
        FormTests.Sta(() =>
        {
            using var status = new StatusForm(); status.UpdateSnapshot(snapshot, null, fixture.Config.Load());
            string failures = FormTests.Field<TextBox>(status, "errors").Text;
            TestRunner.Assert(!failures.Contains(Probe, StringComparison.Ordinal) && failures.Contains(ErrorLogTail.ProviderError, StringComparison.Ordinal),
                "Status did not keep startup probes separate from upload errors.");
            Label startup = FormTests.Field<Label>(status, "startup");
            TestRunner.Assert(startup.Text.Contains("\u5bb3\u306a\u3057", StringComparison.Ordinal) && startup.Text.Contains(ErrorLogTail.StartupNotice, StringComparison.Ordinal));
        });
        TestRunner.Equal(content, File.ReadAllText(path, Encoding.UTF8));
    }

    internal static void Wait(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return;
            Thread.Sleep(25);
        }
        throw new TimeoutException(message);
    }

    public static int Child(string stopFile)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(stopFile) && timer.Elapsed < TimeSpan.FromMinutes(3)) Thread.Sleep(25);
        return 0;
    }

    internal sealed class RcFixture : IDisposable
    {
        public string Root { get; }
        public ConfigStore Config { get; }
        public StateStore State { get; }
        public MountController Controller { get; }
        public Func<string, (int Code, string Body)>? Respond { get; set; }
        private readonly Process child;
        private readonly HttpListener listener = new();
        private readonly Task server;
        private int stopping;

        public RcFixture(string? artifactParent = null)
        {
            Root = artifactParent is null
                ? Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "unit3a-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(artifactParent, "unit3a-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string remote = Path.Combine(Root, "remote"); Directory.CreateDirectory(remote);
            string empty = Path.Combine(Root, "empty.conf"); File.WriteAllText(empty, "", new UTF8Encoding(false));
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            Config = new ConfigStore(Path.Combine(Root, "config.json"));
            State = new StateStore(Path.Combine(Root, "runtime", "state.json"));
            Controller = new MountController(Config, State);
            Config.Save(new DbxConfig { UpdateCheck = false, Remote = remote, RcloneConfigPath = empty, NetworkMode = false,
                MountPoint = Path.Combine(Root, "mount"), CacheDir = Path.Combine(Root, "cache"),
                LogDir = Path.Combine(Root, "logs"), RcPort = port });
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--unit3a-child"); info.ArgumentList.Add(Path.Combine(Root, "child-stop.txt"));
            child = Process.Start(info) ?? throw new IOException("Owned fixture child did not start.");
            try
            {
                RuntimeSnapshots.Save(State, new RuntimeState { RclonePid = child.Id, ProcessStartedUtc = child.StartTime.ToUniversalTime(),
                    ExecutablePath = info.FileName, ConfigPath = Config.PathName, ActiveConfig = Config.Load(),
                    RcPort = port, RcUser = "fixture", RcPassword = "fixture-only" });
                RuntimeState identity = State.Load()!;
                Wait(() =>
                {
                    try
                    {
                        using Process? ready = ProcessIdentity.Open(identity);
                        return ready is not null;
                    }
                    catch (ProcessIdentityUnknownException) { return false; } // Owned child may still be initializing its image.
                }, "Fixture process identity was not ready.");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
                server = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            HttpListenerContext request = await listener.GetContextAsync();
                            string path = request.Request.Url!.AbsolutePath;
                            var response = Respond?.Invoke(path) ?? (path == "/core/quit" ? (503, "{}") :
                                path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}") :
                                path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, $"{{\"pid\":{child.Id}}}"));
                            request.Response.StatusCode = response.Item1;
                            byte[] body = Encoding.UTF8.GetBytes(response.Item2);
                            request.Response.ContentLength64 = body.Length;
                            await request.Response.OutputStream.WriteAsync(body);
                            request.Response.Close();
                        }
                    }
                    catch (Exception e) when (Volatile.Read(ref stopping) != 0 && e is HttpListenerException or ObjectDisposedException or ArgumentException or InvalidOperationException) { }
                });
            }
            catch
            {
                Interlocked.Exchange(ref stopping, 1); listener.Close();
                File.WriteAllText(Path.Combine(Root, "child-stop.txt"), "exit", new UTF8Encoding(false));
                if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(5000); }
                child.Dispose();
                throw;
            }
        }

        public void EndWatch(Task<int> task)
        {
            RuntimeState? current = State.Load();
            if (current is not null) RuntimeSnapshots.Save(State, current with { StopRequested = true });
            TestRunner.Assert(task.Wait(TimeSpan.FromSeconds(10)), "Fixture supervisor did not exit.");
        }

        public void Dispose()
        {
            // Close can cancel IO before HttpListener.IsListening changes.
            Interlocked.Exchange(ref stopping, 1);
            try { listener.Close(); server.GetAwaiter().GetResult(); }
            finally
            {
                File.WriteAllText(Path.Combine(Root, "child-stop.txt"), "exit", new UTF8Encoding(false));
                if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(5000); }
                TestRunner.Assert(child.HasExited, "Owned fixture child is still running.");
                Console.WriteLine($"Fixture owned PID={child.Id}: exited; artifacts retained: {Root}");
                child.Dispose();
            }
        }
    }
}
