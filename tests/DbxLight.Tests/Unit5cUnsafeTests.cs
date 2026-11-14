using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5cTests
{
    private static void UnsafeEntries()
    {
        using var f = new Unit5Fixture("5c-unsafe");
        string[] bad = ["", ".", "..", "private/name", "private\\name", "private:name", "private\nname", "private\0name", "private\u007fname"];
        MetadataItem[] input = [new("good.txt", false, 7, DateTimeOffset.UnixEpoch), .. bad.Select(n => new MetadataItem(n, false, 1, null))];
        using var metadata = new AgentMetadata(f.Controller) { ChildrenReader = _ => input };
        var previous = Console.Error; using var warnings = new StringWriter(); Console.SetError(warnings);
        try
        {
            AgentListing listed = metadata.List("safe-parent", new());
            TestRunner.Equal(9, listed.SkippedUnsafe); TestRunner.Equal("safe-parent/good.txt", listed.Entries.Single().Path);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(listed, JsonFiles.Options));
            TestRunner.Equal(9, json.RootElement.GetProperty("skippedUnsafe").GetInt32());
            TestRunner.Equal(9, metadata.Find("good", "safe-parent").SkippedUnsafe);
            TestRunner.Equal(9, metadata.Stat("safe-parent/good.txt").SkippedUnsafe);
            try { metadata.Stat("safe-parent/absent.txt"); throw new Exception("Expected not_found."); }
            catch (AgentCommandException e) { TestRunner.Equal(4, e.ExitCode); TestRunner.Equal(9, e.SkippedUnsafe); }
        }
        finally { Console.SetError(previous); }
        string[] lines = warnings.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        TestRunner.Equal(4, lines.Length);
        TestRunner.Assert(lines.All(line => line.Contains("9") && line.Contains("safe-parent") && !line.Contains("private", StringComparison.Ordinal)));
        Console.WriteLine("N2 listing/find/stat: one valid entry retained; skippedUnsafe=9; stderr per command=" + lines[0].Trim());
    }
}
