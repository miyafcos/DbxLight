using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class SnapshotTests
{
    private static void Review2Tests(TestRunner runner, string? filter)
    {
        void Test(string id, string name, Action test)
        { if (filter is null || id.StartsWith(filter, StringComparison.Ordinal)) runner.Test("snapshot review " + id + ": " + name, test); }
        Test("Q01a", "stalled headers exhaust three retries before a long overall deadline", HeaderTimeoutExhausted);
        foreach (string route in new[] { SnapshotEndpoints.Account, SnapshotEndpoints.List, SnapshotEndpoints.Continue, SnapshotEndpoints.Metadata, SnapshotEndpoints.Download })
        {
            string selected = route;
            Test("Q01b-" + selected, "one stalled response header retries and succeeds", () => HeaderTimeoutOnce(selected));
        }
        Test("Q01c-list", "a stalled list body restarts the same request", () => BodyTimeoutOnce(false));
        Test("Q01c-fetch", "a stalled download body restarts in a fresh partial", () => BodyTimeoutOnce(true));
        Test("Q01d-head", "overall deadline wins over the header timeout without retry", () => TimeoutPrecedence(false, false));
        Test("Q01d-body", "overall deadline wins over the body timeout without retry", () => TimeoutPrecedence(true, false));
        Test("Q01e", "external cancellation wins without retry", () => TimeoutPrecedence(true, true));
        Test("Q01f", "progress resets body inactivity timeout without a total body limit", BodyProgress);
        Test("Q01g", "post-renewal account verification also bounds response headers", RenewalHeaderTimeout);
        Test("Q01h", "stalled error body retries and preserves not_found mapping", ErrorBodyTimeout);
        Test("Q01i", "docs state both fixed 60-second limits and overall cancellation precedence", RequestTimeoutDocs);
        foreach (string option in new[] { "entries", "cursor-out", "output", "failures", "store" })
        {
            string selected = option;
            Test("Q02-" + selected, "discovered rclone config parent refuses output before HTTP and files", () => DiscoveredConfigDestination(selected));
        }
        Test("Q02-outside", "outside discovered config parent succeeds and discovers once", DiscoveredConfigOutside);
        Test("Q02-preflight", "invalid input still precedes config discovery and authentication", DiscoveryInputPreflight);
        Test("Q02-physical", "discovered config junction protects its physical parent and later store writes", DiscoveredPhysicalRoot);
        Test("Q03", "docs define explicit absence, malformed rev and bounded caller retries", RevisionFailureDocs);
        Test("Q04", "docs handle missing list root and root deleted rows equally", DeletedRootDocs);
        Test("Q05-label", "opaque cloudPath labels never enter download paths or local paths", OpaqueCloudLabels);
        Test("Q05-invalid", "empty and ASCII control labels fail before authentication", InvalidCloudLabels);
        Test("Q05-metadata", "metadata colons remain slash-rooted literal API paths", MetadataColonPaths);
        Test("Q05-metadata-invalid", "metadata rejects separators, traversal and control characters", InvalidMetadataPaths);
        Test("Q05-scope", "list roots and remote prefixes retain existing colon rejection", OriginalPathScope);
        Test("Q05-docs", "docs distinguish opaque cloudPath from metadata paths", CloudPathDocs);
        Test("Q06", "help and usage table expose supported snapshot options and defaults", SnapshotUsageDocs);
        Test("Q07", "guide has no extra space after the final Japanese full stop", GuideFinalSpace);
    }

    // Reflection keeps the exact same tests compilable against b627086, which lacks
    // the optional internal timeout seam. A watchdog bounds that old implementation.
    private static SnapshotResult TimedRun(Fixture f, SnapshotRequest request, HttpMessageHandler handler,
        ISnapshotTokenBroker? broker = null, TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        MethodInfo method = typeof(DropboxSnapshotReader).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (!method.GetParameters().Any(p => p.Name == "httpTimeout")) watchdog.CancelAfter(1800);
        object?[] args = method.GetParameters().Select(p => p.Name switch
        {
            "config" => (object)f.Config, "request" => request, "cancellation" => watchdog.Token,
            "broker" => broker ?? new FakeBroker(), "handler" => handler,
            "httpTimeout" => timeout ?? TimeSpan.FromSeconds(2), _ => null
        }).ToArray();
        return ((Task<SnapshotResult>)method.Invoke(null, args)!).GetAwaiter().GetResult();
    }

    private sealed class RawHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request, cancellationToken);
    }
    private static HttpResponseMessage AccountResponse() => Json(new { account_id = "sample-account", root_info = new Dictionary<string, string>
        { [".tag"] = "team", ["root_namespace_id"] = "sample-team", ["home_namespace_id"] = "sample-home" } });
    private static async Task<HttpResponseMessage> Stall(CancellationToken token)
    {
        // No synthetic transport exception: the product must cancel a genuinely pending task.
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Page();
    }
    private static void HeaderTimeoutExhausted()
    {
        var f = new Fixture(); int calls = 0; var watch = Stopwatch.StartNew();
        var result = TimedRun(f, f.List() with { DeadlineSeconds = 30 }, new Handler((_, ct) => { calls++; return Stall(ct); }));
        Failure(result, 4, "network_error"); TestRunner.Equal(4, calls); TestRunner.Equal(3, result.Retries);
        TestRunner.Equal(5, result.Requests); TestRunner.Assert(watch.Elapsed.TotalSeconds < 20); NoFinal(f, f.List());
    }
    private static void HeaderTimeoutOnce(string route)
    {
        var f = new Fixture(); SnapshotRequest request = route == SnapshotEndpoints.Download ? FetchRequest(f) : f.List();
        if (route == SnapshotEndpoints.Metadata)
        {
            f.Write("paths", "{\"path\":\"sample/a\"}\n");
            request = new("metadata", PathsFile: f.PathFor("paths"), Output: f.PathFor("output"), MaxRps: 100);
        }
        request = request with { DeadlineSeconds = 30 };
        int selectedCalls = 0; string? firstBody = null;
        var result = TimedRun(f, request, new RawHandler(async (r, ct) =>
        {
            string actual = r.RequestUri!.AbsolutePath[3..];
            if (actual == route)
            {
                string body = actual == SnapshotEndpoints.Download ? r.Headers.GetValues("Dropbox-API-Arg").Single() : Body(r);
                firstBody ??= body; TestRunner.Equal(firstBody, body);
                if (++selectedCalls == 1) return await Stall(ct);
            }
            if (actual == SnapshotEndpoints.Account) return AccountResponse();
            if (actual == SnapshotEndpoints.Download) return Download(Content);
            if (actual == SnapshotEndpoints.Metadata) return Json(new Dictionary<string, string> { [".tag"] = "file", ["id"] = "id:sample" });
            return Page([FileEntry()], more: route == SnapshotEndpoints.Continue && actual == SnapshotEndpoints.List);
        }));
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(2, selectedCalls); TestRunner.Equal(1, result.Retries);
    }
    private sealed class StalledBody(byte[] data) : MemoryStream(data)
    {
        private bool started;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (started) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            started = true;
            return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
    private static void BodyTimeoutOnce(bool fetch)
    {
        var f = new Fixture(); var request = fetch ? FetchRequest(f) : f.List(); int calls = 0; string? original = null;
        var result = TimedRun(f, request with { DeadlineSeconds = 30 }, new Handler((r, _) =>
        {
            string body = fetch ? r.Headers.GetValues("Dropbox-API-Arg").Single() : Body(r);
            original ??= body; TestRunner.Equal(original, body);
            HttpResponseMessage response = fetch ? Download(Content) : Page([FileEntry()]);
            if (++calls == 1)
            {
                byte[] bytes = fetch ? Content : Encoding.UTF8.GetBytes(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                response.Content.Dispose(); response.Content = new StreamContent(new StalledBody(bytes));
            }
            return Task.FromResult(response);
        }));
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(2, calls); TestRunner.Equal(1, result.Retries);
        if (fetch)
        {
            using var row = JsonDocument.Parse(File.ReadAllLines(result.ResultsFile!).Single());
            TestRunner.Assert(File.ReadAllBytes(row.RootElement.GetProperty("localPath").GetString()!).SequenceEqual(Content));
            TestRunner.Assert(result.PartialFiles.Any(p => p.Contains(".blob.partial.", StringComparison.Ordinal) && new FileInfo(p).Length == 1));
            TestRunner.Equal(1, Directory.EnumerateFiles(request.Store!, "*.blob", SearchOption.AllDirectories).Count());
        }
        else TestRunner.Equal(1, File.ReadAllLines(result.EntriesFile!).Length);
    }
    private static void TimeoutPrecedence(bool body, bool external)
    {
        var f = new Fixture(); int calls = 0;
        using var cancel = new CancellationTokenSource(); if (external) cancel.CancelAfter(1000);
        var result = TimedRun(f, f.List() with { DeadlineSeconds = external ? 30 : 1 }, new Handler((_, ct) =>
        {
            calls++;
            return body ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody([123])) }) : Stall(ct);
        }), timeout: TimeSpan.FromSeconds(5), cancellation: cancel.Token);
        Failure(result, 3, external ? "cancelled" : "timeout"); TestRunner.Equal(0, result.Retries); TestRunner.Equal(1, calls);
        TestRunner.Assert(!File.Exists(f.List().Entries) && !File.Exists(f.List().CursorOut));
    }
    private sealed class ProgressBody(byte[] data) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(70, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
    private static void BodyProgress()
    {
        var f = new Fixture(); var watch = Stopwatch.StartNew(); byte[] data = new byte[16]; var request = FetchRequest(f);
        // Keep the complete body slower than the per-read limit while each read makes progress.
        f.Write("plan", JsonSerializer.Serialize(new SnapshotPlan("id:sample", "a12345678", "sample/a0.md", data.Length, ContentHash(data)), SnapshotJson.Options) + "\n");
        var result = TimedRun(f, request, new Handler((_, _) => Task.FromResult(Download(data, size: data.Length, hash: ContentHash(data), content: new StreamContent(new ProgressBody(data))))),
            timeout: TimeSpan.FromSeconds(1));
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(0, result.Retries); TestRunner.Assert(watch.Elapsed.TotalMilliseconds >= 1000);
    }
    private static void RenewalHeaderTimeout()
    {
        var f = new Fixture(); int accounts = 0, downloads = 0; var broker = new SequenceBroker();
        var result = TimedRun(f, FetchRequest(f) with { DeadlineSeconds = 30 }, new RawHandler(async (r, ct) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith(SnapshotEndpoints.Account, StringComparison.Ordinal))
                return ++accounts == 2 ? await Stall(ct) : AccountResponse();
            return ++downloads == 1 ? Json(new { }, HttpStatusCode.Unauthorized) : Download(Content);
        }), broker);
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(3, accounts); TestRunner.Equal(2, downloads);
        TestRunner.Equal(1, broker.Renewals); TestRunner.Equal(1, result.Retries);
    }
    private static HttpResponseMessage MissingResponse(HttpContent? content = null)
    {
        var response = Json(new { error = new Dictionary<string, object> { [".tag"] = "path", ["path"] = new Dictionary<string, string> { [".tag"] = "not_found" } } }, HttpStatusCode.Conflict);
        if (content is not null) { response.Content.Dispose(); response.Content = content; }
        return response;
    }
    private static void ErrorBodyTimeout()
    {
        var f = new Fixture(); int calls = 0;
        var result = TimedRun(f, f.List() with { DeadlineSeconds = 30 }, new Handler((_, _) => Task.FromResult(
            ++calls == 1 ? MissingResponse(new StreamContent(new StalledBody([123]))) : MissingResponse())));
        Failure(result, 4, "not_found"); TestRunner.Equal(2, calls); TestRunner.Equal(1, result.Retries);
    }

    private static (Fixture Fixture, string Fake, DbxConfig Config) DiscoveryFixture(bool invalid = false)
    {
        var f = new Fixture(); var (_, fake) = FakeRclone("snapshot");
        File.WriteAllText(Path.Combine(fake, "snapshot.conf"), invalid ? "RCLONE_ENCRYPT_V0:\nignored\n" : Conf("2099-01-01T00:00:00Z"));
        return (f, fake, f.Config with { RclonePath = Path.Combine(fake, "DbxLight.FakeRclone.exe"), RcloneConfigPath = null });
    }
    private static void DiscoveredConfigDestination(string option)
    {
        foreach (bool invalid in new[] { false, true })
        {
        // The invalid variant also proves the safety check precedes authentication.
        var (f, fake, config) = DiscoveryFixture(invalid);
        string blocked = Path.Combine(fake, "new-output", "blocked");
        var request = option is "entries" or "cursor-out" ? f.List() : FetchRequest(f);
        request = option switch
        {
            "entries" => request with { Entries = blocked }, "cursor-out" => request with { CursorOut = blocked },
            "store" => request with { Store = blocked }, "failures" => request with { Failures = blocked },
            _ => new("metadata", PathsFile: f.PathFor("paths"), Output: blocked, MaxRps: 100)
        };
        if (option == "output") f.Write("paths", "{\"path\":\"sample/a\"}\n");
        var result = DropboxSnapshotReader.RunAsync(config, request, handler: new Handler((_, _) => throw new InvalidOperationException("HTTP must not start"))).GetAwaiter().GetResult();
        Failure(result, 2, "unsafe_destination"); TestRunner.Equal(0, result.Requests); TestRunner.Equal(0, result.PartialFiles.Count);
        TestRunner.Assert(!Directory.Exists(Path.Combine(fake, "new-output")) && !Directory.Exists(f.PathFor("store")));
        TestRunner.Assert(!File.Exists(request.Entries) && !File.Exists(request.CursorOut) && !File.Exists(request.Output));
        TestRunner.Equal(1, File.ReadAllLines(Path.Combine(fake, "calls.jsonl")).Length);
        }
    }
    private static void DiscoveredConfigOutside()
    {
        var (f, fake, config) = DiscoveryFixture();
        foreach (SnapshotRequest request in new[] { f.List(), FetchRequest(f) })
        {
            var result = DropboxSnapshotReader.RunAsync(config, request, handler: new Handler((r, _) => Task.FromResult(
                r.RequestUri!.AbsolutePath.EndsWith(SnapshotEndpoints.Download, StringComparison.Ordinal) ? Download(Content) : Page([FileEntry()])))).GetAwaiter().GetResult();
            TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(0, result.Retries);
        }
        TestRunner.Equal(2, File.ReadAllLines(Path.Combine(fake, "calls.jsonl")).Length);
        TestRunner.Assert(File.ReadAllLines(Path.Combine(fake, "calls.jsonl")).All(line => line.Contains("config", StringComparison.Ordinal) && !line.Contains("about", StringComparison.Ordinal)));
    }
    private static void DiscoveryInputPreflight()
    {
        var (f, fake, config) = DiscoveryFixture(invalid: true); var request = FetchRequest(f); f.Write("plan", "{broken\n");
        var result = DropboxSnapshotReader.RunAsync(config, request).GetAwaiter().GetResult();
        Failure(result, 2, "invalid_plan"); TestRunner.Equal(0, result.Requests); TestRunner.Assert(!File.Exists(Path.Combine(fake, "calls.jsonl")));
    }
    private static void DiscoveredPhysicalRoot()
    {
        var f = new Fixture(); string physical = f.PathFor("physical"), link = f.PathFor("link"); Directory.CreateDirectory(physical);
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", link, physical }, RedirectStandardOutput = true, RedirectStandardError = true })!)
        { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(); TestRunner.Equal(0, process.ExitCode); }
        var destination = new SnapshotDestination(f.Config);
        MethodInfo? protect = typeof(SnapshotDestination).GetMethod("ProtectBrokerConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        if (protect is not null) protect.Invoke(destination, [Path.Combine(link, "sample.conf")]);
        foreach (string path in new[] { Path.Combine(physical, "out"), Path.Combine(physical, "store", "binding", "objects", "body.blob") })
        {
            try { destination.Check(path); throw new InvalidOperationException("Discovered physical config parent accepted"); }
            catch (SnapshotException e) { TestRunner.Equal(2, e.ExitCode); TestRunner.Equal("unsafe_destination", e.Code); }
        }
        TestRunner.Equal(0, Directory.EnumerateFiles(physical, "*", SearchOption.AllDirectories).Count());
    }

    private static void OpaqueCloudLabels()
    {
        foreach (string label in new[] { "sample:a", @"C:\sample\..\outside", "../outside", "a//./../b", "id:sample", "rev:a12345678", "/sample/a", " ", "sample\u0085label" })
        {
            var f = new Fixture(); var request = FetchRequest(f); int downloads = 0;
            f.Write("plan", JsonSerializer.Serialize(new SnapshotPlan("id:sample", "a12345678", label, 3, ContentHash(Content)), SnapshotJson.Options) + "\n");
            string[] before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories);
            var result = f.Run(request, r =>
            {
                downloads++; using var arg = JsonDocument.Parse(r.Headers.GetValues("Dropbox-API-Arg").Single());
                TestRunner.Equal("rev:a12345678", arg.RootElement.GetProperty("path").GetString()); TestRunner.Equal(1, arg.RootElement.EnumerateObject().Count());
                TestRunner.Equal("/2/" + SnapshotEndpoints.Download, r.RequestUri!.AbsolutePath);
                TestRunner.Equal(SnapshotEndpoints.ContentHost, r.RequestUri.GetLeftPart(UriPartial.Authority));
                return Download(Content);
            });
            TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(1, downloads);
            using var row = JsonDocument.Parse(File.ReadAllLines(result.ResultsFile!).Single()); TestRunner.Equal(label, row.RootElement.GetProperty("cloudPath").GetString());
            string local = row.RootElement.GetProperty("localPath").GetString()!;
            TestRunner.Equal(Path.Combine(request.Store!, result.BindingId!, "objects", ContentHash(Content) + ".blob"), local);
            TestRunner.Assert(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Except(before).All(p => p.StartsWith(request.Store! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        }
    }
    private static void InvalidCloudLabels()
    {
        foreach (string label in new[] { "" }.Concat(Enumerable.Range(0, 32).Append(127).Select(c => "sample" + (char)c + "label")))
        {
            var f = new Fixture(); var request = FetchRequest(f); var broker = new FakeBroker();
            f.Write("plan", JsonSerializer.Serialize(new SnapshotPlan("id:sample", "a12345678", label, 3, ContentHash(Content)), SnapshotJson.Options) + "\n");
            Failure(f.Run(request, _ => Download(Content), broker), 2, "unsafe_cloud_path"); TestRunner.Equal(0, broker.Reads);
            TestRunner.Assert(!Directory.Exists(request.Store));
        }
    }
    private static void MetadataColonPaths()
    {
        foreach (string prefix in new[] { "", "prefix" })
        foreach (string path in new[] { "sample/a:b", "id:sample", "rev:a12345678", "ns:sample/a" })
        {
            var f = new Fixture(); f.Write("paths", JsonSerializer.Serialize(new { path }) + "\n");
            var request = new SnapshotRequest("metadata", PathsFile: f.PathFor("paths"), Output: f.PathFor("output"), MaxRps: 100);
            var broker = new FakeBroker { Remote = new("sample", prefix, true, null) }; int calls = 0;
            var result = f.Run(request, r =>
            {
                calls++; using var body = JsonDocument.Parse(Body(r)); string actual = body.RootElement.GetProperty("path").GetString()!;
                TestRunner.Equal("/" + (prefix.Length > 0 ? prefix + "/" : "") + path, actual); TestRunner.Assert(actual.StartsWith('/'));
                return Json(new Dictionary<string, string> { [".tag"] = "file", ["id"] = "id:sample" });
            }, broker);
            TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(1, calls);
            using var row = JsonDocument.Parse(File.ReadAllLines(result.ResultsFile!).Single()); TestRunner.Equal(path, row.RootElement.GetProperty("path").GetString());
        }
    }
    private static void InvalidMetadataPaths()
    {
        foreach (string path in new[] { "", @"sample\a", "sample//a", "sample/./a", "sample/../a", "/id:sample", "sample\u0000a", "sample\u007fa" })
        {
            var f = new Fixture(); f.Write("paths", JsonSerializer.Serialize(new { path }) + "\n"); var broker = new FakeBroker();
            var result = f.Run(new("metadata", PathsFile: f.PathFor("paths"), Output: f.PathFor("output")), _ => Page(), broker);
            Failure(result, 2, "unsafe_cloud_path"); TestRunner.Equal(0, broker.Reads); TestRunner.Equal(0, result.Requests);
        }
    }
    private static void OriginalPathScope()
    {
        var f = new Fixture(); var broker = new FakeBroker();
        Failure(f.Run(f.List() with { Folder = "sample:a" }, _ => Page(), broker), 2, "unsafe_cloud_path"); TestRunner.Equal(0, broker.Reads);
        TestRunner.Throws<SnapshotException>(() => new SnapshotTokenBroker(f.Config with { Remote = "sample:/prefix:literal" }));
    }

    private static string CliDocs() => File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), "docs", "cli.md"));
    private static void RequireDocs(params string[] text)
    { string docs = CliDocs(); foreach (string required in text) TestRunner.Assert(docs.Contains(required, StringComparison.Ordinal), "Missing snapshot contract: " + required); }
    private static void RequestTimeoutDocs() => RequireDocs(
        "\u5fdc\u7b54\u30d8\u30c3\u30c0\u307e\u306760\u79d2", "\u7121\u901a\u4fe160\u79d2",
        "\u672c\u6587\u5168\u4f53\u306e\u6240\u8981\u6642\u9593\u306b\u306f\u4e0a\u9650\u3092\u4ed8\u3051\u306a\u3044",
        "timeout\uff083\uff09", "cancelled\uff083\uff09", "\u521d\u56de\u3092\u542b\u3081\u6700\u59274\u8981\u6c42");

    private static void RevisionFailureDocs() => RequireDocs(
        "\u6307\u5b9a\u3057\u305f rev\u30fb\u30d1\u30b9\u30fb\u5bfe\u8c61\u306e\u30d5\u30a9\u30eb\u30c0\u304c\u7121\u3044\u3068 Dropbox \u304c\u660e\u793a\u3057\u305f\u3068\u304d",
        "\u5f62\u306e\u5408\u308f\u306a\u3044 rev", "\u540c\u3058 rev \u3092\u56de\u6570\u306e\u4e0a\u9650\u306a\u3057\u306b\u3084\u308a\u76f4\u3055\u306a\u3044", "2026-11-15");
    private static void DeletedRootDocs() => RequireDocs(
        "list \u306e not_found \u306f\u5bfe\u8c61\u306e\u30d5\u30a9\u30eb\u30c0\u304c\u7121\u3044\u3053\u3068",
        "pathLower \u304c rootPathLower \u3068\u540c\u3058 deleted \u884c", "\u4e21\u65b9\u3092\u540c\u3058\u306b\u6271\u3046");
    private static void CloudPathDocs() => RequireDocs(
        "cloudPath \u306f\u7d50\u679c\u3078\u5199\u3059\u76ee\u5370", "U+0000\u301cU+001F\u30fbU+007F", "pathLower", "metadata \u306e path");
    private static void SnapshotUsageDocs()
    {
        string docs = CliDocs(); string[] rows = docs.Split('\n').Where(line => line.StartsWith("| snapshot ", StringComparison.Ordinal)).ToArray();
        TestRunner.Equal(3, rows.Length);
        foreach (string row in rows) TestRunner.Assert(row.Contains("[--max-rps N] [--deadline-seconds N]"));
        TestRunner.Assert(rows.Single(row => row.StartsWith("| snapshot fetch", StringComparison.Ordinal)).Contains("[--failures <JSONL>]"));
        TestRunner.Assert(rows.Single(row => row.StartsWith("| snapshot list", StringComparison.Ordinal)).Contains("[--cursor-file <cursor>] [--limit N] [--max-rps N]"));
        var text = new StringWriter(); TextWriter saved = Console.Out;
        try { Console.SetOut(text); TestRunner.Equal(0, DbxLight.Cli.Program.Main(["help"])); } finally { Console.SetOut(saved); }
        string[] help = text.ToString().Split('|').Where(line => line.StartsWith("snapshot ", StringComparison.Ordinal)).ToArray(); TestRunner.Equal(3, help.Length);
        foreach (string row in help) TestRunner.Assert(row.Contains("[--max-rps N] [--deadline-seconds N] --json"));
        TestRunner.Assert(help.Single(row => row.StartsWith("snapshot fetch", StringComparison.Ordinal)).Contains("[--failures <JSONL>]"));
        TestRunner.Assert(help.Single(row => row.StartsWith("snapshot list", StringComparison.Ordinal)).Contains("[--cursor-file <cursor>] [--limit N] [--max-rps N]"));
        var defaults = new SnapshotRequest("list"); TestRunner.Equal(4d, defaults.MaxRps); TestRunner.Equal(600d, defaults.DeadlineSeconds);
        TestRunner.Equal(4, defaults.Parallel); TestRunner.Equal(2000, defaults.Limit);
        RequireDocs("\u65e2\u5b9a4", "\u65e2\u5b9a600\u79d2", "\u65e2\u5b9a2000");
    }
    private static void GuideFinalSpace()
    {
        ReviewGuide(); string source = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), "src", "DbxLight.Core", "AgentGuide.cs"));
        TestRunner.Assert(source.Contains("\u3002snapshot list / metadata / fetch --json"));
        TestRunner.Assert(!source.Contains("\u3002 snapshot list / metadata / fetch --json"));
    }
}
