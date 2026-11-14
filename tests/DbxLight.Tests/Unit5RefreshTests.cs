using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void RefreshListing()
    {
        using var f = new Unit5Fixture("refresh", writeBack: "1h");
        f.Seed("old.txt"); f.Start();
        using (var metadata = new AgentMetadata(f.Controller)) _ = metadata.List("", new());
        f.Seed("external.txt", "new external arrival");
        using (var metadata = new AgentMetadata(f.Controller))
            TestRunner.Assert(metadata.List("", new()).Entries.All(e => e.Path != "external.txt"));
        f.Write("pending.txt", "retain pending save");
        using var rc = new RcClient(f.State.Load()!);
        Unit5Fixture.Wait(() => rc.Call("vfs/queue").GetProperty("queue").GetArrayLength() == 1, "Saved file has not entered the queue.");
        Unit5Fixture.Wait(() => f.Controller.Status().CacheBytes == System.Text.Encoding.UTF8.GetByteCount("retain pending save"),
            "Cache byte statistics have not observed the completed save.");
        string queueBefore = rc.Call("vfs/queue").GetProperty("queue")[0].GetProperty("id").ToString();
        DbxStatus before = f.Controller.Status();
        var refreshed = f.Cli("refresh", f.MountRoot, "--json");
        TestRunner.Assert(refreshed.Exit == 0, refreshed.Output + refreshed.Error);
        using (var doc = JsonDocument.Parse(refreshed.Output)) TestRunner.Equal("vfs/refresh", doc.RootElement.GetProperty("source").GetString());
        using (var metadata = new AgentMetadata(f.Controller))
            TestRunner.Assert(metadata.List("", new()).Entries.Any(e => e.Path == "external.txt"));
        TestRunner.Equal(queueBefore, rc.Call("vfs/queue").GetProperty("queue")[0].GetProperty("id").ToString());
        DbxStatus after = f.Controller.Status();
        TestRunner.Equal(before.CacheFiles, after.CacheFiles); TestRunner.Equal(before.CacheBytes, after.CacheBytes);
        TestRunner.Equal(1L, after.UploadsQueued);
        TestRunner.Equal(4, f.Cli("refresh", "missing-directory", "--json").Exit);
        Console.WriteLine("G5 external arrival visible after refresh; pending queue id retained=" + queueBefore + "; output=" + refreshed.Output);
    }
}
