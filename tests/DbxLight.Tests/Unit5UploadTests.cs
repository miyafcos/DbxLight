using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void WaitForUploads()
    {
        using var f = new Unit5Fixture("wait", writeBack: "2s");
        f.Seed("already.txt"); f.Start();
        f.Write("saved.txt", "agent saved content");
        var saved = f.Cli("wait", Path.Combine(f.MountRoot, "saved.txt"), "--timeout", "15", "--json");
        TestRunner.Equal(0, saved.Exit);
        using (var doc = JsonDocument.Parse(saved.Output))
        {
            TestRunner.Equal("uploaded", doc.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
            TestRunner.Assert(doc.RootElement.GetProperty("waitedSeconds").GetDouble() > 0);
        }
        TestRunner.Equal("agent saved content", File.ReadAllText(Path.Combine(f.Config.Remote, "saved.txt")));
        DbxStatus before = f.Controller.Status();
        var present = f.Cli("wait", "already.txt", "--timeout", "0", "--json");
        TestRunner.Equal(0, present.Exit);
        var missing = f.Cli("wait", "missing.txt", "--timeout", "0", "--json");
        TestRunner.Equal(3, missing.Exit); AssertFailure(missing.Output, "unknown");
        DbxStatus after = f.Controller.Status();
        TestRunner.Equal(before.CacheFiles, after.CacheFiles); TestRunner.Equal(before.CacheBytes, after.CacheBytes);
        TestRunner.Equal(0, f.Cli("wait", "--all", "--timeout", "0", "--json").Exit);
        Console.WriteLine("G4 saved: exit=" + saved.Exit + " output=" + saved.Output);
        Console.WriteLine("G4 absent-from-queue verified: exit=" + present.Exit + " output=" + present.Output);
        Console.WriteLine("G4 absent-from-cloud: exit=" + missing.Exit + " output=" + missing.Output);
    }

    private static void WaitFailures()
    {
        using (var f = new Unit5Fixture("wait-timeout", writeBack: "1h"))
        {
            f.Start(); f.Write("pending.txt", "keep this saved content");
            var timeout = f.Cli("wait", "pending.txt", "--timeout", "0", "--json");
            TestRunner.Equal(3, timeout.Exit); AssertFailure(timeout.Output, "timeout");
            using (var doc = JsonDocument.Parse(timeout.Output))
                TestRunner.Equal("queued", doc.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
            var all = f.Cli("wait", "--all", "--timeout", "0", "--json");
            TestRunner.Equal(3, all.Exit);
            using (var doc = JsonDocument.Parse(all.Output)) TestRunner.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
            TestRunner.Equal(2, f.Cli("wait", "--json").Exit);
            TestRunner.Equal(2, f.Cli("wait", "pending.txt", "--all", "--json").Exit);
            Console.WriteLine("G4 timeout: exit=" + timeout.Exit + " output=" + timeout.Output);
        }
        using (var f = new Unit5Fixture("wait-readonly", writable: false))
        {
            f.Seed("old.txt");
            var stopped = f.Cli("wait", "old.txt", "--json");
            TestRunner.Equal(6, stopped.Exit); AssertFailure(stopped.Output, "not_running");
            f.Start();
            var readOnly = f.Cli("wait", "old.txt", "--json");
            TestRunner.Equal(5, readOnly.Exit); AssertFailure(readOnly.Output, "read_only");
            Console.WriteLine("G4 stopped: exit=" + stopped.Exit + " output=" + stopped.Output);
            Console.WriteLine("G4 read-only: exit=" + readOnly.Exit + " output=" + readOnly.Output);
        }
    }

    private static void WaitFailedUpload()
    {
        using var f = new Unit5Fixture("wait-failed", writeBack: "1s");
        f.Seed("locked.txt", "old content"); f.Start();
        using var locked = new FileStream(Path.Combine(f.Config.Remote, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        f.Write("locked.txt", "new data unable to replace locked remote destination");
        using (var rc = new RcClient(f.State.Load()!))
            Unit5Fixture.Wait(() => rc.Call("vfs/queue").GetProperty("queue").EnumerateArray().Any(q => q.GetProperty("tries").GetInt32() > 0 && !q.GetProperty("uploading").GetBoolean()),
                "Expected a real local upload failure.");
        var failed = f.Cli("wait", "locked.txt", "--timeout", "0", "--json");
        TestRunner.Equal(4, failed.Exit); AssertFailure(failed.Output, "upload_failed");
        TestRunner.Assert(f.Controller.Status().Running);
        Console.WriteLine("G4 failed: exit=" + failed.Exit + " output=" + failed.Output);
    }
}
