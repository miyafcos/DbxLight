using System.Diagnostics;
using System.Reflection;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit8Tests
{
    private static void ResidentReviewCases(Action<string, string, Action> test)
    {
        foreach (string mode in new[] { "--hide", "--web", "--hide-shell", "--web-shell", "--register", "--unregister" })
        { string value = mode; test("R3", "one-shot " + value + " removed; relative config/state normalized", () => RelaunchArguments(value)); }
        test("R4", "manual checks always notify; throttled automatic checks remain unnotified", NotificationDelivery);
        test("R4", "next automatic due time follows last check plus 24 hours", ScheduleDue);
        foreach (bool throws in new[] { false, true })
        { bool value = throws; test("R5", "failed helper spawn leaves stopped resident and resume menu; throws=" + value, () => SpawnFailure(value)); }
        test("R6", "watch ends while waiting: cancel update before exit reconciliation", EndedUpdateWatch);
        test("R6", "external clean stop while waiting cannot launch an update", ExternalUpdateStop);
        foreach (string path in new[] { "force", "session", "dispose" })
        { string value = path; test("R6", "update cannot launch through " + value, () => NonNormalExit(value)); }
        test("R6", "uncertain stopped cache prevents update launch", UncertainUpdateStop);
        test("R6", "recovery evidence changed before finish revokes update", ChangedStopEvidence);
        foreach (bool own in new[] { false, true })
        { bool value = own; test("R6", "stopped update accepts clean record without a pause receipt; own=" + value, () => PausedUpdate(value)); }
        test("R11", "updateCheck-only save keeps mount identity; other fields retain refusal", PreferenceOnlySave);
        test("R11", "combined autostart change keeps original guarded stop", PreferenceWithAutoStart);
    }
    private static object? Private(object target, string field) => target.GetType().GetField(field, Hidden)!.GetValue(target);
    private static void Put(object target, string field, object? value) => target.GetType().GetField(field, Hidden)!.SetValue(target, value);
    private static void RelaunchArguments(string mode) => FormTests.Sta(() =>
    {
        string previous = Environment.CurrentDirectory, root = Root();
        try
        {
            Environment.CurrentDirectory = root;
            string registry = RegistrationService.TestHive + "\\Unit8r-" + Guid.NewGuid().ToString("N");
            var raw = new List<string> { "--config", "config.json", "--state", "state.json", "--registry-root", registry, mode };
            if (mode is not ("--register" or "--unregister"))
                raw.Add(mode.EndsWith("-shell", StringComparison.Ordinal) ? Path.Combine(root, "once") + "|" : "once");
            AppOptions options = AppOptions.Parse(raw.ToArray());
            using var instance = new SingleInstance(options.StatePath);
            using var resident = new ResidentContext(options, instance, originalArguments: raw.ToArray());
            string[] actual = (string[])Private(resident, "relaunchArguments")!;
            string[] expected = ["--config", Path.Combine(root, "config.json"), "--state", Path.Combine(root, "state.json"), "--registry-root", options.RegistryRoot!];
            TestRunner.Equal(string.Join("|", expected), string.Join("|", actual));
            var helper = typeof(RegistrationService).GetMethod("StartupArguments");
            TestRunner.Assert(helper is not null, "Autostart and relaunch must share their argument builder.");
            TestRunner.Equal(string.Join("|", expected), string.Join("|", (string[])helper!.Invoke(null, [options, null])!));
        }
        finally { Environment.CurrentDirectory = previous; }
    });
    private static void NotificationDelivery()
    {
        using var fixture = ResidentFixture(); fixture.Config.Save(fixture.Config.Load() with { UpdateCheck = true });
        ResidentTests.Run(fixture, async resident =>
        {
            var store = new UpdateStore(fixture.State.PathName); int notices = 0;
            resident.UpdateCommandOverride = _ => Task.FromResult(Result(new UpdateCheckResult(UpdateVersion.Current, "0.1.1", true, false, UpdateSource.ReleasesUrl, false)));
            resident.UpdateNoticeOverride = (_, _, _) => notices++;
            await resident.CheckUpdateAsync(true); await resident.CheckUpdateAsync(true); TestRunner.Equal(2, notices);
            resident.UpdateNoticeOverride = null;
            var sent = (Dictionary<string, DateTimeOffset>)Private(Private(resident, "throttle")!, "sent")!;
            int previouslySent = sent.Count;
            await resident.CheckUpdateAsync(true); await resident.CheckUpdateAsync(true);
            TestRunner.Equal(previouslySent + 2, sent.Count); // Both calls passed the real common throttle.
            resident.UpdateNoticeOverride = (_, _, _) => notices++;
            var delivery = typeof(ResidentContext).GetProperty("UpdateNoticeDeliveryOverride", Hidden);
            TestRunner.Assert(delivery is not null, "Actual notification delivery must be observed.");
            delivery!.SetValue(resident, (Func<string, string, ToolTipIcon, bool>)((_, _, _) => false));
            store.ChangeCheck(s => s with { CheckedUtc = DateTimeOffset.UtcNow.AddDays(-2) });
            await resident.CheckUpdateAsync(false); TestRunner.Equal(0, store.LoadCheck().NotifiedVersions?.Length ?? 0);
            delivery.SetValue(resident, (Func<string, string, ToolTipIcon, bool>)((_, _, _) => { notices++; return true; }));
            store.ChangeCheck(s => s with { CheckedUtc = DateTimeOffset.UtcNow.AddDays(-2) });
            await resident.CheckUpdateAsync(false); TestRunner.Equal(3, notices);
            TestRunner.Assert(store.LoadCheck().NotifiedVersions?.Contains("0.1.1", StringComparer.Ordinal) == true);
        });
    }
    private static void ScheduleDue()
    {
        var method = typeof(ResidentContext).GetMethod("NextUpdateDelay", BindingFlags.Static | BindingFlags.NonPublic);
        TestRunner.Assert(method is not null, "Next check must be scheduled from the persisted attempt.");
        DateTimeOffset now = DateTimeOffset.UtcNow, first = now.AddMinutes(2);
        TimeSpan Delay(DateTimeOffset? last) => (TimeSpan)method!.Invoke(null, [now, first, last])!;
        TestRunner.Equal(TimeSpan.FromMinutes(2), Delay(null));
        TestRunner.Equal(TimeSpan.FromMinutes(2), Delay(now.AddHours(-25)));
        TestRunner.Equal(TimeSpan.FromHours(1), Delay(now.AddHours(-23)));
        TestRunner.Equal(TimeSpan.FromHours(24), Delay(now));
    }
    private static void CleanRcStop(Unit3aTests.RcFixture fixture)
    {
        int pid = fixture.State.Load()!.RclonePid;
        fixture.Respond = path =>
        {
            if (path == "/core/quit") { File.WriteAllText(Path.Combine(fixture.Root, "child-stop.txt"), "exit", new UTF8Encoding(false)); return (200, "{}"); }
            return path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}") :
                path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + pid + "}");
        };
    }
    private static void SpawnFailure(bool throws)
    {
        using var fixture = ResidentFixture(); CleanRcStop(fixture);
        ResidentTests.Run(fixture, async resident =>
        {
            int launches = 0; ResidentDownload(fixture, resident, () => { });
            FormTests.Field<System.Windows.Forms.Timer>(resident, "updateTimer").Stop();
            resident.UpdateLaunchOverride = _ => { launches++; if (throws) throw new IOException("injected spawn failure"); return 0; };
            var notices = new List<(string, string)>(); resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            await resident.PrepareUpdateAsync();
            TestRunner.Equal(1, launches); TestRunner.Equal(ResidentPhase.Stopped, resident.Phase); TestRunner.Assert(!resident.HasPendingUpdate);
            var pause = FormTests.Field<ToolStripMenuItem>(resident, "pause");
            TestRunner.Assert(pause.Enabled && pause.Text == "再開");
            TestRunner.Assert(FormTests.Field<System.Windows.Forms.Timer>(resident, "updateTimer").Enabled);
            TestRunner.Assert(notices.Contains(("更新できませんでした", "更新を始められなかったため、マウントは停止したままです。トレイのメニューから再開できます。")));
            TestRunner.Assert(!notices.Any(n => n.Item1 == "停止できません"));
            ResidentDownload(fixture, resident, () => launches++);
            await resident.PrepareUpdateAsync();
            TestRunner.Equal(2, launches); TestRunner.Assert(!resident.HasPendingUpdate);
        });
    }
    private static async Task AwaitUpdateWait(Unit3aTests.RcFixture fixture, ResidentContext resident)
    {
        int pid = fixture.State.Load()!.RclonePid;
        fixture.Respond = path => path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}") :
            path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + pid + "}");
        EventHandler choose = (_, _) =>
        {
            if (Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is { } dialog) ((Button)dialog.AcceptButton!).PerformClick();
        };
        Application.Idle += choose;
        try { await resident.PrepareUpdateAsync(); } finally { Application.Idle -= choose; }
        TestRunner.Equal(ResidentPhase.WaitingToExit, resident.Phase); TestRunner.Assert(resident.HasPendingUpdate);
    }
    private static void EndedUpdateWatch()
    {
        using var fixture = new Unit5Fixture("unit8r-ended-watch", writeBack: "5m");
        RunUpdateTray(fixture, async resident =>
        {
            fixture.Write("pending.txt", "retained update content");
            await Task.Run(() => Unit5Fixture.Wait(() => fixture.Controller.Status().UploadsQueued == 1, "Update queue missing."));
            var stage = UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(fixture.State.PathName)); int launches = 0;
            resident.UpdateCommandOverride = _ => Task.FromResult(Result(stage));
            resident.UpdateLaunchOverride = _ => { launches++; return 1; };
            var notices = new List<(string, string)>(); resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            EventHandler choose = (_, _) =>
            {
                if (Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is { } dialog)
                    ((Button)dialog.AcceptButton!).PerformClick();
            };
            Application.Idle += choose;
            try { await resident.PrepareUpdateAsync(); } finally { Application.Idle -= choose; }
            TestRunner.Equal(ResidentPhase.WaitingToExit, resident.Phase); TestRunner.Assert(resident.HasPendingUpdate);
            Task<int> watch = ResidentTests.Watch(resident)!;
            resident.Controller.StartupCheckpoint = (point, _) =>
            {
                if (point == "before-resume") throw StartupFailure.RetryableChild(new System.ComponentModel.Win32Exception(5));
            };
            RuntimeState running = fixture.State.Load()!;
            await Task.Run(() => { using var child = ProcessIdentity.Open(running)!; child.Kill(); TestRunner.Assert(child.WaitForExit(10000)); });
            await watch;
            await ResidentTests.Until(() => resident.Phase == ResidentPhase.Stopped, "Ended update did not leave the resident stopped.");
            AssertUpdateRefusal(resident, notices, stage.Staging);
            TestRunner.Equal(0, launches); TestRunner.Assert(fixture.State.Load()!.RecoveryRequired);
            resident.Controller.StartupCheckpoint = null;
            await resident.StartMountedAsync();
            TestRunner.Equal(ResidentPhase.Watching, resident.Phase);
            TestRunner.Assert(fixture.Controller.Status().Running);
        });
    }
    private static void ExternalUpdateStop()
    {
        using var fixture = ResidentFixture();
        RunUpdateResident(fixture, async resident =>
        {
            int launches = 0; var stage = ResidentDownload(fixture, resident, () => launches++);
            var notices = new List<(string, string)>(); resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            await AwaitUpdateWait(fixture, resident); CleanRcStop(fixture);
            await Task.Run(() => fixture.Controller.Stop());
            await ResidentTests.Until(() => resident.Phase == ResidentPhase.Stopped, "External update stop did not retain the resident.");
            TestRunner.Equal(0, launches); AssertUpdateRefusal(resident, notices, stage.Staging);
        });
    }
    private static void NonNormalExit(string path)
    {
        if (path == "dispose")
        {
            FormTests.Sta(() =>
            {
                string root = Root(); var options = new AppOptions(Path.Combine(root, "config.json"), Path.Combine(root, "state.json"));
                using var instance = new SingleInstance(options.StatePath); var resident = new ResidentContext(options, instance);
                var stage = UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(options.StatePath));
                Put(resident, "pendingUpdate", stage); int launches = 0; resident.UpdateLaunchOverride = _ => ++launches;
                resident.Dispose(); TestRunner.Assert(!resident.HasPendingUpdate); TestRunner.Equal(0, launches);
            });
            return;
        }
        using var fixture = ResidentFixture(); CleanRcStop(fixture);
        ResidentTests.Run(fixture, async resident =>
        {
            int launches = 0; var stage = ResidentDownload(fixture, resident, () => launches++);
            Put(resident, "pendingUpdate", stage);
            if (path == "force") await resident.ShutdownOwnedAsync();
            else typeof(ResidentContext).GetMethod("SessionEndChanged", Hidden)!.Invoke(resident, [true]);
            TestRunner.Assert(!resident.HasPendingUpdate); TestRunner.Equal(0, launches);
        });
    }
    private static void UncertainUpdateStop()
    {
        using var fixture = new Unit5Fixture("unit8r2-uncertain");
        RunUpdateTray(fixture, async resident =>
        {
            string unknown = Path.Combine(fixture.Config.CacheDir, "vfs", "unknown");
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "pending"), "uncertain", new UTF8Encoding(false));
            var stage = UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(fixture.State.PathName));
            int launches = 0; resident.UpdateCommandOverride = _ => Task.FromResult(Result(stage));
            resident.UpdateLaunchOverride = _ => { launches++; return 1; };
            var notices = new List<(string, string)>(); resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            await resident.PrepareUpdateAsync(); TestRunner.Equal(0, launches);
            AssertUpdateRefusal(resident, notices, stage.Staging);
            TestRunner.Assert(fixture.State.Load()!.RecoveryRequired);
            await resident.StartMountedAsync();
            TestRunner.Equal(ResidentPhase.Watching, resident.Phase);
            TestRunner.Assert(fixture.Controller.Status().Running);
        });
    }
    private static void ChangedStopEvidence()
    {
        using var fixture = ResidentFixture(); CleanRcStop(fixture);
        RunUpdateResident(fixture, async resident =>
        {
            int launches = 0; var stage = ResidentDownload(fixture, resident, () => launches++);
            var notices = new List<(string, string)>(); resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            resident.MountStopCompletedCheckpoint = () =>
            {
                RuntimeState state = fixture.State.Load()!;
                RuntimeSnapshots.Save(fixture.State, state with { RecoveryRequired = true, StopDisposition = StopDisposition.StopUncertain });
            };
            await resident.PrepareUpdateAsync();
            TestRunner.Equal(0, launches); AssertUpdateRefusal(resident, notices, stage.Staging);
            resident.MountStopCompletedCheckpoint = null;
        });
    }
    private static void PausedUpdate(bool own)
    {
        using var fixture = ResidentFixture(); CleanRcStop(fixture);
        ResidentTests.Run(fixture, async resident =>
        {
            if (own)
            {
                FormTests.Field<ToolStripMenuItem>(resident, "pause").PerformClick(); await resident.MenuStopTask!;
            }
            else
            {
                await Task.Run(() => fixture.Controller.Stop());
                await ResidentTests.Until(() => resident.Phase == ResidentPhase.Stopped, "External stop did not settle.");
            }
            TestRunner.Equal(ResidentPhase.Stopped, resident.Phase);
            int launches = 0; ResidentDownload(fixture, resident, () => launches++);
            await resident.PrepareUpdateAsync(); TestRunner.Equal(1, launches);
            TestRunner.Equal(ResidentPhase.Exiting, resident.Phase);
        });
    }
    private static void PreferenceWithAutoStart()
    {
        using var fixture = ResidentFixture();
        ResidentTests.Run(fixture, async resident =>
        {
            RuntimeState before = fixture.State.Load()!; int stopCalls = 0;
            fixture.Respond = path =>
            {
                if (path == "/core/quit") stopCalls++;
                return path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}") :
                    path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + before.RclonePid + "}");
            };
            using var form = resident.CreateSettingsForm();
            var autoStart = FormTests.Field<CheckBox>(form, "autoStart"); autoStart.Checked = !autoStart.Checked;
            var save = FormTests.Field<Func<Func<DbxConfig, DbxConfig>, Task>>(form, "save");
            try
            {
                await save(c => c with { UpdateCheck = !c.UpdateCheck });
                throw new InvalidOperationException("Expected upload refusal for combined autostart change.");
            }
            catch (PendingUploadsException error) { TestRunner.Assert(error.Message.Contains("Stop refused", StringComparison.Ordinal)); }
            TestRunner.Equal(ResidentPhase.Watching, resident.Phase);
            TestRunner.Equal(before.RclonePid, fixture.State.Load()!.RclonePid);
            TestRunner.Equal(resident.Config.UpdateCheck, fixture.Config.Load().UpdateCheck);
            TestRunner.Equal(0, stopCalls); CleanRcStop(fixture);
        });
    }
    private static void PreferenceOnlySave()
    {
        using var fixture = ResidentFixture(); fixture.Config.Save(fixture.Config.Load() with { UpdateCheck = false });
        ResidentTests.Run(fixture, async resident =>
        {
            RuntimeState before = fixture.State.Load()!; Task<int> watch = ResidentTests.Watch(resident)!;
            int stopCalls = 0, pid = before.RclonePid;
            fixture.Respond = path =>
            {
                if (path == "/core/quit") stopCalls++;
                return path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}") :
                    path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + pid + "}");
            };
            using var form = resident.CreateSettingsForm();
            var save = FormTests.Field<Func<Func<DbxConfig, DbxConfig>, Task>>(form, "save");
            await save(c => c with { UpdateCheck = true });
            TestRunner.Assert(fixture.Config.Load().UpdateCheck && resident.Config.UpdateCheck);
            TestRunner.Equal(ResidentPhase.Watching, resident.Phase); TestRunner.Assert(ReferenceEquals(watch, ResidentTests.Watch(resident)));
            TestRunner.Equal(before.RclonePid, fixture.State.Load()!.RclonePid); TestRunner.Equal(before.StopSequence, fixture.State.Load()!.StopSequence);
            TestRunner.Equal(0, stopCalls);
            try
            {
                await save(c => c with { UpdateCheck = false, DirCacheTime = "2h" });
                throw new InvalidOperationException("Expected upload refusal for another setting.");
            }
            catch (PendingUploadsException error) { TestRunner.Assert(error.Message.Contains("Stop refused", StringComparison.Ordinal)); }
            TestRunner.Assert(fixture.Config.Load().UpdateCheck); CleanRcStop(fixture);
        });
    }
}
