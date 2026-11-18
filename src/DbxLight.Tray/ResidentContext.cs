using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tray;

internal sealed partial class ResidentContext : ApplicationContext
{
    private readonly AppOptions options;
    private readonly ResidentWindow dispatcher = new() { Text = "DbxLight resident" };
    private readonly NotifyIcon tray;
    private readonly Dictionary<TrayState, Icon> icons = Enum.GetValues<TrayState>().ToDictionary(s => s, TrayIconFactory.Create);
    private readonly System.Windows.Forms.Timer poll = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly NotificationThrottle throttle = new();
    private readonly QueueAgeTracker ages = new();
    private readonly RestartTracker restarts = new();
    private readonly DownloadNoticeTracker downloadNotices = new();
    private readonly ToolStripMenuItem pause, exit;
    private readonly HashSet<Form> windows = [];
    private readonly Func<ResidentContext, Task>? started;
    private Task<int>? supervisor;
    private SupervisionOwnership? supervisionOwnership;
    internal Func<Task>? AutomaticStopAwaitCheckpoint { get; set; }
    private Task? initialization, mountStartup;
    private readonly CancellationTokenSource initializationCancellation = new();
    private CancellationTokenSource? supervisionCancellation, preparationCancellation;
    internal Func<string, Task>? StartupAwaitCheckpoint { get; set; }
    internal Task? MenuStopTask { get; private set; }
    private Task? supervisorObservation;
    private TaskCompletionSource? refreshCompletion;
    private long displayGeneration;
    private int displayChanges;
    private bool refreshing, disposed;
    internal ResidentPhase Phase { get; private set; } = ResidentPhase.Initializing;
    internal Func<Task>? StopAwaitCheckpoint { get; set; }
    internal Action? SessionStateReadCheckpoint { get; set; }
    internal Action? MountStopCompletedCheckpoint { get; set; }
    internal Func<Task>? SetupCloseCheckpoint { get; set; }
    private bool Starting => Phase is ResidentPhase.Initializing or ResidentPhase.Starting;
    private bool Waiting => Phase is ResidentPhase.WaitingToPause or ResidentPhase.WaitingToExit;
    private string? startupError;
    private int failedObservations;
    private bool everMounted;
    private bool configurationKnown, sessionRequested, sessionStoppedClean;
    private StatusForm? statusForm;
    private SettingsForm? settingsForm;
    private SetupForm? setupForm;
    private HiddenFoldersForm? hiddenForm;
    public MountController Controller { get; }
    public DbxConfig Config { get; private set; } = new();
    public TraySnapshot Snapshot { get; private set; } = new(new(), [], [], false, null);
    public QueueAge? Oldest => ages.Oldest(DateTimeOffset.UtcNow);

    public ResidentContext(AppOptions options, SingleInstance instance, Func<ResidentContext, Task>? started = null, string[]? originalArguments = null)
    {
        this.options = options; this.started = started;
        relaunchArguments = RegistrationService.StartupArguments(options);
        Controller = new(new ConfigStore(options.ConfigPath), new StateStore(options.StatePath));
        dispatcher.QuerySessionEnd = QuerySessionEnd;
        dispatcher.SessionEndChanged = SessionEndChanged;
        _ = dispatcher.Handle;
        dispatcher.CloseRequested += async (_, _) => await RequestStopAsync(true);
        var menu = new ContextMenuStrip { Font = Ui.AppFont };
        menu.Items.Add("フォルダを開く", null, (_, _) => OpenFolder());
        menu.Items.Add("状態を見る", null, (_, _) => ShowStatus());
        menu.Items.Add("表示しないフォルダ", null, (_, _) => ShowHidden());
        menu.Items.Add("設定", null, (_, _) => ShowSettings());
        menu.Items.Add("ログインと接続先", null, async (_, _) => await ShowSetupAsync());
        AddUpdateMenu(menu);
        pause = new ToolStripMenuItem("一時停止", null, async (_, _) =>
        {
            if (Phase == ResidentPhase.Stopped) await StartMountedAsync();
            else await (MenuStopTask = RequestStopAsync(false));
        });
        menu.Items.Add(pause);
        exit = new ToolStripMenuItem("終了", null, async (_, _) => await (MenuStopTask = RequestStopAsync(true)));
        menu.Items.Add(exit);
        tray = new NotifyIcon { Icon = icons[TrayState.Preparing], Text = "DbxLight 準備中", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowStatus();
        poll.Tick += async (_, _) => await RefreshAsync();
        poll.Interval = 1000; poll.Start();
        instance.Listen(args => Post(async () => await HandleArgumentsAsync(args)));
        Post(async () => await (initialization = InitializeAsync()));
    }

    private void Post(Action action)
    {
        if (!disposed && !dispatcher.IsDisposed)
            try { dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private async Task InitializeAsync()
    {
        try
        {
            await StartupBoundaryAsync("initial-config", initializationCancellation.Token);
            DbxConfig loaded = await Task.Run(Controller.Config.Load);
            initializationCancellation.Token.ThrowIfCancellationRequested();
            Config = loaded; configurationKnown = true;
            await ReadUpdateResultAsync();
            if (string.IsNullOrWhiteSpace(Config.Remote))
            {
                var checks = await Task.Run(() => Doctor.Check(Config));
                initializationCancellation.Token.ThrowIfCancellationRequested();
                SetPhase(ResidentPhase.Stopped);
                Snapshot = new(new(), [], [], false, checks.Any(c => !c.Passed) ? "設定を確認してください。" : null);
                UpdateTray();
                await ShowSetupAsync();
            }
            else { SetPhase(ResidentPhase.Stopped); await StartMountedAsync(); }
            initializationCancellation.Token.ThrowIfCancellationRequested();
            if (options.HidePath is not null || options.WebPath is not null)
                await HandleArgumentsAsync(options.HidePath is not null ? ["--hide", options.HidePath] : ["--web", options.WebPath!]);
            if (options.WebCopyPath is { } link) await HandleArgumentsAsync(["--web-copy-shell", link + "|"]);
            if (options.SharePath is { } sharePath) await HandleArgumentsAsync(["--share-shell", sharePath + "|"]);
            if (started is not null) await started(this);
        }
        catch (OperationCanceledException) when (initializationCancellation.IsCancellationRequested)
        { if (Phase == ResidentPhase.Initializing) SetPhase(ResidentPhase.Stopped); UpdateTray(); }
        catch (Exception e)
        {
            if (Phase == ResidentPhase.Initializing) SetPhase(ResidentPhase.Stopped);
            Notify("起動できません", Ui.Failure(e), ToolTipIcon.Error);
            if (started is not null)
            {
                Environment.ExitCode = 1;
                SetPhase(ResidentPhase.Exiting);
                try { await CompleteStopAsync(true, false, ResidentPhase.Stopped, ownGenerationOnly: true); }
                catch (Exception stopError) { ReportStopFailure(true, stopError, ResidentPhase.Exiting); }
            }
            else { ShowSettings(); poll.Interval = 30000; }
        }
    }
    private async Task StartupBoundaryAsync(string stage, CancellationToken token)
    {
        if (StartupAwaitCheckpoint is { } checkpoint) await checkpoint(stage);
        token.ThrowIfCancellationRequested();
    }
    public Task StartMountedAsync()
    {
        if (Phase != ResidentPhase.Stopped) return Task.CompletedTask;
        SetPhase(ResidentPhase.Starting);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        supervisionCancellation = source;
        var preparing = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        preparationCancellation = preparing;
        return mountStartup = StartMountedCoreAsync(source, preparing);
    }
    private async Task StartMountedCoreAsync(CancellationTokenSource source, CancellationTokenSource preparing)
    {
        CancellationToken token = preparing.Token;
        bool mountedSuccessfully = false;
        Task<int>? ownedTask = null;
        void CheckCurrentStartup()
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(preparationCancellation, preparing)) throw new PreparationCancelledException();
        }
        sessionStoppedClean = false; startupError = null;
        BeginDisplayChange();
        try
        {
            // Only the observer consumes/logs a completed task's exception.
            if (supervisorObservation is not null) await supervisorObservation;
            CheckCurrentStartup();
            supervisor = null; supervisorObservation = null;
            DbxConfig loaded = await Task.Run(Controller.Config.Load);
            CheckCurrentStartup();
            Config = loaded; configurationKnown = true;
            await StartupBoundaryAsync("before-doctor", token);
            var checks = await Task.Run(() => Doctor.Check(Config));
            CheckCurrentStartup();
            bool ownMount = await Task.Run(() =>
            {
                RuntimeState? state = Controller.State.Load();
                using var process = state is null ? null : ProcessIdentity.Open(state);
                return process is not null && state!.ConfigPath.Equals(options.ConfigPath, StringComparison.OrdinalIgnoreCase);
            });
            CheckCurrentStartup();
            var failed = checks.Where(c => !c.Passed && !(ownMount && c.Name == "mountPoint")).ToArray();
            if (failed.Length > 0) throw new IOException("起動に必要な準備を確認してください。\n" + string.Join("\n", failed.Select(c => c.Detail)));
            var mounted = new TaskCompletionSource<StartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken watchToken = source.Token;
            var ownership = new SupervisionOwnership();
            supervisionOwnership = ownership;
            Task<int> task = Task.Run(() => Controller.RunForResident(ownership, watchToken, result => mounted.TrySetResult(result), token));
            supervisor = ownedTask = task;
            supervisorObservation = ObserveSupervisorAsync(task, source);
            await Task.WhenAny(mounted.Task, task);
            if (!ReferenceEquals(supervisor, task) || !ReferenceEquals(preparationCancellation, preparing))
                throw new PreparationCancelledException();
            if (!mounted.Task.IsCompletedSuccessfully)
            {
                await task;
                throw new PreparationCancelledException();
            }
            mountedSuccessfully = true;
            everMounted = true;
        }
        catch (Exception e) when (e.InnerException is null && StartupFailure.CleanupFailures(e).Count == 0 &&
            (e is PreparationCancelledException || e.Data["PreparationCancelled"] is true || e is OperationCanceledException && token.IsCancellationRequested))
        {
            if (ReferenceEquals(preparationCancellation, preparing))
                Snapshot = new(new DbxStatus(), [], [], false, null);
        }
        catch (Exception e)
        {
            if (!ReferenceEquals(preparationCancellation, preparing)) return;
            StartupFailure.Annotate(e, Config, options.ConfigPath);
            startupError = Ui.Failure(e);
            Notify("マウントを開始できません", startupError, ToolTipIcon.Error);
            Snapshot = new(new(), [], [], false, startupError);
            if (started is not null) throw;
        }
        finally
        {
            await StartupBoundaryAsync("startup-finalizing", CancellationToken.None);
            bool current = ReferenceEquals(preparationCancellation, preparing);
            if (ownedTask is null)
            {
                if (ReferenceEquals(supervisionCancellation, source)) supervisionCancellation = null;
                source.Dispose();
            }
            if (ReferenceEquals(preparationCancellation, preparing)) preparationCancellation = null;
            preparing.Dispose();
            EndDisplayChange();
            if (current && !disposed && !cancellation.IsCancellationRequested && Phase == ResidentPhase.Starting)
            {
                if (ReferenceEquals(supervisor, ownedTask) && ownedTask is { IsCompleted: true })
                    await RestoreSupervisionAsync(ResidentPhase.Stopped, false);
                else SetPhase(mountedSuccessfully && ReferenceEquals(supervisor, ownedTask) && ownedTask is { IsCompleted: false }
                    ? ResidentPhase.Watching : ResidentPhase.Stopped);
            }
        }
        if (mountedSuccessfully) await StartupBoundaryAsync("mounted-before-refresh", CancellationToken.None);
        await RefreshAfterChangeAsync();
    }
    private void SupervisorEnded(Task<int> task)
    {
        if (!ReferenceEquals(supervisor, task)) return;
        // Initial completion is committed only by StartMountedCoreAsync, after its cleanup.
        if (Phase is not (ResidentPhase.Watching or ResidentPhase.WaitingToPause or ResidentPhase.WaitingToExit)) return;
        bool shouldExit = Phase == ResidentPhase.WaitingToExit;
        ResidentPhase checking = shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping;
        SetPhase(checking);
        // Let this observer finish before the normal stop awaits supervisorObservation.
        Post(async () =>
        {
            if (!ReferenceEquals(supervisor, task) || Phase != checking) return;
            await RestoreSupervisionAsync(ResidentPhase.Stopped, shouldExit);
        });
    }
    private async Task ObserveSupervisorAsync(Task<int> task, CancellationTokenSource source)
    {
        try
        {
            int result = await task;
            SupervisorEnded(task);
            if (ReferenceEquals(supervisor, task) && result != 0 && Phase != ResidentPhase.Exiting)
                Notify("マウントを起動し直せません", "自動で起動し直せませんでした。設定とログを確認して、メニューの「再開」を押してください。", ToolTipIcon.Error);
        }
        catch (Exception e)
        {
            SupervisorEnded(task);
            try
            {
                string logDir = Config.LogDir;
                await Task.Run(() =>
                {
                    if (!MountLogStore.IsLocalPath(logDir)) throw new IOException("Application event recording is unavailable.");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "supervisor.log"),
                        DateTime.UtcNow.ToString("O") + " Supervisor task failed: " + e.GetType().Name + ": " + StartupFailure.PublicMessage(e) + "\n",
                        new System.Text.UTF8Encoding(false));
                });
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.WriteLine("Supervisor failure log unavailable: " + logError.Message); }
            if (ReferenceEquals(supervisor, task) && Phase != ResidentPhase.Exiting)
                Notify("マウントが停止しました", Ui.Failure(e), ToolTipIcon.Error);
        }
        if (ReferenceEquals(supervisionCancellation, source)) supervisionCancellation = null;
        source.Dispose();
        if (Phase != ResidentPhase.Exiting && ReferenceEquals(supervisor, task)) await RefreshAsync();
    }
    public async Task RefreshAsync()
    {
        if (refreshing || Phase == ResidentPhase.Exiting || disposed || displayChanges > 0 && Phase != ResidentPhase.Starting) return;
        refreshing = true;
        long generation = displayGeneration;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshCompletion = completion;
        try
        {
            var read = await Task.Run(() =>
            {
                var snapshot = SnapshotReader.Read(Controller);
                return (Config: snapshot.Status.Preparation is null && Phase != ResidentPhase.Starting ? Controller.Config.Load() : Config, Snapshot: snapshot);
            });
            if (generation != displayGeneration || disposed || Phase == ResidentPhase.Exiting) return;
            if (read.Snapshot.Status.Preparation is not null)
            {
                Snapshot = read.Snapshot with { Error = startupError ?? read.Snapshot.Error }; UpdateTray(); statusForm?.UpdateSnapshot(Snapshot, Oldest, Config);
                poll.Interval = 1000; return;
            }
            if (generation != displayGeneration || displayChanges > 0) return;
            bool current = await Task.Run(() => SnapshotReader.IsCurrent(Controller, read.Snapshot));
            if (generation != displayGeneration || displayChanges > 0) return;
            if (!current)
            {
                Snapshot = Snapshot with { Confirming = true };
                UpdateTray(); statusForm?.UpdateSnapshot(Snapshot, Oldest, Config);
                poll.Interval = 1000;
                return;
            }
            Config = read.Config;
            Snapshot = read.Snapshot with { Error = startupError ?? read.Snapshot.Error };
            if (Snapshot.QueueKnown && Snapshot.Status.RclonePid is { } pid) ages.Observe(pid, Snapshot.Queue, DateTimeOffset.UtcNow);
            else if (!Snapshot.Status.Running && Phase == ResidentPhase.Stopped) ages.Observe(0, [], DateTimeOffset.UtcNow);
            if (restarts.Observe(Snapshot.RestartCount) && Snapshot.Status.Running)
                Notify("マウントが停止したため起動し直しました", "状態画面を開き、アップロード待ちと失敗したファイルを確認してください。", ToolTipIcon.Warning);
            UpdateTray();
            statusForm?.UpdateSnapshot(Snapshot, Oldest, Config);
            poll.Interval = (int)TrayPresentation.PollInterval(Snapshot.Status, Waiting).TotalMilliseconds;
            if (downloadNotices.Observe(Snapshot.Identity, Snapshot.Status.DownloadActivity))
                Notify("短時間に多くのファイルをダウンロードしています",
                    "必要なファイルだけ開き、一覧・名前の検索に dbxl ls / find を使ってください。ダウンロードは続けます。", ToolTipIcon.Warning, DownloadNoticeTracker.Key(Snapshot.Identity, Snapshot.Status.DownloadActivity!));
            var newRescues = Snapshot.Rescued?.Where(r => r.RecordStatus == "verified" && !r.NotificationShown && !r.Acknowledged).ToArray() ?? [];
            if (newRescues.Length > 0)
            {
                Notify("未アップロードのファイルを退避しました",
                    $"退避したファイルが {newRescues.Length} 件あります。状態画面で理由と保存先を確認し、必要な内容を自分で戻してください。", ToolTipIcon.Warning,
                    "rescue:" + string.Join(',', newRescues.Select(r => r.Id)));
                await Task.Run(() => Controller.MarkRescueNotified(newRescues.Select(r => r.Id)));
            }
            if (generation != displayGeneration || disposed || Phase == ResidentPhase.Exiting) return;
            bool failed = Snapshot.Status.ErroredFiles > 0 || Snapshot.Queue.Any(q => q.Tries > 0);
            failedObservations = failed ? failedObservations + 1 : 0;
            if (failedObservations >= 2) Notify("アップロードの失敗が続いています", "状態画面を開き、失敗したファイルのパスを確認してください。", ToolTipIcon.Error);
            if (Oldest?.Elapsed > TimeSpan.FromMinutes(10))
                Notify("アップロード待ちが 10 分を超えています", "接続と失敗したファイルを確認してください。長く残ったアップロード待ちは、Dropbox 上の新しい版を上書きすることがあります。", ToolTipIcon.Warning);
            if (everMounted && Phase == ResidentPhase.Watching && !Snapshot.Status.Running)
                Notify("マウントが停止しました", "自動で起動し直しています。戻らない場合は状態とログを確認してください。", ToolTipIcon.Warning);
            if (sessionRequested && !sessionStoppedClean && Phase is ResidentPhase.Watching or ResidentPhase.Stopped &&
                Snapshot.Status.UploadsQueued == 0 && Snapshot.Status.UploadsInProgress == 0)
                _ = PrepareSessionStopAsync();
            if (Waiting && Snapshot.Status.UploadsQueued == 0 && Snapshot.Status.UploadsInProgress == 0)
            {
                bool shouldExit = Phase == ResidentPhase.WaitingToExit;
                SetPhase(shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping);
                try { await CompleteStopAsync(shouldExit, false, ResidentPhase.Watching, ownGenerationOnly: true); }
                catch (PendingUploadsException) { await RestoreSupervisionAsync(shouldExit ? ResidentPhase.WaitingToExit : ResidentPhase.WaitingToPause, shouldExit); }
                catch (Exception e) { ReportStopFailure(shouldExit, e, shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping); }
            }
        }
        catch (Exception e) { ReportRefreshFailure(Phase, e, (title, detail, icon) => Notify(title, detail, icon)); }
        finally { refreshing = false; refreshCompletion = null; completion.TrySetResult(); }
    }
    internal static void ReportRefreshFailure(ResidentPhase phase, Exception error, Action<string, string, ToolTipIcon> notify)
    {
        if (ShouldNotifyRefreshFailure(phase, error)) notify("状態を確認できません", Ui.Failure(error), ToolTipIcon.Warning);
    }
    internal static bool ShouldNotifyRefreshFailure(ResidentPhase phase, Exception error) =>
        error is not ControlBusyException || phase is not (ResidentPhase.Stopping or ResidentPhase.Exiting);
    private void BeginDisplayChange()
    {
        sessionStoppedClean = false;
        displayGeneration++; displayChanges++;
        Snapshot = Snapshot with { Confirming = true };
        UpdateTray(); statusForm?.UpdateSnapshot(Snapshot, Oldest, Config);
    }
    private void EndDisplayChange() { displayGeneration++; displayChanges--; }
    private async Task RefreshAfterChangeAsync()
    {
        if (displayChanges > 0 || Phase == ResidentPhase.Exiting || disposed) return;
        if (refreshCompletion is { } pending) await pending.Task;
        await RefreshAsync();
    }
    private void SetPhase(ResidentPhase value)
    {
        Phase = value;
        UpdateUpdateMenu();
        pause.Text = value switch
        {
            ResidentPhase.Stopped => "再開",
            ResidentPhase.WaitingToPause or ResidentPhase.WaitingToExit => "停止の待機を取り消す",
            ResidentPhase.StopFailed => "停止をやり直す",
            ResidentPhase.ExitFailed => "終了をやり直す",
            _ => "一時停止"
        };
        pause.Enabled = value is ResidentPhase.Initializing or ResidentPhase.Starting or ResidentPhase.Watching or ResidentPhase.Stopped
            or ResidentPhase.WaitingToPause or ResidentPhase.WaitingToExit or ResidentPhase.StopFailed or ResidentPhase.ExitFailed;
        exit.Enabled = pause.Enabled;
        UpdateTray();
    }
    private void UpdateTray()
    {
        tray.Icon = icons[TrayPresentation.State(Snapshot, Starting)];
        tray.Text = TrayPresentation.Tooltip(Snapshot, Config, Starting);
        settingsForm?.UpdateSnapshot(Snapshot, Config);
    }
    public bool Notify(string title, string text, ToolTipIcon icon, string? notificationKey = null)
    {
        if (disposed || !throttle.Allow(notificationKey ?? title + "\n" + text, DateTimeOffset.UtcNow)) return false;
        tray.ShowBalloonTip(8000, title, text, icon);
        return true;
    }
    private SessionEndDecision QuerySessionEnd()
    {
        CancelPendingUpdate();
        sessionRequested = true;
        SessionEndDecision decision = SessionEndPolicy.Decide(Snapshot.ActiveConfig ?? Config, Snapshot.Status,
            sessionStoppedClean, !configurationKnown || displayChanges > 0 ||
                !SnapshotReader.IsCurrentForSession(Controller, Snapshot, Config), Snapshot.PendingCacheClean);
        if (decision.Block && Phase is ResidentPhase.Initializing or ResidentPhase.Starting or ResidentPhase.Watching or ResidentPhase.Stopped)
            _ = PrepareSessionStopAsync();
        return decision;
    }
    private async Task PrepareSessionStopAsync()
    {
        if (Phase is not (ResidentPhase.Initializing or ResidentPhase.Starting or ResidentPhase.Watching or ResidentPhase.Stopped)) return;
        ResidentPhase previous = Phase;
        SetPhase(ResidentPhase.Stopping);
        bool stopped = false;
        try
        {
            await StopMountAsync(false, previous); stopped = true;
            if (Phase != ResidentPhase.Stopping || disposed || cancellation.IsCancellationRequested) return;
            SessionStateReadCheckpoint?.Invoke();
            RuntimeState? state = await Task.Run(Controller.State.Load);
            if (Phase != ResidentPhase.Stopping || disposed || cancellation.IsCancellationRequested) return;
            sessionStoppedClean = state is null || (state.StopDisposition == StopDisposition.Clean && !state.RecoveryRequired);
            SetPhase(ResidentPhase.Stopped);
            if (sessionStoppedClean) dispatcher.ClearShutdownReason();
        }
        catch (PendingUploadsException)
        {
            if (Phase != ResidentPhase.Stopping || disposed || cancellation.IsCancellationRequested) return;
            if (stopped) SetPhase(ResidentPhase.StopFailed);
            else await RestoreSupervisionAsync(ResidentPhase.Watching, false);
        }
        catch (Exception e)
        {
            if (Phase != ResidentPhase.Stopping || disposed || cancellation.IsCancellationRequested) return;
            SetPhase(ResidentPhase.StopFailed);
            System.Diagnostics.Trace.WriteLine("Session stop failed: " + e.Message);
        }
        finally
        {
            if (!disposed && Phase != ResidentPhase.Exiting) { poll.Interval = 1000; await RefreshAsync(); }
        }
    }
    private void SessionEndChanged(bool ending)
    {
        CancelPendingUpdate();
        sessionRequested = false;
        if (ending)
        {
            // A forced end leaves Running/StopUncertain durable; B1 recovers it next time.
            SetPhase(ResidentPhase.Exiting);
            initializationCancellation.Cancel(); preparationCancellation?.Cancel(); supervisionCancellation?.Cancel(); cancellation.Cancel();
            poll.Stop(); tray.Visible = false;
            ExitThread();
        }
    }
    private async Task RequestStopAsync(bool shouldExit)
    {
        shouldExit |= Phase == ResidentPhase.ExitFailed;
        if (Waiting)
        {
            if (!shouldExit) CancelPendingUpdate();
            await RestoreSupervisionAsync(shouldExit ? ResidentPhase.WaitingToExit : ResidentPhase.Watching, shouldExit);
            return;
        }
        if (Phase is not (ResidentPhase.Initializing or ResidentPhase.Starting or ResidentPhase.Watching or ResidentPhase.Stopped
            or ResidentPhase.StopFailed or ResidentPhase.ExitFailed)) return;
        ResidentPhase previous = Phase;
        ResidentPhase stopping = shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping;
        SetPhase(stopping);
        try
        {
            // Set the phase before the first await, including test-delayed stops.
            if (StopAwaitCheckpoint is { } checkpoint) await checkpoint();
            if (Phase != stopping || disposed || cancellation.IsCancellationRequested) return;
            await CompleteStopAsync(shouldExit, false, previous);
        }
        catch (PendingUploadsException refusal)
        {
            if (Phase != stopping || disposed || cancellation.IsCancellationRequested) return;
            long? count = refusal.UploadsQueued is { } queued && refusal.UploadsInProgress is { } uploading ? queued + uploading : null;
            using var choice = new StopChoiceForm(count, shouldExit, refusal.Message, allowForce: pendingUpdate is null);
            choice.ShowDialog();
            if (Phase != stopping || disposed || cancellation.IsCancellationRequested) return;
            if (choice.Choice == StopChoice.Force)
            {
                try { await CompleteStopAsync(shouldExit, true, previous); }
                catch (Exception e) { ReportStopFailure(shouldExit, e, shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping); }
            }
            else if (supervisor is not { IsCompleted: false })
            {
                bool canceledUpdate = choice.Choice == StopChoice.Cancel && updateStopRequested;
                CancelPendingUpdate(keepStopRequest: choice.Choice != StopChoice.Cancel);
                await RestoreSupervisionAsync(ResidentPhase.Watching, shouldExit && !canceledUpdate);
            }
            else if (choice.Choice == StopChoice.Wait)
            {
                SetPhase(shouldExit ? ResidentPhase.WaitingToExit : ResidentPhase.WaitingToPause);
                Notify("アップロードが終わるまで待ちます", count is { } n ? TrayPresentation.StopWaiting(n) : "アップロード待ちを確認できるまで停止しません。", ToolTipIcon.Info);
                poll.Interval = 5000;
            }
            else { CancelPendingUpdate(); SetPhase(ResidentPhase.Watching); }
        }
        catch (Exception e) { ReportStopFailure(shouldExit, e, shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping); }
    }
    private void ReportStopFailure(bool shouldExit, Exception error, ResidentPhase expected)
    {
        if (Phase != expected || disposed || cancellation.IsCancellationRequested) return;
        CancelPendingUpdate();
        SetPhase(shouldExit ? ResidentPhase.ExitFailed : ResidentPhase.StopFailed);
        Notify("停止できません", Ui.Failure(error), ToolTipIcon.Error);
    }
    private async Task RestoreSupervisionAsync(ResidentPhase target, bool shouldExit)
    {
        // The task itself retains completion while a modal stop choice is open.
        // No await separates this check from restoring a live supervisory phase.
        if (disposed || cancellation.IsCancellationRequested) return;
        if (supervisor is { IsCompleted: false }) { SetPhase(target); return; }
        CancelPendingUpdate(keepStopRequest: true);
        SetPhase(shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping);
        ResidentPhase checking = Phase;
        Task<int>? ended = supervisor;
        Task? observation = supervisorObservation;
        SupervisionOwnership? ownership = supervisionOwnership;
        try
        {
            if (AutomaticStopAwaitCheckpoint is { } checkpoint) await checkpoint();
            if (!ReferenceEquals(supervisor, ended) || Phase != checking || disposed || cancellation.IsCancellationRequested) return;
            bool otherActive = await Task.Run(() => Controller.ReconcileSupervision(ownership));
            if (observation is not null) await observation;
            if (!ReferenceEquals(supervisor, ended) || Phase != checking || disposed || cancellation.IsCancellationRequested) return;
            supervisor = null; supervisorObservation = null; supervisionOwnership = null;
            if (otherActive) ExplainOtherActivity();
            await FinishStopAsync(shouldExit, checking);
        }
        catch (Exception e) { ReportStopFailure(shouldExit, e, shouldExit ? ResidentPhase.Exiting : ResidentPhase.Stopping); }
    }
    private void ExplainOtherActivity()
    {
        CancelPendingUpdate(keepStopRequest: true);
        const string message = "このトレイの監視は停止済みです。別のプロセスによる動作または起動準備の記録が残っています。状態を確認してください。";
        startupError = startupError is null ? message : startupError + "\n" + message;
        Snapshot = Snapshot with { Error = startupError };
        Notify("このトレイの監視は停止しました", startupError, ToolTipIcon.Info);
    }
    private async Task StopMountAsync(bool force, ResidentPhase previous, bool ownGenerationOnly = false)
    {
        updateStopAuthorized = false;
        if (force) CancelPendingUpdate();
        Task? pendingStartup = previous is ResidentPhase.Initializing or ResidentPhase.Starting ? mountStartup : null;
        Task? pendingInitialization = previous is ResidentPhase.Initializing or ResidentPhase.Starting ? initialization : null;
        Task<int>? ownedWatch = supervisor;
        bool hadLiveWatch = ownedWatch is { IsCompleted: false };
        SupervisionOwnership? ownedGeneration = supervisionOwnership;
        bool rememberPause = pendingUpdate is null && !force && hadLiveWatch;
        Task? observation = supervisorObservation;
        CancellationTokenSource? watching = supervisionCancellation;
        CancellationTokenSource? preparing = preparationCancellation;
        if (previous is ResidentPhase.Initializing or ResidentPhase.Starting)
        {
            initializationCancellation.Cancel();
            preparing?.Cancel();
        }
        if (pendingUpdate is not null && !force)
        {
            SupervisionOwnership? ownership = supervisionOwnership;
            var proof = await Task.Run(() => StopWithUpdateProof(ownGenerationOnly, ownership, ownedWatch, previous));
            updateStopAuthorized = proof.Clean;
            if (!proof.Clean) CancelPendingUpdate(keepStopRequest: true);
            if (proof.Other) ExplainOtherActivity();
            if (proof.Stop?.PreparationCancellationRequested == true)
                Notify("起動の準備を止めます", proof.Stop.Message!, ToolTipIcon.Info);
        }
        else if (ownGenerationOnly)
        {
            if (await Task.Run(() => Controller.ReconcileSupervision(supervisionOwnership,
                supervisorEnded: ownedWatch is not { IsCompleted: false }))) ExplainOtherActivity();
        }
        else
        {
            StopResult stop = await Task.Run(() => Controller.Stop(force));
            if (stop.PreparationCancellationRequested)
                Notify("起動の準備を止めます", stop.Message!, ToolTipIcon.Info);
        }
        MountStopCompletedCheckpoint?.Invoke();
        if (ReferenceEquals(supervisionCancellation, watching)) watching?.Cancel();
        if (pendingStartup is not null) await pendingStartup;
        if (pendingInitialization is not null) await pendingInitialization;
        if (observation is not null) await observation;
        if (rememberPause) await Task.Run(() => RememberCleanPause(ownedGeneration));
        if (pendingUpdate is not null && updateStopAuthorized && !await Task.Run(UpdateStopStillClean)) CancelPendingUpdate(keepStopRequest: true);
        if (ReferenceEquals(supervisor, ownedWatch)) { supervisor = null; supervisionOwnership = null; }
        if (ReferenceEquals(supervisorObservation, observation)) supervisorObservation = null;
    }
    private async Task CompleteStopAsync(bool shouldExit, bool force, ResidentPhase previous, bool ownGenerationOnly = false)
    {
        ResidentPhase stopping = Phase;
        await StopMountAsync(force, previous, ownGenerationOnly);
        await FinishStopAsync(shouldExit, stopping);
    }
    private async Task FinishStopAsync(bool shouldExit, ResidentPhase stopping)
    {
        if (Phase != stopping || disposed || cancellation.IsCancellationRequested) return;
        if (shouldExit)
        {
            if (!LaunchPendingUpdate()) { await RefreshAsync(); return; }
            SetPhase(ResidentPhase.Exiting); poll.Stop(); cancellation.Cancel();
            foreach (Form form in windows.ToArray()) form.Close();
            tray.Visible = false; ExitThread();
        }
        else { SetPhase(ResidentPhase.Stopped); await RefreshAsync(); }
    }
    public Task ShutdownOwnedAsync()
    {
        CancelPendingUpdate();
        ResidentPhase previous = Phase;
        SetPhase(ResidentPhase.Exiting);
        return CompleteStopAsync(true, true, previous);
    }
    private ResidentPhase BeginConfigChange()
    {
        if (Phase is not (ResidentPhase.Watching or ResidentPhase.Stopped))
            throw new InvalidOperationException("起動・停止・設定変更の処理が終わってから操作してください。");
        ResidentPhase previous = Phase;
        SetPhase(ResidentPhase.Changing);
        BeginDisplayChange();
        return previous;
    }
    private bool OwnsConfigChange(long generation) => !disposed && Phase == ResidentPhase.Changing && displayGeneration == generation;
    private async Task FinishConfigChangeAsync(ResidentPhase previous, long generation, bool stopped, bool refused, bool resume)
    {
        bool current = OwnsConfigChange(generation);
        EndDisplayChange();
        if (!current) return;
        if (stopped)
        {
            SetPhase(ResidentPhase.Stopped);
            if (resume && previous == ResidentPhase.Watching && !string.IsNullOrWhiteSpace(Config.Remote))
                await StartMountedAsync();
        }
        else if (refused) await RestoreSupervisionAsync(previous, false);
        else SetPhase(ResidentPhase.StopFailed);
        await RefreshAfterChangeAsync();
    }
    public async Task ApplyConfigAsync(Func<DbxConfig, DbxConfig> update)
    {
        ResidentPhase previous = BeginConfigChange();
        long generation = displayGeneration;
        bool stopped = false, refused = false;
        try
        {
            await StopMountAsync(false, previous); stopped = true;
            if (!OwnsConfigChange(generation)) return;
            DbxConfig updated = await Task.Run(() => Controller.UpdateConfig(update));
            if (OwnsConfigChange(generation)) Config = updated;
        }
        catch (PendingUploadsException) { refused = true; throw; }
        finally { await FinishConfigChangeAsync(previous, generation, stopped, refused, true); }
    }
    private async Task LogoutAsync()
    {
        ResidentPhase previous = BeginConfigChange();
        long generation = displayGeneration;
        bool stopped = false, refused = false;
        try
        {
            await StopMountAsync(false, previous); stopped = true;
            if (!OwnsConfigChange(generation)) return;
            DbxConfig updated = await new AccountSettings(Controller).LogoutAsync();
            if (OwnsConfigChange(generation)) Config = updated;
        }
        catch (PendingUploadsException) { refused = true; throw; }
        finally { await FinishConfigChangeAsync(previous, generation, stopped, refused, false); }
    }
    private async Task SetWriteAccessAsync(DbxConfig expected, bool enabled, bool confirmed)
    {
        ResidentPhase previous = BeginConfigChange();
        long generation = displayGeneration;
        bool stopped = false, refused = false;
        try
        {
            await StopMountAsync(false, previous); stopped = true;
            if (!OwnsConfigChange(generation)) return;
            DbxConfig updated = await Task.Run(() => new WriteAccessSettings(Controller).SetEnabled(expected, enabled, confirmed));
            if (OwnsConfigChange(generation)) Config = updated;
        }
        catch (PendingUploadsException) { refused = true; throw; }
        finally { await FinishConfigChangeAsync(previous, generation, stopped, refused, true); }
    }

    private void OpenFolder()
    {
        try
        {
            if (!Snapshot.Status.Ready) { Notify("フォルダを開けません", "マウントが停止しています。メニューの「再開」を押してください。", ToolTipIcon.Warning); return; }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { MountPaths.CanonicalMount(Config.MountPoint) } });
        }
        catch (Exception e) { Notify("フォルダを開けません", e.Message, ToolTipIcon.Warning); }
    }
    private T Track<T>(T form) where T : Form
    {
        windows.Add(form);
        form.FormClosed += (_, _) => { windows.Remove(form); form.Dispose(); };
        return form;
    }
    public SettingsForm CreateSettingsForm()
    {
        SettingsForm? form = null;
        form = new SettingsForm(Config, new RegistrationService(options.RegistryRoot),
            Environment.ProcessPath, options,
            update => ApplySettingsAsync(update, allowPreferencesOnly: !form!.AutoStartChanged),
            () => ShowSetupAsync(startAtLogin: true), LogoutAsync, SetWriteAccessAsync);
        form.UpdateSnapshot(Snapshot, Config);
        return form;
    }
    public HiddenFoldersForm CreateHiddenForm() => new(Config, ApplyConfigAsync,
        () => Notify("表示しないフォルダを適用しました", "この PC のフォルダの表示を更新しました。", ToolTipIcon.Info));
    public StatusForm CreateStatusForm()
    {
        var form = new StatusForm();
        form.UpdateSnapshot(Snapshot, Oldest, Config);
        return form;
    }
    internal SetupForm CreateSetupForm() => new(new SetupSession(Controller));
    private async Task ShowSetupAsync(bool startAtLogin = false)
    {
        if (setupForm is not null && !setupForm.IsDisposed) { setupForm.Show(); setupForm.Activate(); return; }
        if (Phase is not (ResidentPhase.Watching or ResidentPhase.Stopped)) return;
        ResidentPhase previous = BeginConfigChange();
        long generation = displayGeneration;
        try { await StopMountAsync(false, previous); }
        catch (Exception e)
        {
            await FinishConfigChangeAsync(previous, generation, false, e is PendingUploadsException, false);
            throw;
        }
        if (!OwnsConfigChange(generation)) { EndDisplayChange(); return; }
        setupForm = Track(CreateSetupForm());
        SetupForm opened = setupForm;
        if (startAtLogin) { opened.Session.Next(); opened.Render(); }
        if (settingsForm is not null) settingsForm.Enabled = false;
        if (hiddenForm is not null) hiddenForm.Enabled = false;
        opened.FormClosed += async (_, _) =>
        {
            if (settingsForm is not null && !settingsForm.IsDisposed) settingsForm.Enabled = true;
            if (hiddenForm is not null && !hiddenForm.IsDisposed) hiddenForm.Enabled = true;
            bool resume = false, read = false;
            try
            {
                if (SetupCloseCheckpoint is { } checkpoint) await checkpoint();
                if (!OwnsConfigChange(generation) || !ReferenceEquals(setupForm, opened)) return;
                DbxConfig loaded = await Task.Run(Controller.Config.Load);
                if (!OwnsConfigChange(generation) || !ReferenceEquals(setupForm, opened)) return;
                Config = loaded; read = true;
                if (opened.Session.Committed) settingsForm?.Close();
                resume = previous == ResidentPhase.Watching || opened.Session.Committed;
            }
            catch (Exception e) { Notify("接続先を確認してください", Ui.Failure(e), ToolTipIcon.Warning); }
            finally
            {
                bool current = OwnsConfigChange(generation) && ReferenceEquals(setupForm, opened);
                EndDisplayChange();
                if (current)
                {
                    SetPhase(read ? ResidentPhase.Stopped : ResidentPhase.StopFailed);
                    if (read && resume && !string.IsNullOrWhiteSpace(Config.Remote)) await StartMountedAsync();
                    await RefreshAfterChangeAsync();
                }
            }
        };
        setupForm.Show(); setupForm.Activate();
    }
    private void ShowStatus()
    {
        if (statusForm is null || statusForm.IsDisposed) statusForm = Track(CreateStatusForm());
        statusForm.Show(); statusForm.Activate();
    }
    private void ShowSettings()
    {
        if (setupForm is { IsDisposed: false }) { setupForm.Activate(); return; }
        if (settingsForm is null || settingsForm.IsDisposed) settingsForm = Track(CreateSettingsForm());
        settingsForm.Show(); settingsForm.Activate();
    }
    private void ShowHidden()
    {
        if (setupForm is { IsDisposed: false }) { setupForm.Activate(); return; }
        if (hiddenForm is null || hiddenForm.IsDisposed) hiddenForm = Track(CreateHiddenForm());
        hiddenForm.Show(); hiddenForm.Activate();
    }
    private async Task HandleArgumentsAsync(string[] args)
    {
        try
        {
            var words = args.ToList();
            if (!words.Contains("--config")) words.AddRange(["--config", options.ConfigPath]);
            if (!words.Contains("--state")) words.AddRange(["--state", options.StatePath]);
            AppOptions forwarded = AppOptions.Parse(words.ToArray());
            if (!forwarded.StatePath.Equals(options.StatePath, StringComparison.OrdinalIgnoreCase) ||
                !forwarded.ConfigPath.Equals(options.ConfigPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("常駐している設定と一致しません。");
            await HandleActionAsync(forwarded);
        }
        catch (Exception e) { Notify("操作できません", Ui.Failure(e), ToolTipIcon.Error); }
    }
    private async Task HandleActionAsync(AppOptions forwarded)
    {
        if (Phase is ResidentPhase.Exiting or ResidentPhase.Stopping or ResidentPhase.Changing or ResidentPhase.StopFailed or ResidentPhase.ExitFailed) return;
        if (forwarded.HidePath is not null && Phase is not (ResidentPhase.Watching or ResidentPhase.Stopped)) return;
        if (forwarded.HidePath is { } path)
        {
            while (true)
            {
                DbxConfig target = await Task.Run(Controller.Config.Load);
                ShellHideConfirmation request = ShellHideConfirmation.Create(target, path);
                if (MessageBox.Show(request.Message, "\u8868\u793a\u3057\u306a\u3044\u30d5\u30a9\u30eb\u30c0", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try
                {
                    // The captured target is checked by UpdateConfig while holding the apply mutex.
                    await ApplyConfigAsync(request.Apply);
                    Notify("\u8868\u793a\u3057\u306a\u3044\u30d5\u30a9\u30eb\u30c0\u3092\u9069\u7528\u3057\u307e\u3057\u305f", "\u3053\u306e PC \u306e\u30d5\u30a9\u30eb\u30c0\u306e\u8868\u793a\u3092\u66f4\u65b0\u3057\u307e\u3057\u305f\u3002", ToolTipIcon.Info);
                    return;
                }
                catch (StaleHideConfirmationException) { /* Present the new target for a new confirmation. */ }
            }
        }
        else if ((forwarded.WebPath ?? forwarded.WebCopyPath) is { } web)
        {
            try
            {
                var mount = new FolderShareMount(options.ConfigPath, options.StatePath);
                string url = await ShellPaths.WebUrlAsync(web, Snapshot.Status.Running ? Snapshot.ActiveConfig ?? Config : Config,
                    mount, DropboxFolderClient.Open, cancellation.Token);
                if (forwarded.WebPath is not null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else Clipboard.SetText(url);
            }
            catch (FolderOperationException e)
            { Notify("Dropbox の Web の場所を確認できません", e.Message, ToolTipIcon.Warning); }
        }
        else if (forwarded.SharePath is { } sharePath)
        {
            var mount = new FolderShareMount(options.ConfigPath, options.StatePath);
            var session = new ShareFolderSession(sharePath, mount, DropboxFolderClient.Open, () => Phase == ResidentPhase.Watching);
            var form = Track(new ShareFolderForm(sharePath,
                token => Task.Run(() => session.LoadAsync(requireMount: false, includeCopyPath: true, token), token),
                (info, plan, token) => Task.Run(() => session.InviteAsync(info, plan, token), token)));
            form.Show(); form.Activate();
        }
        else ShowStatus();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            CancelPendingUpdate(); updateTimer.Dispose();
            disposed = true; poll.Dispose(); initializationCancellation.Cancel(); preparationCancellation?.Cancel(); supervisionCancellation?.Cancel();
            initializationCancellation.Dispose(); cancellation.Cancel(); cancellation.Dispose();
            tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose();
            foreach (Form form in windows.ToArray()) form.Dispose();
            foreach (Icon icon in icons.Values) icon.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
