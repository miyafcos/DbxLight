using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5cTests
{
    private static void NativePagination()
    {
        using var f = new Unit5Fixture("5c-pagination", cloudNames: true, hidden: ["secret"]);
        foreach (string name in Unit5cProbe.Names) { f.Seed(name); f.Seed("dir-" + name + "/sub/leaf.txt"); }
        f.Seed("secret/private.txt"); f.Seed("omitted.skip"); f.Seed("sub/child.skip");
        f.Store.Save(f.Config with { ExcludePatterns = ["*.skip"] });
        f.Start();
        DbxStatus before = f.Controller.Status();
        string[] expected = WindowsPaths(f.MountRoot).Where(p => !p.StartsWith("secret", StringComparison.Ordinal)).ToArray();
        using var metadata = new AgentMetadata(f.Controller);
        var listed = new List<string>(); int offset = 0;
        do
        {
            AgentListing page = metadata.List("", new(Depth: 8, Max: 5, Offset: offset));
            listed.AddRange(page.Entries.Select(e => e.Path));
            TestRunner.Equal(0, page.SkippedUnsafe);
            if (!page.Truncated) break;
            offset = page.Continuation!.Offset!.Value;
        } while (true);
        TestRunner.Assert(expected.SequenceEqual(listed));
        var found = new List<string>(); offset = 0;
        do
        {
            AgentFindResult page = metadata.Find("txt", max: 3, offset: offset);
            found.AddRange(page.Entries.Select(e => e.Path));
            if (!page.Truncated) break;
            offset = page.Continuation!.Offset!.Value;
        } while (true);
        TestRunner.Assert(expected.Where(p => p.EndsWith("txt", StringComparison.Ordinal)).SequenceEqual(found));
        TestRunner.Assert(metadata.Find("txt", depth: 1).DepthLimited);
        TestRunner.Assert(!metadata.Find("txt", depth: 8).DepthLimited);
        AgentListing hidden = metadata.List("", new(Depth: 8, IncludeHidden: true));
        TestRunner.Equal("rc-remote", hidden.Source);
        TestRunner.Assert(hidden.Entries.Any(e => e.Path == "secret/private.txt" && e.Hidden));
        TestRunner.Assert(hidden.Entries.All(e => !e.Path.EndsWith(".skip", StringComparison.Ordinal)));
        DbxStatus after = f.Controller.Status();
        TestRunner.Equal(before.CacheFiles, after.CacheFiles); TestRunner.Equal(0L, after.CacheFiles);
        TestRunner.Equal(0L, after.CacheBytes); TestRunner.Equal(0L, after.DownloadActivity!.Files);
        Console.WriteLine($"N4 native DFS listing pages={listed.Count}, find pages={found.Count}; hidden/excludes/depth flags preserved; cache=0/0, downloads=0.");
    }

    private static void RealUnsafeRemoteNames()
    {
        using var f = new Unit5Fixture("5c-real-unsafe");
        f.Seed(Unit5cProbe.Names[0]); f.Seed(Unit5cProbe.Names[6]); f.Seed("good.txt");
        foreach (string[] args in new[] { new[] { "ls", "", "--json" }, ["find", "good", "--json"] })
        {
            var result = f.Cli(args); TestRunner.Equal(0, result.Exit);
            using var json = JsonDocument.Parse(result.Output);
            TestRunner.Equal("lsjson", json.RootElement.GetProperty("source").GetString());
            TestRunner.Equal(2, json.RootElement.GetProperty("skippedUnsafe").GetInt32());
            TestRunner.Equal("good.txt", json.RootElement.GetProperty("entries")[0].GetProperty("name").GetString());
            TestRunner.Assert(result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1);
            TestRunner.Assert(!result.Error.Contains(Unit5cProbe.Names[0], StringComparison.Ordinal));
            Console.WriteLine("N4 stopped " + args[0] + ": exit=0, source=lsjson, skippedUnsafe=2; " + result.Error.Trim());
        }
    }
}
