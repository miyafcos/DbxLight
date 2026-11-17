using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6s7rTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        if (issue is null or "all" or "R13" or "baseline")
            runner.Test("6s7r R13: one aged body-only remnant is not pending upload", RemnantOnly);
        if (issue is null or "all" or "R13")
        {
            runner.Test("6s7r R13: a recent body stays unknown until the injected clock crosses sixty seconds", RecentRemnant);
            foreach (string kind in new[] { "local", "queue-first", "queue-last", "cloud", "broken-meta", "locked-meta", "size-mismatch", "dirty", "growing", "retimed", "predates-process", "local-io", "local-access", "cloud-io", "cloud-malformed", "missing-root", "metadata-appears", "outside-remote", "undecodable", "hidden" })
            {
                string scenario = kind;
                runner.Test("6s7r R13: unsafe remnant stays pending: " + scenario, () => RemnantRejected(scenario));
            }
            runner.Test("6s7r R13: definite absent parent is absent", MissingParent);
            runner.Test("6s7r R13: stopped and live Clean remain false", RecoveryUnchanged);
            runner.Test("6s7r R13: remnant receipts are capped at twenty and explicit paths stay unknown", RemnantOutput);
        }
        if (issue is null or "all" or "R13B")
        {
            runner.Test("6s7r R13b: slash config plus extended RC paths recognize a remnant", () => RemnantOnlyWithSpelling(true));
            foreach (string kind in new[] { "local", "queue-first", "queue-last", "dirty" })
            {
                string scenario = kind;
                runner.Test("6s7r R13b: mixed spelling retains pending work: " + kind, () => RemnantRejected(scenario, true));
            }
            runner.Test("6s7r R13b: named Dirty with matching cloud metadata is unknown", () => NamedMixedSpelling(true));
            runner.Test("6s7r R13b: named clean with matching cloud metadata is uploaded", () => NamedMixedSpelling(false));
        }
        Review(runner, issue);
        TimeoutReviews(runner, issue);
    }

    private static void RemnantOnly() => RemnantOnlyWithSpelling(false);
    private static void RemnantOnlyWithSpelling(bool productionSpelling)
    {
        using var f = new WaitFixture(productionSpelling);
        f.Body("old.tmp");
        using var agent = f.Agent();
        var result = agent.Wait([], all: true, timeoutSeconds: 0);
        Console.WriteLine("R13 result=" + JsonSerializer.Serialize(result, JsonFiles.Options));
        TestRunner.Equal(0, result.ExitCode);
        TestRunner.Equal("uploaded", result.Result.Code);
        TestRunner.Equal(1, result.Result.RemnantCount);
        TestRunner.Equal(0, result.Result.Items.Count);
        TestRunner.Equal(new AgentCacheRemnant("old.tmp", 7, f.Modified), result.Result.Remnants.Single());
        TestRunner.Assert(result.Result.RemnantMessage.Contains("回復"));
    }

    private static void RecentRemnant()
    {
        using var f = new WaitFixture(); f.Body("old.tmp"); f.Now = f.Modified.AddSeconds(59);
        using var a = f.Agent(); Unknown(a.Wait([], all: true, timeoutSeconds: 0));
        int loops = 0;
        using var waiting = f.Agent(point =>
        {
            if (point == "after-final-queue" && ++loops == 2) f.Now = f.Modified.AddSeconds(60);
        });
        var result = waiting.Wait([], all: true, timeoutSeconds: 120);
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(1, result.Result.RemnantCount);
        TestRunner.Assert(loops >= 2); TestRunner.Equal(60, AgentMetadata.RemnantMinimumAgeSeconds);
    }

    private static void Unknown(AgentWaitOutcome result)
    {
        TestRunner.Equal(3, result.ExitCode); TestRunner.Equal("unknown", result.Result.Code);
        TestRunner.Equal(0, result.Result.RemnantCount);
    }

    private static void RemnantRejected(string kind, bool productionSpelling = false)
    {
        using var f = new WaitFixture(productionSpelling); string body = f.Body("old.tmp");
        string meta = Path.Combine(f.Meta, "old.tmp");
        FileStream? denied = null;
        if (kind == "local") f.Children = _ => [new("old.tmp", false, 7, f.Modified)];
        if (kind.StartsWith("queue-"))
            f.Queue = read => (kind == "queue-first" ? read % 2 == 1 : read % 2 == 0)
                ? "{\"queue\":[{\"name\":\"old.tmp\",\"uploading\":false,\"tries\":0}]}" : "{\"queue\":[]}";
        if (kind == "cloud") f.Cloud = () => (200, "{\"item\":{\"Name\":\"old.tmp\",\"IsDir\":false,\"Size\":7,\"ModTime\":\"" + f.Modified.ToString("O") + "\"}}");
        if (kind == "broken-meta") File.WriteAllText(meta, "{}");
        if (kind is "locked-meta" or "size-mismatch" or "dirty")
        {
            File.WriteAllText(meta, JsonSerializer.Serialize(new { ModTime = f.Modified, ATime = f.Modified, Size = kind == "size-mismatch" ? 8 : 7,
                Rs = new[] { new { Pos = 0, Size = 7 } }, Fingerprint = "", Dirty = kind == "dirty" }));
            if (kind == "locked-meta") denied = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        if (kind == "predates-process") File.SetLastWriteTimeUtc(body, f.Rc.State.Load()!.ProcessStartedUtc.AddSeconds(-1));
        if (kind == "local-io") f.Children = _ => throw new IOException("Injected listing failure.");
        if (kind == "local-access") f.Children = _ => throw new UnauthorizedAccessException("Injected access failure.");
        if (kind == "cloud-malformed") f.Cloud = () => (200, "{\"item\":{\"Name\":\"bad/name\",\"Size\":7,\"IsDir\":false}}");
        if (kind == "missing-root") f.Children = _ => throw new DirectoryNotFoundException("Mount root absent.");
        if (kind == "dirty") f.Body("remnant.tmp");
        if (kind == "cloud-io") f.Cloud = () => (503, "{}");
        if (kind == "outside-remote") File.Move(body, Path.Combine(f.Config.CacheDir, "vfs", "foreign.tmp"));
        if (kind == "undecodable") File.Move(body, Path.Combine(f.Data, "unknown？.tmp"));
        if (kind == "hidden")
        {
            var hidden = f.Config with { HiddenPaths = ["secret"] }; f.Rc.Config.Save(hidden);
            RuntimeSnapshots.Save(f.Rc.State, f.Rc.State.Load()! with { ActiveConfig = hidden });
            Directory.CreateDirectory(Path.Combine(f.Data, "secret")); File.Move(body, Path.Combine(f.Data, "secret", "old.tmp"));
        }
        using var agent = f.Agent(point =>
        {
            if (point != "after-final-queue") return;
            if (kind == "growing") { File.AppendAllText(body, "growing"); File.SetLastWriteTimeUtc(body, f.Modified.UtcDateTime); }
            if (kind == "metadata-appears") File.WriteAllText(meta, "{}");
            if (kind == "retimed") File.SetLastWriteTimeUtc(body, f.Modified.AddSeconds(1).UtcDateTime);
        });
        try
        {
            var result = agent.Wait([], all: true, timeoutSeconds: 0);
            if (kind.StartsWith("queue-"))
            {
                TestRunner.Equal(3, result.ExitCode); TestRunner.Equal("queued", result.Result.Items.Single().State);
                TestRunner.Equal(0, result.Result.RemnantCount);
            }
            else if (kind == "dirty")
            {
                TestRunner.Equal(3, result.ExitCode); TestRunner.Equal("unknown", result.Result.Code);
                TestRunner.Equal(1, result.Result.RemnantCount); TestRunner.Equal("old.tmp", result.Result.Items.Single().Path);
            }
            else if (kind == "hidden") { TestRunner.Equal(3, result.ExitCode); TestRunner.Equal("hidden", result.Result.Code); TestRunner.Equal(0, result.Result.RemnantCount); }
            else Unknown(result);
        }
        finally { denied?.Dispose(); }
        if (kind == "dirty")
        {
            // The fake upload removes its own pair; retain both files outside the scanned cache.
            File.Move(body, Path.Combine(f.Rc.Root, "uploaded-body")); File.Move(meta, Path.Combine(f.Rc.Root, "uploaded-meta"));
            f.Body("remnant.tmp");
            using var done = f.Agent(); TestRunner.Equal(0, done.Wait([], all: true, timeoutSeconds: 0).ExitCode);
        }
    }

    private static void MissingParent()
    {
        using var f = new WaitFixture(); f.Body("gone/old.tmp");
        f.Children = _ => throw new DirectoryNotFoundException("Definite missing parent.");
        using var a = f.Agent(); TestRunner.Equal(0, a.Wait([], all: true, timeoutSeconds: 0).ExitCode);
    }

    private static void RecoveryUnchanged()
    {
        using var f = new WaitFixture(); f.Body("old.tmp");
        var stopped = PendingCacheInspector.InspectStopped(f.Config.CacheDir);
        var live = PendingCacheInspector.Observe(f.Config.CacheDir);
        TestRunner.Assert(!stopped.Clean && !live.Clean);
        TestRunner.Assert(stopped.CompleteScan && live.CompleteScan);
        TestRunner.Equal(0, stopped.PendingFiles); TestRunner.Assert(!stopped.Entries.Single().MetadataValid);
        using var a = f.Agent(); TestRunner.Equal(0, a.Wait([], all: true, timeoutSeconds: 0).ExitCode);
        TestRunner.Assert(!PendingCacheInspector.InspectStopped(f.Config.CacheDir).Clean);
    }

    private static void RemnantOutput()
    {
        using var f = new WaitFixture();
        for (int i = 0; i < 21; i++) f.Body($"old-{i:D2}.tmp");
        using var a = f.Agent(); var result = a.Wait([], all: true, timeoutSeconds: 0);
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(21, result.Result.RemnantCount);
        TestRunner.Equal(20, result.Result.Remnants.Count); TestRunner.Equal(0, result.Result.Items.Count);
        using var explicitPath = f.Agent(); Unknown(explicitPath.Wait(["old-00.tmp"], timeoutSeconds: 0));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Result, JsonFiles.Options));
        TestRunner.Equal(21, json.RootElement.GetProperty("remnantCount").GetInt32());
        TestRunner.Equal(20, json.RootElement.GetProperty("remnants").GetArrayLength());
        TestRunner.Assert(json.RootElement.GetProperty("remnantMessage").GetString()!.Length > 0);
    }

    private static void NamedMixedSpelling(bool dirty)
    {
        using var f = new WaitFixture(true); f.Body("named.txt");
        File.WriteAllText(Path.Combine(f.Meta, "named.txt"), JsonSerializer.Serialize(new
        {
            ModTime = f.Modified, ATime = f.Modified, Size = 7,
            Rs = new[] { new { Pos = 0, Size = 7 } }, Fingerprint = "", Dirty = dirty
        }));
        f.Children = _ => [new("named.txt", false, 7, f.Modified)];
        f.Cloud = () => (200, JsonSerializer.Serialize(new { item = new { Name = "named.txt", IsDir = false, Size = 7, ModTime = f.Modified } }));
        using var a = f.Agent(); var result = a.Wait(["named.txt"], timeoutSeconds: 0);
        Console.WriteLine("R13b named dirty=" + dirty + ": " + JsonSerializer.Serialize(result, JsonFiles.Options));
        if (dirty) Unknown(result);
        else { TestRunner.Equal(0, result.ExitCode); TestRunner.Equal("uploaded", result.Result.Items.Single().State); }
    }

    private sealed class WaitFixture : IDisposable
    {
        internal readonly Unit3aTests.RcFixture Rc = new(Path.Combine(TestPaths.WorkRoot, "run-unit6s7r-" + Guid.NewGuid().ToString("N")));
        internal readonly DbxConfig Config;
        internal readonly string Data, Meta;
        internal DateTimeOffset Modified, Now;
        internal Func<string, IReadOnlyList<MetadataItem>> Children = _ => [];
        internal Func<int, string> Queue = _ => "{\"queue\":[]}";
        internal Func<(int, string)> Cloud = () => (200, "{\"item\":null}");
        private int queueReads;
        internal WaitFixture(bool productionSpelling = false)
        {
            Config = Rc.Config.Load();
            Config = Config with { WriteAllowedRemotes = [Config.Remote], CacheDir = productionSpelling ? Config.CacheDir.Replace('\\', '/') : Config.CacheDir };
            Rc.Config.Save(Config);
            var state = Rc.State.Load()!;
            RuntimeSnapshots.Save(Rc.State, state with { ActiveConfig = Config, RecoveryRequired = false, StopDisposition = StopDisposition.Running, StartedReadOnly = false });
            Modified = state.ProcessStartedUtc.AddSeconds(1); Now = Modified.AddMinutes(2);
            Data = Path.Combine(Config.CacheDir, "vfs", "active");
            Meta = Path.Combine(Config.CacheDir, "vfsMeta", "active");
            Directory.CreateDirectory(Data); Directory.CreateDirectory(Meta); Directory.CreateDirectory(Config.MountPoint);
            string rcData = productionSpelling ? MountMetadata.Extended(Path.GetFullPath(Data)) : Data;
            string rcMeta = productionSpelling ? MountMetadata.Extended(Path.GetFullPath(Meta)) : Meta;
            Console.WriteLine($"R13 fixture productionSpelling={productionSpelling}; config={Config.CacheDir}; rcData={rcData}; rcMeta={rcMeta}");
            Rc.Respond = path => path == "/operations/stat" ? Cloud() : (200, path switch
            {
                "/vfs/stats" => JsonSerializer.Serialize(new { diskCache = new { uploadsQueued = 0, uploadsInProgress = 0, erroredFiles = 0, files = 0, bytesUsed = 0, path = rcData, pathMeta = rcMeta } }),
                "/vfs/queue" => Queue(++queueReads),
                "/operations/stat" => "{\"item\":null}",
                _ => "{\"pid\":" + state.RclonePid + "}"
            });
        }
        internal string Body(string name)
        {
            string path = Path.Combine(Data, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "remnant"); File.SetLastWriteTimeUtc(path, Modified.UtcDateTime); return path;
        }
        internal AgentMetadata Agent(Action<string>? checkpoint = null) => new(Rc.Controller) { ChildrenReader = path => Children(path), WaitUtcNow = () => Now, WaitCheckpoint = checkpoint };
        public void Dispose() => Rc.Dispose();
    }
}
