using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tray;

internal sealed partial class ResidentContext
{
    private readonly System.Windows.Forms.Timer updateTimer = new();
    private readonly DateTimeOffset updateFirstDueUtc = DateTimeOffset.UtcNow.AddMinutes(2);
    private ToolStripMenuItem checkUpdate = null!, applyUpdate = null!;
    private UpdateDownloadResult? pendingUpdate;
    private string? latestUpdate;
    private bool updateBusy, updateStopAuthorized, updateStopRequested, updateFromStopped;
    private RuntimeState? updateCleanPause;
    private string[] relaunchArguments = [];
    internal Func<string, Task<JsonElement>>? UpdateCommandOverride { get; set; }
    internal Func<UpdateDownloadResult, int>? UpdateLaunchOverride { get; set; }
    internal Action<string, string, ToolTipIcon>? UpdateNoticeOverride { get; set; }
    internal Func<string, string, ToolTipIcon, bool>? UpdateNoticeDeliveryOverride { get; set; }
    internal string? UpdateExecutableOverride { get; set; }
    internal bool HasPendingUpdate => pendingUpdate is not null;
    private UpdateStore Updates => new(options.StatePath);
    private string UpdateExecutable => UpdateExecutableOverride ?? Path.Combine(AppContext.BaseDirectory, "dbxl.exe");
    private bool HasUpdateCli => UpdateCommandOverride is not null || File.Exists(UpdateExecutable);
    private void AddUpdateMenu(ContextMenuStrip menu)
    {
        checkUpdate = new("更新を確認", null, async (_, _) => await CheckUpdateAsync(true));
        applyUpdate = new("", null, async (_, _) =>
        {
            if (latestUpdate is null) return;
            if (!UpdatePackage.CanApply(AppContext.BaseDirectory)) { UnavailableUpdate(); return; }
            using var confirmation = new UpdateConfirmationForm(latestUpdate, Config.MountPoint);
            if (confirmation.ShowDialog() == DialogResult.OK) await PrepareUpdateAsync();
        }) { Visible = false };
        menu.Items.Add(checkUpdate); menu.Items.Add(applyUpdate);
        updateTimer.Interval = (int)TimeSpan.FromMinutes(2).TotalMilliseconds;
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Stop();
            try { await CheckUpdateAsync(false); } finally { ScheduleUpdateCheck(); }
        };
        updateTimer.Start();
    }
    internal static TimeSpan NextUpdateDelay(DateTimeOffset now, DateTimeOffset first, DateTimeOffset? checkedUtc)
    {
        DateTimeOffset due = checkedUtc is { } last && last.AddHours(24) > first ? last.AddHours(24) : first;
        return due > now ? due - now : TimeSpan.FromMilliseconds(1);
    }
    private void ScheduleUpdateCheck()
    {
        if (disposed || Phase == ResidentPhase.Exiting) { updateTimer.Stop(); return; }
        try
        {
            TimeSpan delay = Config.UpdateCheck && HasUpdateCli && configurationKnown
                ? NextUpdateDelay(DateTimeOffset.UtcNow, updateFirstDueUtc, Updates.LoadCheck().CheckedUtc)
                : TimeSpan.FromHours(24);
            updateTimer.Interval = (int)Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 1, int.MaxValue);
            updateTimer.Start();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { System.Diagnostics.Trace.WriteLine(error); updateTimer.Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds; updateTimer.Start(); }
    }
    private bool UpdateNotice(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, bool repeat = false)
    {
        if (UpdateNoticeDeliveryOverride is { } delivery) return delivery(title, text, icon);
        if (UpdateNoticeOverride is { } notice) { notice(title, text, icon); return true; }
        return Notify(title, text, icon, repeat ? "manual-update:" + Guid.NewGuid().ToString("N") : null);
    }
    private void UnavailableUpdate() => UpdateNotice("この場所では自動で更新できません",
        "新しいバージョンの zip を展開して入れ直してください。\n" + UpdateSource.ReleasesUrl, ToolTipIcon.Warning);
    private Task<JsonElement> RunUpdateCommand(string command) => UpdateCommandOverride is { } run
        ? run(command) : new UpdateCli(UpdateExecutable, options).RunAsync(command, cancellation.Token);
    internal async Task CheckUpdateAsync(bool manual)
    {
        if (disposed || updateBusy || !configurationKnown || Phase == ResidentPhase.Exiting) return;
        if (manual && Phase is not (ResidentPhase.Watching or ResidentPhase.Stopped)) return;
        if (!HasUpdateCli) { if (manual) UnavailableUpdate(); return; }
        try
        {
            if (!manual && (!Config.UpdateCheck || !Updates.IsDue(DateTimeOffset.UtcNow))) return;
            updateBusy = true; UpdateUpdateMenu();
            await Task.Run(Controller.State.EnsurePrivateDirectory);
            await Task.Run(() => Updates.ChangeCheck(s => s with { CheckedUtc = DateTimeOffset.UtcNow }));
            JsonElement json = await RunUpdateCommand("check");
            var result = json.Deserialize<UpdateCheckResult>(JsonFiles.Options) ?? throw new InvalidDataException("Missing update check result.");
            if (disposed) return;
            latestUpdate = result.UpdateAvailable ? result.LatestVersion : null;
            if (latestUpdate is { } version)
            {
                if (manual) UpdateNotice("新しいバージョンがあります", $"バージョン {version} に更新できます。トレイのメニューから更新してください。", repeat: true);
                else if (!Updates.WasNotified(version) && UpdateNotice("新しいバージョンがあります", $"バージョン {version} に更新できます。トレイのメニューから更新してください。"))
                    Updates.MarkNotified(version);
            }
            else if (manual) UpdateNotice("お使いのバージョンは最新です", "バージョン " + result.CurrentVersion);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (manual && !disposed) UpdateNotice("更新を確認できません", "時間をおいて、もう一度お試しください。", ToolTipIcon.Warning); }
        finally { updateBusy = false; if (!disposed) { UpdateUpdateMenu(); ScheduleUpdateCheck(); } }
    }
    private void UpdateUpdateMenu()
    {
        bool enabled = !updateBusy && pendingUpdate is null && Phase is ResidentPhase.Watching or ResidentPhase.Stopped;
        checkUpdate.Enabled = applyUpdate.Enabled = enabled;
        applyUpdate.Visible = latestUpdate is not null;
        if (latestUpdate is not null) applyUpdate.Text = "バージョン " + latestUpdate + " に更新";
    }
    internal async Task PrepareUpdateAsync()
    {
        if (updateBusy || pendingUpdate is not null || disposed || Phase is not (ResidentPhase.Watching or ResidentPhase.Stopped)) return;
        if (!HasUpdateCli || UpdateCommandOverride is null && !UpdatePackage.CanApply(AppContext.BaseDirectory)) { UnavailableUpdate(); return; }
        updateBusy = true; UpdateUpdateMenu();
        try
        {
            if (Phase == ResidentPhase.Stopped && !await Task.Run(CanUpdateStopped))
            { RefuseUnverifiedUpdate(); return; }
            await Task.Run(Controller.State.EnsurePrivateDirectory);
            JsonElement result = await RunUpdateCommand("download");
            pendingUpdate = result.Deserialize<UpdateDownloadResult>(JsonFiles.Options) ?? throw new InvalidDataException("Missing staging result.");
            updateStopAuthorized = false;
            if (disposed || Phase is not (ResidentPhase.Watching or ResidentPhase.Stopped)) { CancelPendingUpdate(); return; }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!disposed) UpdateNotice("更新をダウンロードできません", Ui.Failure(error), ToolTipIcon.Error); return; }
        finally { updateBusy = false; if (!disposed) UpdateUpdateMenu(); }
        updateStopRequested = true;
        await RequestStopAsync(true);
    }
    private void CancelPendingUpdate(bool keepStopRequest = false)
    {
        if (!keepStopRequest) updateStopRequested = false;
        updateStopAuthorized = false; updateFromStopped = false;
        if (pendingUpdate is not { } pending) return;
        pendingUpdate = null;
        try { Updates.CompleteStage(pending.Staging); UpdateCleanup.Run(Updates, AppContext.BaseDirectory, cleanupBackups: false); } catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(error); }
        if (!disposed) UpdateUpdateMenu();
    }
    private bool LaunchPendingUpdate()
    {
        if (sessionRequested) { CancelPendingUpdate(); return true; }
        if (pendingUpdate is not { } pending)
        { if (updateStopRequested) { RefuseUnverifiedUpdate(); return false; } return true; }
        if (!updateStopAuthorized) { RefuseUnverifiedUpdate(); return false; }
        try
        {
            int pid = UpdateLaunchOverride is { } launch ? launch(pending)
                : UpdateLauncher.Swap(pending, AppContext.BaseDirectory, options.StatePath, Environment.ProcessId, true, relaunchArguments);
            if (pid <= 0) throw new IOException("The update process was not created.");
            pendingUpdate = null; updateStopAuthorized = false; updateStopRequested = false; return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            CancelPendingUpdate(); SetPhase(ResidentPhase.Stopped); ScheduleUpdateCheck();
            UpdateNotice("更新できませんでした", "更新を始められなかったため、マウントは停止したままです。トレイのメニューから再開できます。", ToolTipIcon.Error);
            return false;
        }
    }
    private (StopResult? Stop, bool Other, bool Clean) StopWithUpdateProof(bool ownGenerationOnly, SupervisionOwnership? ownership, Task<int>? watch, ResidentPhase previous)
        => ControlGate.Execute(() =>
        {
            RuntimeState? before = Controller.State.Load();
            RuntimeState? generation = ownership?.Generation;
            bool previouslyClean = previous == ResidentPhase.Stopped && StoppedUpdateAllowed(before);
            bool owned = before is not null && generation is not null && watch is { IsCompleted: false }
                && before.GenerationId == generation.GenerationId && before.GenerationNumber == generation.GenerationNumber
                && before.RclonePid == generation.RclonePid && before.ProcessStartedUtc == generation.ProcessStartedUtc
                && before.SupervisorPid == generation.SupervisorPid && before.SupervisorStartedUtc == generation.SupervisorStartedUtc;
            using var process = before is null ? null : ProcessIdentity.Open(before);
            bool eligible = previouslyClean || owned && process is not null && !before!.StopRequested;
            StopResult? stop = null; bool other = false;
            if (ownGenerationOnly) other = Controller.ReconcileSupervision(ownership, supervisorEnded: watch is not { IsCompleted: false });
            else stop = Controller.Stop(false);
            RuntimeState? after = Controller.State.Load();
            bool clean = previous == ResidentPhase.Stopped
                ? eligible && !other && StoppedUpdateAllowed(after) && (before is null ? after is null || after.RclonePid == 0 && after.GenerationNumber == 0
                    : after is not null && SameUpdateGeneration(before, after) && after.StopSequence > before.StopSequence)
                : eligible && !other && CleanStop(after) && after!.StopSequence > (before?.StopSequence ?? 0)
                    && string.Equals(after.ConfigPath, options.ConfigPath, StringComparison.OrdinalIgnoreCase);
            updateFromStopped = clean && previous == ResidentPhase.Stopped;
            if (clean) updateCleanPause = after;
            return (stop, other, clean);
        });
    private bool CanUpdateStopped()
    {
        try { return ControlGate.Execute(() => StoppedUpdateAllowed(Controller.State.Load())); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { System.Diagnostics.Trace.WriteLine(error); return false; }
    }
    private bool StoppedUpdateAllowed(RuntimeState? state)
    {
        if (state is null) return true;
        if (!CleanStop(state) || !string.Equals(state.ConfigPath, options.ConfigPath, StringComparison.OrdinalIgnoreCase)) return false;
        using var child = ProcessIdentity.Open(state);
        return child is null;
    }
    private void RefuseUnverifiedUpdate()
    {
        CancelPendingUpdate(); SetPhase(ResidentPhase.Stopped); ScheduleUpdateCheck();
        UpdateNotice("更新できませんでした", "マウントの状態を確認できなかったため、更新を始めませんでした。トレイのメニューからマウントを再開し、状態を確かめてから、もう一度お試しください。", ToolTipIcon.Warning, repeat: true);
    }
    private static bool SameUpdateGeneration(RuntimeState a, RuntimeState b) =>
        a.GenerationId == b.GenerationId && a.GenerationNumber == b.GenerationNumber && a.RclonePid == b.RclonePid
        && a.ProcessStartedUtc == b.ProcessStartedUtc && string.Equals(a.ConfigPath, b.ConfigPath, StringComparison.OrdinalIgnoreCase);
    private void RememberCleanPause(SupervisionOwnership? ownership)
    {
        try
        {
            RuntimeState? state = Controller.State.Load();
            if (ownership?.Generation is { } generation && CleanStop(state) && SameUpdateGeneration(state!, generation)) updateCleanPause = state;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(error); }
    }
    private bool UpdateStopStillClean()
    {
        RuntimeState? state = Controller.State.Load();
        if (updateFromStopped && state is null && updateCleanPause is null) return true;
        bool clean = CleanStop(state) && updateCleanPause is { } receipt && SameUpdateGeneration(state!, receipt)
            && state!.StopSequence >= receipt.StopSequence;
        if (clean) updateCleanPause = state;
        return clean;
    }
    private static bool CleanStop(RuntimeState? state) => state is { StopRequested: true, StopInProgress: false,
        LaunchPending: false, RecoveryRequired: false, UncertainProcess: null, StopDisposition: StopDisposition.Clean };
    internal static (string Title, string Body) UpdateResultNotice(UpdateResult result)
    {
        if (result.Stage == "rollback") return ("更新に失敗し、元に戻せませんでした", "前のバージョンのファイルは次の場所にあります: " + result.BackupPath);
        string message = (result.Stage ?? (result.RolledBack ? "swap" : "verify")) switch
        {
            "target" => "DbxLight を置いたフォルダの状態を確認できませんでした。",
            "wait" => "前のバージョンの終了を確認できませんでした。",
            "locked" => "ほかのプログラムが DbxLight のファイルを使用中でした。",
            "swap" => "ファイルの入れ替えに失敗したため、元に戻しました。",
            _ => "ダウンロードした更新を確認できませんでした。"
        };
        return ("更新できませんでした", "前のバージョンのまま起動しました。" + message);
    }
    internal async Task ReadUpdateResultAsync()
    {
        try
        {
            UpdateResult? result = await Task.Run(Updates.ConsumeResult);
            if (result is not null && !disposed)
            {
                if (result.Success) UpdateNotice("バージョン " + result.Version + " に更新しました", "");
                else { var notice = UpdateResultNotice(result); UpdateNotice(notice.Title, notice.Body, ToolTipIcon.Warning); }
            }
            try { await Task.Run(() => UpdateCleanup.Run(Updates, AppContext.BaseDirectory)); }
            catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(error); }
            var known = Updates.LoadCheck().LatestVersion;
            if (UpdateVersion.TryParse(known, out var latest) && UpdateVersion.TryParse(UpdateVersion.Current, out var current) && latest.CompareTo(current) > 0)
                latestUpdate = known;
            if (!disposed) UpdateUpdateMenu();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(error); }
    }
    internal async Task ApplySettingsAsync(Func<DbxConfig, DbxConfig> update, bool allowPreferencesOnly = true)
    {
        if (Phase is not (ResidentPhase.Watching or ResidentPhase.Stopped))
            throw new InvalidOperationException("起動・停止・設定変更の処理が終わってから操作してください。");
        var expected = await Task.Run(Controller.Config.Load);
        var next = await DropboxCopyPaths.BindSettingsAsync(expected, update(expected).Validate(), DropboxFolderClient.Open,
            CancellationToken.None, mount: new FolderShareMount(Controller.Config.PathName, Controller.State.PathName));
        var saved = allowPreferencesOnly ? await Task.Run(() => Controller.TrySavePreferences(expected, next)) : null;
        if (saved is null)
        {
            string serialized = JsonSerializer.Serialize(expected, JsonFiles.Options);
            await ApplyConfigAsync(current => JsonSerializer.Serialize(current, JsonFiles.Options) == serialized ? next :
                throw new InvalidOperationException("別の操作で設定が変わりました。この画面を開き直してください。"));
            return;
        }
        Config = saved; ScheduleUpdateCheck(); await RefreshAfterChangeAsync();
    }
}
