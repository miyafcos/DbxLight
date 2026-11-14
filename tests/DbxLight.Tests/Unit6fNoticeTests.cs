using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    private static void RunNotices(Action<string, string, Action> test)
    {
        test("G5", "CLI start, restart, status text/JSON and wait report preserved files without mixing JSON values", CliNotices);
        test("G5", "resident run reports the notice on startup and returns after cancellation", RunNotice);
    }
    private static void NoticePending(Unit5Fixture f)
    {
        f.Start(); f.Write("saved.txt", "preserved notice body");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Pending pair missing.");
        f.Controller.Stop(true);
        var entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        File.WriteAllText(entry.MetadataPath, "{", new UTF8Encoding(false));
    }
    private static void CliNotices()
    {
        using var f = new Unit5Fixture("u6f-notice", writeBack: "5m"); NoticePending(f);
        var started = f.Cli("start", "--json"); TestRunner.Equal(0, started.Exit);
        f.Start(); // Track the same child for fixture cleanup.
        using var start = JsonDocument.Parse(started.Output);
        TestRunner.Equal(1, start.RootElement.GetProperty("result").GetProperty("rescueNotice").GetProperty("unacknowledged").GetInt32());
        var known = f.Controller.RescueList().Single();
        PrivateJsonFile.Write(Path.Combine(Path.GetDirectoryName(known.RecordPath)!, Guid.NewGuid().ToString("N") + ".json"), new { invalid = true });
        foreach (string command in new[] { "status", "restart" })
        {
            var reply = f.Cli(command, "--json"); TestRunner.Equal(0, reply.Exit);
            using var json = JsonDocument.Parse(reply.Output);
            var root = command == "status" ? json.RootElement : json.RootElement.GetProperty("result");
            var notice = root.GetProperty("rescueNotice");
            TestRunner.Equal(1, notice.GetProperty("unacknowledged").GetInt32());
            TestRunner.Equal(1, notice.GetProperty("unknownRecords").GetInt32());
            TestRunner.Assert(notice.GetProperty("hint").GetString()!.Contains("rescue list", StringComparison.Ordinal));
        }
        f.Start();
        foreach (string command in new[] { "start", "status" })
        {
            var reply = f.Cli(command); TestRunner.Equal(0, reply.Exit);
            TestRunner.Assert(reply.Output.Contains("rescue list", StringComparison.Ordinal) && reply.Output.Contains("未確認", StringComparison.Ordinal));
        }
        var waited = f.Cli("wait", "--all", "--timeout", "0", "--json"); TestRunner.Equal(0, waited.Exit);
        using var wait = JsonDocument.Parse(waited.Output);
        TestRunner.Assert(wait.RootElement.GetProperty("rescueScope").GetString()!.Contains("対象に入りません", StringComparison.Ordinal));
        TestRunner.Assert(f.Cli("wait", "--all", "--timeout", "0").Output.Contains("対象に入りません", StringComparison.Ordinal));
        TestRunner.Equal("preserved notice body", File.ReadAllText(known.DestinationPath));
    }
    private static void RunNotice()
    {
        using var f = new Unit5Fixture("u6f-run-notice", writeBack: "5m"); NoticePending(f);
        using var stop = new CancellationTokenSource();
        StartResult? observed = null;
        int exit = f.Controller.Run(stop.Token, result => { observed = result; stop.Cancel(); });
        TestRunner.Equal(0, exit);
        TestRunner.Equal(1, observed!.RescueNotice.Unacknowledged);
        TestRunner.Assert(!f.Controller.Status().Running);
    }
}
