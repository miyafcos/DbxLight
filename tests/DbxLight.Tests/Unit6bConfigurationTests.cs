using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void NotConfigured()
    {
        using var f = new Unit5Fixture("u6b-not-configured");
        foreach (bool missing in new[] { false, true })
        {
            if (!missing) f.Store.Save(f.Config with { Remote = "", TestOverrides = null });
            else File.Move(f.Store.PathName, f.Store.PathName + ".saved");
            var status = f.Cli("status", "--json");
            TestRunner.Equal(0, status.Exit);
            using (var json = JsonDocument.Parse(status.Output))
            {
                TestRunner.Assert(!json.RootElement.GetProperty("ok").GetBoolean());
                TestRunner.Equal("not_configured", json.RootElement.GetProperty("code").GetString());
                TestRunner.Assert(!json.RootElement.GetProperty("running").GetBoolean());
            }
            foreach (string[] words in new[] { new[] { "ls", "" }, ["find", "sample"], ["stat", "file.txt"],
                ["resolve", "file.txt"], ["wait", "file.txt"], ["wait", "--all"], ["refresh"], ["agent-guide", "--format", "json"] })
            {
                var result = f.Cli(words.Concat(["--json"]).ToArray());
                TestRunner.Equal(2, result.Exit);
                using var json = JsonDocument.Parse(result.Output);
                TestRunner.Equal("not_configured", json.RootElement.GetProperty("code").GetString());
            }
            var guide = f.Cli("agent-guide"); TestRunner.Equal(2, guide.Exit);
            TestRunner.Assert(guide.Output.Contains("--config") && guide.Output.Contains("DBXL_CONFIG"));
            TestRunner.Assert(!guide.Output.Contains("T:") && !guide.Output.Contains(f.Config.Remote));
            Console.WriteLine("A2 " + (missing ? "missing config" : "empty remote") + ": status exit=0/ok=false/not_configured; all seven agent commands exit=2; guide has no default drive.");
        }
    }
}
