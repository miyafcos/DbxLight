using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6s7rTests
{
    private static void TimeoutReviews(TestRunner runner, string? issue)
    {
        if (issue == "R13C-identity")
        {
            runner.Test("6s7r R13c: a replaced generation wins over its status timeout", TimeoutChangedWhileStatusTimeout);
            return;
        }
        if (issue is not (null or "all" or "R13C" or "R13C-before")) return;
        foreach (string kind in new[] { "metadata", "queue-first" })
        foreach (bool all in new[] { false, true })
        {
            string scenario = kind; bool entire = all;
            runner.Test($"6s7r R13c: one {kind} timeout recovers; all={all}", () => TimeoutRecovers(scenario, entire));
        }
        if (issue == "R13C-before") return;
        runner.Test("6s7r R13c: final queue timeout discards the first receipt", FinalQueueTimeout);
        foreach (bool before in new[] { true, false })
        {
            bool metadata = before;
            runner.Test("6s7r R13c: repeated timeouts exhaust the own budget; metadata=" + before, () => TimeoutExhaustion(metadata));
            runner.Test("6s7r R13c: refused connection is not retried; metadata=" + before, () => TimeoutRefused(metadata));
        }
        foreach (bool replaced in new[] { false, true })
        {
            bool change = replaced;
            runner.Test("6s7r R13c: next round rechecks stopped or replaced generation; replaced=" + change, () => TimeoutGeneration(change));
        }
        runner.Test("6s7r R13c: zero timeout still returns after one metadata failure", ZeroTimeout);
        foreach (bool all in new[] { false, true })
        {
            bool entire = all;
            runner.Test("6s7r R13c: pending queue remains pending across a timeout; all=" + all, () => TimeoutPending(entire));
        }
        foreach (string kind in new[] { "json", "queue-unknown", "outside", "cancellation" })
        {
            string failure = kind;
            runner.Test("6s7r R13c: permanent failure is not retried: " + kind, () => TimeoutPermanent(failure));
        }
        runner.Test("6s7r R13c: status refresh timeout is recognized and retried", StatusTimeout);
        runner.Test("6s7r R13c: a replaced generation wins over its status timeout", TimeoutChangedWhileStatusTimeout);
    }
    private static void TimeoutRecovers(string kind, bool all)
    {
        using var f = new TimeoutFixture();
        f.Plan = (command, count) => command == (kind == "metadata" ? "vfs/stats" : "vfs/queue") && count == 1;
        using var agent = f.Agent(); var result = agent.Wait(all ? [] : ["saved.txt"], all, timeoutSeconds: 2);
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal("uploaded", result.Result.Code);
        TestRunner.Equal(1, f.TransportTimeouts); TestRunner.Equal(1, f.Retries);
        TestRunner.Assert(f.Count(kind == "metadata" ? "vfs/stats" : "vfs/queue") >= 2);
    }
    private static void FinalQueueTimeout()
    {
        using var f = new TimeoutFixture();
        f.Plan = (command, count) => command == "vfs/queue" && count == 2;
        f.OnRetry = () => f.Bytes = 8;
        using var agent = f.Agent(); var result = agent.Wait(["saved.txt"], timeoutSeconds: 2);
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(1, f.Retries);
        TestRunner.Equal(4, f.Count("vfs/queue")); TestRunner.Equal(2, f.Count("operations/stat"));
        TestRunner.Equal(8L, result.Result.Items.Single().Verification!.CloudSize);
    }
    private static void TimeoutExhaustion(bool metadata)
    {
        using var f = new TimeoutFixture();
        f.Plan = (command, _) => command == (metadata ? "vfs/stats" : "vfs/queue"); f.AdvanceOnTimeout = 1;
        using var agent = f.Agent(); var result = agent.Wait(["saved.txt"], timeoutSeconds: 2);
        TestRunner.Equal(metadata ? 3 : 4, result.ExitCode); TestRunner.Equal(metadata ? "unknown" : "failed", result.Result.Code);
        TestRunner.Assert(result.Result.Message.StartsWith(metadata ? "Cache metadata could not be inspected: " : "Upload verification failed: ", StringComparison.Ordinal));
        TestRunner.Equal(2, f.TransportTimeouts); TestRunner.Equal(2, f.Retries);
        TestRunner.Assert(result.Result.Items.All(i => !i.Verified));
    }
    private static void TimeoutRefused(bool metadata)
    {
        using var f = new TimeoutFixture(); f.RefuseCommand = metadata ? "vfs/stats" : "vfs/queue";
        using var agent = f.Agent(); var result = agent.Wait(["saved.txt"], timeoutSeconds: 2);
        TestRunner.Equal(metadata ? 3 : 4, result.ExitCode); TestRunner.Equal(metadata ? "unknown" : "failed", result.Result.Code);
        TestRunner.Equal(0, f.Retries); TestRunner.Equal(1, f.Count(f.RefuseCommand));
    }
    private static void TimeoutGeneration(bool replaced)
    {
        using var f = new TimeoutFixture(); f.Plan = (command, count) => command == "vfs/stats" && count == 1;
        f.OnRetry = () =>
        {
            RuntimeState original = f.Rc.State.Load()!;
            if (replaced) RuntimeSnapshots.Save(f.Rc.State, original with { ActiveConfig = original.ActiveConfig with { Retention = "3h" } });
            else
            {
                File.WriteAllText(Path.Combine(f.Rc.Root, "child-stop.txt"), "exit");
                Unit3aTests.Wait(() => { using var child = ProcessIdentity.Open(original); return child is null; }, "Owned timeout fixture did not exit.");
                RuntimeSnapshots.Save(f.Rc.State, original with { StopRequested = true, StoppedUtc = DateTime.UtcNow, StopDisposition = StopDisposition.Clean, RecoveryRequired = false });
            }
        };
        using var agent = f.Agent(); var result = agent.Wait(["saved.txt"], timeoutSeconds: 2);
        TestRunner.Equal(replaced ? 3 : 6, result.ExitCode); TestRunner.Equal(replaced ? "mount_changed" : "not_running", result.Result.Code);
        TestRunner.Equal(1, f.Count("vfs/stats")); TestRunner.Equal(1, f.Retries);
    }
    private static void TimeoutChangedWhileStatusTimeout()
    {
        using var f = new TimeoutFixture(); f.Plan = (command, count) => command == "vfs/stats" && count == 1;
        f.OnRetry = () =>
        {
            if (f.Retries != 1) return;
            RuntimeState original = f.Rc.State.Load()!;
            RuntimeSnapshots.Save(f.Rc.State, original with { ActiveConfig = original.ActiveConfig with { Retention = "3h" } });
            f.DelayUntaggedStats = true;
        };
        using var agent = f.Agent(); var result = agent.Wait(["saved.txt"], timeoutSeconds: 2);
        TestRunner.Equal(3, result.ExitCode); TestRunner.Equal("mount_changed", result.Result.Code);
        TestRunner.Equal(1, f.Retries); TestRunner.Equal(1, f.Count("vfs/stats"));
    }
    private static void ZeroTimeout()
    {
        using var f = new TimeoutFixture(); f.Plan = (command, _) => command == "vfs/stats";
        using var agent = f.Agent(); var result = agent.Wait(["saved.txt"], timeoutSeconds: 0);
        TestRunner.Equal(3, result.ExitCode); TestRunner.Equal("unknown", result.Result.Code);
        TestRunner.Assert(result.Result.Message.StartsWith("Cache metadata could not be inspected: ", StringComparison.Ordinal));
        TestRunner.Equal(1, f.TransportTimeouts); TestRunner.Equal(1, f.Count("vfs/stats"));
    }
    private static void TimeoutPending(bool all)
    {
        using var f = new TimeoutFixture(); f.QueuePresent = true;
        f.Plan = (command, count) => command == "vfs/queue" && count == 2;
        f.OnCheckpoint = point => { if (point == "after-final-queue") f.QueuePresent = false; };
        using var agent = f.Agent(); var result = agent.Wait(all ? [] : ["saved.txt"], all, timeoutSeconds: 2);
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(1, f.TransportTimeouts);
        TestRunner.Assert(f.Count("vfs/queue") >= 6);
        TestRunner.Equal("uploaded", result.Result.Items.Single().State);
    }
    private static void TimeoutPermanent(string kind)
    {
        using var f = new TimeoutFixture(); f.Permanent = kind;
        using var agent = f.Agent();
        if (kind is "json" or "outside")
        {
            // InvalidDataException already propagates to the CLI's existing error formatter.
            TestRunner.Throws<InvalidDataException>(() => agent.Wait(["saved.txt"], timeoutSeconds: 2));
            TestRunner.Equal(0, f.Retries); return;
        }
        var result = agent.Wait(["saved.txt"], timeoutSeconds: 2);
        TestRunner.Equal(kind == "json" ? 4 : 3, result.ExitCode);
        TestRunner.Equal(kind == "queue-unknown" ? "queue_unknown" : kind == "json" ? "failed" : "unknown", result.Result.Code);
        TestRunner.Equal(0, f.Retries);
    }
    private static void StatusTimeout()
    {
        using var f = new TimeoutFixture(); using var agent = f.Agent();
        f.DelayUntaggedStats = true;
        var result = agent.Wait(["saved.txt"], timeoutSeconds: 2);
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(1, f.Retries);
    }
    private sealed class TimeoutFixture : IDisposable
    {
        internal readonly Unit3aTests.RcFixture Rc = new(Path.Combine(TestPaths.WorkRoot, "run-unit6s7r-timeout-" + Guid.NewGuid().ToString("N")));
        internal Func<string, int, bool> Plan = (_, _) => false;
        internal Action? OnRetry;
        internal Action<string>? OnCheckpoint;
        internal string? RefuseCommand, Permanent;
        internal volatile bool QueuePresent;
        private int delayStatus;
        private TaskCompletionSource? statusDelay;
        internal bool DelayUntaggedStats
        {
            get => Volatile.Read(ref delayStatus) != 0;
            set
            {
                if (value) { statusDelay = new(TaskCreationOptions.RunContinuationsAsynchronously); delayed.Add(statusDelay); }
                Volatile.Write(ref delayStatus, value ? 1 : 0);
            }
        }
        internal long Bytes = 7;
        internal int TransportTimeouts, Retries;
        internal double Elapsed, AdvanceOnTimeout;
        private readonly DateTimeOffset modified = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly Dictionary<string, int> calls = [];
        private readonly string data, meta;
        private readonly ConcurrentBag<TaskCompletionSource> delayed = [];
        private readonly ConcurrentBag<Task> requests = [];
        private readonly ConcurrentDictionary<int, TaskCompletionSource> delaySignals = [];
        private int nextDelay;
        private readonly int fixturePid;
        private readonly HttpListener listener = new();
        private readonly Task accept;
        private readonly RcClient normal, timeout, refused;
        private int stopping;
        internal TimeoutFixture()
        {
            var config = Rc.Config.Load(); config = config with { WriteAllowedRemotes = [config.Remote] };
            data = Path.Combine(config.CacheDir, "vfs", "active"); meta = Path.Combine(config.CacheDir, "vfsMeta", "active");
            Directory.CreateDirectory(data); Directory.CreateDirectory(meta); Directory.CreateDirectory(config.MountPoint);
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            var refusedProbe = new TcpListener(IPAddress.Loopback, 0); refusedProbe.Start(); int refusedPort = ((IPEndPoint)refusedProbe.LocalEndpoint).Port; refusedProbe.Stop();
            config = config with { RcPort = port }; Rc.Config.Save(config);
            RuntimeState state = Rc.State.Load()!; fixturePid = state.RclonePid;
            RuntimeSnapshots.Save(Rc.State, state with { ActiveConfig = config, RcPort = port, RecoveryRequired = false, StopDisposition = StopDisposition.Running, StartedReadOnly = false });
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            normal = new RcClient(port, new RcCredentials("fixture", "fixture-only"), Timeout.InfiniteTimeSpan);
            timeout = new RcClient(port, new RcCredentials("fixture", "fixture-only"), TimeSpan.FromMilliseconds(100));
            refused = new RcClient(refusedPort, new RcCredentials("fixture", "fixture-only"), Timeout.InfiniteTimeSpan);
            accept = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var request = await listener.GetContextAsync(); requests.Add(Serve(request));
                    }
                }
                catch (Exception e) when (Volatile.Read(ref stopping) != 0 && e is HttpListenerException or ObjectDisposedException) { }
            });
        }
        internal int Count(string command) => calls.GetValueOrDefault(command);
        internal AgentMetadata Agent() => new(Rc.Controller)
        {
            ChildrenReader = _ => [new("saved.txt", false, Bytes, modified)],
            WaitRcCallTestOverride = Call, WaitElapsedTestOverride = () => Elapsed, WaitDelayTestOverride = _ => { },
            WaitCheckpoint = point =>
            {
                if (point.StartsWith("rc-timeout:", StringComparison.Ordinal)) { Retries++; Release(); OnRetry?.Invoke(); }
                OnCheckpoint?.Invoke(point);
            }
        };
        private JsonElement Call(string command, object? body)
        {
            int count = calls[command] = Count(command) + 1;
            TestRunner.Assert(count <= 30, "Timeout scenario exceeded its request order guard.");
            if (Permanent == "cancellation" && command == "vfs/stats") throw new OperationCanceledException("Injected non-timeout cancellation.");
            var node = JsonSerializer.SerializeToNode(body ?? new { })!.AsObject();
            bool delay = Plan(command, count); node["_waitTest"] = true; node["_delay"] = delay;
            if (delay)
            {
                int id = Interlocked.Increment(ref nextDelay);
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delaySignals[id] = signal; delayed.Add(signal); node["_delayId"] = id;
            }
            node["_permanent"] = Permanent;
            try { return (RefuseCommand == command ? refused : delay ? timeout : normal).Call(command, node); }
            catch (OperationCanceledException error)
            {
                TestRunner.Assert(error.InnerException is TimeoutException, "Expected the real HttpClient timeout exception chain.");
                TransportTimeouts++; Elapsed += AdvanceOnTimeout; Release();
                TestTimeline.Mark(Rc.Root, "rc-timeout", new { command, count, type = error.GetType().Name, inner = error.InnerException!.GetType().Name });
                throw;
            }
        }
        private async Task Serve(HttpListenerContext context)
        {
            try
            {
                string command = context.Request.Url!.AbsolutePath.TrimStart('/');
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                bool test = body.RootElement.TryGetProperty("_waitTest", out var marker) && marker.GetBoolean();
                bool hold = body.RootElement.TryGetProperty("_delay", out var pause) && pause.GetBoolean();
                TaskCompletionSource? held = hold ? delaySignals[body.RootElement.GetProperty("_delayId").GetInt32()] : null;
                if (!test && command == "vfs/stats" && DelayUntaggedStats) { DelayUntaggedStats = false; held = statusDelay; }
                if (held is not null) await held.Task;
                string? permanent = test && body.RootElement.TryGetProperty("_permanent", out var failure) ? failure.GetString() : null;
                string response = command switch
                {
                    "vfs/stats" => JsonSerializer.Serialize(new { diskCache = new { uploadsQueued = QueuePresent ? 1 : 0, uploadsInProgress = 0, erroredFiles = 0, files = 0, bytesUsed = 0, path = data, pathMeta = permanent == "outside" ? Rc.Root : meta } }),
                    "vfs/queue" => permanent == "json" ? "{" : permanent == "queue-unknown" ? "{}" : JsonSerializer.Serialize(new { queue = QueuePresent ? new[] { new { name = "saved.txt", uploading = false, tries = 0 } } : [] }),
                    "operations/stat" => JsonSerializer.Serialize(new { item = new { Name = "saved.txt", IsDir = false, Size = Bytes, ModTime = modified } }),
                    _ => JsonSerializer.Serialize(new { pid = fixturePid })
                };
                byte[] bytes = Encoding.UTF8.GetBytes(response); context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
            catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or JsonException) { }
        }
        private void Release() { foreach (var pending in delayed) pending.TrySetResult(); }
        public void Dispose()
        {
            Interlocked.Exchange(ref stopping, 1); Release(); listener.Close(); accept.GetAwaiter().GetResult();
            Task.WhenAll(requests).GetAwaiter().GetResult(); normal.Dispose(); timeout.Dispose(); refused.Dispose(); Rc.Dispose();
        }
    }
}
