using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5dTests
{
    private static void MissingPaths()
    {
        using var f = new Unit5Fixture("5d-missing", hidden: ["hidden"]);
        f.Seed("visible/leaf.txt"); f.Seed("hidden/private.txt"); f.Start();
        foreach (string target in new[] { "missing", "missing/child", "visible/missing" })
        {
            CheckMissing(f.Cli("ls", target, "--json"), "ls");
            CheckMissing(f.Cli("find", "leaf", "--in", target, "--json"), "find");
            CheckMissing(f.Cli("refresh", target, "--json"), "refresh");
        }
        foreach (string[] command in new[]
        {
            new[] { "ls", "hidden", "--json" },
            new[] { "find", "leaf", "--in", "hidden", "--json" },
            new[] { "refresh", "hidden", "--json" }
        })
        {
            var call = f.Cli(command);
            TestRunner.Equal(3, call.Exit);
            using var doc = JsonDocument.Parse(call.Output);
            TestRunner.Equal("hidden", doc.RootElement.GetProperty("code").GetString());
        }
        TestRunner.Equal(0, f.Cli("refresh", "visible", "--json").Exit);
        TestRunner.Equal(3, f.Cli("wait", "missing/leaf.txt", "--timeout", "0", "--json").Exit);
        Console.WriteLine("S2 mounted: missing paths=9/9 exit=4 code=not_found; hidden=3/3 exit=3 code=hidden; existing refresh=0.");
    }

    private static void StoppedMissingPaths()
    {
        using var f = new Unit5Fixture("5d-stopped-missing");
        CheckMissing(f.Cli("ls", "missing", "--json"), "stopped ls");
        CheckMissing(f.Cli("find", "leaf", "--in", "missing", "--json"), "stopped find");
        TestRunner.Equal(6, f.Cli("refresh", "missing", "--json").Exit);
        using var metadata = new AgentMetadata(f.Controller)
        {
            ChildrenReader = _ => throw new IOException("Injected listing failure.")
        };
        TestRunner.Throws<IOException>(() => metadata.List("", new()));
        Console.WriteLine("S2 stopped: ls/find exit=4 code=not_found; refresh retains not_running=6; unrelated I/O failure remains a failure.");
    }

    private static void CheckMissing((int Exit, string Output, string Error) call, string command)
    {
        TestRunner.Equal(4, call.Exit);
        using var doc = JsonDocument.Parse(call.Output);
        TestRunner.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        string message = doc.RootElement.GetProperty("message").GetString()!;
        TestRunner.Assert(message.Contains("current listing", StringComparison.Ordinal) &&
            message.Contains("name", StringComparison.Ordinal) && message.Contains("ls the parent", StringComparison.Ordinal));
        Console.WriteLine("S2 " + command + ": exit=4 code=not_found; message=" + message);
    }
}
