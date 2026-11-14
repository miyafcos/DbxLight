using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void AgentFlow()
    {
        using var f = new Unit5Fixture("agent-flow");
        f.Seed("\u8cc7\u6599 \u7a7a\u767d/message.txt", "before save"); f.Start();
        var listing = f.Cli("ls", f.MountRoot, "--json"); TestRunner.Equal(0, listing.Exit);
        var search = f.Cli("find", "MESSAGE", "--in", f.MountRoot, "--json"); TestRunner.Equal(0, search.Exit);
        using var result = JsonDocument.Parse(search.Output);
        string relative = result.RootElement.GetProperty("entries").EnumerateArray().Single().GetProperty("path").GetString()!;
        DbxStatus metadataOnly = f.Controller.Status(); TestRunner.Equal(0L, metadataOnly.CacheFiles); TestRunner.Equal(0L, metadataOnly.CacheBytes);
        string local = Path.Combine(f.MountRoot, relative.Replace('/', '\\'));
        TestRunner.Equal("before save", File.ReadAllText(local));
        File.WriteAllText(local, "after agent save", new System.Text.UTF8Encoding(false));
        var wait = f.Cli("wait", local, "--timeout", "15", "--json"); TestRunner.Equal(0, wait.Exit);
        TestRunner.Equal("after agent save", File.ReadAllText(Path.Combine(f.Config.Remote, relative.Replace('/', '\\'))));
        Console.WriteLine("F4 transcript: ls=0 -> find=0 -> open exactly one file -> save -> wait=" + wait.Exit);
        Console.WriteLine("F4 wait output=" + wait.Output);
    }

    private static void JsonContracts()
    {
        using var f = new Unit5Fixture("json-contract");
        (string[] Args, int Exit)[] invalid =
        [
            (["ls"], 2), (["find"], 2), (["stat"], 2), (["wait"], 2), (["resolve"], 2),
            (["refresh", "one", "two"], 2), (["agent-guide", "--format", "xml"], 2),
            (["ls", "", "--json"], 2), (["ls", "", "--max", "1", "--max", "2"], 2),
            (["unknown-command"], 1), (["start", "--unknown"], 1), (["run", "--unknown"], 1),
            (["stop", "--unknown"], 1), (["restart", "--unknown"], 1), (["status", "--unknown"], 1),
            (["hide"], 1), (["unhide"], 1), (["hidden", "--unknown"], 1), (["folders", "one", "two"], 1),
            (["set"], 1), (["config", "other"], 1), (["doctor", "--unknown"], 1),
            (["recovery", "clear"], 1), (["hide", "ls", "--unknown"], 1)
        ];
        foreach (var test in invalid)
        {
            var result = f.Cli(test.Args.Concat(["--json"]).ToArray());
            TestRunner.Assert(test.Exit == result.Exit, string.Join(' ', test.Args) + " exit=" + result.Exit + " " + result.Output);
            using var doc = JsonDocument.Parse(result.Output); // Also rejects a second object or trailing log text.
            TestRunner.Assert(!doc.RootElement.GetProperty("ok").GetBoolean());
            TestRunner.Assert(doc.RootElement.GetProperty("code").GetString()!.Length > 0);
            TestRunner.Assert(doc.RootElement.GetProperty("message").GetString()!.Length > 0);
        }
        var formatError = f.Cli("agent-guide", "--format", "json", "--unknown");
        TestRunner.Equal(2, formatError.Exit); AssertFailure(formatError.Output, "usage");
        AgentContinuation lastPage = AgentContinuation.ForOffset(1000001);
        TestRunner.Assert(lastPage.Offset is null && lastPage.Hint.Contains("narrower", StringComparison.Ordinal));
        Console.WriteLine("JSON contract: " + (invalid.Length + 1) + " invalid calls each returned one object and documented exit.");
    }

    private static void RecoveryContracts()
    {
        using var f = new Unit5Fixture("agent-recovery"); f.Start();
        RuntimeState state = f.State.Load()!;
        Directory.CreateDirectory(Path.Combine(f.Config.CacheDir, "vfsMeta", "unknown"));
        File.WriteAllText(Path.Combine(f.Config.CacheDir, "vfsMeta", "unknown", "broken"), "{}");
        RuntimeSnapshots.Save(f.State, state with { RecoveryRequired = true, StopDisposition = StopDisposition.Forced });
        foreach (string[] args in new[] { new[] { "ls", "" }, ["find", "file"], ["stat", "file"], ["wait", "file", "--timeout", "0"], ["refresh"], ["resolve", "file"], ["agent-guide"] })
        {
            var result = f.Cli(args.Concat(["--json"]).ToArray()); TestRunner.Equal(3, result.Exit); AssertFailure(result.Output, "recovery_required");
            if (args[0] == "wait")
            {
                using var wait = JsonDocument.Parse(result.Output);
                TestRunner.Equal("unknown", wait.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
                TestRunner.Assert(wait.RootElement.GetProperty("waitedSeconds").GetDouble() >= 0);
            }
        }
        var status = f.Cli("status", "--json"); TestRunner.Equal(0, status.Exit); // Existing exit contract is preserved.
        using var doc = JsonDocument.Parse(status.Output);
        TestRunner.Assert(!doc.RootElement.GetProperty("ok").GetBoolean());
        TestRunner.Assert(doc.RootElement.GetProperty("recoveryRequired").GetBoolean());
        TestRunner.Equal("Forced", doc.RootElement.GetProperty("stopDisposition").GetString());
        TestRunner.Assert(!doc.RootElement.GetProperty("processUncertain").GetBoolean());
        TestRunner.Assert(doc.RootElement.TryGetProperty("uncertainProcess", out _) && doc.RootElement.GetProperty("recoveryLocation").ValueKind == JsonValueKind.String);
        Console.WriteLine("Recovery contract: all seven new commands returned 3; original status field names/values and exit retained.");
    }
}
