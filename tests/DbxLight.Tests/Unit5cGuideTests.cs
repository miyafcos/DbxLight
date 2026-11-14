using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5cTests
{
    private static void NamesGuide()
    {
        using var f = new Unit5Fixture("5c-guide");
        foreach (bool writable in new[] { true, false })
        {
            f.Store.Save(f.Config with { WriteAllowedRemotes = writable ? [f.Config.Remote] : [] });
            string guide = AgentGuide.Create(f.Controller).Markdown;
            TestRunner.Assert(guide.Split('\n').Length <= 40);
            TestRunner.Assert(guide.Contains("skippedUnsafe>0", StringComparison.Ordinal) && guide.Contains("Windows", StringComparison.Ordinal));
            Console.WriteLine("N6 " + (writable ? "writable" : "read-only") + " guide lines=" + guide.Split('\n').Length);
        }
        foreach (string relative in new[] { "integrations/AGENTS.snippet.md", "integrations/claude-code/SKILL.md" })
        {
            string text = File.ReadAllText(Path.Combine(UnitTests.RepositoryRoot(), relative));
            TestRunner.Assert(text.TrimEnd().Split('\n').Length <= 40);
            TestRunner.Assert(text.Contains("skippedUnsafe>0", StringComparison.Ordinal) && text.Contains("Windows", StringComparison.Ordinal));
            Console.WriteLine("N6 " + relative + " lines=" + text.TrimEnd().Split('\n').Length);
        }
        var status = f.Cli("status", "--json"); TestRunner.Equal(0, status.Exit);
        using var json = System.Text.Json.JsonDocument.Parse(status.Output);
        TestRunner.Assert(!json.RootElement.GetProperty("running").GetBoolean() && json.RootElement.GetProperty("ok").GetBoolean());
        TestRunner.Assert(!json.RootElement.TryGetProperty("skippedUnsafe", out _));
        Console.WriteLine("N6 isolated status --json=" + status.Output.Trim());
    }
}
