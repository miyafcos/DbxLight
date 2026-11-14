using System.Reflection;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static class Unit6nTests
{
    private const string BusyMessage = "Another DbxLight operation is running. Retry in a few seconds.";
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == "all" || issue == id) runner.Test(id + ": " + name, action); }
        Test("T1", "all read-only CLI entries return busy, preserve status shape and never verify wait targets", BusyReadCommands);
        Test("T1", "unrelated timeouts remain failed for readers and operations", OtherTimeouts);
        Test("T1", "precise control timeouts leave operation exit, code and message unchanged", BusyOperationFailures);
        Test("T2", "refused normal stop reports self-clearing confirmation without changing code or exit", () => RecoveryMessages(false));
        Test("T2", "forced stopped mount retains human recovery guidance", () => RecoveryMessages(true));
        Test("T2", "uncertain process retains human verification guidance", UncertainRecoveryMessage);
        Test("T3", "busy retry guidance fits the forty-line guide and both templates", BusyGuidance);
    }

    private static (int Exit, string Output, string Error) Cli(MountController controller, params string[] args) =>
        Unit3cTests.Capture(() => (int)typeof(DbxLight.Cli.Program)
            .GetMethod("MainCore", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [args, controller])!);

    private static void BusyReadCommands()
    {
        using var f = new Unit5Fixture("u6n-busy");
        f.Seed("one.txt");
        string[][] commands = [["ls", ""], ["find", "one"], ["stat", "one.txt"],
            ["wait", "one.txt", "--timeout", "0"], ["wait", "--all", "--timeout", "0"],
            ["refresh"], ["resolve", "one.txt"], ["agent-guide"], ["agent-guide", "--format", "json"],
            ["rescue", "list"], ["hidden"], ["folders"], ["config", "show"], ["doctor"], ["status"]];
        var before = commands.Select(c => Cli(f.Controller, [.. c, "--json"])).ToArray();
        foreach (Action read in new Action[] { () => _ = f.Controller.Status(), () => _ = f.Controller.ReadRuntime(), () => _ = f.Controller.ReadAgentRuntime() })
            TestControlGate.WhileHeld(() => { TestRunner.Throws<ControlBusyException>(read); return 0; });
        foreach (string[] command in commands)
        {
            var result = TestControlGate.WhileHeld(() => Cli(f.Controller, [.. command, "--json"]));
            TestRunner.Equal(command[0] == "status" ? 0 : 3, result.Exit);
            using var json = JsonDocument.Parse(result.Output);
            var value = json.RootElement;
            TestRunner.Assert(!value.GetProperty("ok").GetBoolean());
            TestRunner.Equal("busy", value.GetProperty("code").GetString());
            TestRunner.Equal(BusyMessage, value.GetProperty("message").GetString());
            if (command[0] == "status")
                foreach (var field in JsonSerializer.SerializeToElement(new DbxStatus(), JsonFiles.Options).EnumerateObject())
                    TestRunner.Equal(JsonValueKind.Null, value.GetProperty(field.Name).ValueKind);
            if (command[0] == "wait")
                foreach (var item in value.GetProperty("items").EnumerateArray())
                {
                    TestRunner.Equal("unknown", item.GetProperty("state").GetString());
                    TestRunner.Assert(!item.GetProperty("verified").GetBoolean());
                }
            var text = TestControlGate.WhileHeld(() => Cli(f.Controller, command));
            TestRunner.Equal(command[0] == "status" ? 0 : 3, text.Exit);
            TestRunner.Assert((text.Output + text.Error).Contains(BusyMessage, StringComparison.Ordinal));
            Console.WriteLine("T1 " + string.Join(' ', command) + ": exit=" + result.Exit + "; code=busy; text=retry; unknown status values=null");
        }
        for (int i = 0; i < commands.Length; i++)
        {
            var after = Cli(f.Controller, [.. commands[i], "--json"]);
            TestRunner.Equal(before[i].Exit, after.Exit);
            using var prior = JsonDocument.Parse(before[i].Output); using var current = JsonDocument.Parse(after.Output);
            TestRunner.Equal(prior.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null,
                current.RootElement.TryGetProperty("code", out code) ? code.GetString() : null);
        }
        Console.WriteLine("T1 core readers still throw; all uncontended exit/code pairs unchanged; no timed sleeps.");
    }

    private static void OtherTimeouts()
    {
        using var f = new Unit5Fixture("u6n-other-timeout");
        RuntimeSnapshots.Save(f.State, new RuntimeState { ActiveConfig = f.Config, ConfigPath = f.Store.PathName,
            RecoveryRequired = false, StopDisposition = StopDisposition.Clean });
        var controller = new MountController(f.Store, f.State, processQuery: _ => throw new TimeoutException("Synthetic rc timeout."));
        foreach (string command in new[] { "stat", "status", "stop", "start", "restart" })
        {
            string[] args = command == "stat" ? [command, "one.txt", "--json"] : [command, "--json"];
            var reply = Cli(controller, args);
            TestRunner.Equal(command == "stat" ? 4 : 1, reply.Exit);
            using var json = JsonDocument.Parse(reply.Output);
            TestRunner.Equal("failed", json.RootElement.GetProperty("code").GetString());
        }
        Console.WriteLine("T1 unrelated TimeoutException: stat=4/failed; status, stop, start, restart=1/failed.");
    }

    private static void RecoveryMessages(bool forced)
    {
        using var f = new Unit5Fixture("u6n-recovery-" + forced, writeBack: "5m");
        f.Start(); f.Write("pending.txt", "retained local content");
        Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Pending body missing.");
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Stop());
        TestRunner.Equal(StopDisposition.StopUncertain, f.State.Load()!.StopDisposition);
        if (forced) f.Controller.Stop(true);
        foreach (string command in new[] { "wait", "stat", "status" })
        {
            string[] args = command == "wait" ? [command, "pending.txt", "--timeout", "0"] :
                command == "stat" ? [command, "pending.txt"] : [command];
            var result = Cli(f.Controller, [.. args, "--json"]);
            TestRunner.Equal(command == "status" ? 0 : 3, result.Exit);
            using var json = JsonDocument.Parse(result.Output);
            var value = json.RootElement;
            TestRunner.Assert(!value.GetProperty("ok").GetBoolean());
            TestRunner.Equal("recovery_required", value.GetProperty("code").GetString());
            var status = command == "status" ? value : value.GetProperty("status");
            TestRunner.Equal(!forced, status.GetProperty("running").GetBoolean());
            TestRunner.Assert(status.GetProperty("recoveryRequired").GetBoolean() && !status.GetProperty("processUncertain").GetBoolean());
            string message = value.GetProperty("message").GetString()!;
            var text = Cli(f.Controller, args);
            TestRunner.Equal(result.Exit, text.Exit);
            if (!forced)
            {
                TestRunner.Equal("Uploads from before the last stop are still being confirmed. This clears by itself when they finish; retry in a few seconds. If it persists, tell a person.", message);
                TestRunner.Assert((text.Output + text.Error).Contains(message, StringComparison.Ordinal));
            }
            else
            {
                TestRunner.Assert(!message.Contains("clears by itself", StringComparison.Ordinal));
                if (command != "status") TestRunner.Equal("Recovery or process verification is incomplete; tell a person. Do not clear recovery automatically.", message);
                else TestRunner.Equal(status.GetProperty("recoveryLocation").GetString(), message);
            }
            if (command == "wait")
                TestRunner.Assert(!value.GetProperty("items")[0].GetProperty("verified").GetBoolean());
            Console.WriteLine($"T2 forced={forced}; {command}: exit={result.Exit}; code=recovery_required; message={message}");
        }
    }

    private static void BusyOperationFailures()
    {
        using var f = new Unit5Fixture("u6n-busy-operations");
        RuntimeSnapshots.Save(f.State, new RuntimeState { ActiveConfig = f.Config, ConfigPath = f.Store.PathName,
            RecoveryRequired = false, StopDisposition = StopDisposition.Clean });
        var original = new MountController(f.Store, f.State,
            processQuery: _ => throw new TimeoutException("Another DbxLight operation is still running."));
        var controller = new MountController(f.Store, f.State, processQuery: _ => throw new ControlBusyException());
        foreach (string command in new[] { "start", "stop", "restart" })
        {
            var before = Cli(original, command, "--json");
            var result = Cli(controller, command, "--json");
            TestRunner.Equal(1, result.Exit);
            TestRunner.Equal(before.Exit, result.Exit);
            using var prior = JsonDocument.Parse(before.Output); using var json = JsonDocument.Parse(result.Output);
            TestRunner.Equal("failed", json.RootElement.GetProperty("code").GetString());
            foreach (string field in new[] { "code", "message", "failureClass" })
                TestRunner.Equal(prior.RootElement.GetProperty(field).GetRawText(), json.RootElement.GetProperty(field).GetRawText());
        }
        Console.WriteLine("T1 precise control timeout on start/stop/restart: exit=1; code=failed; original message retained; no 45-second sleep.");
    }

    private static void UncertainRecoveryMessage()
    {
        using var f = new Unit5Fixture("u6n-uncertain");
        RuntimeSnapshots.Save(f.State, new RuntimeState { ActiveConfig = f.Config, ConfigPath = f.Store.PathName,
            RecoveryRequired = true, StopDisposition = StopDisposition.Forced });
        var controller = new MountController(f.Store, f.State,
            processQuery: _ => throw new ProcessIdentityUnknownException("Synthetic process identity is unknown."));
        foreach (string command in new[] { "wait", "stat", "status" })
        {
            string[] args = command == "status" ? [command, "--json"] : [command, "pending.txt", "--json"];
            var result = Cli(controller, args);
            TestRunner.Equal(command == "status" ? 0 : 3, result.Exit);
            using var json = JsonDocument.Parse(result.Output);
            TestRunner.Equal("process_uncertain", json.RootElement.GetProperty("code").GetString());
            TestRunner.Assert(!json.RootElement.GetProperty("message").GetString()!.Contains("clears by itself", StringComparison.Ordinal));
        }
        Console.WriteLine("T2 process uncertainty: wait/stat=3, status=0; process_uncertain; human verification retained.");
    }

    private static void BusyGuidance()
    {
        using var f = new Unit5Fixture("u6n-guide");
        foreach (bool writable in new[] { false, true })
        {
            f.Store.Save(f.Config with { WriteAllowedRemotes = writable ? [f.Config.Remote] : [] });
            string guide = AgentGuide.Create(f.Controller).Markdown;
            TestRunner.Assert(guide.Split('\n').Length <= 40 && guide.Contains("- busy ", StringComparison.Ordinal));
            Console.WriteLine($"T3 generated guide: writable={writable}; lines={guide.Split('\n').Length}; busy retry present.");
        }
        foreach (string name in new[] { "integrations/claude-code/SKILL.md", "integrations/AGENTS.snippet.md" })
        {
            string text = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), name));
            TestRunner.Assert(text.TrimEnd().Split('\n').Length <= 40 && text.Contains("- busy ", StringComparison.Ordinal));
            Console.WriteLine($"T3 {name}: lines={text.TrimEnd().Split('\n').Length}; busy retry present.");
        }
    }
}
