using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit8Tests
{
    internal static void Run(TestRunner runner, string? group = null)
    {
        if (group == "regressions")
        {
            UnitTests.Run(runner); TrayTests.Run(runner); Unit7Tests.Run(runner);
            Unit3aTests.Run(runner, "A3"); Unit6gTests.Run(runner, "J1");
            Unit6hTests.Run(runner, "N2"); Unit6hTests.Run(runner, "N3");
            Unit6iTests.Run(runner); Unit6sTests.Run(runner);
            return;
        }
        void Test(string id, string title, Action action) { if (group is null || group == id) runner.Test("8 " + id + ": " + title, action); }
        Test("T1", "strict tags and numeric ordering", Versions);
        Test("T1", "CLI version aliases and separate commit", VersionCli);
        Test("T2", "only explicit loopback HTTP feeds work", Feeds);
        foreach (string scenario in new[] { "new", "same", "old", "invalid-tag", "prerelease", "draft", "304", "404", "403", "json", "timeout", "missing", "external", "redirect-http", "redirect-limit" })
        { string value = scenario; Test("T3", "check " + value, () => Check(value)); }
        foreach (string scenario in new[] { "good", "sha", "digest", "size", "checksum-size", "old", "manifest-version", "schema", "file-hash", "extra", "missing-exe", "../bad", "bad/name", "bad\\name", "C:bad", "NUL.txt", "tail.", "tail ", "duplicate", "count", "expanded", "zip-size" })
        { string value = scenario; Test("T4", "validate " + value + " without changing target", () => Validation(value)); }
        foreach (string scenario in new[] { "success", "locked", "backed-up", "installed:DbxLight.exe", "verify", "rollback" })
        { string value = scenario; Test("T5", "swap " + value, () => Swap(value)); }
        Test("T5", "real owned PID exceeds shortened deadline without changes", WaitDeadline);
        Test("T6", "cleanup keeps active staging, unknown names and two newest backups", Cleanup);
        Test("T7", "legacy config defaults and preserves existing fields", Configuration);
        ResidentCases(Test);
        ReviewFixCases(runner, group);
        SecondReviewCases(runner, group);
    }
    private static string Root()
    {
        string root = Path.Combine(TestPaths.WorkRoot, "unit8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static void Versions()
    {
        foreach (string bad in new[] { "", "v1.0", "1.0.0", "v1.0.0-rc1", "v1.0.0+hash", "v01.0.0", "v-1.0.0", "v1.0.0\n" })
            TestRunner.Assert(!UpdateVersion.TryTag(bad, out _), bad);
        TestRunner.Assert(UpdateVersion.TryTag("v0.9.0", out var a) && UpdateVersion.TryTag("v0.10.0", out var b) && a.CompareTo(b) < 0);
        TestRunner.Assert(UpdateVersion.TryParse("0.10.0", out var same) && same.CompareTo(new(0, 10, 0)) == 0);
        TestRunner.Assert(new UpdateVersion(1, 0, 0).CompareTo(new(0, 99, 99)) > 0);
    }
    private static (int Exit, string Output) Cli(params string[] args)
    {
        TextWriter original = Console.Out; Encoding encoding = Console.OutputEncoding; using var output = new StringWriter();
        try { Console.SetOut(output); int exit = DbxLight.Cli.Program.Main(args); return (exit, output.ToString()); }
        finally { Console.OutputEncoding = encoding; Console.SetOut(original); }
    }
    private static void VersionCli()
    {
        foreach (string word in new[] { "version", "--version" })
        {
            var text = Cli(word); TestRunner.Equal(0, text.Exit); TestRunner.Equal(UpdateVersion.Current, text.Output.Trim());
            var json = Cli(word, "--json"); using var doc = JsonDocument.Parse(json.Output);
            var result = doc.RootElement.GetProperty("result");
            TestRunner.Equal(UpdateVersion.Current, result.GetProperty("version").GetString());
            TestRunner.Assert(!result.GetProperty("version").GetString()!.Contains('+')); TestRunner.Assert(result.TryGetProperty("commit", out _));
        }
        TestRunner.Equal(1, Cli("version", "extra", "--json").Exit);
    }
    private static void Feeds()
    {
        foreach (string host in new[] { "127.0.0.1", "localhost" })
        { var source = new UpdateSource("http://" + host + ":12345/"); TestRunner.Assert(source.TestFeed is not null); TestRunner.Equal(host, source.Latest.Host); }
        foreach (string bad in new[] { "http://example.com:12345/", "https://127.0.0.1:12345/", "http://127.0.0.1.example.com:12345/", "http://localhost.example.com:12345/", "http://127.0.0.1/", "http://127.0.0.1:0/", "http://127.0.0.1:12345@evil.test/", "http://127.0.0.1:12345/?x=1", "http://[::1]:12345/" })
            TestRunner.Assert(new UpdateSource(bad).TestFeed is null, bad);
        var saved = Environment.GetEnvironmentVariable("DBXL_UPDATE_FEED");
        try { Environment.SetEnvironmentVariable("DBXL_UPDATE_FEED", "http://localhost:23456/"); TestRunner.Assert(new UpdateSource().TestFeed is not null); }
        finally { Environment.SetEnvironmentVariable("DBXL_UPDATE_FEED", saved); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private static readonly UpdateSource Feed = new("http://127.0.0.1:34567/");
    private static HttpResponseMessage Response(HttpStatusCode status, byte[]? data = null) => new(status) { Content = new ByteArrayContent(data ?? []) };
    private static byte[] Release(byte[] zip, string version = "0.1.1", string? scenario = null)
    {
        string name = "DbxLight-" + version + "-win-x64.zip", checksum = UpdatePackage.Hash(zip) + "  " + name + "\n";
        var assets = new List<object>
        {
            new { name, browser_download_url = scenario == "external" ? "http://elsewhere.test/file.zip" : new Uri(Feed.TestFeed!, name).AbsoluteUri,
                size = zip.Length + (scenario == "size" ? 1 : 0), digest = "sha256:" + (scenario == "digest" ? new string('0',64) : UpdatePackage.Hash(zip)) }
        };
        if (scenario != "missing") assets.Add(new { name = name + ".sha256", browser_download_url = new Uri(Feed.TestFeed!, name + ".sha256").AbsoluteUri,
            size = Encoding.UTF8.GetByteCount(checksum) + (scenario == "checksum-size" ? 1 : 0), digest = (string?)null });
        return JsonSerializer.SerializeToUtf8Bytes(new { tag_name = scenario == "invalid-tag" ? "vbad" : "v" + version, draft = scenario == "draft", prerelease = scenario == "prerelease", assets });
    }
    private static void Check(string scenario)
    {
        string root = Root(); var store = new UpdateStore(Path.Combine(root, "state.json")); byte[] zip = Package("0.1.1"); int calls = 0;
        var handler = new Handler(request =>
        {
            calls++; TestRunner.Assert(request.Headers.Authorization is null);
            TestRunner.Equal("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
            TestRunner.Equal("DbxLight/0.1.0", request.Headers.UserAgent.ToString());
            if (scenario == "timeout") throw new TaskCanceledException("injected timeout");
            if (scenario is "404" or "403") return Response((HttpStatusCode)int.Parse(scenario));
            if (scenario == "json") return Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{"));
            if (scenario.StartsWith("redirect", StringComparison.Ordinal))
            { var redirect = Response(HttpStatusCode.Redirect); redirect.Headers.Location = new(scenario == "redirect-http" ? "http://elsewhere.test/" : Feed.Latest.AbsoluteUri); return redirect; }
            if (scenario == "304" && calls == 2)
            { TestRunner.Equal("\"fixture\"", request.Headers.IfNoneMatch.Single().ToString()); return Response(HttpStatusCode.NotModified); }
            string version = scenario == "same" ? "0.1.0" : scenario == "old" ? "0.0.9" : "0.1.1";
            var response = Response(HttpStatusCode.OK, Release(zip, version, scenario)); response.Headers.ETag = new("\"fixture\""); return response;
        });
        using var client = new UpdateClient(store, Feed, handler, "0.1.0");
        if (new[] { "404", "403", "json", "timeout", "missing", "external", "redirect-http", "redirect-limit" }.Contains(scenario))
        {
            Action checkFailure = () => client.CheckAsync(root).GetAwaiter().GetResult();
            if (scenario is "404" or "403") Fails<HttpRequestException>(checkFailure, scenario);
            else if (scenario == "json") Fails<JsonException>(checkFailure, "depth");
            else if (scenario == "timeout") Fails<TaskCanceledException>(checkFailure, "injected timeout");
            else if (scenario is "external" or "redirect-http") Fails<InvalidDataException>(checkFailure, "unsupported host or transport");
            else if (scenario == "missing") Fails<InvalidDataException>(checkFailure, "Required release asset");
            else Fails<HttpRequestException>(checkFailure, "Too many update redirects");
            TestRunner.Assert(store.LoadCheck().CheckedUtc is not null); TestRunner.Assert(calls <= 6); return;
        }
        var check = client.CheckAsync(root).GetAwaiter().GetResult();
        TestRunner.Equal(scenario is "new" or "304", check.UpdateAvailable); TestRunner.Assert(!check.CanApply);
        if (scenario == "304")
        {
            var again = client.CheckAsync(root).GetAwaiter().GetResult();
            TestRunner.Assert(again.Unchanged && again.UpdateAvailable); TestRunner.Equal(check.LatestVersion, again.LatestVersion);
        }
    }
    private static byte[] Package(string version, string? scenario = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["DbxLight.exe"] = Encoding.UTF8.GetBytes("tray-" + version), ["dbxl.exe"] = Encoding.UTF8.GetBytes("cli-" + version), ["payload.txt"] = Encoding.UTF8.GetBytes("data-" + version)
        };
        if (scenario == "missing-exe") files.Remove("dbxl.exe");
        if (scenario is "../bad" or "bad/name" or "bad\\name" or "C:bad" or "NUL.txt" or "tail." or "tail ") files[scenario] = [1];
        if (scenario == "count") for (int n = 0; n < 65; n++) files["extra" + n] = [1];
        var manifest = new ReleaseManifest(scenario == "schema" ? 2 : 1, scenario == "manifest-version" ? "0.2.0" : version,
            files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ReleaseFile(p.Key, p.Value.Length,
                scenario == "file-hash" ? new string('0', 64) : UpdatePackage.Hash(p.Value))).ToArray());
        files["release.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonFiles.Options);
        if (scenario == "extra") files["unlisted.txt"] = [1];
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var pair in files) { using var output = zip.CreateEntry(pair.Key).Open(); output.Write(pair.Value); }
            if (scenario == "duplicate") { using var duplicate = zip.CreateEntry("DBXL.EXE").Open(); duplicate.WriteByte(1); }
        }
        byte[] bytes = memory.ToArray();
        if (scenario == "expanded")
            for (int i = 0; i < bytes.Length - 46; i++)
                if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) { BitConverter.GetBytes((uint)(UpdatePackage.MaximumExpandedBytes + 1)).CopyTo(bytes, i + 24); break; }
        return bytes;
    }
    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(directory, p), p => Directory.Exists(p) ? "<directory>" : UpdatePackage.HashFile(p));
    private static void Same(Dictionary<string, string> before, string directory)
    {
        var after = Snapshot(directory); TestRunner.Equal(before.Count, after.Count);
        foreach (var pair in before) TestRunner.Equal(pair.Value, after[pair.Key]);
    }
    private static string Install(string root, string version = "0.1.0")
    {
        var seed = new UpdateStore(Path.Combine(root, "seed", "state.json")); string stage = UpdatePackage.Stage(Package(version), seed).Staging;
        string target = Path.Combine(root, "app"); Directory.CreateDirectory(target);
        foreach (string path in Directory.EnumerateFiles(stage)) File.Copy(path, Path.Combine(target, Path.GetFileName(path)));
        File.WriteAllText(Path.Combine(target, "unmanaged.txt"), "keep", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(target, "empty-unmanaged"));
        Directory.CreateDirectory(Path.Combine(target, "unmanaged"));
        File.WriteAllText(Path.Combine(target, "unmanaged", "keep.txt"), "untouched", new UTF8Encoding(false)); return target;
    }
    private static void Validation(string scenario)
    {
        string root = Root(), target = Install(root); var before = Snapshot(target); var store = new UpdateStore(Path.Combine(root, "state.json"));
        byte[] zip = scenario == "zip-size" ? new byte[UpdateClient.MaximumZipBytes + 1] : Package(scenario == "old" ? "0.0.9" : "0.1.1", scenario);
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal))
                return Response(HttpStatusCode.OK, Release(zip, scenario == "old" ? "0.0.9" : "0.1.1", scenario));
            if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
                return Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes((scenario == "sha" ? new string('0',64) : UpdatePackage.Hash(zip)) + "  DbxLight-0.1.1-win-x64.zip\n"));
            return Response(HttpStatusCode.OK, zip);
        });
        using var client = new UpdateClient(store, Feed, handler, "0.1.0");
        if (scenario == "good")
        { var result = client.DownloadAsync(target).GetAwaiter().GetResult(); TestRunner.Equal("0.1.1", UpdatePackage.ValidateDirectory(result.Staging).Version); }
        else
        {
            string reason = scenario switch
            {
                "sha" => "Release ZIP SHA-256", "digest" => "Release asset digest", "size" or "checksum-size" => "Release asset size",
                "old" => "No newer release", "manifest-version" or "schema" => "Invalid release manifest",
                "file-hash" => "ZIP file does not match", "extra" => "ZIP contains files outside",
                "missing-exe" => "Release is incomplete", "count" => "Invalid ZIP entry count",
                "expanded" => "Unsafe ZIP entry", "zip-size" => "Invalid release asset size", _ => "Unsafe ZIP entry"
            };
            Fails<InvalidDataException>(() => client.DownloadAsync(target).GetAwaiter().GetResult(), reason);
        }
        Same(before, target);
    }
}
