using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class SnapshotTests
{
    private const string Access = "SNAPSHOT_ACCESS_MARKER_NEVER_DISPLAY_9183";
    internal static void Run(TestRunner runner)
    {
        runner.Test("snapshot: ordered pages and all three entry kinds", Pages);
        foreach (string fault in new[] { "unknown", "json", "fields" })
        {
            string issue = fault;
            runner.Test("snapshot: invalid page " + issue + " cannot publish cursor", () => BadPage(issue));
        }
        runner.Test("snapshot: reset returns 3 without publishing cursor", Reset);
        runner.Test("snapshot: binding and folder mismatches reject continue", Binding);
        runner.Test("snapshot: Retry-After is honored", RateRetry);
        runner.Test("snapshot: Retry-After beyond deadline fails promptly", RateDeadline);
        runner.Test("snapshot: three consecutive 429 stop", RateStop);
        runner.Test("snapshot: deadline cancels in-flight HTTP", Deadline);
        runner.Test("snapshot: cancellation cannot publish final list", Cancel);
        runner.Test("snapshot: 401 renewals are guarded per token", Unauthorized);
        runner.Test("snapshot: expiring token renews before requests", Expiry);
        runner.Test("snapshot: namespace change during renewal is rejected", RenewalBinding);
        runner.Test("snapshot: account changes after 401 reject completion", AccountChange);
        runner.Test("snapshot: team/home/explicit Path-Root and prefix", PathRoot);
        runner.Test("snapshot: mount/UNC/cache/device destinations rejected", Unsafe);
        runner.Test("snapshot: junction target resolved and refused", Reparse);
        runner.Test("snapshot: dangling junction is rejected before directory creation", DanglingReparse);
        runner.Test("snapshot: existing and colliding outputs preserved", ExistingOutputs);
        runner.Test("snapshot: invalid numeric settings are usage failures", InvalidNumbers);
        runner.Test("snapshot: metadata absence requires explicit not_found", Metadata);
        FetchTests(runner);
        BrokerTests(runner);
        ReviewTests(runner);
    }
    private sealed class Fixture
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "snapshot-tests", Guid.NewGuid().ToString("N"));
        internal DbxConfig Config { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Config = new() { Remote = "sample:/", MountPoint = "Z:", CacheDir = Path.Combine(Root, "cache"),
                RescueDir = Path.Combine(Root, "rescue"), LogDir = Path.Combine(Root, "logs") };
        }
        internal string PathFor(string name) => Path.Combine(Root, name);
        internal void Write(string name, string value) => File.WriteAllText(PathFor(name), value, new UTF8Encoding(false));
        internal SnapshotRequest List(string name = "list") => new("list", Folder: "sample", Entries: PathFor(name + ".ndjson"),
            CursorOut: PathFor(name + ".cursor"), MaxRps: 100, DeadlineSeconds: 5);
        internal SnapshotResult Run(SnapshotRequest request, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer,
            FakeBroker? broker = null, CancellationToken token = default) =>
            DropboxSnapshotReader.RunAsync(Config, request, token, broker ?? new(), new Handler(answer)).GetAwaiter().GetResult();
        internal SnapshotResult Run(SnapshotRequest request, Func<HttpRequestMessage, HttpResponseMessage> answer,
            FakeBroker? broker = null, CancellationToken token = default) => Run(request, (r, _) => Task.FromResult(answer(r)), broker, token);
    }
    private sealed class FakeBroker : ISnapshotTokenBroker
    {
        public SnapshotRemote Remote { get; set; } = new("sample", "", true, null);
        internal int Reads, Renewals;
        internal bool Expiring, ChangeBinding;
        private readonly DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1);
        public Task<SnapshotToken> ReadAsync(CancellationToken token)
        {
            Reads++;
            return Task.FromResult(new SnapshotToken(Access + (Renewals == 0 ? "" : "_renewed"),
                Expiring && Renewals == 0 ? DateTimeOffset.MinValue : expiry));
        }
        public Task RenewAsync(CancellationToken token)
        {
            Renewals++; if (ChangeBinding) Remote = Remote with { NamespaceId = "changed" };
            return Task.CompletedTask;
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(SnapshotEndpoints.Account, StringComparison.Ordinal))
                return Task.FromResult(Json(new { account_id = "sample-account", root_info = new Dictionary<string, string>
                    { [".tag"] = "team", ["root_namespace_id"] = "sample-team", ["home_namespace_id"] = "sample-home" } }));
            if (request.RequestUri.AbsolutePath.EndsWith(SnapshotEndpoints.Metadata, StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(Body(request));
                string path = body.RootElement.GetProperty("path").GetString()!;
                if (path is "/sample" or "/prefix/sample")
                    return Task.FromResult(Json(new Dictionary<string, string> { [".tag"] = "folder", ["id"] = "id:root", ["path_lower"] = path }));
            }
            return answer(request, cancellationToken);
        }
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static object FileEntry() => new Dictionary<string, object> { [".tag"] = "file", ["id"] = "id:sample", ["name"] = "A.md",
        ["path_lower"] = "/sample/a.md", ["path_display"] = "/SAMPLE/A.md", ["size"] = 3,
        ["client_modified"] = "2026-01-01T00:00:00Z", ["server_modified"] = "2026-01-01T00:00:01Z",
        ["rev"] = "a12345678", ["content_hash"] = new string('a', 64), ["is_downloadable"] = true };
    private static object FolderEntry() => new Dictionary<string, string> { [".tag"] = "folder", ["id"] = "id:folder", ["name"] = "Folder", ["path_lower"] = "/sample/folder" };
    private static object DeletedEntry() => new Dictionary<string, string> { [".tag"] = "deleted", ["name"] = "Gone", ["path_lower"] = "/sample/gone" };
    private static HttpResponseMessage Page(object[]? entries = null, bool more = false, string cursor = "PRIVATE_CURSOR_MARKER") => Json(new { entries = entries ?? [], has_more = more, cursor });
    private static void Failure(SnapshotResult result, int exit, string code)
    {
        TestRunner.Equal(exit, result.ExitCode); TestRunner.Equal(code, result.Code); TestRunner.Assert(!result.Complete && !result.Ok);
    }
    private static string Body(HttpRequestMessage request) => request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    private static void NoFinal(Fixture f, SnapshotRequest r)
    {
        TestRunner.Assert(!File.Exists(r.Entries) && !File.Exists(r.CursorOut));
        TestRunner.Assert(Directory.EnumerateFiles(f.Root, "*.partial.*").Any());
    }
    private static void Pages()
    {
        var f = new Fixture(); int n = 0; var request = f.List();
        var result = f.Run(request, r =>
        {
            using var body = JsonDocument.Parse(Body(r));
            if (++n == 1) { TestRunner.Assert(body.RootElement.GetProperty("recursive").GetBoolean()); return Page([FileEntry(), FolderEntry()], true); }
            TestRunner.Assert(r.RequestUri!.AbsolutePath.EndsWith(SnapshotEndpoints.Continue));
            TestRunner.Equal("PRIVATE_CURSOR_MARKER", body.RootElement.GetProperty("cursor").GetString()); return Page([DeletedEntry()]);
        });
        TestRunner.Equal(0, result.ExitCode); TestRunner.Equal(2, result.Pages); TestRunner.Equal(3, result.Requests);
        TestRunner.Assert(result.Files == 1 && result.Folders == 1 && result.Deleted == 1);
        var rows = File.ReadAllLines(request.Entries!).Select(s => JsonSerializer.Deserialize<SnapshotEntry>(s, SnapshotJson.Options)!).ToArray();
        TestRunner.Equal("A.md", rows[0].Name); TestRunner.Equal("/sample", rows[0].ParentPathLower); TestRunner.Assert(rows[2].Id is null);
        string envelope = JsonSerializer.Serialize(result, SnapshotJson.Options);
        TestRunner.Assert(!envelope.Contains("PRIVATE_CURSOR_MARKER") && !envelope.Contains("mountGeneration") && !envelope.Contains("apiAtomicSnapshotGuaranteed"));
    }
    private static void BadPage(string issue)
    {
        var f = new Fixture(); var r = f.List(); int n = 0;
        var result = f.Run(r, _ => ++n == 1 ? Page([FileEntry()], true) : issue switch
        {
            "json" => new(HttpStatusCode.OK) { Content = new StringContent("{invalid") },
            "unknown" => Page([new Dictionary<string, string> { [".tag"] = "future" }]),
            _ => Page([new Dictionary<string, string> { [".tag"] = "file" }])
        });
        Failure(result, 3, issue == "unknown" ? "unknown_entry" : "invalid_response"); NoFinal(f, r);
    }
    private static void Reset()
    {
        var f = new Fixture(); f.Run(f.List("seed"), _ => Page()); var r = f.List("reset") with { CursorFile = f.PathFor("seed.cursor") };
        Failure(f.Run(r, _ => Json(new { error = new Dictionary<string, string> { [".tag"] = "reset" } }, HttpStatusCode.Conflict)), 3, "cursor_reset");
        NoFinal(f, r);
    }
    private static void Binding()
    {
        var f = new Fixture(); f.Run(f.List("seed"), _ => Page()); int requests = 0;
        foreach (bool otherFolder in new[] { true, false })
        {
            var r = f.List("other" + otherFolder) with { CursorFile = f.PathFor("seed.cursor"), Folder = otherFolder ? "other" : "sample" };
            var b = new FakeBroker { Remote = new(otherFolder ? "sample" : "other", "", true, null) };
            Failure(f.Run(r, _ => { requests++; return Page(); }, b), 3, "binding_changed");
        }
        TestRunner.Equal(0, requests);
    }
    private static HttpResponseMessage Limited(int seconds)
    {
        var r = Json(new { error_summary = Access }, (HttpStatusCode)429);
        r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)); return r;
    }
    private static void RateRetry()
    {
        var f = new Fixture(); int n = 0; var watch = Stopwatch.StartNew();
        var r = f.Run(f.List(), _ => ++n == 1 ? Limited(1) : Page());
        TestRunner.Equal(0, r.ExitCode); TestRunner.Equal(1, r.Throttled); TestRunner.Assert(watch.Elapsed.TotalSeconds >= 1);
    }
    private static void RateDeadline()
    {
        var f = new Fixture(); var watch = Stopwatch.StartNew();
        Failure(f.Run(f.List() with { DeadlineSeconds = 0.5 }, _ => Limited(10)), 3, "rate_limited");
        TestRunner.Assert(watch.Elapsed.TotalSeconds < 2);
    }
    private static void RateStop()
    {
        var f = new Fixture(); var r = f.Run(f.List(), _ => Limited(0));
        Failure(r, 3, "rate_limited"); TestRunner.Equal(3, r.Throttled);
    }
    private static void Deadline()
    {
        var f = new Fixture(); var r = f.List() with { DeadlineSeconds = 0.05 };
        Failure(f.Run(r, async (_, ct) => { await Task.Delay(5000, ct); return Page(); }), 3, "timeout"); NoFinal(f, r);
    }
    private static void Cancel()
    {
        var f = new Fixture(); var r = f.List(); using var token = new CancellationTokenSource();
        Failure(f.Run(r, _ => { token.Cancel(); return Page([FileEntry()], true); }, token: token.Token), 3, "cancelled"); NoFinal(f, r);
    }
    private static void Unauthorized()
    {
        foreach (bool twice in new[] { false, true })
        {
            var f = new Fixture(); var b = new FakeBroker(); int n = 0;
            var r = f.Run(f.List(), _ => ++n == 1 || twice ? Json(new { error_summary = Access }, HttpStatusCode.Unauthorized) : Page(), b);
            if (twice) Failure(r, 3, "auth_unavailable"); else TestRunner.Equal(0, r.ExitCode);
            TestRunner.Equal(twice ? 2 : 1, b.Renewals); TestRunner.Equal(2, n);
        }
    }
    private static void Expiry()
    {
        var f = new Fixture(); var b = new FakeBroker { Expiring = true };
        TestRunner.Equal(0, f.Run(f.List(), _ => Page(), b).ExitCode); TestRunner.Equal(1, b.Renewals);
    }
    private static void RenewalBinding()
    {
        var f = new Fixture(); var b = new FakeBroker { ChangeBinding = true };
        Failure(f.Run(f.List(), _ => Json(new { }, HttpStatusCode.Unauthorized), b), 3, "binding_changed");
    }
    private sealed class ChangingAccount(FakeBroker broker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(SnapshotEndpoints.Account, StringComparison.Ordinal)
                ? Json(new { account_id = broker.Renewals == 0 ? "sample-account" : "other-account",
                    root_info = new Dictionary<string, string> { [".tag"] = "team", ["root_namespace_id"] = "sample-team", ["home_namespace_id"] = "sample-home" } })
                : Json(new { error_summary = Access }, HttpStatusCode.Unauthorized));
    }
    private static void AccountChange()
    {
        var f = new Fixture(); var broker = new FakeBroker(); var request = f.List();
        var result = DropboxSnapshotReader.RunAsync(f.Config, request, broker: broker, handler: new ChangingAccount(broker)).GetAwaiter().GetResult();
        Failure(result, 3, "binding_changed"); TestRunner.Equal(1, broker.Renewals); NoFinal(f, request);
    }
    private static void PathRoot()
    {
        foreach (var (team, ns, expected) in new[] { (true, (string?)null, "sample-team"), (false, (string?)null, "sample-home"), (false, "explicit", "explicit") })
        {
            var f = new Fixture(); var b = new FakeBroker { Remote = new("sample", "prefix", team, ns) };
            TestRunner.Equal(0, f.Run(f.List(), r =>
            {
                using var h = JsonDocument.Parse(r.Headers.GetValues("Dropbox-API-Path-Root").Single());
                TestRunner.Equal("namespace_id", h.RootElement.GetProperty(".tag").GetString());
                TestRunner.Equal(expected, h.RootElement.GetProperty("namespace_id").GetString());
                using var body = JsonDocument.Parse(Body(r)); TestRunner.Equal("/prefix/sample", body.RootElement.GetProperty("path").GetString()); return Page();
            }, b).ExitCode);
        }
    }
    private static void Unsafe()
    {
        var f = new Fixture();
        foreach (string path in new[] { "Z:/blocked", @"\\server\share\blocked", Path.Combine(f.Config.CacheDir, "file"), @"\\?\C:\file", @"C:\sample:stream" })
        {
            var b = new FakeBroker(); Failure(f.Run(f.List() with { Entries = path }, _ => Page(), b), 2, "unsafe_destination"); TestRunner.Equal(0, b.Reads);
        }
    }
    private static void Reparse()
    {
        var f = new Fixture(); string target = f.PathFor("cache"), link = f.PathFor("junction"); Directory.CreateDirectory(target);
        using var p = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, ArgumentList = { "/c", "mklink", "/J", link, target } })!;
        p.WaitForExit(); TestRunner.Equal(0, p.ExitCode);
        Failure(f.Run(f.List() with { Entries = Path.Combine(link, "file") }, _ => Page()), 2, "unsafe_destination");
        TestRunner.Assert(Directory.Exists(link));
        var guard = new SnapshotDestination(f.Config with { CacheDir = link });
        TestRunner.Throws<SnapshotException>(() => guard.Check(Path.Combine(target, "direct")));
    }
    private static void DanglingReparse()
    {
        var f = new Fixture(); string target = f.PathFor("cache"), link = f.PathFor("dangling");
        using var p = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, target } })!;
        p.WaitForExit(); TestRunner.Equal(0, p.ExitCode);
        var broker = new FakeBroker();
        Failure(f.Run(f.List() with { Entries = Path.Combine(link, "file") }, _ => Page(), broker), 2, "unsafe_destination");
        TestRunner.Equal(0, broker.Reads); TestRunner.Assert(!Directory.Exists(target));
    }
    private static void ExistingOutputs()
    {
        var f = new Fixture(); f.Write("existing", "preserve");
        Failure(f.Run(f.List() with { Entries = f.PathFor("existing") }, _ => Page()), 2, "destination_exists");
        TestRunner.Equal("preserve", File.ReadAllText(f.PathFor("existing")));
        var r = f.List(); Failure(f.Run(r with { CursorOut = r.Entries }, _ => Page()), 2, "unsafe_destination");
    }
    private static void InvalidNumbers()
    {
        foreach (double seconds in new[] { 0, -1, double.NaN, double.PositiveInfinity, 86401 })
        {
            var f = new Fixture(); var b = new FakeBroker();
            Failure(f.Run(f.List() with { DeadlineSeconds = seconds }, _ => Page(), b), 2, "usage");
            TestRunner.Equal(0, b.Reads);
        }
    }
    private static void Metadata()
    {
        foreach (string response in new[] { "not_found", "permission", "unknown" })
        {
            var f = new Fixture(); f.Write("paths", "{\"path\":\"sample/a\"}\n{\"path\":\"sample/missing\"}\n");
            var r = new SnapshotRequest("metadata", PathsFile: f.PathFor("paths"), Output: f.PathFor("out"), MaxRps: 100, DeadlineSeconds: 5); int n = 0;
            var result = f.Run(r, _ => ++n == 1 ? Json(new Dictionary<string, string> { [".tag"] = "file", ["id"] = "id:sample" }) :
                response == "unknown" ? Json(new Dictionary<string, string> { [".tag"] = "future" }) :
                Json(new { error_summary = "path/not_found/" + Access, error = response == "not_found" ?
                    new Dictionary<string, object> { [".tag"] = "path", ["path"] = new Dictionary<string, string> { [".tag"] = "not_found" } } :
                    new Dictionary<string, object> { [".tag"] = "access_denied" } }, HttpStatusCode.Conflict));
            if (response == "not_found")
            {
                TestRunner.Equal(0, result.ExitCode); using var no = JsonDocument.Parse(File.ReadAllLines(r.Output!)[1]); TestRunner.Assert(!no.RootElement.GetProperty("exists").GetBoolean());
            }
            else { Failure(result, response == "unknown" ? 3 : 4, response == "unknown" ? "unknown_entry" : "permission_denied"); TestRunner.Assert(!File.Exists(r.Output)); }
        }
    }
}
