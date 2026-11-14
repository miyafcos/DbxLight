using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void FindNames()
    {
        using var f = new Unit5Fixture("find", hidden: ["secret"]);
        f.Seed("\uff21\uff22\uff23 Report.txt"); f.Seed("another report.txt");
        f.Seed("cafe\u0301 space.txt"); f.Seed("\u30ab\u3099 \u65e5\u672c\u8a9e.txt");
        f.Seed("ReportDir/inside.txt"); f.Seed("secret/report.txt"); f.Seed("level1/level2/deep target.txt");
        f.Start(); DbxStatus before = f.Controller.Status();
        using (var metadata = new AgentMetadata(f.Controller))
        {
            TestRunner.Equal("\uff21\uff22\uff23 Report.txt", metadata.Find("abc report").Entries.Single().Name);
            TestRunner.Equal(1, metadata.Find("CAF\u00c9 SPACE").Entries.Count);
            TestRunner.Equal(1, metadata.Find("\uff76\uff9e \u65e5\u672c\u8a9e").Entries.Count);
            TestRunner.Assert(metadata.Find("report").Entries.All(e => !e.Hidden && !e.Path.StartsWith("secret/", StringComparison.Ordinal)));
            TestRunner.Equal("dir", metadata.Find("report", type: "dir").Entries.Single().Type);
            var limited = metadata.Find("report", max: 1);
            TestRunner.Assert(limited.Truncated && limited.Continuation is not null);
            var next = metadata.Find("report", max: 1, offset: limited.Continuation!.Offset!.Value);
            TestRunner.Assert(!next.Entries.Select(e => e.Path).Intersect(limited.Entries.Select(e => e.Path)).Any());
            var shallow = metadata.Find("deep target", depth: 1);
            TestRunner.Assert(shallow.DepthLimited && shallow.Entries.Count == 0);
            TestRunner.Equal(1, metadata.Find("deep target", path: "level1", depth: 2).Entries.Count);
        }
        var cli = f.Cli("find", "\u30ac \u65e5\u672c\u8a9e", "--in", f.MountRoot, "--json");
        TestRunner.Equal(0, cli.Exit); using var doc = JsonDocument.Parse(cli.Output);
        TestRunner.Equal(1, doc.RootElement.GetProperty("entries").GetArrayLength());
        DbxStatus after = f.Controller.Status();
        TestRunner.Equal(before.CacheFiles, after.CacheFiles); TestRunner.Equal(before.CacheBytes, after.CacheBytes);
        Console.WriteLine("G2 normalized names and pagination passed; cache before=" + before.CacheFiles + "/" + before.CacheBytes + "; after=" + after.CacheFiles + "/" + after.CacheBytes + "; output=" + cli.Output);
    }
}
