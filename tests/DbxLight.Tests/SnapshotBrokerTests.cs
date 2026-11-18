using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbxLight.Core;

namespace DbxLight.Tests;
internal static partial class SnapshotTests
{
    private const string Other = "OTHER_REMOTE_MARKER_NEVER_DISPLAY_6032";
    private static void BrokerTests(TestRunner runner)
    {
        runner.Test("snapshot: broker remote selection, encryption and unsupported settings", Broker);
        runner.Test("snapshot: default config discovery and repeated owned rclone renewals", DefaultBroker);
        runner.Test("snapshot: cancellation kills owned rclone helper", BrokerCancel);
        runner.Test("snapshot: no credentials in stdout/stderr/files/exceptions", Privacy);
        runner.Test("snapshot: five reader routes, one folder cursor route and two fixed hosts", Routes);
        runner.Test("snapshot: CLI/docs contract and readonly config", Documentation);
    }
    private static string Conf(string expiry) => "[other]\ntype = dropbox\ntoken = {\"access_token\":\"" + Other +
        "\",\"refresh_token\":\"discard-other\"}\n[sample]\ntype = dropbox\ntoken = {\"access_token\":\"" + Access +
        "\",\"refresh_token\":\"discard-selected\",\"expiry\":\"" + expiry + "\"}\nclient_secret = discard-secret\n";
    private static void Broker()
    {
        var f = new Fixture(); f.Write("fake.conf", Conf("2099-01-01T00:00:00Z"));
        var b = new SnapshotTokenBroker(f.Config with { RcloneConfigPath = f.PathFor("fake.conf") });
        var token = b.ReadAsync(default).GetAwaiter().GetResult();
        TestRunner.Assert(token.AccessToken == Access && !token.ToString().Contains(Access));
        foreach (var (text, code) in new[] { ("RCLONE_ENCRYPT_V0:\nignored\n", "encrypted_config"),
            ("[sample]\ntype = local\n", "unsupported_remote"), (Conf("2099-01-01T00:00:00Z") + "impersonate = sample\n", "unsupported_namespace"),
            ("[sample]\ntype = dropbox\ntoken = {broken\n", "auth_unavailable") })
        {
            f.Write("fake.conf", text);
            try { b.ReadAsync(default).GetAwaiter().GetResult(); throw new InvalidOperationException("Expected broker refusal"); }
            catch (SnapshotException e) { TestRunner.Equal(code, e.Code); TestRunner.Assert(!e.ToString().Contains(Access)); }
        }
    }
    internal static void AdminOverride()
    {
        foreach (string value in new[] { "", "false", "true" })
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.PathFor("auth"));
            string configFile = Path.Combine("auth", "fake.conf");
            f.Write(configFile, Conf("2099-01-01T00:00:00Z") + "impersonate_admin = " + value + "\n");
            var config = f.Config with { RcloneConfigPath = f.PathFor(configFile) };
            var request = f.List();
            int listCalls = 0;
            var result = DropboxSnapshotReader.RunAsync(config, request, broker: new SnapshotTokenBroker(config),
                handler: new Handler((_, _) => { listCalls++; return Task.FromResult(Page([FolderEntry()])); }))
                .GetAwaiter().GetResult();
            if (value.Length == 0)
            {
                TestRunner.Assert(result.ExitCode == 0, result.Code);
                TestRunner.Equal(1, listCalls);
                TestRunner.Assert(result.Ok && File.Exists(request.Entries));
            }
            else
            {
                Failure(result, 2, "unsupported_namespace");
                TestRunner.Equal(0, listCalls);
                TestRunner.Assert(!File.Exists(request.Entries));
            }
        }
    }

    private static (SnapshotTokenBroker Broker, string Fake) FakeRclone(string scenario)
    {
        var f = new Fixture(); string fake = f.PathFor("fake"); Directory.CreateDirectory(fake);
        string source = Path.Combine(UnitTests.RepositoryRoot(), "tests", "DbxLight.FakeRclone", "bin", "Release", "net8.0-windows");
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(fake, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(fake, "scenario.txt"), scenario);
        File.WriteAllText(Path.Combine(fake, "snapshot.conf"), Conf("2000-01-01T00:00:00Z"));
        return (new(f.Config with { RclonePath = Path.Combine(fake, "DbxLight.FakeRclone.exe"), RcloneConfigPath = null }), fake);
    }
    private static void DefaultBroker()
    {
        var (b, fake) = FakeRclone("snapshot");
        TestRunner.Assert(b.ReadAsync(default).GetAwaiter().GetResult().ExpiresUtc.Year == 2000);
        b.RenewAsync(default).GetAwaiter().GetResult();
        TestRunner.Assert(b.ReadAsync(default).GetAwaiter().GetResult().ExpiresUtc.Year == 2099);
        b.RenewAsync(default).GetAwaiter().GetResult(); // The client, not the process runner, guards each token.
        string calls = File.ReadAllText(Path.Combine(fake, "calls.jsonl"));
        TestRunner.Assert(!calls.Contains(Access) && !calls.Contains(Other));
        TestRunner.Equal(3, File.ReadAllLines(Path.Combine(fake, "calls.jsonl")).Length);
    }
    private static void BrokerCancel()
    {
        var (b, fake) = FakeRclone("snapshot-wait"); b.ReadAsync(default).GetAwaiter().GetResult();
        using var cancelled = new CancellationTokenSource(250);
        TestRunner.Throws<OperationCanceledException>(() => b.RenewAsync(cancelled.Token).GetAwaiter().GetResult());
        foreach (string line in File.ReadAllLines(Path.Combine(fake, "calls.jsonl")))
        {
            using var row = JsonDocument.Parse(line); int pid = row.RootElement.GetProperty("pid").GetInt32();
            try { using var p = Process.GetProcessById(pid); TestRunner.Assert(p.WaitForExit(5000)); } catch (ArgumentException) { }
        }
    }
    private static void Privacy()
    {
        var f = new Fixture(); var output = new StringWriter(); var errors = new StringWriter();
        TextWriter oldOut = Console.Out, oldErr = Console.Error; SnapshotResult? result = null;
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            result = f.Run(f.List(), _ => Json(new { error_summary = Access,
                error = new Dictionary<string, object> { [".tag"] = Other, ["detail"] = Access } }, HttpStatusCode.BadRequest));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        string captured = output.ToString() + errors + JsonSerializer.Serialize(result, SnapshotJson.Options);
        foreach (string file in Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories)) captured += File.ReadAllText(file);
        try { throw new SnapshotException(3, "auth_unavailable"); } catch (SnapshotException e) { captured += e.ToString(); }
        TestRunner.Assert(!captured.Contains(Access) && !captured.Contains(Other), "Credential escaped public boundary.");
    }
    private static void Routes()
    {
        string[] files = Directory.EnumerateFiles(Path.Combine(UnitTests.RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).ToArray();
        string folderClient = files.Single(file => Path.GetFileName(file) == "DropboxFolderClient.cs");
        string snapshotSource = string.Join("\n", files.Where(file => file != folderClient).Select(File.ReadAllText));
        TestRunner.Assert(RoutesIn(snapshotSource).SetEquals(new[] { SnapshotEndpoints.Account, SnapshotEndpoints.List,
            SnapshotEndpoints.Continue, SnapshotEndpoints.Metadata, SnapshotEndpoints.Download }));
        string folderSource = File.ReadAllText(folderClient);
        TestRunner.Assert(RoutesIn(folderSource).SetEquals(new[] { "files/list_folder/get_latest_cursor" }));
        string source = snapshotSource + "\n" + folderSource;
        var hosts = Regex.Matches(source, "https://[a-z.]*dropboxapi[.]com").Select(m => m.Value).ToHashSet();
        TestRunner.Assert(hosts.SetEquals(new[] { SnapshotEndpoints.ApiHost, SnapshotEndpoints.ContentHost }));
        TestRunner.Assert(source.Contains("AllowAutoRedirect = false"));

        static HashSet<string> RoutesIn(string text) =>
            Regex.Matches(text, "\"(?<route>(?:files|users)/[a-z_/]+)\"").Select(match => match.Groups["route"].Value).ToHashSet();
    }
    private static void Documentation()
    {
        string docs = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), "docs", "cli.md"));
        foreach (string word in new[] { "snapshot list", "snapshot metadata", "snapshot fetch", "bindingId", "parentPathLower",
            "contentHash", "auth_unavailable", "cursor_reset", "unsafe_destination" })
            TestRunner.Assert(docs.Contains(word), "Missing documented snapshot field: " + word);
        var f = new Fixture(); f.Write("config.json", JsonSerializer.Serialize(f.Config, JsonFiles.Options));
        string before = File.ReadAllText(f.PathFor("config.json"));
        _ = DropboxSnapshotReader.ReadConfiguration(f.PathFor("config.json")); TestRunner.Equal(before, File.ReadAllText(f.PathFor("config.json")));
        TestRunner.Assert(!Directory.EnumerateFiles(f.Root, "*.state.json").Any());
        var stdout = new StringWriter(); TextWriter previous = Console.Out;
        try
        {
            Console.SetOut(stdout);
            int exit = DbxLight.Cli.Program.Main(["--config", f.PathFor("missing"), "snapshot", "list", "sample",
                "--entries", f.PathFor("e"), "--cursor-out", f.PathFor("c"), "--json"]);
            TestRunner.Equal(2, exit);
        }
        finally { Console.SetOut(previous); }
        using var doc = JsonDocument.Parse(stdout.ToString()); TestRunner.Equal("not_configured", doc.RootElement.GetProperty("code").GetString());
    }
}
