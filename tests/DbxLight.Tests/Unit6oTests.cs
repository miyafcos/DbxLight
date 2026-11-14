using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static class Unit6oTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == "all" || issue == id) runner.Test(id + ": " + name, action); }
        Test("T1", "wait discards failed then uploaded observations on later busy and recovers upload failure", () => WaitBusy(true, false));
        Test("T1", "wait --all discards collected failures on later busy and recovers upload failure", () => WaitBusy(true, true));
        Test("T1", "wait discards queued observations on later busy", () => WaitBusy(false, false));
        Test("T1", "wait discards uploaded then failed observations on later busy and recovers upload failure", () => WaitBusy(true, false, uploadedFirst: true));
        Test("T1", "stat preserves the original rc failure code, exit and message over later busy", StatFailureBusy);
        Test("T1", "ls/find/stat preserve prior exceptions only for busy and reject changed generations", StableReadBoundaries);
        Test("T2", "wait-time refused normal stop uses the same self-clearing recovery message", () => RecoveryDuringWait("running"));
        Test("T2", "wait-time stopped recovery retains human guidance", () => RecoveryDuringWait("stopped"));
        Test("T2", "wait-time uncertain process retains human verification guidance", () => RecoveryDuringWait("uncertain"));
    }

    private static (int Exit, string Output, string Error) Cli(MountController controller, params string[] args) =>
        Unit3cTests.Capture(() => (int)typeof(DbxLight.Cli.Program)
            .GetMethod("MainCore", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [args, controller])!);

    private static void WaitBusy(bool failed, bool all, bool uploadedFirst = false)
    {
        using var gate = new TestControlGate();
        using var f = new ReadFixture();
        bool mixed = failed && !all;
        string[] paths = all ? ["--all"] : mixed
            ? uploadedFirst ? ["two.txt", "one.txt"] : ["one.txt", "two.txt"]
            : ["one.txt"];
        string[] args = ["wait", .. paths, "--timeout", failed ? "120" : "0", "--json"];
        f.QueueReply = _ => (200, Queue(failed));
        if (mixed)
        {
            string local = Path.Combine(f.Root, "mount", "two.txt");
            File.WriteAllText(local, "two", new UTF8Encoding(false));
            var file = new FileInfo(local);
            f.CloudStatReply = () => (200, JsonSerializer.Serialize(new
            { item = new { Name = "two.txt", Path = "two.txt", IsDir = false, Size = file.Length, ModTime = file.LastWriteTimeUtc } }));
            // Prove this fixture can remember an uploaded result before adding contention.
            var before = Cli(f.Controller, args);
            TestRunner.Equal(4, before.Exit);
            using var prior = JsonDocument.Parse(before.Output);
            var uploaded = prior.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("path").GetString() == "two.txt");
            TestRunner.Equal("uploaded", uploaded.GetProperty("state").GetString());
            TestRunner.Assert(uploaded.GetProperty("verified").GetBoolean());
            TestRunner.Equal(JsonValueKind.Object, uploaded.GetProperty("verification").ValueKind);
            f.QueueRequests = 0; f.CloudStatRequests = 0;
        }
        f.QueueReply = n =>
        {
            // Constructor status, first wait status, target queue, then final target queue.
            // The final reply also records a new save of the formerly uploaded target.
            if (n == 4)
            {
                if (mixed) TestRunner.Equal(1, f.CloudStatRequests);
                gate.Acquire();
                if (mixed) return (200, JsonSerializer.Serialize(new
                { queue = new[] { new { name = "one.txt", uploading = false, tries = 2 },
                    new { name = "two.txt", uploading = false, tries = 0 } } }));
            }
            return (200, Queue(failed));
        };
        try
        {
            var reply = Cli(f.Controller, args);
            TestRunner.Equal(3, reply.Exit);
            using var json = JsonDocument.Parse(reply.Output);
            var result = json.RootElement;
            TestRunner.Assert(!result.GetProperty("ok").GetBoolean());
            TestRunner.Equal("busy", result.GetProperty("code").GetString());
            var items = result.GetProperty("items");
            // The 6n CLI rebuilds explicit targets; --all has no explicit paths to return.
            TestRunner.Equal(all ? 0 : paths.Length, items.GetArrayLength());
            for (int i = 0; i < items.GetArrayLength(); i++)
            {
                TestRunner.Equal(paths[i], items[i].GetProperty("path").GetString());
                TestRunner.Equal("unknown", items[i].GetProperty("state").GetString());
                TestRunner.Assert(!items[i].GetProperty("verified").GetBoolean());
                TestRunner.Equal(JsonValueKind.Null, items[i].GetProperty("verification").ValueKind);
                TestRunner.Equal("Not verified.", items[i].GetProperty("message").GetString());
            }
            TestRunner.Equal(4, f.QueueRequests);
            Console.WriteLine($"U17-01/02 wait all={all} failed={failed} uploadedFirst={uploadedFirst}: exit=3; code=busy; items={items.GetRawText()}; final queue checkpoint reached.");
        }
        finally { gate.Dispose(); }
        f.QueueReply = _ => (200, Queue(failed));
        var retried = Cli(f.Controller, args);
        using var retry = JsonDocument.Parse(retried.Output);
        TestRunner.Equal(failed ? 4 : 3, retried.Exit);
        TestRunner.Equal(failed ? "upload_failed" : "timeout", retry.RootElement.GetProperty("code").GetString());
        TestRunner.Equal(failed ? "failed" : "queued", retry.RootElement.GetProperty("items")[uploadedFirst ? 1 : 0].GetProperty("state").GetString());
        Console.WriteLine($"U17-01/02 same wait after gate release: exit={retried.Exit}; code={retry.RootElement.GetProperty("code").GetString()}; failed={failed}.");
    }

    private static void StatFailureBusy()
    {
        using var gate = new TestControlGate();
        using var f = new ReadFixture();
        bool hold = false;
        f.QueueReply = n =>
        {
            if (n != 2) return (200, Queue(false));
            if (hold) gate.Acquire();
            return (503, "{}"); // The stat target queue fails after constructor status.
        };
        var before = Cli(f.Controller, "stat", "one.txt", "--json");
        f.QueueRequests = 0; hold = true;
        try
        {
            var after = Cli(f.Controller, "stat", "one.txt", "--json");
            TestRunner.Equal(4, before.Exit); TestRunner.Equal(before.Exit, after.Exit);
            using var prior = JsonDocument.Parse(before.Output); using var current = JsonDocument.Parse(after.Output);
            foreach (string field in new[] { "code", "message" })
                TestRunner.Equal(prior.RootElement.GetProperty(field).GetString(), current.RootElement.GetProperty(field).GetString());
            TestRunner.Equal("failed", current.RootElement.GetProperty("code").GetString());
            TestRunner.Equal("rc vfs/queue: HTTP 503.", current.RootElement.GetProperty("message").GetString());
            TestRunner.Equal(2, f.QueueRequests);
            Console.WriteLine("U16-01 stat rc 503 -> generation busy: exit=4; code=failed; original rc message retained.");
        }
        finally { gate.Dispose(); }
    }

    private static void StableReadBoundaries()
    {
        foreach (string command in new[] { "ls", "find", "stat" })
        foreach (string scenario in new[] { "failure-busy", "success-busy", "failure-mount-changed" })
        {
            using var gate = new TestControlGate();
            using var f = new ReadFixture();
            var original = new IOException("Original metadata read failure.");
            using var metadata = new AgentMetadata(f.Controller)
            {
                ChildrenReader = _ =>
                {
                    if (scenario == "failure-mount-changed")
                    {
                        RuntimeState state = f.State.Load()!;
                        RuntimeSnapshots.Save(f.State, state with { ActiveConfig = state.ActiveConfig with { Retention = "3h" } });
                    }
                    else gate.Acquire();
                    if (scenario != "success-busy") throw original;
                    return [new("one.txt", false, 1, null)];
                }
            };
            Exception? observed = null;
            try
            {
                if (command == "ls") _ = metadata.List("", new());
                else if (command == "find") _ = metadata.Find("one");
                else _ = metadata.Stat("one.txt");
            }
            catch (Exception e) { observed = e; }
            finally { gate.Dispose(); }
            if (scenario == "failure-busy") TestRunner.Assert(ReferenceEquals(original, observed), "Prior read exception was replaced.");
            else if (scenario == "success-busy") TestRunner.Assert(observed is ControlBusyException, "Successful reads must still discard results on busy.");
            else TestRunner.Assert(observed is AgentCommandException { Code: "mount_changed", ExitCode: 3 }, "Mount change must override the prior read failure.");
            Console.WriteLine($"U16-01 {command} {scenario}: {observed!.GetType().Name}; prior exception identity/mount guard checked.");
        }
    }

    private static void RecoveryDuringWait(string mode)
    {
        const string human = "Recovery or process verification is incomplete; tell a person. Do not clear recovery automatically.";
        const string confirming = "Uploads from before the last stop are still being confirmed. This clears by itself when they finish; retry in a few seconds. If it persists, tell a person.";
        foreach (bool jsonOutput in new[] { true, false })
        {
            using var f = new ReadFixture();
            bool refused = false;
            f.QueueReply = n =>
            {
                if (n == 4)
                {
                    // The wait already holds its target items. Refuse a real normal stop
                    // before returning the final queue response, with no polling or sleep.
                    TestRunner.Throws<PendingUploadsException>(() => f.Controller.Stop());
                    TestRunner.Equal(StopDisposition.StopUncertain, f.State.Load()!.StopDisposition);
                    refused = true;
                    if (mode == "stopped") f.Query = _ => null;
                    if (mode == "uncertain") f.Query = _ => throw new ProcessIdentityUnknownException("Injected process uncertainty.");
                }
                return (200, Queue(false));
            };
            string[] args = ["wait", "one.txt", "--timeout", "0"];
            var reply = Cli(f.Controller, jsonOutput ? [.. args, "--json"] : args);
            TestRunner.Assert(refused, "The wait-time normal stop was not refused.");
            TestRunner.Equal(3, reply.Exit);
            string expected = mode == "running" ? confirming : human;
            if (jsonOutput)
            {
                using var json = JsonDocument.Parse(reply.Output);
                var result = json.RootElement;
                TestRunner.Equal(mode == "uncertain" ? "process_uncertain" : "recovery_required", result.GetProperty("code").GetString());
                TestRunner.Equal(expected, result.GetProperty("message").GetString());
                TestRunner.Assert(result.GetProperty("status").GetProperty("recoveryRequired").GetBoolean());
                TestRunner.Equal(mode == "running", result.GetProperty("status").GetProperty("running").GetBoolean());
                TestRunner.Equal(mode == "uncertain", result.GetProperty("status").GetProperty("processUncertain").GetBoolean());
                TestRunner.Assert(!result.GetProperty("items")[0].GetProperty("verified").GetBoolean());
            }
            else TestRunner.Assert(reply.Error.Contains(expected, StringComparison.Ordinal), reply.Error);
            Console.WriteLine($"U16-02 wait-time stop refused; mode={mode}; json={jsonOutput}; exit=3; message={expected}");
        }
    }

    private static string Queue(bool failed) => JsonSerializer.Serialize(new
    { queue = new[] { new { name = "one.txt", uploading = false, tries = failed ? 2 : 0 } } });

    private sealed class RcServer : IDisposable
    {
        internal int Port { get; }
        private readonly HttpListener listener = new();
        private readonly Task accept;
        private readonly List<Task> requests = [];
        private int stopping;
        internal RcServer(Func<string, (int Code, string Body)> reply)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/"); listener.Start();
            accept = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var context = await listener.GetContextAsync();
                        // A wait checkpoint can synchronously refuse a normal stop,
                        // whose stats request must be serviced independently.
                        requests.Add(Task.Run(async () =>
                        {
                            var response = reply(context.Request.Url!.AbsolutePath);
                            byte[] bytes = Encoding.UTF8.GetBytes(response.Body);
                            context.Response.StatusCode = response.Code;
                            context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes);
                            context.Response.Close();
                        }));
                    }
                }
                catch (Exception e) when (Volatile.Read(ref stopping) != 0 &&
                    e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
            });
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref stopping, 1); listener.Close();
            accept.GetAwaiter().GetResult(); Task.WhenAll(requests).GetAwaiter().GetResult();
        }
    }

    private sealed class ReadFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(@"D:\dbxl-test", "run-u6o-" + Guid.NewGuid().ToString("N"));
        internal ConfigStore Store { get; }
        internal StateStore State { get; }
        internal MountController Controller { get; }
        internal DbxConfig Config { get; }
        internal Func<int, (int Code, string Body)>? QueueReply { get; set; }
        internal int QueueRequests;
        internal int CloudStatRequests;
        internal Func<(int Code, string Body)>? CloudStatReply { get; set; }
        internal Func<RuntimeState, Process?> Query { get; set; } = _ => Process.GetCurrentProcess();
        private readonly RcServer proxy;
        internal ReadFixture()
        {
            foreach (string folder in new[] { "remote", "mount", "cache/data", "cache/meta" })
                Directory.CreateDirectory(Path.Combine(Root, folder));
            string empty = Path.Combine(Root, "isolated.conf"); File.WriteAllText(empty, "", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Root, "mount", "one.txt"), "one", new UTF8Encoding(false));
            string stats = JsonSerializer.Serialize(new { diskCache = new { uploadsQueued = 1, uploadsInProgress = 0,
                erroredFiles = 0, files = 0, bytesUsed = 0, path = Path.Combine(Root, "cache", "data"), pathMeta = Path.Combine(Root, "cache", "meta") } });
            proxy = new RcServer(path => path == "/vfs/stats" ? (200, stats) :
                path == "/vfs/queue" ? QueueReply?.Invoke(Interlocked.Increment(ref QueueRequests)) ?? (200, Queue(false)) :
                path == "/operations/stat" ? ReadCloudStat() : (503, "{}"));
            Config = new DbxConfig { Remote = Path.Combine(Root, "remote"), RcloneConfigPath = empty,
                MountPoint = Path.Combine(Root, "mount"), NetworkMode = false, CacheDir = Path.Combine(Root, "cache"),
                RescueDir = Path.Combine(Root, "rescue"), LogDir = Path.Combine(Root, "logs"), RcPort = proxy.Port,
                WriteAllowedRemotes = [Path.Combine(Root, "remote")] };
            Store = new ConfigStore(Path.Combine(Root, "config.json")); Store.Save(Config);
            State = new StateStore(Path.Combine(Root, "runtime", "state.json"));
            using var self = Process.GetCurrentProcess();
            RuntimeSnapshots.Save(State, new RuntimeState { ConfigPath = Store.PathName, ActiveConfig = Config,
                RclonePid = self.Id, ProcessStartedUtc = self.StartTime.ToUniversalTime(), ExecutablePath = Environment.ProcessPath!,
                RcPort = proxy.Port, RcUser = "fixture", RcPassword = "fixture-only",
                StartedReadOnly = false, RecoveryRequired = false, StopDisposition = StopDisposition.Running });
            Controller = new MountController(Store, State, processQuery: state => Query(state));
        }
        private (int Code, string Body) ReadCloudStat()
        {
            Interlocked.Increment(ref CloudStatRequests);
            return CloudStatReply?.Invoke() ?? (503, "{}");
        }
        public void Dispose()
        {
            proxy.Dispose();
            long bytes = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            TestRunner.Assert(bytes < 50L * 1024 * 1024);
            Console.WriteLine($"U16 isolated RC fixture: no child started; bytes={bytes}; artifacts={Root}");
        }
    }
}
