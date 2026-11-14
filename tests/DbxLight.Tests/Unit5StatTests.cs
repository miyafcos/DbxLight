using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void StatMetadata()
    {
        using var f = new Unit5Fixture("stat", writeBack: "1h", hidden: ["secret"]);
        f.Seed("folder/\u8cc7\u6599 \u7a7a\u767d.txt", "metadata only"); f.Seed("secret/private.txt"); f.Start();
        DbxStatus before = f.Controller.Status();
        var stat = f.Cli("stat", Path.Combine(f.MountRoot, "folder", "\u8cc7\u6599 \u7a7a\u767d.txt"), "--json");
        TestRunner.Equal(0, stat.Exit);
        using (var doc = JsonDocument.Parse(stat.Output))
        {
            JsonElement entry = doc.RootElement.GetProperty("entry");
            TestRunner.Equal("file", entry.GetProperty("type").GetString());
            TestRunner.Equal(13L, entry.GetProperty("size").GetInt64());
            TestRunner.Assert(!entry.GetProperty("cached").GetBoolean());
            TestRunner.Assert(entry.GetProperty("modifiedUtc").ValueKind == JsonValueKind.String);
            TestRunner.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("webUrl").ValueKind);
            TestRunner.Equal("idle", doc.RootElement.GetProperty("uploadState").GetString());
        }
        DbxStatus after = f.Controller.Status();
        TestRunner.Equal(before.CacheFiles, after.CacheFiles); TestRunner.Equal(before.CacheBytes, after.CacheBytes);
        TestRunner.Equal(3, f.Cli("stat", "secret/private.txt", "--json").Exit);
        TestRunner.Equal(4, f.Cli("stat", "missing.txt", "--json").Exit);
        TestRunner.Equal(0, f.Cli("stat", "folder", "--json").Exit);
        f.Write("pending.txt", "pending contents");
        Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Saved file has not entered the queue.");
        var pending = f.Cli("stat", "pending.txt", "--json"); TestRunner.Equal(0, pending.Exit);
        using (var doc = JsonDocument.Parse(pending.Output))
        {
            TestRunner.Assert(doc.RootElement.GetProperty("queued").GetBoolean());
            TestRunner.Assert(doc.RootElement.GetProperty("entry").GetProperty("cached").GetBoolean());
        }
        Console.WriteLine("G3 no download: before=" + before.CacheFiles + "/" + before.CacheBytes + "; after=" + after.CacheFiles + "/" + after.CacheBytes + "; output=" + stat.Output);
    }
}
