using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void DownloadWindows()
    {
        DbxConfig config = new() { DownloadWindowSeconds = 60, DownloadFileThreshold = 2, DownloadByteThreshold = 1000 };
        DateTimeOffset now = DateTimeOffset.Parse("2026-01-01T00:01:00Z");
        JsonElement History(DateTimeOffset at) => JsonSerializer.SerializeToElement(new { transferred = new[]
        {
            new { name = "a", bytes = 10, srcFs = "sample:", dstFs = "", what = "transferring", started_at = at.AddSeconds(-1), completed_at = at },
            new { name = "b", bytes = 10, srcFs = "sample:", dstFs = "", what = "transferring", started_at = at.AddSeconds(-1), completed_at = at },
            new { name = "upload", bytes = 9000, srcFs = "local:", dstFs = "sample:", what = "transferring", started_at = at.AddSeconds(-1), completed_at = at }
        }});
        JsonElement Stats(long total, long? activeBytes = null) => JsonSerializer.SerializeToElement(new
        {
            transfers = total,
            transferring = activeBytes is null ? Array.Empty<object>() : new object[] { new { name = "large", srcFs = "sample:", dstFs = "", bytes = activeBytes.Value } }
        });
        var first = DownloadMonitor.Observe(null, config, "sample:", History(now), Stats(3), now);
        TestRunner.Equal(2L, first.Activity.Files); TestRunner.Equal(20L, first.Activity.Bytes);
        TestRunner.Assert(first.Activity.Exceeded && first.Activity.Known);
        var notices = new DownloadNoticeTracker(); var identity = new MountIdentity(123, now.UtcDateTime);
        int count = notices.Observe(identity, first.Activity) ? 1 : 0;
        for (int i = 0; i < 20; i++) if (notices.Observe(identity, first.Activity)) count++;
        TestRunner.Equal(1, count);
        var throttle = new NotificationThrottle();
        TestRunner.Assert(throttle.Allow(DownloadNoticeTracker.Key(identity, first.Activity), now));
        TestRunner.Assert(!throttle.Allow(DownloadNoticeTracker.Key(identity, first.Activity), now.AddSeconds(1)));
        var expired = DownloadMonitor.Observe(first.State, config, "sample:", History(now), Stats(3), now.AddSeconds(61));
        TestRunner.Assert(!expired.Activity.Exceeded && expired.Activity.Files == 0);
        var again = DownloadMonitor.Observe(expired.State, config, "sample:", History(now.AddSeconds(62)), Stats(6), now.AddSeconds(62));
        TestRunner.Equal(2, again.Activity.Episode); TestRunner.Assert(notices.Observe(identity, again.Activity));
        TestRunner.Assert(throttle.Allow(DownloadNoticeTracker.Key(identity, again.Activity), now.AddSeconds(62)),
            "A distinct burst must not be suppressed by the generic ten-minute notice throttle.");
        TestRunner.Assert(!notices.Observe(identity, again.Activity));
        var backward = DownloadMonitor.Observe(again.State, config, "sample:", History(now.AddSeconds(62)), Stats(6), now);
        TestRunner.Equal(again.Activity.Episode, backward.Activity.Episode);
        JsonElement empty = JsonSerializer.SerializeToElement(new { transferred = Array.Empty<object>() });
        DbxConfig byteConfig = config with { DownloadFileThreshold = 100, DownloadByteThreshold = 50 };
        var zero = DownloadMonitor.Observe(null, byteConfig, "sample:", empty, Stats(0, 0), now);
        var progress = DownloadMonitor.Observe(zero.State, byteConfig, "sample:", empty, Stats(0, 40), now.AddSeconds(1));
        TestRunner.Equal(40L, progress.Activity.Bytes);
        var slow = DownloadMonitor.Observe(progress.State, byteConfig, "sample:", empty, Stats(0, 45), now.AddSeconds(61));
        TestRunner.Equal(5L, slow.Activity.Bytes); TestRunner.Assert(!slow.Activity.Exceeded);
        var gap = DownloadMonitor.Observe(slow.State, byteConfig, "sample:", empty, Stats(0, 9999), now.AddSeconds(1000));
        TestRunner.Equal(0L, gap.Activity.Bytes); TestRunner.Assert(gap.Activity.HistoryLimited && !gap.Activity.Exceeded);
        var oldCompletion = DownloadMonitor.Observe(null, config, "sample:", History(now.AddSeconds(-59)), Stats(3), now);
        var agedOut = DownloadMonitor.Observe(oldCompletion.State, config, "sample:", History(now.AddSeconds(-59)), Stats(3), now.AddSeconds(2));
        TestRunner.Equal(0L, agedOut.Activity.Files);
        TestRunner.Equal(12, ConfigStore.Set(config, "downloadFileThreshold", "12").DownloadFileThreshold);
        TestRunner.Equal(500L, ConfigStore.Set(config, "downloadByteThreshold", "500").DownloadByteThreshold);
        TestRunner.Throws<ArgumentException>(() => (config with { DownloadWindowSeconds = 0 }).Validate());
        TestRunner.Throws<ArgumentException>(() => (config with { DownloadFileThreshold = 0 }).Validate());
        Console.WriteLine("G10 windows: upload excluded; one notice per continuous episode; expiry/rearm and backward clock checked; active byte deltas expire.");
    }

    private static void DownloadNotice()
    {
        using (var f = new Unit5Fixture("download-notice"))
        {
            f.Store.Save(f.Config with { DownloadFileThreshold = 2, DownloadByteThreshold = 1000000 });
            f.Seed("a.txt", new string('a', 64)); f.Seed("b.txt", new string('b', 64)); f.Seed("c.txt", new string('c', 64)); f.Start();
            int pid = f.State.Load()!.RclonePid;
            foreach (string file in new[] { "a.txt", "b.txt", "c.txt" }) TestRunner.Equal(64, File.ReadAllText(Path.Combine(f.MountRoot, file)).Length);
            DbxStatus? observed = null;
            Unit5Fixture.Wait(() => { observed = f.Controller.Status(); return observed.DownloadActivity is { Files: >= 3, Exceeded: true }; }, "RC did not observe the three downloads.");
            TestRunner.Assert(observed!.DownloadActivity!.Known);
            var notices = new DownloadNoticeTracker(); var identity = new MountIdentity(pid, f.State.Load()!.ProcessStartedUtc);
            int count = 0;
            for (int i = 0; i < 5; i++) if (notices.Observe(identity, f.Controller.Status().DownloadActivity)) count++;
            TestRunner.Equal(1, count); TestRunner.Equal(pid, f.Controller.Status().RclonePid); TestRunner.Assert(f.Controller.Status().Running);
            using var rc = new RcClient(f.State.Load()!);
            JsonFiles.Write(Path.Combine(f.Root, "download-rc.json"), new { completed = rc.Call("core/transferred"), stats = rc.Call("core/stats") });
            Console.WriteLine("G10 real downloads: notices=" + count + ", same owned PID remains running; status=" + JsonSerializer.Serialize(observed.DownloadActivity, JsonFiles.Options));
        }
        using (var f = new Unit5Fixture("upload-not-download", writeBack: "1s"))
        {
            f.Store.Save(f.Config with { DownloadFileThreshold = 1 }); f.Start(); f.Write("upload.txt", "upload only");
            TestRunner.Equal(0, f.Cli("wait", "upload.txt", "--timeout", "10", "--json").Exit);
            DownloadActivity activity = f.Controller.Status().DownloadActivity!;
            TestRunner.Assert(activity.Known); TestRunner.Equal(0L, activity.Files); TestRunner.Equal(0L, activity.Bytes); TestRunner.Assert(!activity.Exceeded);
            Console.WriteLine("G10 real upload: download files=0, bytes=0; no notice.");
        }
    }
}
