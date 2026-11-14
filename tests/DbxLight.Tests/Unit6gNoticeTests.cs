using System.Diagnostics;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6gTests
{
    private static void RunNotices(Action<string, string, Action> test)
    {
        foreach (bool json in new[] { false, true })
        foreach (bool restart in new[] { false, true })
            test("J5", "cancel after one preserved pair reports actual notice; json=" + json + "; restart=" + restart,
                () => CancelledRunNotice(json, restart, false));
        test("J5", "unreadable final state reports unknown counts rather than zero", () => CancelledRunNotice(true, false, true));
    }

    private static void PendingRunPair(Unit5Fixture f, bool kill)
    {
        f.Write("one.txt", "run saved one"); f.Write("two.txt", "run saved two");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == 2, "Two run pairs missing.");
        if (kill)
        {
            using var child = ProcessIdentity.Open(f.State.Load()!)!;
            child.Kill(); TestRunner.Assert(child.WaitForExit(10000));
        }
        else f.Controller.Stop(true);
        foreach (var entry in PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Where(e => e.Dirty == true))
            File.WriteAllText(entry.MetadataPath, "{");
    }

    private static void CancelledRunNotice(bool json, bool restart, bool unreadable)
    {
        using var f = new Unit5Fixture("u6g-cancelled-run-" + json + "-" + restart + "-" + unreadable, writeBack: "5m");
        string configText = File.ReadAllText(f.Store.PathName);
        bool cancelled = false, killed = false;
        RuntimeState? owned = null;
        if (!restart) { f.Start(); PendingRunPair(f, false); }
        else f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage != "mount-ready" || killed) return;
            killed = true; owned = state;
            PendingRunPair(f, true);
        };
        RescueStore.MoveCheckpoint = (stage, _) =>
        {
            if (stage != "after-data-move" || cancelled) return;
            cancelled = true;
            TestRunner.Assert(f.Controller.Stop().PreparationCancellationRequested);
            if (unreadable) File.WriteAllText(f.Store.PathName, "{");
        };
        try
        {
            var reply = Unit3cTests.Capture(() => restart
                ? (int)typeof(DbxLight.Cli.Program).GetMethod("RunResident", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(null, [f.Controller, json, CancellationToken.None])!
                : DbxLight.Cli.Program.Main(["--config", f.Store.PathName, "--state", f.State.PathName, "run", .. json ? new[] { "--json" } : []]));
            TestRunner.Equal(0, reply.Exit); TestRunner.Assert(cancelled && (!restart || killed));
            if (json)
            {
                using var document = JsonDocument.Parse(reply.Output);
                var root = document.RootElement;
                TestRunner.Equal("cancelled", root.GetProperty("code").GetString());
                TestRunner.Assert(root.GetProperty("preparationCancelled").GetBoolean());
                TestRunner.Equal(!unreadable, root.GetProperty("rescueCountsKnown").GetBoolean());
                var notice = root.GetProperty("rescueNotice");
                if (unreadable) TestRunner.Equal(JsonValueKind.Null, notice.GetProperty("unacknowledged").ValueKind);
                else TestRunner.Equal(1, notice.GetProperty("unacknowledged").GetInt32());
                TestRunner.Assert(notice.GetProperty("hint").GetString()!.Contains("rescue list", StringComparison.Ordinal));
            }
            else TestRunner.Assert(reply.Output.Contains("=1 \u4ef6", StringComparison.Ordinal) &&
                reply.Output.Contains("rescue list", StringComparison.Ordinal) && reply.Output.Contains("\u53d6\u308a\u6d88", StringComparison.Ordinal));
            if (unreadable) File.WriteAllText(f.Store.PathName, configText, new System.Text.UTF8Encoding(false));
            TestRunner.Equal(1, f.Controller.RescueList().Count(r => r.MoveCompleted));
            TestRunner.Equal(1, PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Count(e => !e.MetadataValid));
            TestRunner.Assert(!f.Controller.Status().Running);
            Console.WriteLine("J5 run: cancelled; preserved=1; remaining=1; countsKnown=" + !unreadable + "; restart=" + restart + "; json=" + json);
        }
        finally
        {
            RescueStore.MoveCheckpoint = null; f.Controller.StartupCheckpoint = null;
            if (unreadable) File.WriteAllText(f.Store.PathName, configText, new System.Text.UTF8Encoding(false));
            f.Controller.Stop(true);
            if (owned is not null) { using var process = ProcessIdentity.Open(owned); TestRunner.Assert(process is null); }
        }
    }
}
