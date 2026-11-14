using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void SavePending(Unit5Fixture f, bool existing)
    {
        if (existing) f.Seed("shared.txt", "original cloud A");
        f.Start();
        if (existing) TestRunner.Equal("original cloud A", File.ReadAllText(Path.Combine(f.MountRoot, "shared.txt")));
        f.Write("shared.txt", "local edit A");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData &&
            (!existing || !string.IsNullOrEmpty(e.Fingerprint))), "Complete pending original was not saved.");
        f.Controller.Stop(true);
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
    }

    private static void ConflictCase(string kind)
    {
        using var f = new Unit5Fixture("u6a-conflict-" + kind, writeBack: "5m");
        SavePending(f, kind != "created");
        string? expectedCloud = kind switch { "updated" => "colleague cloud B", "created" => "colleague new file", "deleted" => null, _ => "local edit A" };
        if (kind == "deleted") File.Move(Path.Combine(f.RemoteRoot, "shared.txt"), Path.Combine(f.Root, "deleted-cloud.saved"));
        if (kind is "updated" or "created") f.Seed("shared.txt", expectedCloud!);
        f.Start(awaitRecovery: false);
        Unit5Fixture.Wait(() => !f.Controller.Status().RecoveryRequired, "Conflict copy was not confirmed.", 25);
        TestRunner.Equal(expectedCloud, File.Exists(Path.Combine(f.RemoteRoot, "shared.txt")) ? File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")) : null);
        var list = f.Controller.RescueList();
        if (kind == "unchanged") TestRunner.Equal(0, list.Count);
        else
        {
            TestRunner.Equal(1, list.Count);
            RescueRecord record = list[0];
            TestRunner.Assert(record.Conflict && record.MoveCompleted);
            TestRunner.Equal("local edit A", File.ReadAllText(record.DestinationPath));
            TestRunner.Equal(kind == "deleted" ? 0 : 1, Directory.GetFiles(f.RemoteRoot).Length);
            TestRunner.Equal(1, f.Controller.Status().RescueFiles);
            f.Controller.MarkRescueNotified([record.Id]);
            f.Controller.MarkRescueNotified([record.Id]);
            TestRunner.Assert(f.Controller.RescueList().Single().NotificationShown);
            var cli = f.Cli("rescue", "list", "--json"); TestRunner.Equal(0, cli.Exit);
            using var doc = JsonDocument.Parse(cli.Output);
            TestRunner.Equal(1, doc.RootElement.GetProperty("result").GetProperty("items").GetArrayLength());
            TestRunner.Assert(!cli.Output.Contains(f.State.Load()!.RcPassword, StringComparison.Ordinal));
            Console.WriteLine("P4 " + kind + ": original cloud preserved; complete body retained outside cache; no cloud copies; notification flag persisted.");
        }
        if (kind == "unchanged") Console.WriteLine("P4 unchanged: original path uploaded; no rescue.");
    }

    private static void ReadOnlyConflict()
    {
        using var f = new Unit5Fixture("u6a-readonly-conflict", writeBack: "5m");
        SavePending(f, true); f.Seed("shared.txt", "colleague readonly B");
        f.Store.Save(f.Store.Load() with { WriteAllowedRemotes = [] });
        f.Start(awaitRecovery: false);
        for (int i = 0; i < 3; i++) TestRunner.Assert(!f.Controller.Status().RecoveryRequired);
        var record = f.Controller.RescueList().Single();
        TestRunner.Assert(record.Conflict && record.MoveCompleted);
        TestRunner.Equal("local edit A", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal("colleague readonly B", File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")));
        Console.WriteLine("P4 readonly: original B unchanged; local body rescued; recovery resolved.");
    }

    private static void OfflineComparison()
    {
        using var f = new Unit5Fixture("u6a-offline", writeBack: "5m");
        SavePending(f, true);
        string offline = Path.Combine(f.Root, "remote-offline");
        TestRunner.Assert(MountPaths.IsSameOrBelow(Path.GetFullPath(offline), Path.GetFullPath(f.Root)));
        Directory.Move(f.RemoteRoot, offline);
        Task<int>? watcher = null; RuntimeState? newChild = null;
        using var cancel = new CancellationTokenSource();
        try
        {
            int previous = f.State.Load()!.RclonePid;
            TestRunner.Throws<RecoveryComparisonException>(() => f.Controller.Start());
            TestRunner.Equal(previous, f.State.Load()!.RclonePid);
            TestRunner.Equal(1, PendingCacheInspector.InspectStopped(f.Config.CacheDir).PendingFiles);
            TestRunner.Equal(0, f.Controller.RescueList().Count);
            watcher = Task.Run(() => f.Controller.Run(cancel.Token));
            string log = Path.Combine(f.Config.LogDir, "supervisor.log");
            Unit5Fixture.Wait(() => File.ReadAllText(log).Contains("retrying after 5 seconds", StringComparison.Ordinal), "Watcher did not defer comparison.");
            Directory.Move(offline, f.RemoteRoot);
            Unit5Fixture.Wait(() => f.Controller.Status() is { Running: true, RecoveryRequired: false }, "Watcher did not recover after connection returned.", 25);
            newChild = f.State.Load()!;
            TestRunner.Equal("local edit A", File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")));
            cancel.Cancel();
            TestRunner.Assert(watcher.Wait(15000)); TestRunner.Equal(0, watcher.Result);
            using Process? remaining = ProcessIdentity.Open(newChild); TestRunner.Assert(remaining is null);
            Console.WriteLine("P4 unavailable: start refused without moving Dirty; watcher retried after interval, then uploaded when root returned.");
        }
        finally
        {
            if (Directory.Exists(offline)) Directory.Move(offline, f.RemoteRoot);
            cancel.Cancel();
            if (watcher is not null && !watcher.IsCompleted) watcher.Wait(15000);
            if (newChild is not null)
            {
                using Process? p = ProcessIdentity.Open(newChild);
                if (p is not null) { p.Kill(); TestRunner.Assert(p.WaitForExit(10000)); }
            }
        }
    }

    private static void FingerprintTimes()
    {
        TestRunner.Equal("2026-01-01 00:00:00.123456789 +0000 UTC", RecoveryRemoteProbe.GoUtc("2026-01-01T09:00:00.123456789+09:00"));
        TestRunner.Equal("2026-01-01 00:00:00.999999999 +0000 UTC", RecoveryRemoteProbe.GoUtc("2026-01-01T00:00:00.999999999Z"));
        TestRunner.Equal("2026-01-01 00:00:00 +0000 UTC", RecoveryRemoteProbe.GoUtc("2026-01-01T00:00:00.000000000Z"));
    }

    private static void RescueWindow()
    {
        FormTests.Sta(() =>
        {
            var record = new RescueRecord { Id = "sample", CloudPath = "Sample/saved.txt",
                DestinationPath = @"D:\Sample\rescue\saved.txt", Reason = "\u7ba1\u7406\u60c5\u5831\u304c\u9014\u4e2d\u3067\u3059\u3002" };
            var snapshot = new TraySnapshot(new DbxStatus { RescueFiles = 1 }, [], [], true, null, Rescued: [record]);
            using var form = new StatusForm(); form.UpdateSnapshot(snapshot, null, new DbxConfig());
            var grid = FormTests.Field<DataGridView>(form, "rescued");
            TestRunner.Equal(1, grid.Rows.Count); TestRunner.Equal(record.CloudPath, grid.Rows[0].Cells["original"].Value?.ToString());
            TestRunner.Equal(record.DestinationPath, grid.Rows[0].Cells["destination"].Value?.ToString());
            TestRunner.Assert(grid.Columns["open"] is DataGridViewButtonColumn);
        });
    }
}
