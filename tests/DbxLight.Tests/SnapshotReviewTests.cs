using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class SnapshotTests
{
    internal static void ReviewTests(TestRunner runner, string? filter = null)
    {
        void Test(string id, string name, Action test)
        { if (filter is null || id.StartsWith(filter, StringComparison.Ordinal)) runner.Test("snapshot review " + id + ": " + name, test); }
        Test("P01a", "strict fake rejects unknown and reordered renewal arguments", RenewalFake);
        Test("P01b", "renewal argument sequence succeeds with real rclone local remote", RenewalReal);
        Test("P01c", "10 to 60 seconds remaining uses the current token", RenewalThreshold);
        Test("P01d", "each successive rejected token can be renewed once", RenewalPerToken);
        Test("P01e", "unchanged token and expiry fail without another request", RenewalUnchanged);
        Test("P02a", "parallel fetch shares delayed successful renewals", () => ParallelRenewal(false));
        Test("P02b", "parallel fetch shares auth_unavailable without internal_error", () => ParallelRenewal(true));
        Test("P03a", "failures identify plan row and preserve verified objects for reuse", FailureRows);
        Test("P03b", "corrupt existing objects identify localPath", CorruptFailure);
        Test("P03d", "explicit failure file is durable and never overwritten", ExplicitFailures);
        Test("P03c", "explicit failure output is validated before authentication", FailureDestination);
        Test("P04", "guide preserves master exactly except approved guidance within 40 lines", ReviewGuide);
        Test("P05a", "authentication transition and relogin marks refuse readonly", AuthenticationMarks);
        Test("P05b", "configuration read shares writes and deletion", ConfigurationSharing);
        Test("P06a", "application-owned output roots are refused", ProtectedDestinations);
        Test("P06b", "CLI protects selected configuration and state folders", ProtectedSelectedFolders);
        Test("P07a", "all malformed input rows fail before authentication or HTTP", InputPreflight);
        Test("P07b", "unknown plan fields are ignored but required size is mandatory", PlanFields);
        Test("P07d", "UTF-8 BOM input remains validated on its second pass", InputBom);
        Test("P07c", "non-JSON 5xx exhausts retries as exit 4", RemoteHtml);
        Test("P08", "zero and absent token expiry are non-expiring", LegacyExpiry);
        Test("P09a", "send failures retry the same request with 1/2/4 second waits", SendRetries);
        Test("P09b", "JSON response body interruption retries from the same cursor", JsonBodyRetry);
        Test("P09c", "download interruption restarts in a fresh partial", DownloadRetry);
        Test("P09d", "deadline prevents retry delay", RetryDeadline);
        Test("P10a", "full and delta publish root path and cursor byte hash and count", CursorIntegrity);
        Test("P10b", "empty full folder and namespace root have a rootPathLower", EmptyRoots);
        Test("P10c", "docs define application rules and all failure codes", ReviewDocs);
        Test("P11", "staged output uses durable flush before promotion", DurableFlush);
        Review2Tests(runner, filter);
        Review3Tests(runner, filter);
    }

    // Access new JSON fields through the public envelope, so these tests also compile
    // unchanged against the pre-fix implementation for the regression demonstration.
    private static JsonElement Envelope(SnapshotResult value) => JsonSerializer.SerializeToElement(value, SnapshotJson.Options);
    private sealed class SequenceBroker(int changes = int.MaxValue, double remainingSeconds = 3600, bool fail = false) : ISnapshotTokenBroker
    {
        public SnapshotRemote Remote { get; } = new("sample", "", true, null);
        private readonly DateTimeOffset expiry = remainingSeconds == double.PositiveInfinity ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddSeconds(remainingSeconds);
        internal int Renewals, Reads;
        public Task<SnapshotToken> ReadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(new SnapshotToken(Access + "_" + Math.Min(Renewals, changes), expiry));
        }
        public async Task RenewAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Renewals);
            await Task.Delay(120, token);
            if (fail) throw new SnapshotException(3, "auth_unavailable");
        }
    }
    private static void RenewalFake()
    {
        var (broker, fake) = FakeRclone("snapshot"); broker.ReadAsync(default).GetAwaiter().GetResult();
        broker.RenewAsync(default).GetAwaiter().GetResult();
        string[] expected = ["about", "sample:/", "--json", "--contimeout", "5s", "--timeout", "10s", "--retries", "1",
            "--low-level-retries", "1", "--ask-password=false", "--ftp-ask-password=false", "--sftp-ask-password=false"];
        using var recorded = JsonDocument.Parse(File.ReadAllLines(Path.Combine(fake, "calls.jsonl"))[1]);
        TestRunner.Assert(recorded.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()).SequenceEqual(expected));
        foreach (string[] invalid in new[] { expected.Concat(new[] { "--read-only" }).ToArray(), expected.Concat(new[] { "--fake-child" }).ToArray(), expected.Reverse().ToArray() })
        {
            using var process = Process.Start(RcloneArguments.ProcessInfo(Path.Combine(fake, "DbxLight.FakeRclone.exe"), invalid))!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            TestRunner.Assert(process.WaitForExit(10000)); Task.WaitAll(stdout, stderr); TestRunner.Assert(process.ExitCode != 0);
        }
    }
    private static void RenewalReal()
    {
        var f = new Fixture(); f.Write("local.conf", "[sample]\ntype = local\n");
        var broker = new SnapshotTokenBroker(f.Config with { Remote = "sample:", RcloneConfigPath = f.PathFor("local.conf") });
        broker.RenewAsync(default).GetAwaiter().GetResult();
    }
    private static void RenewalThreshold()
    {
        var f = new Fixture(); var broker = new SequenceBroker(remainingSeconds: 30);
        var result = DropboxSnapshotReader.RunAsync(f.Config, f.List(), broker: broker, handler: new Handler((_, _) => Task.FromResult(Page([FileEntry()])))).GetAwaiter().GetResult();
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(0, broker.Renewals);
    }
    private static void RenewalPerToken()
    {
        var f = new Fixture(); var broker = new SequenceBroker(); int calls = 0;
        var result = DropboxSnapshotReader.RunAsync(f.Config, f.List(), broker: broker, handler: new Handler((_, _) =>
            Task.FromResult(++calls <= 2 ? Json(new { }, HttpStatusCode.Unauthorized) : Page([FileEntry()])))).GetAwaiter().GetResult();
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(2, broker.Renewals); TestRunner.Equal(3, calls);
    }
    private static void RenewalUnchanged()
    {
        var f = new Fixture(); var broker = new SequenceBroker(changes: 0); int calls = 0;
        var result = DropboxSnapshotReader.RunAsync(f.Config, f.List(), broker: broker, handler: new Handler((_, _) =>
        { calls++; return Task.FromResult(Json(new { }, HttpStatusCode.Unauthorized)); })).GetAwaiter().GetResult();
        Failure(result, 3, "auth_unavailable"); TestRunner.Equal(1, calls); TestRunner.Equal(1, broker.Renewals);
    }
    private static void ParallelRenewal(bool fail)
    {
        var f = new Fixture(); var request = FetchRequest(f, 12) with { Parallel = 4, DeadlineSeconds = 8 };
        // Distinct content prevents object reuse from bypassing later HTTP requests.
        f.Write("plan", string.Concat(Enumerable.Range(0, 12).Select(i => JsonSerializer.Serialize(new SnapshotPlan(
            "id:sample" + i, (0xa12345678L + i).ToString("x"), "sample/a" + i, 3, ContentHash([(byte)i, 2, 3])), SnapshotJson.Options) + "\n")));
        var broker = new SequenceBroker(fail: fail);
        var result = DropboxSnapshotReader.RunAsync(f.Config, request, broker: broker, handler: new Handler(async (r, ct) =>
        {
            using var arg = JsonDocument.Parse(r.Headers.GetValues("Dropbox-API-Arg").Single());
            string rev = arg.RootElement.GetProperty("path").GetString()![4..]; int i = (int)(Convert.ToInt64(rev, 16) - 0xa12345678L);
            int required = i < 4 ? 1 : i < 8 ? 2 : 3;
            int supplied = int.Parse(r.Headers.Authorization!.Parameter!.Split('_').Last(), System.Globalization.CultureInfo.InvariantCulture);
            if (supplied < required) return Json(new { }, HttpStatusCode.Unauthorized);
            await Task.Delay(20, ct); byte[] bytes = [(byte)i, 2, 3];
            return Download(bytes, "id:sample" + i, rev, hash: ContentHash(bytes));
        })).GetAwaiter().GetResult();
        if (fail)
        {
            Failure(result, 3, "auth_unavailable"); TestRunner.Equal(1, broker.Renewals); TestRunner.Equal(0L, result.Verified);
            TestRunner.Equal(12L, Envelope(result).GetProperty("failed").GetInt64());
            string[] failures = File.ReadAllLines(Envelope(result).GetProperty("failuresFile").GetString()!);
            TestRunner.Equal(12, failures.Length);
            foreach (string line in failures)
            {
                using var row = JsonDocument.Parse(line); TestRunner.Equal("auth_unavailable", row.RootElement.GetProperty("code").GetString());
            }
        }
        else { TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(12L, result.Verified); TestRunner.Equal(3, broker.Renewals); }
        TestRunner.Assert(result.ExitCode != 1);
    }
    private static void FailureRows()
    {
        var f = new Fixture(); var request = FetchRequest(f) with { Parallel = 1 };
        string good = File.ReadAllText(request.Plan!);
        f.Write("plan", good + good.Replace("id:sample", "id:missing").Replace("a12345678", "b12345678").Replace(ContentHash(Content), new string('b', 64)));
        var result = f.Run(request, r => r.Headers.GetValues("Dropbox-API-Arg").Single().Contains("b12345678")
            ? Json(new { error = new Dictionary<string, object> { [".tag"] = "path", ["path"] = new Dictionary<string, string> { [".tag"] = "not_found" } } }, HttpStatusCode.Conflict)
            : Download(Content));
        Failure(result, 4, "not_found"); var json = Envelope(result); TestRunner.Equal(1L, json.GetProperty("failed").GetInt64());
        string failures = json.GetProperty("failuresFile").GetString()!;
        TestRunner.Equal(Path.GetFullPath(request.Store!), Path.GetDirectoryName(failures));
        TestRunner.Assert(Path.GetFileName(failures).StartsWith("fetch-failures.", StringComparison.Ordinal));
        using var row = JsonDocument.Parse(File.ReadAllLines(failures).Single());
        TestRunner.Equal(2L, row.RootElement.GetProperty("planLine").GetInt64()); TestRunner.Equal("id:missing", row.RootElement.GetProperty("id").GetString());
        TestRunner.Equal("b12345678", row.RootElement.GetProperty("rev").GetString()); TestRunner.Equal("not_found", row.RootElement.GetProperty("code").GetString());
        f.Write("plan", good); var next = f.Run(request, _ => throw new InvalidOperationException("Verified object should be reused"));
        TestRunner.Equal(0, next.ExitCode); TestRunner.Equal(1L, next.Reused);
    }
    private static void CorruptFailure()
    {
        var f = new Fixture(); var request = FetchRequest(f); var first = f.Run(request, _ => Download(Content));
        using var row = JsonDocument.Parse(File.ReadAllLines(first.ResultsFile!).Single()); string local = row.RootElement.GetProperty("localPath").GetString()!;
        File.WriteAllText(local, "xyz"); var result = f.Run(request, _ => Download(Content)); Failure(result, 4, "content_hash_mismatch");
        using var failure = JsonDocument.Parse(File.ReadAllLines(Envelope(result).GetProperty("failuresFile").GetString()!).Single());
        TestRunner.Equal(local, failure.RootElement.GetProperty("localPath").GetString()); TestRunner.Equal("xyz", File.ReadAllText(local));
    }
    private static void FailureDestination()
    {
        var f = new Fixture(); SnapshotRequest request = FetchRequest(f);
        JsonElement json = JsonSerializer.SerializeToElement(request, SnapshotJson.Options);
        var fields = json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        fields["failures"] = JsonSerializer.SerializeToElement(Path.Combine(f.Config.LogDir, "blocked.jsonl"));
        request = JsonSerializer.Deserialize<SnapshotRequest>(JsonSerializer.Serialize(fields), SnapshotJson.Options)!;
        var broker = new FakeBroker(); Failure(f.Run(request, _ => Download(Content), broker), 2, "unsafe_destination"); TestRunner.Equal(0, broker.Reads);
    }
    private static void ReviewGuide()
    {
        string source = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), "src", "DbxLight.Core", "AgentGuide.cs"));
        const string updateHint = "新しいバージョンは dbxl update check --json で確認する。入れ替えは人が決める。";
        TestRunner.Equal(2, source.Split(updateHint, StringSplitOptions.None).Length);
        source = source.Replace(updateHint, "");
        string suffix = "snapshot list / metadata / fetch --json \u306f\u30de\u30a6\u30f3\u30c8\u4e0d\u8981\u306e API \u8aad\u307f\u51fa\u3057\u3002\u5f15\u6570\u30fb\u51fa\u529b\u30fb\u7d42\u4e86\u5024\u306f docs/cli.md \u3092\u53c2\u7167\u3057\u30010 \u3068 complete=true \u3092\u78ba\u304b\u3081\u308b\u3002";
        const string remnant = "wait --all は名前の変更の途中で残った本体を待ちの対象から外すことがある。その本体は常駐を止めたときに回復の対象として残る。";
        const string stalledPage = "continuation.hint を読み、前に進まない場合は --time-limit を延ばすか --in で絞る。それ以外は、";
        foreach (string approved in new[] { suffix, remnant, stalledPage })
            TestRunner.Equal(2, source.Split(approved, StringSplitOptions.None).Length);
        TestRunner.Equal("7790b29c1812f26ef4feec41af645bb085e33631b4a6e0bee063d793c33aa1b3", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
        TestRunner.Equal("472b76c20315447701e9acff215ec93985886e788e4adc6067ead5a8244444fd", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace(suffix, "").Replace(remnant, "").Replace(stalledPage, "")))).ToLowerInvariant());
        var f = new Fixture(); var controller = new MountController(new ConfigStore(f.PathFor("guide.json"), f.Config), new StateStore(f.PathFor("guide.state.json")));
        string guide = AgentGuide.Create(controller).Markdown;
        TestRunner.Equal(40, guide.Split('\n').Length);
        TestRunner.Assert(guide.Contains("|\n\n| \u7d42\u4e86\u5024"));
    }
    private static void AuthenticationMarks()
    {
        foreach (bool relogin in new[] { false, true })
        {
            var f = new Fixture(); string config = f.PathFor("config.json"); f.Write("config.json", JsonSerializer.Serialize(f.Config, JsonFiles.Options));
            string mark = relogin ? CredentialSwitch.ReloginPath(config) : CredentialSwitch.TransactionPath(config);
            Directory.CreateDirectory(Path.GetDirectoryName(mark)!); File.WriteAllText(mark, "{}");
            try { DropboxSnapshotReader.ReadConfiguration(config); throw new InvalidOperationException("Authentication mark ignored"); }
            catch (SnapshotException e) { TestRunner.Equal(3, e.ExitCode); TestRunner.Equal("auth_unavailable", e.Code); }
            TestRunner.Equal("{}", File.ReadAllText(mark)); TestRunner.Equal(2, Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories).Count());
        }
    }
    private static void ConfigurationSharing()
    {
        var f = new Fixture(); f.Write("config.json", JsonSerializer.Serialize(f.Config, JsonFiles.Options));
        using var writer = new FileStream(f.PathFor("config.json"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        TestRunner.Equal(f.Config.Remote, DropboxSnapshotReader.ReadConfiguration(f.PathFor("config.json")).Remote);
    }
    private static void ProtectedDestinations()
    {
        var f = new Fixture(); var destination = new SnapshotDestination(f.Config);
        foreach (string parent in new[] { f.Config.RescueDir, f.Config.LogDir, DbxConfig.LocalRoot, Path.GetDirectoryName(DbxConfig.DefaultConfigPath)! })
        {
            try { destination.Check(Path.Combine(parent, "sample-output.jsonl")); throw new InvalidOperationException("Application root accepted"); }
            catch (SnapshotException e) { TestRunner.Equal(2, e.ExitCode); TestRunner.Equal("unsafe_destination", e.Code); }
        }
    }
    private static void ProtectedSelectedFolders()
    {
        var f = new Fixture(); string configDir = f.PathFor("configuration"), stateDir = f.PathFor("runtime"); Directory.CreateDirectory(configDir);
        string config = Path.Combine(configDir, "config.json"), state = Path.Combine(stateDir, "state.json"); File.WriteAllText(Path.Combine(configDir, "empty.conf"), "[sample]\ntype = local\n"); File.WriteAllText(config, JsonSerializer.Serialize(f.Config with { RcloneConfigPath = Path.Combine(configDir, "empty.conf") }, JsonFiles.Options));
        foreach (string parent in new[] { configDir, stateDir })
        {
            var text = new StringWriter(); TextWriter saved = Console.Out;
            try
            {
                Console.SetOut(text); TestRunner.Equal(2, DbxLight.Cli.Program.Main(["--config", config, "--state", state, "snapshot", "list", "sample",
                    "--entries", Path.Combine(parent, "blocked.jsonl"), "--cursor-out", f.PathFor("cursor"), "--json"]));
            }
            finally { Console.SetOut(saved); }
            using var result = JsonDocument.Parse(text.ToString()); TestRunner.Equal("unsafe_destination", result.RootElement.GetProperty("code").GetString());
        }
    }
    private static void InputPreflight()
    {
        foreach (string kind in new[] { "plan", "paths", "cursor" })
        foreach (string bad in new[] { "{broken", "{}", "[]", "null", "{\"path\":42}", "{\"path\":\"../escape\"}" })
        {
            var f = new Fixture(); SnapshotRequest request;
            if (kind == "plan") { request = FetchRequest(f); f.Write("plan", File.ReadAllText(request.Plan!) + bad + "\n"); }
            else if (kind == "paths") { f.Write("paths", "{\"path\":\"sample/a\"}\n" + bad + "\n"); request = new("metadata", PathsFile: f.PathFor("paths"), Output: f.PathFor("output")); }
            else { f.Write("cursor", bad); request = f.List() with { CursorFile = f.PathFor("cursor") }; }
            int requests = 0; var broker = new FakeBroker(); var result = f.Run(request, _ => { requests++; return Download(Content); }, broker);
            TestRunner.Equal(2, result.ExitCode); TestRunner.Equal(0, broker.Reads); TestRunner.Equal(0, requests); TestRunner.Equal(0, result.Requests);
            TestRunner.Assert(!result.Complete);
        }
    }
    private static void PlanFields()
    {
        var f = new Fixture(); var request = FetchRequest(f); string plan = File.ReadAllText(request.Plan!);
        f.Write("plan", plan.TrimEnd().Insert(1, "\"futureField\":{\"allowed\":true},") + "\n");
        TestRunner.Equal(0, f.Run(request, _ => Download(Content)).ExitCode);
        using var parsed = JsonDocument.Parse(plan); var values = parsed.RootElement.EnumerateObject().Where(p => p.Name != "size").ToDictionary(p => p.Name, p => p.Value);
        f.Write("plan", JsonSerializer.Serialize(values) + "\n"); var broker = new FakeBroker();
        Failure(f.Run(request, _ => Download(Content), broker), 2, "invalid_plan"); TestRunner.Equal(0, broker.Reads);
    }
    private static void InputBom()
    {
        var f = new Fixture(); var request = FetchRequest(f);
        File.WriteAllText(request.Plan!, File.ReadAllText(request.Plan!), new UTF8Encoding(true));
        TestRunner.Equal(0, f.Run(request, _ => Download(Content)).ExitCode);
        File.WriteAllText(f.PathFor("paths"), "{\"path\":\"sample/a\"}\n", new UTF8Encoding(true));
        var metadata = new SnapshotRequest("metadata", PathsFile: f.PathFor("paths"), Output: f.PathFor("meta"), MaxRps: 100);
        TestRunner.Equal(0, f.Run(metadata, _ => Json(new Dictionary<string, string> { [".tag"] = "file", ["id"] = "id:sample" })).ExitCode);
    }
    private static void ExplicitFailures()
    {
        var f = new Fixture(); SnapshotRequest request = FetchRequest(f);
        string path = f.PathFor("diagnostics/failures.jsonl");
        var fields = JsonSerializer.SerializeToElement(request, SnapshotJson.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        fields["failures"] = JsonSerializer.SerializeToElement(path);
        request = JsonSerializer.Deserialize<SnapshotRequest>(JsonSerializer.Serialize(fields), SnapshotJson.Options)!;
        var result = f.Run(request, _ => Json(new { error = new Dictionary<string, object> { [".tag"] = "path", ["path"] = new Dictionary<string, string> { [".tag"] = "not_found" } } }, HttpStatusCode.Conflict));
        Failure(result, 4, "not_found"); TestRunner.Equal(Path.GetFullPath(path), Envelope(result).GetProperty("failuresFile").GetString());
        string before = File.ReadAllText(path); var broker = new FakeBroker();
        Failure(f.Run(request, _ => Download(Content), broker), 2, "destination_exists"); TestRunner.Equal(0, broker.Reads); TestRunner.Equal(before, File.ReadAllText(path));
    }
    private static void RemoteHtml()
    {
        var f = new Fixture(); int calls = 0;
        var result = f.Run(f.List() with { DeadlineSeconds = 12 }, _ => { calls++; return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>temporary failure</html>") }; });
        Failure(result, 4, "remote_error"); TestRunner.Equal(4, calls); TestRunner.Equal(3, Envelope(result).GetProperty("retries").GetInt32());
    }
    private static void LegacyExpiry()
    {
        foreach (string expiry in new[] { ",\"expiry\":\"0001-01-01T00:00:00Z\"", "" })
        {
            var f = new Fixture(); f.Write("legacy.conf", "[sample]\ntype = dropbox\ntoken = {\"access_token\":\"" + Access + "\"" + expiry + "}\n");
            var broker = new SnapshotTokenBroker(f.Config with { RcloneConfigPath = f.PathFor("legacy.conf") });
            TestRunner.Equal(DateTimeOffset.MaxValue, broker.ReadAsync(default).GetAwaiter().GetResult().ExpiresUtc);
        }
        var sample = new Fixture(); var nonexpiring = new SequenceBroker(remainingSeconds: double.PositiveInfinity); int n = 0;
        var result = DropboxSnapshotReader.RunAsync(sample.Config, sample.List(), broker: nonexpiring, handler: new Handler((_, _) => Task.FromResult(
            ++n == 1 ? Json(new { }, HttpStatusCode.Unauthorized) : Page([FileEntry()])))).GetAwaiter().GetResult();
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(1, nonexpiring.Renewals);
    }
    private static void SendRetries()
    {
        var f = new Fixture(); var times = new List<double>(); var watch = Stopwatch.StartNew(); int calls = 0;
        var result = f.Run(f.List() with { DeadlineSeconds = 12 }, r =>
        {
            times.Add(watch.Elapsed.TotalSeconds); TestRunner.Assert(r.RequestUri!.AbsolutePath.EndsWith(SnapshotEndpoints.List));
            if (++calls == 1) throw new HttpRequestException("synthetic connection failure");
            if (calls == 2) throw new TaskCanceledException("synthetic transport timeout");
            return calls == 3 ? new(HttpStatusCode.BadGateway) { Content = new StringContent("temporary") } : Page([FileEntry()]);
        });
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(4, calls); TestRunner.Equal(3, Envelope(result).GetProperty("retries").GetInt32());
        for (int i = 1; i < times.Count; i++) TestRunner.Assert(times[i] - times[i - 1] >= (1 << (i - 1)) - 0.03);
    }
    private sealed class BrokenBody(byte[] data) : MemoryStream(data)
    {
        private bool started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (started) throw new IOException("synthetic response interruption"); started = true;
            return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
    private static void JsonBodyRetry()
    {
        var f = new Fixture(); int calls = 0; string? first = null;
        var result = f.Run(f.List() with { DeadlineSeconds = 5 }, r =>
        {
            string body = Body(r); first ??= body; TestRunner.Equal(first, body);
            return ++calls == 1 ? new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenBody(Encoding.UTF8.GetBytes("{\"entries\":[]}"))) } : Page([FileEntry()]);
        });
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(2, calls); TestRunner.Equal(1, Envelope(result).GetProperty("retries").GetInt32());
    }
    private static void DownloadRetry()
    {
        var f = new Fixture(); var request = FetchRequest(f); int calls = 0;
        var result = f.Run(request, _ => ++calls == 1 ? Download(Content, content: new StreamContent(new BrokenBody(Content))) : Download(Content));
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(2, calls); TestRunner.Equal(1, Envelope(result).GetProperty("retries").GetInt32());
        using var row = JsonDocument.Parse(File.ReadAllLines(result.ResultsFile!).Single());
        TestRunner.Assert(File.ReadAllBytes(row.RootElement.GetProperty("localPath").GetString()!).SequenceEqual(Content));
        TestRunner.Assert(result.PartialFiles.Any(p => File.Exists(p) && new FileInfo(p).Length == 1));
    }
    private static void RetryDeadline()
    {
        var f = new Fixture(); var watch = Stopwatch.StartNew();
        var result = f.Run(f.List() with { DeadlineSeconds = 0.5 }, _ => new(HttpStatusCode.ServiceUnavailable));
        Failure(result, 3, "timeout"); TestRunner.Equal(0, Envelope(result).GetProperty("retries").GetInt32()); TestRunner.Assert(watch.Elapsed.TotalSeconds < 1);
    }
    private static void CursorIntegrity()
    {
        var f = new Fixture(); var firstRequest = f.List() with { Folder = "SAMPLE" };
        var first = f.Run(firstRequest, _ => Page([FolderEntry(), FileEntry(), DeletedEntry()])); TestRunner.Equal(0, first.ExitCode);
        CheckCursor(first, "/sample");
        var next = f.Run(f.List("delta") with { Folder = "SAMPLE", CursorFile = first.CursorFile }, _ => Page()); TestRunner.Equal(0, next.ExitCode);
        CheckCursor(next, "/sample");
        File.AppendAllText(next.EntriesFile!, "{}\n"); using var cursor = JsonDocument.Parse(File.ReadAllText(next.CursorFile!));
        TestRunner.Assert(cursor.RootElement.GetProperty("entriesSha256").GetString() != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(next.EntriesFile!))).ToLowerInvariant());
    }
    private static void CheckCursor(SnapshotResult result, string root)
    {
        TestRunner.Equal(root, Envelope(result).GetProperty("rootPathLower").GetString());
        using var cursor = JsonDocument.Parse(File.ReadAllText(result.CursorFile!));
        TestRunner.Equal(root, cursor.RootElement.GetProperty("rootPathLower").GetString());
        TestRunner.Equal(result.Entries, cursor.RootElement.GetProperty("entries").GetInt64());
        TestRunner.Equal(File.ReadAllLines(result.EntriesFile!).LongLength, cursor.RootElement.GetProperty("entries").GetInt64());
        TestRunner.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.EntriesFile!))).ToLowerInvariant(), cursor.RootElement.GetProperty("entriesSha256").GetString());
    }
    private static void EmptyRoots()
    {
        var f = new Fixture(); var empty = f.Run(f.List(), _ => Page()); TestRunner.Equal(0, empty.ExitCode); CheckCursor(empty, "/sample");
        var root = f.Run(f.List("root") with { Folder = "" }, _ => Page()); TestRunner.Equal(0, root.ExitCode); CheckCursor(root, "");
    }
    private static void ReviewDocs()
    {
        string docs = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), "docs", "cli.md"));
        foreach (string required in new[] { "rootPathLower", "entriesSha256", "planLine", "--failures", "retries", "mode=full", "include_deleted=true",
            "https://www.dropbox.com/developers/documentation/http/documentation#files-list_folder", "invalid_plan", "invalid_cursor", "network_error", "remote_error", "content_hash_mismatch" })
            TestRunner.Assert(docs.Contains(required), "Missing snapshot documentation: " + required);
    }
    private static void DurableFlush()
    {
        MethodInfo method = typeof(SnapshotStagedFile).GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!; int flush = -1, promote = -1;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6f)) continue;
            try
            {
                MethodBase? called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                if (called?.DeclaringType == typeof(FileStream) && called.Name == "Flush" && called.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(bool) })) flush = i;
                if (called?.DeclaringType == typeof(SnapshotDestination) && called.Name == "Promote") promote = i;
            }
            catch (ArgumentException) { }
        }
        TestRunner.Assert(flush > 0 && il[flush - 1] == 0x17 && promote > flush, "Missing durable FileStream.Flush(true) before rename");
        string source = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), "src", "DbxLight.Core", "SnapshotReader.cs"));
        TestRunner.Assert(source.Contains("file.Flush(true)"));
        TestRunner.Assert(source.IndexOf("entries.Commit()", StringComparison.Ordinal) < source.IndexOf("cursorOutput.Commit()", StringComparison.Ordinal));
    }
}
