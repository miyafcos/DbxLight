using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit8Tests
{
    private const string UnverifiedUpdateBody = "マウントの状態を確認できなかったため、更新を始めませんでした。トレイのメニューからマウントを再開し、状態を確かめてから、もう一度お試しください。";
    private static void SecondReviewCases(TestRunner runner, string? group)
    {
        void Test(string id, string title, Action action)
        { if (group is null or "review2" || group == id) runner.Test("8r2 " + id + ": " + title, action); }
        foreach (string scenario in new[] { "startup-failure", "startup-no-record", "remote-unset", "external-stop", "paused-save", "record-change" })
        { string value = scenario; Test("S1", "stopped update: " + value, () => StoppedUpdateScenario(value)); }
        Test("S1", "canceling update after watch ends retains stopped resident without a failure notice", UpdateCancelAfterEndedWatch);
        Test("S1", "other process activity retains both notices, status and resume", UpdateOtherActivity);
        foreach (string scenario in new[] { "verify", "eligibility", "old-hash", "unmanaged-file", "unmanaged-directory", "backup-file", "backup-link", "readonly", "locked", "swap" })
        { string value = scenario; Test("S2", "failure stage: " + value, () => StageClassification(value)); }
        foreach (bool exit in new[] { false, true })
        { bool value = exit; Test("S3", "wait identity exception; exited=" + value, () => WaitStartRace(value)); }
        Test("S4", "successful rollback clears removed backup path", RollbackPath);
        Test("S6", "release asset URL requires ASCII", AsciiAssetUrls);
        Test("S8", "wait chosen before watch ends completes normal exit", WaitingBeforeWatchEnds);
        Test("S8", "session query and sessionRequested launch guard", SessionUpdateGuard);
        foreach (bool match in new[] { false, true })
        { bool value = match; Test("S8", "wait timestamp CLI roundtrip; match=" + value, () => WaitArgumentRoundtrip(value)); }
        Test("S8", "real live child reaches shortened wait deadline", WaitDeadline);
        foreach (string scenario in new[] { "backup-junction", "backup-symlink", "stage-symlink" })
        { string value = scenario; Test("S8", "cleanup link: " + value, () => CleanupMoreLinks(value)); }
        Test("S8", "crashed gate worker is reacquired within bounded retries", Unit6qTests.GateCrashReleases);
    }
    private static void AssertUpdateRefusal(ResidentContext resident, List<(string, string)> notices, string? staging)
    {
        TestRunner.Equal(ResidentPhase.Stopped, resident.Phase);
        TestRunner.Assert(!resident.HasPendingUpdate);
        var resume = FormTests.Field<ToolStripMenuItem>(resident, "pause");
        TestRunner.Assert(resume.Enabled && resume.Text == "再開");
        TestRunner.Assert(FormTests.Field<NotifyIcon>(resident, "tray").Visible);
        TestRunner.Equal(1, notices.Count(n => n == ("更新できませんでした", UnverifiedUpdateBody)));
        if (staging is not null) TestRunner.Assert(!Directory.Exists(staging) && !File.Exists(staging + ".json") && !File.Exists(staging + ".lock"));
    }
    private static void StoppedUpdateScenario(string scenario)
    {
        if (scenario == "remote-unset") { NoRemoteUpdate(); return; }
        if (scenario == "startup-no-record") { FailedStartupWithoutRecord(); return; }
        using var fixture = new Unit5Fixture("unit8r2-" + scenario);
        RunUpdateTray(fixture, async resident =>
        {
            if (scenario == "external-stop")
            {
                await Task.Run(() => fixture.Controller.Stop());
                await ResidentTests.Until(() => resident.Phase == ResidentPhase.Stopped, "External stop did not settle.");
            }
            else
            {
                FormTests.Field<ToolStripMenuItem>(resident, "pause").PerformClick();
                await resident.MenuStopTask!;
            }
            TestRunner.Equal(ResidentPhase.Stopped, resident.Phase);
            RuntimeState clean = fixture.State.Load()!;
            if (scenario == "startup-failure")
                RuntimeSnapshots.Save(fixture.State, clean with { StopDisposition = StopDisposition.StartupFailed, RecoveryRequired = true });
            if (scenario == "paused-save")
            {
                using var settings = resident.CreateSettingsForm();
                var save = FormTests.Field<Func<Func<DbxConfig, DbxConfig>, Task>>(settings, "save");
                await save(c => c with { DirCacheTime = "2h" });
                TestRunner.Assert(fixture.State.Load()!.StopSequence > clean.StopSequence);
            }
            var store = new UpdateStore(fixture.State.PathName);
            int downloads = 0, launches = 0;
            string? staging = null;
            var notices = new List<(string, string)>();
            resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            resident.UpdateCommandOverride = command =>
            {
                TestRunner.Equal("download", command); downloads++;
                var stage = UpdatePackage.Stage(Package("0.1.1"), store); staging = stage.Staging;
                if (scenario == "record-change")
                    RuntimeSnapshots.Save(fixture.State, fixture.State.Load()! with { RecoveryRequired = true, StopDisposition = StopDisposition.StopUncertain });
                return Task.FromResult(Result(stage));
            };
            resident.UpdateLaunchOverride = _ => { launches++; return 1; };
            await resident.PrepareUpdateAsync();
            if (scenario is "startup-failure" or "record-change")
            {
                TestRunner.Equal(scenario == "startup-failure" ? 0 : 1, downloads); TestRunner.Equal(0, launches);
                AssertUpdateRefusal(resident, notices, staging);
                RuntimeSnapshots.Save(fixture.State, clean);
                await resident.StartMountedAsync();
                TestRunner.Equal(ResidentPhase.Watching, resident.Phase);
                TestRunner.Assert(fixture.Controller.Status().Running);
            }
            else
            {
                TestRunner.Equal(1, downloads); TestRunner.Equal(1, launches);
                TestRunner.Equal(ResidentPhase.Exiting, resident.Phase);
                TestRunner.Assert(!notices.Any(n => n.Item1 == "更新できませんでした"));
            }
        });
    }

    private static void StageClassification(string scenario)
    {
        string root = Root(), target = Install(root);
        var store = new UpdateStore(Path.Combine(root, "state.json"));
        var stage = UpdatePackage.Stage(scenario.StartsWith("unmanaged", StringComparison.Ordinal) ? ExtraPackage() : Package("0.1.1"), store);
        if (scenario == "verify") File.WriteAllText(Path.Combine(stage.Staging, "payload.txt"), "broken", new UTF8Encoding(false));
        if (scenario == "eligibility") File.WriteAllText(Path.Combine(target, "release.json"), "{}", new UTF8Encoding(false));
        if (scenario == "old-hash") File.WriteAllText(Path.Combine(target, "payload.txt"), "broken", new UTF8Encoding(false));
        if (scenario == "unmanaged-file") File.WriteAllText(Path.Combine(target, "new.txt"), "foreign", new UTF8Encoding(false));
        if (scenario == "unmanaged-directory") Directory.CreateDirectory(Path.Combine(target, "new.txt"));
        if (scenario == "backup-file") File.WriteAllText(Path.Combine(target, "backup"), "foreign", new UTF8Encoding(false));
        if (scenario == "backup-link")
        {
            string destination = Path.Combine(root, "backup-destination"); Directory.CreateDirectory(destination);
            if (!Junction(Path.Combine(target, "backup"), destination)) return;
        }
        if (scenario == "readonly") File.SetAttributes(Path.Combine(target, "payload.txt"), FileAttributes.ReadOnly);
        var before = Snapshot(target);
        var swap = new UpdateSwap(store);
        if (scenario == "swap") swap.Checkpoint = point => { if (point == "backed-up") throw new IOException("injected swap"); };
        using var handle = scenario == "locked" ? new FileStream(Path.Combine(target, "payload.txt"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        UpdateResult result;
        try { result = swap.Run(target, stage.Staging, 0, false, []); }
        finally
        {
            handle?.Dispose();
            if (scenario == "readonly") File.SetAttributes(Path.Combine(target, "payload.txt"), FileAttributes.Normal);
        }
        TestRunner.Assert(!result.Success);
        string expected = scenario is "verify" or "locked" or "swap" ? scenario : "target";
        TestRunner.Equal(expected, result.Stage); Same(before, target);
        TestRunner.Equal(scenario == "swap", result.RolledBack);
        var notice = ResidentContext.UpdateResultNotice(result);
        if (expected == "target") TestRunner.Equal("前のバージョンのまま起動しました。DbxLight を置いたフォルダの状態を確認できませんでした。", notice.Body);
    }
    private static bool Junction(string link, string destination)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string word in new[] { "/c", "mklink", "/J", link, destination }) info.ArgumentList.Add(word);
        using var child = Process.Start(info)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
        TestRunner.Assert(child.WaitForExit(10000)); output.GetAwaiter().GetResult(); error.GetAwaiter().GetResult();
        if (child.ExitCode != 0) { Console.WriteLine("SKIP 8r2 junction unavailable; exit=" + child.ExitCode); return false; }
        return true;
    }
    private static void WaitStartRace(bool exit)
    {
        string root = Root(), target = Install(root);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var stage = UpdatePackage.Stage(Package("0.1.1"), store);
        string signal = @"Local\DbxLight.Unit8r2." + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, signal);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--unit8-child"); info.ArgumentList.Add(signal);
        using var child = Process.Start(info)!;
        try
        {
            TestRunner.Equal("ready", child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            DateTime started = child.StartTime.ToUniversalTime(); int queries = 0;
            var before = Snapshot(target);
            var swap = new UpdateSwap(store) { WaitLimit = TimeSpan.FromMilliseconds(20) };
            var property = typeof(UpdateSwap).GetProperty("WaitStartedUtcOverride", BindingFlags.Instance | BindingFlags.NonPublic);
            TestRunner.Assert(property is not null, "Wait start-time query seam is missing.");
            property!.SetValue(swap, (Func<Process, DateTime>)(_ =>
            {
                queries++;
                if (exit) { release.Set(); TestRunner.Assert(child.WaitForExit(10000)); }
                throw new ArgumentException("injected start-time read failure");
            }));
            var result = swap.Run(target, stage.Staging, child.Id, false, [], started);
            TestRunner.Equal(1, queries);
            if (exit) TestRunner.Assert(result.Success, result.Reason ?? "Swap failed.");
            else
            {
                TestRunner.Assert(!result.Success && !child.HasExited);
                TestRunner.Equal("wait", result.Stage); TestRunner.Equal("injected start-time read failure", result.Reason);
                Same(before, target);
            }
        }
        finally { release.Set(); TestRunner.Assert(child.WaitForExit(10000)); }
    }
    private static void RollbackPath()
    {
        string root = Root(), target = Install(root); var before = Snapshot(target);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var stage = UpdatePackage.Stage(Package("0.1.1"), store);
        var swap = new UpdateSwap(store) { Checkpoint = point => { if (point == "backed-up") throw new IOException("injected rollback"); } };
        var result = swap.Run(target, stage.Staging, 0, false, []);
        TestRunner.Assert(result.RolledBack && result.BackupPath is null); Same(before, target);
        TestRunner.Assert(JsonFiles.Read<UpdateResult>(store.ResultPath).BackupPath is null);
    }
    private static void AsciiAssetUrls()
    {
        var source = new UpdateSource("invalid-feed"); string prefix = UpdateSource.ReleasesUrl[..^"/releases".Length];
        source.RequireAsset(new(prefix.ToUpperInvariant() + "/releases/download/v0.1.1/file.zip"));
        foreach (string path in new[] { "/releases/download/v0.1.1/ı.zip", "/releases/download/v0.1.1/%C5%BF.zip" })
            Fails<InvalidDataException>(() => source.RequireAsset(new(prefix + path)), "outside the release source");
    }
    private static void WaitingBeforeWatchEnds()
    {
        using var fixture = ResidentFixture(); CleanRcStop(fixture);
        bool waited = false, exitedAfterWatch = false;
        ResidentTests.Run(fixture, async resident =>
        {
            Task<int> watch = ResidentTests.Watch(resident)!;
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resident.ThreadExit += (_, _) =>
            {
                exitedAfterWatch = waited && watch.IsCompleted && resident.Phase == ResidentPhase.Exiting;
                exited.TrySetResult();
            };
            int pid = fixture.State.Load()!.RclonePid;
            fixture.Respond = path => path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0}}") :
                path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + pid + "}");
            EventHandler choose = (_, _) =>
            {
                if (Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is { } dialog)
                    ((Button)dialog.AcceptButton!).PerformClick();
            };
            Application.Idle += choose;
            try { FormTests.Field<ToolStripMenuItem>(resident, "exit").PerformClick(); await resident.MenuStopTask!; }
            finally { Application.Idle -= choose; }
            TestRunner.Equal(ResidentPhase.WaitingToExit, resident.Phase); TestRunner.Assert(!watch.IsCompleted); waited = true;
            CleanRcStop(fixture); await Task.Run(() => fixture.Controller.Stop());
            await watch; await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        TestRunner.Assert(waited && exitedAfterWatch, "WaitingToExit did not reach the natural resident ThreadExit after its watch ended.");
        TestRunner.Assert(!fixture.Controller.Status().Running);
    }
    private static void SessionUpdateGuard()
    {
        using var fixture = ResidentFixture(); CleanRcStop(fixture);
        ResidentTests.Run(fixture, async resident =>
        {
            int launches = 0, stateReads = 0; var stage = ResidentDownload(fixture, resident, () => launches++);
            Put(resident, "pendingUpdate", stage); Put(resident, "updateStopRequested", true); Put(resident, "updateStopAuthorized", true);
            resident.SessionStateReadCheckpoint = () => stateReads++;
            Put(resident, "<Snapshot>k__BackingField", resident.Snapshot with { Status = resident.Snapshot.Status with { UploadsQueued = 1 } });
            var decision = (SessionEndDecision)typeof(ResidentContext).GetMethod("QuerySessionEnd", Hidden)!.Invoke(resident, [])!;
            TestRunner.Assert(decision.Block);
            await ResidentTests.Until(() => stateReads == 1 && resident.Phase == ResidentPhase.Stopped, "PrepareSessionStopAsync was not completed.");
            TestRunner.Assert(!resident.HasPendingUpdate && !Directory.Exists(stage.Staging)); TestRunner.Equal(0, launches);
            var guarded = UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(fixture.State.PathName));
            Put(resident, "pendingUpdate", guarded); Put(resident, "updateStopRequested", true); Put(resident, "updateStopAuthorized", true);
            TestRunner.Assert((bool)Private(resident, "sessionRequested")!);
            typeof(ResidentContext).GetMethod("LaunchPendingUpdate", Hidden)!.Invoke(resident, []);
            TestRunner.Equal(0, launches); TestRunner.Assert(!resident.HasPendingUpdate && !Directory.Exists(guarded.Staging));
            Put(resident, "sessionRequested", false);
        });
    }
    private static void WaitArgumentRoundtrip(bool match)
    {
        string root = Root(), target = Install(root); var before = Snapshot(target);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var stage = UpdatePackage.Stage(Package("0.1.1"), store);
        using var process = Process.GetCurrentProcess();
        DateTime started = process.StartTime.ToUniversalTime().AddTicks(match ? 0 : 1);
        var actual = Cli("--state", Path.Combine(root, "state.json"), "update", "swap", "--target", target, "--staging", stage.Staging,
            "--wait-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--wait-started-utc", started.ToString("O", System.Globalization.CultureInfo.InvariantCulture), "--json");
        using var json = JsonDocument.Parse(actual.Output);
        TestRunner.Equal(match ? 1 : 0, actual.Exit);
        if (match)
        {
            TestRunner.Equal("wait", json.RootElement.GetProperty("result").GetProperty("stage").GetString());
            TestRunner.Assert(json.RootElement.GetProperty("message").GetString()!.Contains("cannot wait for itself", StringComparison.Ordinal)); Same(before, target);
        }
        else TestRunner.Assert(json.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
    }
    private static void CleanupMoreLinks(string scenario)
    {
        string root = Root(), target = Install(root), destination = Path.Combine(root, "link-destination"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep"), "retained", new UTF8Encoding(false));
        var store = new UpdateStore(Path.Combine(root, "state.json"));
        string linked;
        if (scenario.StartsWith("backup", StringComparison.Ordinal))
        {
            linked = Path.Combine(target, "backup", "0.1.0-20260101T0102030000000Z"); Directory.CreateDirectory(linked);
            foreach (string path in Directory.EnumerateFiles(target).Where(p => Path.GetFileName(p) != "unmanaged.txt"))
                File.Copy(path, Path.Combine(linked, Path.GetFileName(path)));
            foreach (int day in new[] { 2, 3, 4 })
            {
                string backup = Path.Combine(target, "backup", "0.1.0-2026010" + day + "T0102030000000Z");
                Directory.CreateDirectory(backup);
                foreach (string path in Directory.EnumerateFiles(linked)) File.Copy(path, Path.Combine(backup, Path.GetFileName(path)));
            }
        }
        else { linked = UpdatePackage.Stage(Package("0.1.1"), store).Staging; store.CompleteStage(linked); }
        string link = Path.Combine(linked, "link");
        if (scenario == "backup-junction") { if (!Junction(link, destination)) return; }
        else
        {
            try { Directory.CreateSymbolicLink(link, destination); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { Console.WriteLine("SKIP 8r2 symbolic link unavailable: " + error.GetType().Name); return; }
        }
        UpdateCleanup.Run(store, target);
        TestRunner.Assert(Directory.Exists(linked)); TestRunner.Equal("retained", File.ReadAllText(Path.Combine(destination, "keep")));
        if (scenario.StartsWith("backup", StringComparison.Ordinal))
        {
            TestRunner.Assert(!Directory.Exists(Path.Combine(target, "backup", "0.1.0-20260102T0102030000000Z")));
            foreach (int day in new[] { 3, 4 }) TestRunner.Assert(Directory.Exists(Path.Combine(target, "backup", "0.1.0-2026010" + day + "T0102030000000Z")));
        }
        Console.WriteLine("8r2 S8 link preserved: " + scenario);
    }

    private static void NoRemoteUpdate()
    {
        using var fixture = new Unit5Fixture("unit8r2-no-remote");
        fixture.Store.Save(fixture.Config with { Remote = "", TestOverrides = null, WriteAllowedRemotes = [] });
        RunUpdateTray(fixture, async resident =>
        {
            Application.OpenForms.OfType<SetupForm>().Single().Close();
            await ResidentTests.Until(() => resident.Phase == ResidentPhase.Stopped, "Unset remote setup did not close.");
            TestRunner.Assert(fixture.State.Load() is null);
            int downloads = 0, launches = 0;
            resident.UpdateCommandOverride = command =>
            {
                TestRunner.Equal("download", command); downloads++;
                return Task.FromResult(Result(UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(fixture.State.PathName))));
            };
            resident.UpdateLaunchOverride = _ => { launches++; return 1; };
            await resident.PrepareUpdateAsync();
            TestRunner.Equal(1, downloads); TestRunner.Equal(1, launches);
            TestRunner.Equal(ResidentPhase.Exiting, resident.Phase);
        });
    }

    private static void FailedStartupWithoutRecord()
    {
        using var fixture = new Unit5Fixture("unit8r2-startup-no-record");
        fixture.Store.Save(fixture.Config with { RclonePath = Path.Combine(fixture.Root, "absent-rclone.exe") });
        fixture.State.EnsurePrivateDirectory();
        RunUpdateTray(fixture, async resident =>
        {
            TestRunner.Equal(ResidentPhase.Stopped, resident.Phase); TestRunner.Assert(fixture.State.Load() is null);
            int downloads = 0, launches = 0;
            resident.UpdateCommandOverride = command =>
            {
                TestRunner.Equal("download", command); downloads++;
                return Task.FromResult(Result(UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(fixture.State.PathName))));
            };
            resident.UpdateLaunchOverride = _ => { launches++; return 1; };
            await resident.PrepareUpdateAsync();
            TestRunner.Equal(1, downloads); TestRunner.Equal(1, launches); TestRunner.Equal(ResidentPhase.Exiting, resident.Phase);
        }, (resident, execute) =>
        {
            var ready = new System.Windows.Forms.Timer { Interval = 10 };
            ready.Tick += async (_, _) =>
            {
                if (Private(resident, "initialization") is not Task { IsCompleted: true } || resident.Phase != ResidentPhase.Stopped) return;
                ready.Stop(); ready.Dispose(); await execute();
            };
            ready.Start();
        }, autoStart: false);
    }

    private static void UpdateCancelAfterEndedWatch()
    {
        using var fixture = ResidentFixture(); CleanRcStop(fixture);
        RunUpdateResident(fixture, async resident =>
        {
            int launches = 0; var stage = ResidentDownload(fixture, resident, () => launches++);
            var notices = new List<(string, string)>(); resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            int pid = fixture.State.Load()!.RclonePid;
            fixture.Respond = path => path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0}}") :
                path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + pid + "}");
            Task<int> watch = ResidentTests.Watch(resident)!;
            bool chosen = false; Exception? injection = null;
            EventHandler choose = async (_, _) =>
            {
                if (chosen || Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is not { } dialog) return;
                chosen = true;
                foreach (var button in Controls(dialog).OfType<Button>()) button.Enabled = false;
                dialog.ControlBox = false;
                try { CleanRcStop(fixture); await Task.Run(() => fixture.Controller.Stop()); await watch; }
                catch (Exception error) { injection = error; }
                finally { var cancel = (Button)dialog.CancelButton!; cancel.Enabled = true; cancel.PerformClick(); }
            };
            Application.Idle += choose;
            try { await resident.PrepareUpdateAsync(); }
            finally { Application.Idle -= choose; }
            if (injection is not null) throw injection;
            TestRunner.Assert(chosen); TestRunner.Equal(0, launches);
            TestRunner.Equal(ResidentPhase.Stopped, resident.Phase); TestRunner.Assert(!resident.HasPendingUpdate);
            var resume = FormTests.Field<ToolStripMenuItem>(resident, "pause");
            TestRunner.Assert(resume.Enabled && resume.Text == "再開" && FormTests.Field<NotifyIcon>(resident, "tray").Visible);
            TestRunner.Assert(!Directory.Exists(stage.Staging));
            TestRunner.Assert(!notices.Any(n => n.Item1 == "更新できませんでした"));
        });
    }

    private static void UpdateOtherActivity()
    {
        using var fixture = new Unit5Fixture("unit8r2-other-activity", writeBack: "5m");
        RunUpdateTray(fixture, async resident =>
        {
            fixture.Write("pending.txt", "other activity retained");
            await Task.Run(() => Unit5Fixture.Wait(() => fixture.Controller.Status().UploadsQueued == 1, "Pending upload did not appear."));
            var stage = UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(fixture.State.PathName));
            int launches = 0; resident.UpdateCommandOverride = _ => Task.FromResult(Result(stage));
            resident.UpdateLaunchOverride = _ => { launches++; return 1; };
            var notices = new List<(string, string)>(); resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            EventHandler choose = (_, _) =>
            {
                if (Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is { } dialog) ((Button)dialog.AcceptButton!).PerformClick();
            };
            Application.Idle += choose;
            try { await resident.PrepareUpdateAsync(); }
            finally { Application.Idle -= choose; }
            TestRunner.Equal(ResidentPhase.WaitingToExit, resident.Phase);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resident.AutomaticStopAwaitCheckpoint = () => { reached.TrySetResult(); return release.Task; };
            try
            {
                await Task.Run(() =>
                {
                    using var rc = new RcClient(fixture.State.Load()!);
                    foreach (var item in rc.Call("vfs/queue").GetProperty("queue").EnumerateArray())
                        rc.Call("vfs/queue-set-expiry", new { id = item.GetProperty("id").GetInt64(), expiry = -1 });
                    Unit5Fixture.Wait(() => fixture.Controller.Status().UploadsQueued == 0 && fixture.Controller.Status().UploadsInProgress == 0
                        && File.Exists(Path.Combine(fixture.RemoteRoot, "pending.txt")), "Pending upload did not drain.");
                    fixture.Controller.Stop();
                });
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Run(() => fixture.Start());
                int otherPid = fixture.State.Load()!.RclonePid;
                release.TrySetResult();
                await ResidentTests.Until(() => resident.Phase == ResidentPhase.Stopped, "Other activity did not leave the resident stopped.");
                AssertUpdateRefusal(resident, notices, stage.Staging); TestRunner.Equal(0, launches);
                TestRunner.Assert(resident.Snapshot.Error?.Contains("別のプロセス", StringComparison.Ordinal) == true);
                var sent = (Dictionary<string, DateTimeOffset>)Private(Private(resident, "throttle")!, "sent")!;
                TestRunner.Assert(sent.Keys.Any(key => key.StartsWith("このトレイの監視は停止しました\n", StringComparison.Ordinal)));
                using (var other = ProcessIdentity.Open(fixture.State.Load()!)) TestRunner.Assert(other is not null && other.Id == otherPid);
                resident.AutomaticStopAwaitCheckpoint = null;
                await resident.StartMountedAsync();
                TestRunner.Equal(ResidentPhase.Watching, resident.Phase); TestRunner.Equal(otherPid, fixture.State.Load()!.RclonePid);
            }
            finally { release.TrySetResult(); resident.AutomaticStopAwaitCheckpoint = null; }
        });
    }

    private static void RunUpdateTray(Unit5Fixture fixture, Func<ResidentContext, Task> scenario,
        Action<ResidentContext, Func<Task>>? configure = null, bool autoStart = true)
    {
        bool completed = false;
        Unit6iTests.RunTray(fixture, async resident => { await scenario(resident); completed = true; }, configure, autoStart);
        TestRunner.Assert(completed, "The resident exited before the update scenario assertions completed.");
    }
    private static void RunUpdateResident(Unit3aTests.RcFixture fixture, Func<ResidentContext, Task> scenario)
    {
        bool completed = false;
        ResidentTests.Run(fixture, async resident => { await scenario(resident); completed = true; });
        TestRunner.Assert(completed, "The resident exited before the update scenario assertions completed.");
    }
}
