using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class SnapshotTests
{
    private static void Review3Tests(TestRunner runner, string? filter)
    {
        void Test(string id, string name, Action test)
        { if (filter is null || id.StartsWith(filter, StringComparison.Ordinal)) runner.Test("snapshot review " + id + ": " + name, test); }
        foreach (string field in new[] { "cloudPath", "id", "path" })
        foreach (string escape in new[] { "\\udc00", "\\ud800" })
        {
            string selected = field, surrogate = escape;
            Test("R01-" + selected + "-" + surrogate, "unpaired escaped surrogate is an input error before authentication", () => SurrogateInput(selected, surrogate));
        }
        Test("R03", "docs distinguish list roots from trimmed remote prefixes", () => RequireDocs(
            "list \u306e\u5bfe\u8c61\u30d5\u30a9\u30eb\u30c0\u306f\u5f93\u6765\u3069\u304a\u308a :\u30fb\\\\\u30fb\u5236\u5fa1\u6587\u5b57\u30fb\u5148\u982d\u306e /",
            "remote \u306e prefix \u306f\u3001\u5148\u982d\u3068\u672b\u5c3e\u306e / \u3092\u53d6\u308a\u9664\u3044\u305f\u3042\u3068\u306b :\u30fb\\\\\u30fb\u5236\u5fa1\u6587\u5b57\u30fb\u7a7a\u306e\u533a\u5207\u308a\u30fb.\u30fb.. \u3092\u62d2\u5426\u3059\u308b",
            "\u5148\u982d\u306e / \u306f\u30c1\u30fc\u30e0\u306e\u6839\u3092\u9078\u3076\u5370"));
        Test("R04", "docs date the sixteen-zero rev observation and exhausted retries", () => RequireDocs(
            "\u5b9f\u5728\u306e rev \u306e\u5f62\u3068\u9055\u3046 rev", "16\u6841\u306e0\uff080000000000000000\uff09",
            "2026-11-15\uff08\u65e5\u672c\u6642\u9593\uff09\u306e\u5b9f\u6e2c", "\u3084\u308a\u76f4\u3057\u30923\u56de\u4f7f\u3044\u5207\u3063\u3066 remote_error\uff084\uff09"));
        foreach (string option in new[] { "entries", "cursor-out", "output", "failures", "store" })
        {
            string selected = option;
            Test("R06-" + selected, "rclone.conf variants refuse every selectable output before authentication", () => ReservedDestination(selected));
        }
        Test("R06-store-separator", "a trailing directory separator cannot hide the reserved store name", ReservedStoreSeparator);
        Test("R06-allowed", "rclone.conf parent folders and rclone.conf.json remain allowed beside the executable", ReservedDestinationAllowed);
        Test("R06-docs", "docs prohibit the reserved output name", () => RequireDocs(
            "\u51fa\u529b\u306e\u540d\u524d\u306b rclone.conf\uff08\u5927\u6587\u5b57\u5c0f\u6587\u5b57\u3092\u554f\u308f\u306a\u3044\uff09\u306f\u4f7f\u3048\u306a\u3044"));
    }

    private static void SurrogateInput(string field, string escape)
    {
        var f = new Fixture(); var request = FetchRequest(f); var broker = new FakeBroker();
        if (field == "path")
        {
            f.Write("paths", "{\"path\":\"" + escape + "\"}\n");
            request = new("metadata", PathsFile: f.PathFor("paths"), Output: f.PathFor("output"));
        }
        else
        {
            var plan = new Dictionary<string, object> { ["id"] = "id:sample", ["rev"] = "a12345678",
                ["cloudPath"] = "sample/a", ["size"] = 3, ["contentHash"] = ContentHash(Content) };
            plan[field] = "UNPAIRED";
            f.Write("plan", JsonSerializer.Serialize(plan).Replace("UNPAIRED", field == "id" ? "id:" + escape : escape, StringComparison.Ordinal) + "\n");
        }
        string[] before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories);
        var result = f.Run(request, _ => throw new InvalidOperationException("HTTP must not start"), broker);
        Console.WriteLine($"R01 observation {field} {escape}: exit={result.ExitCode} code={result.Code} requests={result.Requests} brokerReads={broker.Reads} partials={result.PartialFiles.Count}");
        TestRunner.Equal(0, result.Requests); TestRunner.Equal(0, broker.Reads); TestRunner.Equal(0, result.PartialFiles.Count);
        TestRunner.Assert(before.SequenceEqual(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)));
        TestRunner.Assert(!Directory.Exists(request.Store) && !File.Exists(request.Output));
        Failure(result, 2, field == "path" ? "invalid_input" : "invalid_plan");
    }

    private static void ReservedDestination(string option)
    {
        foreach (string name in new[] { "rclone.conf", "RCLONE.CONF", "rclone.conf.", "rclone.conf " })
        {
            var f = new Fixture(); var broker = new FakeBroker(); string blocked = f.PathFor(name);
            SnapshotRequest request = option is "entries" or "cursor-out" ? f.List() : FetchRequest(f);
            if (option == "output") f.Write("paths", "{\"path\":\"sample/a\"}\n");
            request = option switch
            {
                "entries" => request with { Entries = blocked }, "cursor-out" => request with { CursorOut = blocked },
                "failures" => request with { Failures = blocked }, "store" => request with { Store = blocked },
                _ => new("metadata", PathsFile: f.PathFor("paths"), Output: blocked)
            };
            string[] before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories);
            var result = f.Run(request, _ => Page(), broker);
            Console.WriteLine($"R06 observation {option} {JsonSerializer.Serialize(name)}: exit={result.ExitCode} code={result.Code} requests={result.Requests} brokerReads={broker.Reads} partials={result.PartialFiles.Count}");
            Failure(result, 2, "unsafe_destination"); TestRunner.Equal(0, broker.Reads); TestRunner.Equal(0, result.Requests);
            TestRunner.Equal(0, result.PartialFiles.Count);
            TestRunner.Assert(before.SequenceEqual(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)));
            TestRunner.Assert(!File.Exists(blocked) && !Directory.Exists(blocked) && !Directory.Exists(request.Store));
        }
    }

    private static void ReservedStoreSeparator()
    {
        var f = new Fixture(); var broker = new FakeBroker();
        var result = f.Run(FetchRequest(f) with { Store = f.PathFor("rclone.conf") + Path.DirectorySeparatorChar }, _ => Download(Content), broker);
        Failure(result, 2, "unsafe_destination"); TestRunner.Equal(0, broker.Reads); TestRunner.Equal(0, result.Requests);
        TestRunner.Assert(!Directory.Exists(f.PathFor("rclone.conf")) && result.PartialFiles.Count == 0);
    }

    private static void ReservedDestinationAllowed()
    {
        var f = new Fixture(); var config = f.Config with { RclonePath = f.PathFor("rclone.exe") };
        var destination = new SnapshotDestination(config);
        foreach (string path in new[] { f.PathFor("rclone.conf/entries.jsonl"), f.PathFor("rclone.conf.json") })
            TestRunner.Equal(Path.GetFullPath(path), destination.Check(path));
        var list = f.List() with { Entries = f.PathFor("rclone.conf/entries.jsonl"), CursorOut = f.PathFor("rclone.conf.json") };
        TestRunner.Equal(0, f.Run(list, _ => Page()).ExitCode);
        var fetch = FetchRequest(f) with { Store = f.PathFor("rclone.conf/store") };
        var result = f.Run(fetch, _ => Download(Content));
        TestRunner.Equal(0, result.ExitCode); TestRunner.Assert(File.Exists(result.ResultsFile));
        TestRunner.Assert(result.ResultsFile!.StartsWith(Path.GetFullPath(fetch.Store!) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
