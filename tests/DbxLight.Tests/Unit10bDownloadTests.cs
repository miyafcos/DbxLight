using System.Diagnostics;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class Unit10bDownloadTests
{
    private const long MB = 1024 * 1024;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly DbxConfig Config = new() { Remote = "sample:" };
    private const string Sample = "見本.pdf";

    internal static void Run(TestRunner runner)
    {
        runner.Test("10b P1: RC download parsing, icon, tooltip, amount and ETA", Presentation);
        runner.Test("10b P1: the status form shows and clears the live rows", StatusRows);
        runner.Test("10b P1: size, elapsed time, ETA, duplicate notices and bulk warnings", Notices);
        runner.Test("10b P1: download polls fit between the existing full refreshes", Polling);
    }

    private static object Transfer(string name, long bytes, long? size, double? eta = null, double? speed = null,
        string src = "sample:", string dst = "", bool checkedFile = false) =>
        new { name, bytes, size, eta, speed, srcFs = src, dstFs = dst, @checked = checkedFile };

    private static DownloadObservation Observe(params object[] transfers) => DownloadMonitor.Observe(null, Config, "sample:",
        JsonSerializer.SerializeToElement(new { transferred = Array.Empty<object>() }),
        JsonSerializer.SerializeToElement(new { transfers = 0, transferring = transfers }), Now);

    private static TraySnapshot Snapshot(DownloadObservation observation) =>
        new(new DbxStatus { Running = true, Ready = true, DownloadProgress = observation.Progress, DownloadActivity = observation.Activity },
            [], [], true, null, Identity: new MountIdentity(1, Now.UtcDateTime));

    private static void Presentation()
    {
        var observation = Observe(
            Transfer("資料/" + Sample, 120 * MB, 480 * MB, 9),
            Transfer("資料/" + Sample, 90 * MB, 480 * MB, 11),
            Transfer("upload", 60 * MB, 480 * MB, src: "local:", dst: "sample:"),
            Transfer("copy", 60 * MB, 480 * MB, dst: "somewhere:"),
            Transfer("outside", 60 * MB, 480 * MB, src: "foreign:"),
            Transfer("check", 60 * MB, 480 * MB, checkedFile: true));
        var snapshot = Snapshot(observation);
        var file = observation.Progress!.Files.Single();
        TestRunner.Equal(@"資料\" + Sample, file.Name);
        TestRunner.Equal(120 * MB, file.Bytes);
        TestRunner.Equal(480 * MB, file.Size);
        TestRunner.Equal(9d, file.RemainingSeconds);
        TestRunner.Equal("ダウンロード中: 見本.pdf 120 / 480 MB", TrayPresentation.Tooltip(snapshot, Config));
        TestRunner.Equal(TrayState.Downloading, TrayPresentation.State(snapshot));
        using var icon = TrayIconFactory.Draw(TrayState.Downloading, 32);
        using var runningIcon = TrayIconFactory.Draw(TrayState.Running, 32);
        TestRunner.Equal(TrayIconFactory.ColorFor(TrayState.Downloading).ToArgb(), icon.GetPixel(16, 19).ToArgb());
        TestRunner.Equal(Color.White.ToArgb(), runningIcon.GetPixel(16, 19).ToArgb());

        var multiple = Snapshot(Observe(Transfer("資料/案?.pdf", 120 * MB, 480 * MB),
            Transfer("第２版.pdf", 60 * MB, 120 * MB, speed: 10 * MB)));
        TestRunner.Equal("ダウンロード中: 2 件 180 / 600 MB", TrayPresentation.Tooltip(multiple, Config));
        TestRunner.Equal(@"資料\案" + '\uf03f' + ".pdf", multiple.Status.DownloadProgress!.Files.Single(f => f.Name.Contains('\uf03f')).Name);
        TestRunner.Equal(6d, multiple.Status.DownloadProgress.Files.Single(f => f.Name == "第２版.pdf").RemainingSeconds);
        var unknown = Snapshot(Observe(Transfer("sample.bin", MB, null)));
        TestRunner.Equal("ダウンロード中: sample.bin 1 / 不明 MB", TrayPresentation.Tooltip(unknown, Config));
        TestRunner.Equal("確認できません", DownloadPresentation.Remaining(unknown.Status.DownloadProgress!.Files[0].RemainingSeconds));

        string name = string.Concat(Enumerable.Repeat("\U0001f4c4", 90));
        var longName = Snapshot(Observe(Transfer(name, 120 * MB, 480 * MB)));
        using var tray = new NotifyIcon { Text = TrayPresentation.Tooltip(longName, Config) };
        TestRunner.Assert(tray.Text.Length <= 127 && tray.Text.EndsWith("… 120 / 480 MB", StringComparison.Ordinal));
        _ = new System.Text.UTF8Encoding(false, true).GetBytes(tray.Text);
        TestRunner.Assert(!JsonSerializer.Serialize(snapshot.Status).Contains(".pdf", StringComparison.Ordinal));
        TestRunner.Assert(!JsonSerializer.Serialize(observation.State).Contains(".pdf", StringComparison.Ordinal));
        TestRunner.Equal(TrayState.Failed, TrayPresentation.State(snapshot with { Status = snapshot.Status with { ErroredFiles = 1 } }));
    }

    private static void StatusRows() => FormTests.Sta(() =>
    {
        using var form = new StatusForm();
        var data = Snapshot(Observe(Transfer(Sample, 120 * MB, 480 * MB, 32.2), Transfer("sample.bin", MB, null)));
        form.UpdateSnapshot(data, null, Config);
        form.Show();
        var grid = FormTests.Field<DataGridView>(form, "downloads");
        TestRunner.Equal("ダウンロード中", grid.AccessibleName);
        TestRunner.Equal(2, grid.RowCount);
        var pdf = grid.Rows.Cast<DataGridViewRow>().Single(row => Equals(row.Cells[0].Value, Sample));
        TestRunner.Equal("120 / 480 MB", pdf.Cells[1].Value);
        TestRunner.Equal("約 33 秒", pdf.Cells[2].Value);
        TestRunner.Assert(grid.Visible && grid.Height >= grid.ColumnHeadersHeight + grid.Rows.GetRowsHeight(DataGridViewElementStates.Visible));
        var one = Snapshot(Observe(Transfer(Sample, 120 * MB, 480 * MB, 32.2)));
        form.UpdateDownloadProgress(one, Config);
        grid.Rows[0].Height = 60;
        form.UpdateDownloadProgress(one, Config);
        TestRunner.Assert(grid.Height >= grid.ColumnHeadersHeight + grid.Rows[0].Height);
        form.UpdateDownloadProgress(Snapshot(Observe()), Config);
        TestRunner.Equal(0, grid.RowCount);
        TestRunner.Assert(!grid.Visible);
    });

    private static void Notices()
    {
        var tracker = new LongDownloadNoticeTracker();
        var shortTime = Snapshot(Observe(Transfer(Sample, MB, 20 * MB, 15)));
        TestRunner.Assert(tracker.Observe(shortTime, Now) is null);
        TestRunner.Assert(tracker.Observe(shortTime, Now.AddSeconds(2.9)) is null);
        var nearEnd = Snapshot(Observe(Transfer(Sample, 19 * MB, 20 * MB, 1)));
        TestRunner.Assert(tracker.Observe(nearEnd, Now.AddSeconds(3)) is null);
        var small = Snapshot(Observe(Transfer(Sample, MB, 2 * MB, 15)));
        TestRunner.Assert(tracker.Observe(small, Now.AddSeconds(3)) is null);
        var large = Snapshot(Observe(Transfer(Sample, 4 * MB, 20 * MB, 12)));
        var warning = large with { Status = large.Status with { DownloadActivity = large.Status.DownloadActivity! with { Exceeded = true } } };
        TestRunner.Assert(tracker.Observe(warning, Now.AddSeconds(3)) is null);
        TestRunner.Equal(TrayState.Attention, TrayPresentation.State(warning));
        TestRunner.Equal("ダウンロードが多い 1 件 (読み取り専用)", TrayPresentation.Tooltip(warning, Config));
        TestRunner.Equal(Sample, tracker.Observe(large, Now.AddSeconds(4))?.Name);
        TestRunner.Assert(tracker.Observe(large, Now.AddSeconds(20)) is null);
        TestRunner.Assert(tracker.Observe(Snapshot(Observe()), Now.AddSeconds(21)) is null);
        var alreadySlow = Snapshot(Observe(Transfer(Sample, 4 * MB, 20 * MB, 12, speed: MB)));
        TestRunner.Equal(Sample, tracker.Observe(alreadySlow, Now.AddSeconds(22))?.Name);
        var stalled = Snapshot(Observe(Transfer(Sample, 0, 20 * MB)));
        var fresh = new LongDownloadNoticeTracker();
        TestRunner.Assert(fresh.Observe(stalled, Now) is null);
        TestRunner.Equal(Sample, fresh.Observe(stalled, Now.AddSeconds(3))?.Name);
        var lowThreshold = alreadySlow with
        {
            Status = alreadySlow.Status with { DownloadActivity = alreadySlow.Status.DownloadActivity! with { ByteThreshold = MB } }
        };
        TestRunner.Assert(new LongDownloadNoticeTracker().Observe(lowThreshold, Now) is null);
    }

    private static void Polling()
    {
        TestRunner.Equal(30000, DownloadPresentation.PollDelay(TimeSpan.FromSeconds(30), downloading: false));
        TestRunner.Equal(5000, DownloadPresentation.PollDelay(TimeSpan.FromSeconds(5), downloading: false));
        TestRunner.Equal(1000, DownloadPresentation.PollDelay(TimeSpan.FromSeconds(30), downloading: true));
        TestRunner.Equal(250, DownloadPresentation.PollDelay(TimeSpan.FromMilliseconds(250), downloading: true));
        TestRunner.Equal(1, DownloadPresentation.PollDelay(TimeSpan.Zero, downloading: true));
    }

    internal static void MountedDownload()
    {
        const string fileName = "見本 unit10b.pdf";
        const long size = 20 * MB;
        using var f = new Unit5Fixture("download-progress", writable: false);
        f.Store.Save(f.Config with { CacheMaxSize = "64M" });
        using (var file = File.Create(Path.Combine(f.RemoteRoot, fileName))) file.SetLength(size);

        Unit6iTests.RunTray(f, async resident =>
        {
            var tray = FormTests.Field<NotifyIcon>(resident, "tray");
            var poll = FormTests.Field<System.Windows.Forms.Timer>(resident, "poll");
            TestRunner.Assert(resident.Snapshot.Status is { Running: true, Ready: true }, "The test mount did not become ready.");
            TestRunner.Assert(poll.Interval is > 29000 and <= 30000, $"Idle tick: {poll.Interval} ms.");
            tray.ContextMenuStrip!.Items[1].PerformClick();
            var form = FormTests.Field<StatusForm>(resident, "statusForm");
            var grid = FormTests.Field<DataGridView>(form, "downloads");
            TestRunner.Assert(!grid.Visible && !DownloadPresentation.HasDownloads(resident.Snapshot));
            var live = f.State.Load()!;
            using var rc = new RcClient(live);
            rc.Call("core/bwlimit", new { rate = "512K" });

            var duration = Stopwatch.StartNew();
            var read = Task.Run(() =>
            {
                using var file = File.OpenRead(Path.Combine(f.MountRoot, fileName));
                file.CopyTo(Stream.Null);
                return file.Position;
            });
            using var detection = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            while (!DownloadPresentation.HasDownloads(resident.Snapshot))
                await Task.Delay(50, detection.Token);
            var first = resident.Snapshot.Status.DownloadProgress!.Files.Single();
            double discoveredAt = duration.Elapsed.TotalSeconds;
            TestRunner.Equal(fileName, first.Name);
            TestRunner.Equal(size, first.Size);
            TestRunner.Assert(first.Bytes > 0 && first.Bytes < size && first.RemainingSeconds > 0,
                $"First sample: {first.Bytes}/{first.Size}, ETA={first.RemainingSeconds}.");
            TestRunner.Assert(poll.Interval is > 0 and <= 1000, $"Active tick: {poll.Interval} ms.");
            TestRunner.Assert(tray.Text.StartsWith("ダウンロード中: " + fileName, StringComparison.Ordinal),
                $"Active tray text: {tray.Text}, errors={resident.Snapshot.Errors.Count}, status={resident.Snapshot.Error}.");
            TestRunner.Assert(grid.Visible && Equals(grid.Rows[0].Cells[0].Value, fileName), "The live status row is missing.");

            await ResidentTests.Until(() => resident.Snapshot.Status.DownloadProgress?.Files.SingleOrDefault()?.Bytes > first.Bytes,
                "Transfer bytes did not advance on the resident timer.");
            var second = resident.Snapshot.Status.DownloadProgress!.Files.Single();
            double advancedAt = duration.Elapsed.TotalSeconds;
            TestRunner.Assert(advancedAt - discoveredAt < 4, $"Active refresh took {advancedAt - discoveredAt:F2}s.");
            TestRunner.Equal(DownloadPresentation.Amount(second.Bytes, second.Size), grid.Rows[0].Cells[1].Value);
            Console.WriteLine($"During download: {tray.Text}, ETA={grid.Rows[0].Cells[2].Value}, poll={poll.Interval}ms.");
            TestRunner.Equal(size, await read.WaitAsync(TimeSpan.FromSeconds(20)));
            double finishedAt = duration.Elapsed.TotalSeconds;
            await ResidentTests.Until(() => !DownloadPresentation.HasDownloads(resident.Snapshot) && !grid.Visible,
                "Completed transfer was left visible.");
            TestRunner.Assert(duration.Elapsed.TotalSeconds - finishedAt < 4 && poll.Interval > 1000,
                $"Completion update took {duration.Elapsed.TotalSeconds - finishedAt:F2}s, interval={poll.Interval}ms.");
            TestRunner.Assert(!tray.Text.Contains(fileName, StringComparison.Ordinal), "The tooltip retained a finished transfer.");
            TestRunner.Equal(live.RclonePid, f.State.Load()!.RclonePid);
            TestRunner.Assert(!File.ReadAllText(f.State.PathName).Contains("unit10b.pdf", StringComparison.Ordinal) &&
                !File.ReadAllText(Path.Combine(f.Config.LogDir, "supervisor.log")).Contains("unit10b.pdf", StringComparison.Ordinal),
                "The sample file name was written to runtime state or the application log.");
            Console.WriteLine($"Download progress {first.Bytes}->{second.Bytes}/{second.Size}; detected={discoveredAt:F2}s, advanced={advancedAt:F2}s, finished={finishedAt:F2}s, cleared={duration.Elapsed.TotalSeconds:F2}s.");
        });
    }
}
