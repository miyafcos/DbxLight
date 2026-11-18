using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

public sealed class PendingUploadsException(string message, long? uploadsQueued = null, long? uploadsInProgress = null, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public long? UploadsQueued { get; } = uploadsQueued;
    public long? UploadsInProgress { get; } = uploadsInProgress;
}
public sealed record StartResult(int Pid, bool AlreadyRunning)
{
    public RescueNotice RescueNotice { get; init; } = new();
    public MountLogInfo LogCapture { get; init; } = new();
}
public sealed record StopResult(bool WasRunning, int? Pid)
{
    public bool PreparationCancellationRequested { get; init; }
    public string? Message { get; init; }
}

internal sealed class SupervisionOwnership
{
    internal RuntimeState? Generation { get; set; }
}

/// <summary>Synchronous, serialized lifecycle API; a GUI should invoke it on a background thread.</summary>
public sealed partial class MountController
{
    public ConfigStore Config { get; }
    public StateStore State { get; }
    public TimeSpan StartupTimeout { get; }
    private readonly Func<RuntimeState, Process?> queryProcess;
    private StartupPreparation? preparation;
    internal Func<RuntimeState, FolderMountObservation?>? FolderMountInspector { get; set; }
    internal TimeSpan? PreparationTimeTestLimit { get; set; }
    internal Func<TimeSpan>? PreparationElapsedTestOverride { get; set; }
    internal int? PreparationFolderTestLimit { get; set; }
    internal Action<string, RuntimeState>? StartupCheckpoint { get; set; }
    internal Action<string>? SupervisorCheckpoint { get; set; }
    internal Func<RemoteInfo, RemoteInfo>? RecoveryInfoOverride { get; set; }
    internal Action<string>? PreparationCompletionCheckpoint { get; set; }
    internal Action<string>? PreparationCancellationCheckpoint { get; set; }
    internal Action? OwnedStopGateCheckpoint { get; set; }
    internal double? StopWaitTestScale { get; set; }
    internal Action<int>? StopWaitCheckpoint { get; set; }
    // Bounded waits total 41s: stats 3 + quit 3 + exit 25 + mount release 10.
    // This excludes the stopped-cache scan and records, and any work under the same gate
    // (connection-change reads, settings application and restart). A 45s waiter can receive busy.
    internal const int NormalStopWaitMilliseconds = 25000;
    internal const int ForcedStopWaitMilliseconds = 10000;
    internal Func<string, RemoteInfo, RecoveryFolder>? RecoveryFolderTestOverride { get; set; }
    private RemoteInfo RecoveryInfo(RecoveryRemoteProbe probe)
    {
        RemoteInfo info = probe.Info();
        return RecoveryInfoOverride?.Invoke(info) ?? info;
    }

    public MountController(ConfigStore? config = null, StateStore? state = null, TimeSpan? startupTimeout = null, Func<RuntimeState, Process?>? processQuery = null)
    {
        queryProcess = processQuery ?? ProcessIdentity.Open;
        Config = config ?? new ConfigStore();
        State = state ?? new StateStore();
        StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
    }

    public StartResult Start() => Start(CancellationToken.None);
    public StartResult Start(CancellationToken startupCancellation) => ControlGate.Execute(() => StartLocked(null, 0, startupCancellation));
    public StopResult Stop(bool force = false)
    {
        if (StartupPreparation.Read(State.PathName, Config.PathName) is not null && StartupPreparation.RequestStop(State.PathName, Config.PathName))
            return new(false, null) { PreparationCancellationRequested = true, Message = "起動の準備中です。退避の区切りで止めます。" };
        return ControlGate.Execute(() => StopLocked(force));
    }
    private static bool OwnsGeneration(RuntimeState? current, RuntimeState? owned) => current is not null && owned is not null &&
        current.SupervisorPid == owned.SupervisorPid && current.SupervisorStartedUtc == owned.SupervisorStartedUtc &&
        current.GenerationId == owned.GenerationId && current.GenerationNumber == owned.GenerationNumber &&
        current.RclonePid == owned.RclonePid && current.ProcessStartedUtc == owned.ProcessStartedUtc;

    internal bool ReconcileSupervision(SupervisionOwnership? ownership, bool supervisorEnded = true)
    {
        bool OtherActivity(RuntimeState? state)
        {
            if (StartupPreparation.Read(State.PathName, Config.PathName) is not null) return true;
            using var child = state is null ? null : queryProcess(state);
            return child is not null;
        }
        // Read only first: another run may hold ControlGate throughout its preparation.
        RuntimeState? current = PrivateJsonFile.Read<RuntimeState>(State.PathName);
        if (supervisorEnded)
        {
            if (!OwnsGeneration(current, ownership?.Generation)) return OtherActivity(current);
            using var child = queryProcess(current!);
            if (child is null) return OtherActivity(current);
        }
        OwnedStopGateCheckpoint?.Invoke();
        return ControlGate.Execute(() =>
        {
            current = LoadRuntime();
            if (!OwnsGeneration(current, ownership?.Generation)) return OtherActivity(current);
            using var child = queryProcess(current!);
            if (child is not null || !supervisorEnded)
            {
                // A live watcher also needs a durable stop while its child is absent.
                StopLocked(false);
                RuntimeState? stopped = State.Load();
                if (OwnsGeneration(stopped, ownership?.Generation))
                    SaveRuntime(stopped! with { SupervisorPid = null, SupervisorStartedUtc = null, SupervisorExecutablePath = null });
            }
            return false;
        });
    }

    internal static readonly TimeSpan ReadControlWait = TimeSpan.FromSeconds(2);
    public DbxStatus Status() => ReadRuntime().Status;
    public (DbxStatus Status, RuntimeState? Runtime) ReadRuntime() => ReadRuntimeCore(requireMatchingConfig: false);
    internal (DbxStatus Status, RuntimeState? Runtime) ReadAgentRuntime() => ReadRuntimeCore(requireMatchingConfig: true);
    private (DbxStatus Status, RuntimeState? Runtime) ReadRuntimeCore(bool requireMatchingConfig)
    {
        if (ControlGate.TryExecute(() => ReadRuntimeLocked(requireMatchingConfig), ReadControlWait, out var result)) return result;
        var progress = StartupPreparation.Read(State.PathName, Config.PathName);
        if (progress is not null) return (new DbxStatus { Preparation = progress.Progress, RescueCountsKnown = false,
            ReadOnlySource = "unknown", OldestQueuedAgeReason = "起動の準備中です。" }, null);
        throw new ControlBusyException();
    }

    private (DbxStatus Status, RuntimeState? Runtime) ReadRuntimeLocked(bool requireMatchingConfig)
    {
        void CheckConfigPath(RuntimeState? value)
        {
            if (requireMatchingConfig && value is not null && !value.ConfigPath.Equals(Config.PathName, StringComparison.OrdinalIgnoreCase))
                throw new AgentCommandException(3, "state_mismatch", "State belongs to another config; ask a person to check it.");
        }
        if (requireMatchingConfig) CheckConfigPath(State.Load());
        RuntimeState? state = LoadRuntime();
        CheckConfigPath(state);
        DbxStatus status = StatusReader.Read(state, queryProcess);
        if (state is not null && RecoveryJournal.ForeignOwner(state, State.PathName))
        {
            string message = RecoveryJournal.ForeignOwnerMessage(state);
            return (status with { ProcessUncertain = true, UncertainProcess = new(state.RclonePid, null), StatusError = message,
                RecoveryRequired = state.RecoveryRequired, RecoveryLocation = message, StopDisposition = state.StopDisposition.ToString(),
                RescueFiles = RescueStore.Pending(state).Count, RescueNotice = RescueStore.Notice(RescueStore.Pending(state)), ReadOnly = null, ReadOnlySource = "unknown" }, state);
        }
        if (state is not null)
        {
            if (status.ProcessUncertain) state = MarkRecovery(state, StopDisposition.ProcessUnknown, status.UncertainProcess);
            else if (!status.Running && state.StopDisposition is StopDisposition.Unknown or StopDisposition.Running)
                state = MarkRecovery(state, StopDisposition.UnexpectedExit);
            else if (status.Running && status.Ready && status.UploadsQueued == 0 && status.UploadsInProgress == 0 && state.RecoveryRequired &&
                RecoveryDestination.Resolved(state.ActiveConfig) && PendingCacheInspector.Observe(state.ActiveConfig.CacheDir).Clean)
            {
                state = state with { RecoveryRequired = false, StopDisposition = StopDisposition.Running };
                state = SaveRuntime(state);
            }
            if (status.Running && status.Ready && status.StatusError is null)
            {
                DownloadObservation downloads = DownloadMonitor.Read(state, DateTimeOffset.UtcNow);
                status = status with { DownloadActivity = downloads.Activity };
                if (downloads.State is not null)
                {
                    state = state with { DownloadWatch = downloads.State };
                    state = SaveRuntime(state);
                }
            }
            status = status with { RecoveryRequired = state.RecoveryRequired, StopDisposition = state.StopDisposition.ToString(),
                RecoveryLocation = state.RecoveryRequired ? RecoveryDestination.Explain(state) : null };
        }
        DbxConfig permissionConfig = Config.Load();
        status = status with
        {
            UnregisteredCredentialFiles = CredentialSwitch.UnregisteredFiles(Config.PathName),
            ReadOnly = status.ProcessUncertain ? null : status.Running ? state?.StartedReadOnly :
                !permissionConfig.WriteAllowedRemotes.Contains(permissionConfig.Remote, StringComparer.Ordinal),
            ReadOnlySource = status.ProcessUncertain || (status.Running && state?.StartedReadOnly is null) ? "unknown" :
                status.Running ? "active_arguments" : "configured"
        };
        var rescued = state is null ? RescueStore.Pending(permissionConfig) : RescueStore.Pending(state);
        status = status with { RescueFiles = rescued.Count, RescueNotice = RescueStore.Notice(rescued),
            LogCapture = state is null ? new() : MountLogStore.Read(state) };
        return (status, state);
    }
    public StartResult Restart() => ControlGate.Execute(() =>
    {
        RuntimeState? before = LoadRuntime();
        EnsureRuntimeMatches(before);
        _ = Config.Load().Validate(forMount: true);
        StopLocked(false);
        return StartLocked(SupervisorOwner(before), before?.RestartCount ?? 0);
    });

    public DbxConfig UpdateConfig(Func<DbxConfig, DbxConfig> update) => ControlGate.Execute(() =>
    {
        DbxConfig before = Config.Load();
        DbxConfig after = update(before).Validate();
        bool connectionChanged = !DropboxCopyPaths.SameConnection(before, after);
        if (connectionChanged && (after.CopyPathBinding is null || after.CopyPathBinding == before.CopyPathBinding))
            after = DropboxCopyPaths.ClearBinding(after);
        RequireRescueDirectoryChange(before, after);
        if (!RecoveryDestination.Matches(before, after) || !before.WriteAllowedRemotes.SequenceEqual(after.WriteAllowedRemotes, StringComparer.Ordinal))
            EnsureConnectionChangeAllowed();
        RuntimeState? runtime = LoadRuntime();
        EnsureRuntimeMatches(runtime);
        using var process = runtime is null ? null : OpenRuntime(runtime);
        bool running = process is not null;
        if (runtime is not null)
        {
            if (!running && runtime.StopDisposition is StopDisposition.Unknown or StopDisposition.Running)
                runtime = MarkRecovery(runtime, StopDisposition.UnexpectedExit);
            EnsureRecoveryDestination(runtime, after);
        }
        if (running)
        {
            _ = after.Validate(forMount: true);
            StopLocked(false); // Refusal leaves both the saved config and the filter intact.
        }
        // A stop may discover additional pending work; never abandon that cache.
        EnsureRecoveryDestination(LoadRuntime(), after);
        Config.Save(after);
        if (connectionChanged) DropboxCopyPaths.InvalidateCache(Config.PathName);
        if (running) StartLocked(SupervisorOwner(runtime), runtime?.RestartCount ?? 0);
        return after;
    });

    internal DbxConfig? TrySavePreferences(DbxConfig expected, DbxConfig next) => ControlGate.Execute<DbxConfig?>(() =>
    {
        RequireUnchanged(expected);
        if (JsonSerializer.Serialize(expected with { UpdateCheck = next.UpdateCheck, CopyPathPrefix = next.CopyPathPrefix,
            CopyPathBinding = next.CopyPathBinding }, JsonFiles.Options) != JsonSerializer.Serialize(next, JsonFiles.Options))
            return null;
        if (expected.UpdateCheck != next.UpdateCheck || expected.CopyPathPrefix != next.CopyPathPrefix ||
            expected.CopyPathBinding != next.CopyPathBinding)
            Config.Save(next);
        return next;
    });

    // Credential changes must also refuse recovery when the destination text is unchanged.
    public void PrepareConnectionChange(DbxConfig expected) => ControlGate.Execute(() =>
    {
        RequireUnchanged(expected);
        EnsureConnectionChangeAllowed();
        StopLocked(false);
        EnsureConnectionChangeAllowed();
        return 0;
    });

    public DbxConfig ApplyConnectionChange(DbxConfig expected, Func<DbxConfig, DbxConfig> update) => ControlGate.Execute(() =>
    {
        PrepareConnectionChange(expected);
        // All new connection paths pass the existing destination and stop guards.
        return UpdateConfig(update);
    });

    private void RequireUnchanged(DbxConfig expected)
    {
        if (JsonSerializer.Serialize(Config.Load(), JsonFiles.Options) != JsonSerializer.Serialize(expected, JsonFiles.Options))
            throw new InvalidOperationException("別の操作で設定が変わりました。この画面を開き直してください。");
    }

    private void EnsureConnectionChangeAllowed()
    {
        RuntimeState? state = LoadRuntime();
        EnsureRuntimeMatches(state);
        var read = ReadRuntime();
        state = read.Runtime;
        if (read.Status.ProcessUncertain)
            throw new PendingUploadsException(read.Status.RecoveryLocation ?? read.Status.StatusError ?? "プロセスの状態を確認できません。");
        if (read.Status.Running && (!read.Status.Ready || read.Status.StatusError is not null ||
            read.Status.UploadsQueued != 0 || read.Status.UploadsInProgress != 0))
            throw new PendingUploadsException("アップロード待ちとアップロード中が両方 0 と確認できるまで、接続先と権限は変更できません。");
        EnsureConnectionStorage(Config.Load(), state, read.Status.Running);
        if (state is { RecoveryRequired: true } && !read.Status.Running)
            SaveRuntime(state with { RecoveryRequired = false, StopDisposition = StopDisposition.Clean });
    }

    public DbxConfig Hide(string path) => UpdateConfig(config => config with
    {
        HiddenPaths = [.. config.HiddenPaths, MountPaths.ToRelative(path, config.MountPoint)]
    });

    public DbxConfig Unhide(string path) => UpdateConfig(config =>
    {
        string relative = MountPaths.ToRelative(path, config.MountPoint);
        string? ancestor = config.HiddenPaths.FirstOrDefault(parent => !parent.Equals(relative, StringComparison.OrdinalIgnoreCase)
            && HiddenFolders.IsHidden(relative, [parent]));
        if (ancestor is not null) throw new ArgumentException("Unhide the hidden ancestor first: " + ancestor);
        return config with { HiddenPaths = config.HiddenPaths.Where(p => !p.Equals(relative, StringComparison.OrdinalIgnoreCase)).ToArray() };
    });

    public int Run(CancellationToken cancellationToken, Action<StartResult>? onStarted = null, Action? onPreparationCancelled = null,
        CancellationToken startupCancellation = default) =>
        RunCore(cancellationToken, onStarted, onPreparationCancelled, startupCancellation, null);

    internal int RunForResident(SupervisionOwnership ownership, CancellationToken cancellationToken,
        Action<StartResult> onStarted, CancellationToken startupCancellation) =>
        RunCore(cancellationToken, onStarted, null, startupCancellation, ownership);

    private int RunCore(CancellationToken cancellationToken, Action<StartResult>? onStarted,
        Action? onPreparationCancelled, CancellationToken startupCancellation, SupervisionOwnership? ownership)
    {
        using Process self = Process.GetCurrentProcess();
        var owner = (Pid: self.Id, Started: self.StartTime.ToUniversalTime(), ExecutablePath: ProcessIdentity.ImagePath(self));
        using var initial = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCancellation);
        CancellationToken initialToken = initial.Token;
        long? stopSequence = null;
        RuntimeState? ownedGeneration = null;
        bool Owns(RuntimeState? current) => OwnsGeneration(current, ownedGeneration);
        void Remember(RuntimeState current)
        {
            ownedGeneration = current;
            if (ownership is not null) ownership.Generation = current;
        }
        StartResult StartOwnedLocked(int count, CancellationToken token)
        {
            Exception? failure = null;
            try { return StartLocked(owner, count, token, Remember); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                StartupFailure.Cleanup(failure, () =>
                {
                    SupervisorCheckpoint?.Invoke("before-ownership-read");
                    RuntimeState? current = State.Load();
                    if (current?.SupervisorPid == owner.Pid && current.SupervisorStartedUtc == owner.Started)
                        Remember(current);
                }, State.PathName);
            }
        }
        int restarts = 0;
        Exception? runFailure = null;
        try
        {
            while (true)
            {
                try
                {
                    SupervisorCheckpoint?.Invoke("before-initial-gate");
                    initialToken.ThrowIfCancellationRequested();
                    StartResult? started = ControlGate.Execute(() =>
                    {
                        initialToken.ThrowIfCancellationRequested();
                        long currentSequence = LoadRuntime()?.StopSequence ?? 0;
                        stopSequence ??= currentSequence;
                        if (currentSequence != stopSequence) return null;
                        return StartOwnedLocked(0, initialToken);
                    });
                    if (started is null) { onPreparationCancelled?.Invoke(); return 0; }
                    onStarted?.Invoke(started); break;
                }
                catch (OperationCanceledException e) when (initialToken.IsCancellationRequested && StartupFailure.CleanupFailures(e).Count == 0)
                {
                    StartupFailure.Cleanup(e, () => onPreparationCancelled?.Invoke(), State.PathName);
                    if (StartupFailure.CleanupFailures(e).Count > 0) throw;
                    return 0;
                }
                catch (PendingUploadsException e) when (e.Data["PreparationCancelled"] is true)
                {
                    if (e.InnerException is not null || StartupFailure.CleanupFailures(e).Count > 0) throw;
                    StartupFailure.Cleanup(e, () => onPreparationCancelled?.Invoke(), State.PathName);
                    if (StartupFailure.CleanupFailures(e).Count > 0) throw;
                    return 0;
                }
                catch (RecoveryComparisonException e) when (StartupFailure.CanRetry(e))
                {
                    StartupFailure.Cleanup(e, () =>
                    {
                        LogEvent(Config.Load(), "Remote comparison unavailable; retrying after 5 seconds: " + StartupFailure.PublicMessage(e));
                        SupervisorCheckpoint?.Invoke("initial-retry-wait");
                    }, Config.PathName);
                    if (!StartupFailure.CanRetry(e)) throw;
                    if (initialToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) return 0;
                }
            }
            SupervisorCheckpoint?.Invoke("watch-started");
            bool watchBusy = false;
            while (!cancellationToken.WaitHandle.WaitOne(250))
            {
                (RuntimeState? Current, bool Exited) observation;
                try
                {
                    SupervisorCheckpoint?.Invoke("before-watch-observation");
                    observation = ControlGate.Execute(() =>
                    {
                        RuntimeState? snapshot = LoadRuntime();
                        if (snapshot is null || snapshot.StopRequested || snapshot.SupervisorPid != owner.Pid || snapshot.SupervisorStartedUtc != owner.Started)
                            return (Current: (RuntimeState?)null, Exited: false);
                        Remember(snapshot);
                        Process? observed;
                        try { observed = OpenRuntime(snapshot); }
                        catch (PendingUploadsException error) when (StartupFailure.Get(error)?.Classification == "cache_owner_unknown" &&
                            StartupFailure.CleanupFailures(error).Count == 0) { return (Current: snapshot, Exited: false); }
                        using var alive = observed;
                        if (alive is not null) { if (snapshot.RecoveryRequired) _ = ReadRuntime(); return (Current: snapshot, Exited: false); }
                        snapshot = MarkRecovery(snapshot, StopDisposition.UnexpectedExit);
                        LogEvent(snapshot.ActiveConfig, $"Unexpected exit pid={snapshot.RclonePid}; restarts={restarts}.");
                        return (Current: snapshot, Exited: true);
                    });
                }
                catch (ControlBusyException)
                {
                    if (!watchBusy) LogEvent(ownedGeneration!.ActiveConfig, "Watch observation skipped: control gate busy.", false);
                    watchBusy = true;
                    continue;
                }
                if (watchBusy) LogEvent(ownedGeneration!.ActiveConfig, "Watch observation resumed.", false);
                watchBusy = false;
                RuntimeState? current = observation.Current;
                if (current is null) return 0;
                if (!observation.Exited) continue;
                bool restarted = false;
                while (!restarted && restarts < 3)
                {
                    restarts++;
                    SupervisorCheckpoint?.Invoke("restart-wait");
                    if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Pow(2, restarts - 1)))) break;
                    try
                    {
                        restarted = ControlGate.Execute(() =>
                        {
                            var latest = LoadRuntime();
                            if (latest is null || latest.StopRequested || latest.SupervisorPid != owner.Pid || latest.SupervisorStartedUtc != owner.Started) return false;
                            var started = StartOwnedLocked(restarts, cancellationToken);
                            onStarted?.Invoke(started);
                            return true;
                        });
                        if (!restarted) return 0;
                    }
                    catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested && StartupFailure.CleanupFailures(e).Count == 0)
                    {
                        StartupFailure.Cleanup(e, () => onPreparationCancelled?.Invoke(), State.PathName);
                        if (StartupFailure.CleanupFailures(e).Count > 0) throw;
                        return 0;
                    }
                    catch (PendingUploadsException e) when (e.Data["PreparationCancelled"] is true)
                    {
                        if (e.InnerException is not null || StartupFailure.CleanupFailures(e).Count > 0) throw;
                        StartupFailure.Cleanup(e, () => onPreparationCancelled?.Invoke(), State.PathName);
                        if (StartupFailure.CleanupFailures(e).Count > 0) throw;
                        return 0;
                    }
                    catch (PendingUploadsException e)
                    {
                        if (StartupFailure.CleanupFailures(e).Count > 0) throw;
                        // A safety refusal ends this run without changing the CLI failure result.
                        StartupFailure.Cleanup(e, () => LogEvent(current.ActiveConfig,
                            "Restart refused: " + StartupFailure.PublicMessage(e)), State.PathName);
                        if (StartupFailure.CleanupFailures(e).Count > 0) throw;
                        return 1;
                    }
                    catch (RecoveryComparisonException e) when (StartupFailure.CanRetry(e))
                    {
                        restarts--;
                        StartupFailure.Cleanup(e, () =>
                        {
                            LogEvent(current.ActiveConfig, "Remote comparison unavailable; pending data retained: " + StartupFailure.PublicMessage(e));
                            SupervisorCheckpoint?.Invoke("restart-comparison-wait");
                        }, State.PathName);
                        if (!StartupFailure.CanRetry(e)) throw;
                        if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) break;
                    }
                    catch (Exception e) when (StartupFailure.CanRetry(e))
                    {
                        StartupFailure.Cleanup(e, () => LogEvent(current.ActiveConfig,
                            $"Restart attempt {restarts} failed: {StartupFailure.PublicMessage(e)}"), State.PathName);
                        if (!StartupFailure.CanRetry(e)) throw;
                    }
                }
                if (!restarted && !cancellationToken.IsCancellationRequested)
                {
                    LogEvent(current.ActiveConfig, "Restart limit reached (3).");
                    return 1;
                }
            }
            SupervisorCheckpoint?.Invoke("before-cancellation-stop");
            ControlGate.Execute(() =>
            {
                // A revoked watcher never cancels another preparation or stops its child.
                if (Owns(LoadRuntime())) StopLocked(false);
                return 0;
            });
            return 0;
        }
        catch (Exception error) { runFailure = error; throw; }
        finally
        {
            if (ownedGeneration is not null)
            {
                StartupFailure.Cleanup(runFailure, () => ControlGate.Execute(() =>
                {
                    SupervisorCheckpoint?.Invoke("before-ownership-release");
                    RuntimeState? current = LoadRuntime();
                    if (Owns(current))
                    {
                        // A resident retains its live child's identity for normal-stop reconciliation.
                        using var child = ownership is null ? null : queryProcess(current!);
                        if (child is null)
                            SaveRuntime(current! with { SupervisorPid = null, SupervisorStartedUtc = null, SupervisorExecutablePath = null });
                    }
                    return 0;
                }), State.PathName);
            }
        }
    }

    private PendingUploadsException PreparationStopped(Exception? persistenceFailure)
    {
        var stopped = new PendingUploadsException(persistenceFailure is null
            ? "起動の準備を退避の区切りで止めました。子プロセスは起動していません。次の起動で続けます。"
            : "起動の取り消しは受け付けましたが、記録を保存できませんでした。起動し直しは行いません。状態と回復の記録を確認してください。",
            innerException: persistenceFailure);
        stopped.Data["PreparationCancelled"] = true;
        if (persistenceFailure is not null)
            StartupFailure.ForUser(stopped, "preparation_cancel_persistence_failed", State.PathName, stopped.Message,
                "元の設定・状態と回復の記録を残し、保存先を確認してから停止をやり直してください。");
        return stopped;
    }

    private StartResult StartLocked((int Pid, DateTime Started, string ExecutablePath)? supervisor, int restartCount,
        CancellationToken startupCancellation = default, Action<RuntimeState>? onOwned = null)
    {
        startupCancellation.ThrowIfCancellationRequested();
        try { return StartCore(supervisor, restartCount, startupCancellation, onOwned); }
        catch (PreparationCancelledException cancelled)
        {
            Exception? persistenceFailure = cancelled.InnerException;
            foreach (var error in StartupFailure.CleanupFailures(cancelled))
                persistenceFailure = persistenceFailure is null ? error : new AggregateException(persistenceFailure, error);
            try
            {
                PreparationCancellationCheckpoint?.Invoke("before-persistence");
                var state = State.Load();
                if (state is null || IsStopMarker(state)) SaveStopMarker(state);
                else SaveRuntime(state with { StopRequested = true, StoppedUtc = DateTime.UtcNow, RecoveryRequired = true,
                    StopSequence = checked(state.StopSequence + 1),
                    SupervisorPid = null, SupervisorStartedUtc = null, SupervisorExecutablePath = null });
            }
            catch (Exception error)
            {
                persistenceFailure = persistenceFailure is null ? error : new AggregateException(persistenceFailure, error);
            }
            try { PreparationCancellationCheckpoint?.Invoke("after-persistence"); }
            catch (Exception error)
            {
                persistenceFailure = persistenceFailure is null ? error : new AggregateException(persistenceFailure, error);
            }
            var stopped = PreparationStopped(persistenceFailure);
            if (cancelled.Data["DbxLight.PriorFailure"] is Exception prior)
                stopped.Data["DbxLight.PriorFailure"] = prior;
            throw stopped;
        }
        catch (Exception e) when (e is not PendingUploadsException && e is not RecoveryComparisonException)
        {
            DbxConfig? config = null;
            StartupFailure.Cleanup(e, () => config = Config.Load(), Config.PathName);
            StartupFailure.Cleanup(e, () => StartupFailure.Annotate(e, config, Config.PathName), Config.PathName);
            throw;
        }
    }

    private StartResult StartCore((int Pid, DateTime Started, string ExecutablePath)? supervisor, int restartCount,
        CancellationToken startupCancellation, Action<RuntimeState>? onOwned)
    {
        DbxConfig config = Config.Load().Validate(forMount: true);
        CredentialSwitch.RefuseMount(Config.PathName);
        PrivateLoginFile.RefuseActiveChange(config.RcloneConfigPath);
        RuntimeState? before = LoadRuntime();
        EnsureRuntimeMatches(before);
        if (before is not null)
        {
            using var initial = OpenRuntime(before);
            if (initial is null && before.StopDisposition is StopDisposition.Unknown or StopDisposition.Running)
                before = MarkRecovery(before, StopDisposition.UnexpectedExit);
            EnsureRecoveryDestination(before, config);
            using var aliveSupervisor = SupervisorProcess(before);
            if (aliveSupervisor is not null && (supervisor is null || before.SupervisorPid != supervisor.Value.Pid))
            {
                using var alive = ProcessIdentity.Open(before);
                if (supervisor is not null || alive is null)
                    throw new InvalidOperationException("A supervisor already owns this runtime.");
            }
            using var process = OpenRuntime(before);
            if (process is not null)
            {
                if (!before.ConfigPath.Equals(Config.PathName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The runtime belongs to another config file.");
                // The config path and full process identity have both been checked above.
                RuntimeState adopted = SaveRuntime(before with
                {
                    StopInProgress = false, StopRequested = false, StoppedUtc = null,
                    SupervisorPid = supervisor?.Pid ?? before.SupervisorPid,
                    SupervisorStartedUtc = supervisor?.Started ?? before.SupervisorStartedUtc,
                    SupervisorExecutablePath = supervisor?.ExecutablePath ?? before.SupervisorExecutablePath,
                    RestartCount = supervisor is null ? before.RestartCount : restartCount
                });
                onOwned?.Invoke(adopted);
                _ = ReadRuntime();
                return new(process.Id, true) { RescueNotice = RescueStore.Notice(RescueStore.Pending(config, before.RescueRecords)), LogCapture = MountLogStore.Read(before) };
            }
            if (before.StoppedUtc is null) LogEvent(config, $"Previous process is no longer alive: pid={before.RclonePid}.");
        }
        if (!MountPaths.IsAvailable(config.MountPoint)) throw new IOException("mountPoint is already in use: " + config.MountPoint);
        EnsurePortAvailable(config.RcPort);
        State.EnsurePrivateDirectory();
        Directory.CreateDirectory(config.CacheDir);
        var preparing = StartupPreparation.Begin(State.PathName, Config.PathName, PreparationTimeTestLimit, PreparationFolderTestLimit,
            startupCancellation, PreparationElapsedTestOverride);
        preparation = preparing;
        preparing.CompletionCheckpoint = PreparationCompletionCheckpoint;
        Exception? startFailure = null;
        try
        {
            string cacheOwnerPath = Path.Combine(config.CacheDir, "dbxl-owner.json");
            EnsureCacheOwnerStopped(config, before ?? new RuntimeState { ActiveConfig = config });
            preparation.Update("キャッシュの走査");
            EnsureCacheProvenance(config, before);
            CacheInspection preflight = PendingCacheInspector.InspectStopped(config.CacheDir, preparation.CheckCancellation);
            if (before is not null && !preflight.Clean)
            {
                // Preserve uncertainty before any pair moves. A failure after rescue but before
                // the new launch intent must not leave the old Clean record as completion proof.
                before = before with { RecoveryRequired = true, CacheInspection = preflight.Detail };
                before = SaveRuntime(before);
            }
            _ = PrepareCache(config, before);
            StartupCheckpoint?.Invoke("after-cache-preservation", before ?? new RuntimeState { ActiveConfig = config });
            var dropboxCapture = FolderMountCredentialCapture.Begin(config, startupCancellation);
            string runtimeDir = Path.GetDirectoryName(State.PathName)!;
            string filterPath = Path.Combine(runtimeDir, "hidden-" + Path.GetFileNameWithoutExtension(State.PathName) + ".filter");
            WriteFilter(filterPath, HiddenFolders.Render(config.HiddenPaths, config.ExcludePatterns));
            string logFile = Path.Combine(config.LogDir, "supervisor.log");
            RcCredentials credentials = RcCredentials.Create();
            var logging = MountLogStore.Prepare(Config.PathName, config);
            var info = RcloneArguments.ProcessInfo(config.RclonePath,
                RcloneArguments.Mount(config, filterPath, logging.Captured ? MountLogStore.FilePath(logging) : null), credentials);
            using var launchParent = Process.GetCurrentProcess();
            DateTime parentStarted = launchParent.StartTime.ToUniversalTime();
            string parentImage = ProcessIdentity.ImagePath(launchParent);
            // Generation election, argument/key preparation and all moves precede the final batch.
            var generation = RecoveryJournal.NextGeneration(Config.PathName, config, State.Load());
            int comparisonAttempt = 0;
            while (true)
            {
                RecoveryLaunchBatch batch = PrepareRecoveryComparison(config, before, comparisonAttempt);
                StartupCheckpoint?.Invoke("after-final-comparison", before ?? new RuntimeState { ActiveConfig = config });
                preparation.SealForLaunch();
                string[] rescuePaths = batch.RescuePaths;
                var intent = new RuntimeState { StopSequence = before?.StopSequence ?? 0, GenerationId = generation.Id, GenerationNumber = generation.Number, OwnerStatePath = State.PathName,
                    LaunchParentPid = launchParent.Id,
                    LaunchParentStartedUtc = parentStarted, LaunchParentExecutablePath = parentImage, LaunchPhase = StartupPhase.Intent,
                    LaunchRecoveryProof = before?.LaunchRecoveryProof,
                    LogCapture = logging,
                    ConfigPath = Config.PathName, ActiveConfig = config, RcPort = config.RcPort,
                    StartedReadOnly = info.ArgumentList.Contains("--read-only"),
                    RcUser = credentials.User, RcPassword = credentials.Password, RecoveryRequired = true, LaunchPending = true,
                    RecoveryComparedUtc = batch.OldestUtc, RecoveryComparedFiles = batch.Items.Length,
                    RescueRecords = rescuePaths, CacheDataRoot = batch.DataRoot, CacheMetadataRoot = batch.MetaRoot,
                    StopDisposition = StopDisposition.StartupFailed, SupervisorPid = supervisor?.Pid,
                    SupervisorStartedUtc = supervisor?.Started, SupervisorExecutablePath = supervisor?.ExecutablePath, RestartCount = restartCount };
                intent = SaveRuntime(intent);
                JsonFiles.Write(cacheOwnerPath, CacheOwnership(intent, true));
                DetachedProcess.SuspendedChild createdChild;
                try
                {
                    StartupCheckpoint?.Invoke("before-child-create", intent);
                    createdChild = DetachedProcess.CreateSuspended(info);
                }
                catch (DetachedProcess.NoChildCreatedException error)
                {
                    // CreateProcess returned false: no child exists. Distinguish that proof from
                    // a parent crash while launch ownership was still incomplete.
                    StartupFailure.RetryableChild(error);
                    StartupFailure.Cleanup(error, () =>
                    {
                        SaveRuntime(intent with { LaunchPending = false, StoppedUtc = DateTime.UtcNow, StopRequested = supervisor is null });
                        JsonFiles.Write(cacheOwnerPath, CacheOwnership(intent, false));
                    }, State.PathName);
                    throw;
                }
                var launch = createdChild;
                Exception? launchFailure = null;
                Process child = launch.Process;
                RuntimeState? state = null;
                try
                {
                    string image = WaitForImage(child);
                    state = new RuntimeState
                    {
                        StopSequence = intent.StopSequence,
                        GenerationId = intent.GenerationId, GenerationNumber = intent.GenerationNumber, OwnerStatePath = intent.OwnerStatePath,
                        LaunchParentPid = intent.LaunchParentPid,
                        LaunchParentStartedUtc = intent.LaunchParentStartedUtc, LaunchParentExecutablePath = intent.LaunchParentExecutablePath,
                        LaunchPhase = StartupPhase.RecordedSuspended, LaunchRecoveryProof = intent.LaunchRecoveryProof,
                        LogCapture = logging,
                        RclonePid = child.Id, ProcessStartedUtc = child.StartTime.ToUniversalTime(),
                        ExecutablePath = image, RcPort = config.RcPort, StartedReadOnly = intent.StartedReadOnly,
                        RcUser = credentials.User, RcPassword = credentials.Password, ConfigPath = Config.PathName,
                        RecoveryComparedUtc = intent.RecoveryComparedUtc, RecoveryComparedFiles = intent.RecoveryComparedFiles,
                        RescueRecords = rescuePaths, CacheDataRoot = batch.DataRoot, CacheMetadataRoot = batch.MetaRoot,
                        ActiveConfig = config, RecoveryRequired = true, LaunchPending = true, StopDisposition = StopDisposition.Running, SupervisorPid = supervisor?.Pid, SupervisorStartedUtc = supervisor?.Started, SupervisorExecutablePath = supervisor?.ExecutablePath, RestartCount = restartCount
                    };
                    StartupCheckpoint?.Invoke("before-record", state);
                    state = SaveRuntime(state);
                    JsonFiles.Write(cacheOwnerPath, CacheOwnership(state, true));
                    DateTimeOffset resumedUtc = default;
                    double comparisonAge = 0;
                    launch.ResumeRecorded(() =>
                    {
                        StartupCheckpoint?.Invoke("before-resume", state);
                        preparation.CheckCancellation();
                        comparisonAge = RecoveryComparisonAgeTestOverride?.Invoke() ?? batch.AgeSeconds;
                        if (batch.Items.Length > 0 && comparisonAge > (RecoveryFreshnessTestLimit ?? RecoveryFreshnessLimit).TotalSeconds)
                            throw new RecoveryFreshnessExpiredException();
                        // This is the final callback before ResumeThread; no filesystem work follows.
                        resumedUtc = DateTimeOffset.UtcNow;
                    }, afterResume: () => StartupCheckpoint?.Invoke("after-resume", state));
                    state = state with { ChildResumedUtc = resumedUtc, LaunchPhase = StartupPhase.Resumed,
                        RecoveryComparisonAgeSeconds = comparisonAge };
                    state = SaveRuntime(state);
                    StartupCheckpoint?.Invoke("after-record", state);
                    state = state with { LaunchPending = false };
                    state = SaveRuntime(state);
                    JsonFiles.Write(cacheOwnerPath, CacheOwnership(state, false));
                    using var rc = new RcClient(state);
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed < StartupTimeout)
                    {
                        if (child.HasExited) throw StartupFailure.RetryableChild(new InvalidOperationException($"rclone exited during startup ({child.ExitCode}); log: {logFile}"));
                        JsonElement disk = default, stats = default;
                        bool ready = false;
                        try
                        {
                            if (StatusReader.Number(rc.Call("core/pid"), "pid") == child.Id &&
                                (stats = rc.Call("vfs/stats")).TryGetProperty("diskCache", out disk) &&
                                Directory.Exists(MountPaths.CanonicalMount(config.MountPoint)))
                                ready = true;
                        }
                        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException) { }
                        if (ready)
                        {
                            string? device = null, dropboxBinding = null, dropboxCursor = null;
                            // These observations are only needed for optional Dropbox operations.
                            // Readiness was already proved by the mount and its RC process.
                            try
                            {
                                device = FolderShareMount.Device(config.MountPoint);
                                if (device is not null && dropboxCapture is not null &&
                                    FolderShareMount.MatchesVfsRemote(config.Remote, DropboxFolderClient.Text(stats, "fs")))
                                {
                                    var binding = dropboxCapture.Confirm(FolderShareMount.RcloneConfigPath(rc), startupCancellation);
                                    dropboxCursor = dropboxCapture.CursorFor(binding);
                                    dropboxBinding = binding;
                                }
                            }
                            catch (Exception) { }
                            state = state with
                            {
                                MountDevice = device,
                                MountDropboxBinding = dropboxBinding,
                                MountDropboxCursor = dropboxCursor,
                                CacheDataRoot = disk.TryGetProperty("path", out var dataPath) ? OrdinaryPath(dataPath.GetString()) : null,
                                CacheMetadataRoot = disk.TryGetProperty("pathMeta", out var metaPath) ? OrdinaryPath(metaPath.GetString()) : null
                            };
                            state = SaveRuntime(state);
                            StartupCheckpoint?.Invoke("mount-ready", state);
                            _ = ReadRuntime();
                            LogEvent(config, $"Started pid={child.Id}; restart={restartCount}; comparedFiles={state.RecoveryComparedFiles}; oldestComparedUtc={state.RecoveryComparedUtc:O}; childResumedUtc={state.ChildResumedUtc:O}; oldestComparisonAgeSeconds={state.RecoveryComparisonAgeSeconds:F3}.");
                            preparation.Complete(launched: true);
                            onOwned?.Invoke(state);
                            return new(child.Id, false) { RescueNotice = RescueStore.Notice(RescueStore.Pending(config, state.RescueRecords)), LogCapture = MountLogStore.Read(state) };
                        }
                        Thread.Sleep(100);
                    }
                    throw StartupFailure.RetryableChild(new TimeoutException("rclone startup timed out (rc and mount must both be ready); log: " + logFile));
                }
                catch (RecoveryFreshnessExpiredException)
                {
                    try
                    {
                        launch.AbortIfSuspended();
                        state = SaveRuntime((state ?? intent) with { LaunchPending = false, StoppedUtc = DateTime.UtcNow,
                            StopDisposition = StopDisposition.StartupFailed, RecoveryRequired = true });
                        JsonFiles.Write(cacheOwnerPath, CacheOwnership(state, false));
                        comparisonAttempt++;
                    }
                    catch (Exception error) { launchFailure = error; throw; }
                }
                catch (Exception error)
                {
                    launchFailure = error;
                    StartupFailure.Cleanup(error, launch.AbortIfSuspended, State.PathName);
                    RuntimeState failed = (state ?? intent) with { RecoveryRequired = true, StopDisposition = StopDisposition.StartupFailed };
                    StartupFailure.Cleanup(error, () => SaveRuntime(failed), State.PathName);
                    StartupFailure.Cleanup(error, () =>
                    {
                        if (state is not null) TerminateOwned(state, allowKill: true);
                        else if (!child.HasExited) { child.Kill(); if (!child.WaitForExit(10000)) throw new TimeoutException("Owned startup child did not exit."); }
                    }, State.PathName);
                    StartupFailure.Cleanup(error, () =>
                    {
                        bool stopped = child.HasExited;
                        SaveRuntime(failed with { StopRequested = stopped && supervisor is null,
                            LaunchPending = !stopped, StoppedUtc = stopped ? DateTime.UtcNow : null });
                    }, State.PathName);
                    throw;
                }
                finally { StartupFailure.Cleanup(launchFailure, launch.Dispose, State.PathName); }
            }
        }
        catch (Exception error) { startFailure = error; throw; }
        finally { StartupFailure.Cleanup(startFailure, preparing.Dispose, State.PathName); }
    }

    private static bool IsStopMarker(RuntimeState state) =>
        state.RclonePid == 0 && state.ProcessStartedUtc == default && state.SupervisorPid is null &&
        state.GenerationNumber == 0 && state.GenerationId.Length == 0 && state.StopSequence > 0 &&
        state.StopRequested && !state.LaunchPending && !state.RecoveryRequired && state.StopDisposition == StopDisposition.Clean;

    private void SaveStopMarker(RuntimeState? before)
    {
        // No runtime has claimed this state yet. Record the stop without claiming a cache owner.
        DbxConfig config = Config.Load();
        // An unresolved destination cannot have a comparison retry; do not create an invalid runtime.
        if (!RecoveryDestination.Resolved(config)) return;
        State.Save(new RuntimeState { ConfigPath = Config.PathName, ActiveConfig = config, StopSequence = checked((before?.StopSequence ?? 0) + 1),
            StopRequested = true, StoppedUtc = DateTime.UtcNow, StopDisposition = StopDisposition.Clean, RecoveryRequired = false });
    }

    private StopResult StopLocked(bool force)
    {
        RuntimeState? state = LoadRuntime();
        EnsureRuntimeMatches(state);
        if (state is null || IsStopMarker(state)) { SaveStopMarker(state); return new(false, null); }
        using var process = OpenRuntime(state);
        if (process is null)
        {
            if (state.StopDisposition is StopDisposition.Unknown or StopDisposition.Running)
                state = MarkRecovery(state, StopDisposition.UnexpectedExit);
            SaveRuntime(state with { StopInProgress = false, StopRequested = true, StoppedUtc = DateTime.UtcNow,
                StopSequence = checked(state.StopSequence + 1) });
            return new(false, state.RclonePid);
        }
        // Persist uncertainty before the last queue check, never between it and core/quit.
        SaveRuntime(state with { StopInProgress = true, StopRequested = false, RecoveryRequired = true, StopDisposition = StopDisposition.StopUncertain });
        try
        {
            using var rc = new RcClient(state);
            if (!force)
            {
                try
                {
                    JsonElement disk = rc.Call("vfs/stats").GetProperty("diskCache");
                    long queued = StatusReader.Number(disk, "uploadsQueued");
                    long uploading = StatusReader.Number(disk, "uploadsInProgress");
                    if (queued != 0 || uploading != 0)
                        throw new PendingUploadsException($"Stop refused: uploadsQueued={queued}, uploadsInProgress={uploading}. Wait for uploads or use stop --force explicitly.", queued, uploading);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidDataException)
                { throw new PendingUploadsException("Stop refused: upload state is unknown (" + StartupFailure.PublicMessage(e) + ")."); }
            }
            TerminateOwned(state, allowKill: force, process, rc, StopWaitTestScale, StopWaitCheckpoint);
        }
        catch
        {
            using var remaining = OpenRuntime(state);
            SaveRuntime(state with
            {
                RecoveryRequired = true, StopDisposition = StopDisposition.StopUncertain,
                StopInProgress = false, StopRequested = remaining is null,
                StopSequence = remaining is null ? checked(state.StopSequence + 1) : state.StopSequence,
                StoppedUtc = remaining is null ? DateTime.UtcNow : null
            });
            LogEvent(state.ActiveConfig, $"Stop failed pid={state.RclonePid}; alive={remaining is not null}; supervision retained if alive.");
            throw;
        }
        CacheInspection cache = RecoveryDestination.Resolved(state.ActiveConfig)
            ? PendingCacheInspector.InspectStopped(state.ActiveConfig.CacheDir)
            : new(false, 0, RecoveryText.CacheUnknown("\u5143\u306e\u7d76\u5bfe\u30d1\u30b9\u3092\u78ba\u8a8d\u3067\u304d\u306a\u3044\u305f\u3081\u3001\u65e7\u5f62\u5f0f\u306e\u76f8\u5bfe\u30d1\u30b9\u306f\u691c\u67fb\u3057\u3066\u3044\u307e\u305b\u3093\u3002", state.ActiveConfig.CacheDir));
        SaveRuntime(state with { StopInProgress = false, StopRequested = true, StoppedUtc = DateTime.UtcNow,
            StopSequence = checked(state.StopSequence + 1),
            RecoveryRequired = force || !cache.Clean, CacheInspection = cache.Detail,
            StopDisposition = force ? StopDisposition.Forced : cache.Clean ? StopDisposition.Clean : StopDisposition.StopUncertain });
        LogEvent(state.ActiveConfig, $"Stopped pid={state.RclonePid}; force={force}.");
        return new(true, state.RclonePid);
    }

    private static void TerminateOwned(RuntimeState state, bool allowKill, Process? existingProcess = null, RcClient? existingRc = null, double? stopWaitTestScale = null, Action<int>? stopWaitCheckpoint = null)
    {
        using var openedProcess = existingProcess is null ? ProcessIdentity.Open(state) : null;
        Process? process = existingProcess ?? openedProcess;
        if (process is null) return;
        using var openedRc = existingRc is null ? new RcClient(state) : null;
        RcClient rc = existingRc ?? openedRc!;
        try { rc.Call("core/quit"); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (!allowKill) throw new IOException("core/quit failed; process was left running.", e);
        }
        int waitMilliseconds = checked((int)((allowKill ? ForcedStopWaitMilliseconds : NormalStopWaitMilliseconds) * (stopWaitTestScale ?? 1)));
        stopWaitCheckpoint?.Invoke(waitMilliseconds);
        if (!process.WaitForExit(waitMilliseconds))
        {
            if (!allowKill) throw new TimeoutException("rclone did not stop; process was left running.");
            using var owned = ProcessIdentity.Open(state);
            if (owned is not null) { owned.Kill(); if (!owned.WaitForExit(10000)) throw new TimeoutException("Owned rclone PID did not exit."); }
        }
        var timer = Stopwatch.StartNew();
        while (!MountPaths.IsAvailable(state.ActiveConfig.MountPoint) && timer.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
    }

    private static Process? SupervisorProcess(RuntimeState state)
    {
        // Under ControlGate, a verified stopped/explicitly cleared runtime has revoked the old
        // watcher's work. Replacement changes SupervisorPid before the watcher can acquire the
        // same gate; its loop rechecks that owner before every restart. Do not infer process death.
        if (state.StopRequested && state.StoppedUtc is not null && !state.RecoveryRequired &&
            state.StopDisposition is StopDisposition.Clean or StopDisposition.ClearedByUser)
        {
            using var stoppedChild = ProcessIdentity.Open(state);
            if (stoppedChild is null) return null;
        }
        return ExplainOtherOwner(state, () => ProcessIdentity.OpenSupervisor(state));
    }

    private static Process? ExplainOtherOwner(RuntimeState state, Func<Process?> query)
    {
        try { return query(); }
        catch (ProcessIdentityUnknownException e)
        {
            string reason = RecoveryDestination.Explain(state with { RecoveryRequired = true,
                StopDisposition = StopDisposition.ProcessUnknown, UncertainProcess = e.Identity });
            throw StartupFailure.ForUser(new PendingUploadsException(reason), "cache_owner_unknown", state.ActiveConfig.CacheDir, reason,
                "元の設定と状態を残し、所有プロセスの終了と起動マークを手動で確認してください。");
        }
    }

    private void EnsureRuntimeMatches(RuntimeState? state)
    {
        if (state is null) return;
        RecoveryJournal.RequireOwner(state, State.PathName);
        if (state.ConfigPath.Equals(Config.PathName, StringComparison.OrdinalIgnoreCase)) return;
        using var process = OpenRuntime(state);
        using var supervisor = SupervisorProcess(state);
        if (process is not null || supervisor is not null || state.RecoveryRequired)
            throw new InvalidOperationException("The runtime belongs to another config file; keep its recovery state.");
    }

    private static (int Pid, DateTime Started, string ExecutablePath)? SupervisorOwner(RuntimeState? state)
    {
        if (state is null) return null;
        using var process = SupervisorProcess(state);
        return process is null ? null : (process.Id, process.StartTime.ToUniversalTime(), ProcessIdentity.ImagePath(process));
    }

    public void ClearRecovery(bool acknowledged) => ControlGate.Execute(() =>
    {
        if (!acknowledged) throw new ArgumentException("Use recovery clear --i-understand-pending-may-be-lost to acknowledge possible data loss.");
        RuntimeState? state = LoadRuntime();
        EnsureRuntimeMatches(state);
        if (state is null) throw new InvalidOperationException("No recovery state exists.");
        using var live = OpenRuntime(state);
        if (live is not null) throw new InvalidOperationException("Stop the mount before explicitly clearing recovery.");
        EnsureCacheOwnerStopped(state.ActiveConfig, state);
        RescueStore.Acknowledge(state);
        SaveRuntime(state with { RecoveryRequired = false, StopDisposition = StopDisposition.ClearedByUser,
            StopRequested = true, StopInProgress = false, StoppedUtc = state.StoppedUtc ?? DateTime.UtcNow });
        return 0;
    });

    private Process? OpenRuntime(RuntimeState state)
    {
        try { return queryProcess(state); }
        catch (Exception e) when (e is ProcessIdentityUnknownException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            RuntimeState pending = MarkRecovery(state, StopDisposition.ProcessUnknown, ProcessUncertainty.From(e, state.RclonePid));
            string reason = RecoveryDestination.Explain(pending);
            throw StartupFailure.ForUser(new PendingUploadsException(reason), "cache_owner_unknown", state.ActiveConfig.CacheDir, reason,
                "元の設定と状態を残し、所有プロセスの終了と起動マークを手動で確認してください。");
        }
    }

    private RuntimeState MarkRecovery(RuntimeState state, StopDisposition reason, ProcessUncertainty? identity = null)
    {
        RuntimeState pending = state with { RecoveryRequired = true, StopDisposition = reason,
            UncertainProcess = reason == StopDisposition.ProcessUnknown ? identity ?? state.UncertainProcess : null };
        if (pending != state) pending = SaveRuntime(pending);
        return pending;
    }

    private static void EnsureRecoveryDestination(RuntimeState? state, DbxConfig config)
    {
        if (state is { RecoveryRequired: true } &&
            (!RecoveryDestination.Resolved(state.ActiveConfig) || !RecoveryDestination.Matches(state.ActiveConfig, config)))
            throw new PendingUploadsException(RecoveryDestination.Explain(state));
    }

    private void EnsureCacheOwnerStopped(DbxConfig config, RuntimeState state)
    {
        string path = Path.Combine(config.CacheDir, "dbxl-owner.json");
        if (!File.Exists(path)) return;
        CacheOwner owner;
        try { owner = JsonFiles.Read<CacheOwner>(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw StartupFailure.ForUser(new PendingUploadsException("キャッシュの所有プロセスを確認できません。"), "cache_owner_unknown", path,
            "キャッシュの所有プロセスの記録を読み取れません。", "元の設定と状態を残し、記録と所有プロセスの終了を手動で確認してください。"); }
        bool legacy = owner.IdentityVersion == 0;
        if (owner.IdentityVersion is not (0 or 1) || !Path.IsPathFullyQualified(owner.StatePath) ||
            legacy && (owner.LaunchPending || !ProcessIdentity.CompleteRecord(owner.Pid, owner.ProcessStartedUtc, owner.ExecutablePath)))
            throw StartupFailure.ForUser(new PendingUploadsException("古いキャッシュの所有プロセスの同一性を確認できません。"), "cache_owner_unknown", path, "古いキャッシュの所有プロセスの同一性を確認できません。", "元の設定と状態を残し、所有プロセスの終了と起動マークを手動で確認してください。");
        if (owner.LaunchPending && (owner.IdentityVersion != 1 ||
            !ProcessIdentity.CompleteRecord(owner.LaunchParentPid, owner.LaunchParentStartedUtc, owner.LaunchParentExecutablePath) ||
            (owner.Pid <= 0 ? owner.LaunchPhase != StartupPhase.Intent : owner.LaunchPhase is not (StartupPhase.RecordedSuspended or StartupPhase.Resumed))))
            throw StartupFailure.ForUser(new PendingUploadsException("キャッシュの古い起動マークを確認できません。手動の回復確認が必要です。"), "cache_owner_unknown", path, "キャッシュの古い起動マークを確認できません。手動の回復確認が必要です。", "元の設定と状態を残し、所有プロセスの終了と起動マークを手動で確認してください。");
        if (owner.LaunchPending && owner.IdentityVersion == 1)
        {
            var marker = new RuntimeState { IdentityVersion = owner.IdentityVersion, RclonePid = owner.Pid,
                ProcessStartedUtc = owner.ProcessStartedUtc, ExecutablePath = owner.ExecutablePath, LaunchPending = true,
                LaunchPhase = owner.LaunchPhase, LaunchParentPid = owner.LaunchParentPid,
                LaunchParentStartedUtc = owner.LaunchParentStartedUtc, LaunchParentExecutablePath = owner.LaunchParentExecutablePath };
            bool stopped = owner.Pid <= 0 && owner.LaunchPhase == StartupPhase.Intent && ParentStopped(marker);
            if (owner.Pid > 0)
            {
                if (!ProcessIdentity.CompleteRecord(owner.Pid, owner.ProcessStartedUtc, owner.ExecutablePath))
                    throw StartupFailure.ForUser(new PendingUploadsException("キャッシュの起動マークの子プロセスの同一性を確認できません。"), "cache_owner_unknown", path, "キャッシュの起動マークの子プロセスの同一性を確認できません。", "元の設定と状態を残し、所有プロセスの終了と起動マークを手動で確認してください。");
                using var child = ExplainOtherOwner(state, () => ProcessIdentity.Open(marker));
                stopped = child is null;
                if (child is not null && owner.LaunchPhase == StartupPhase.RecordedSuspended && ThreadsSuspended(child) == true && ParentStopped(marker))
                {
                    child.Kill();
                    if (!child.WaitForExit(10000)) throw new PendingUploadsException("Owned suspended cache child did not exit.");
                    stopped = true;
                }
            }
            if (stopped)
            {
                owner = owner with { Pid = 0, ProcessStartedUtc = default, ExecutablePath = "", LaunchPending = false, LaunchPhase = StartupPhase.None };
                JsonFiles.Write(path, owner);
            }
        }
        if (owner.Pid <= 0 && owner.LaunchPending)
            throw StartupFailure.ForUser(new PendingUploadsException("Cache launch ownership is unresolved; retain recovery state."), "cache_owner_unknown", path, "Cache launch ownership is unresolved; retain recovery state.", "元の設定と状態を残し、所有プロセスの終了と起動マークを手動で確認してください。");
        using var process = ExplainOtherOwner(state, () => ProcessIdentity.Open(owner.Pid, owner.ProcessStartedUtc,
            owner.ExecutablePath, ProcessIdentityQueries.System, trustedRecord: owner.IdentityVersion == 1));
        if (process is not null) throw StartupFailure.ForUser(new InvalidOperationException("Cache owner is still running."), "cache_owner_running", config.CacheDir,
            "キャッシュの所有プロセスの rclone がまだ動いているため使えません。", "その所有プロセスの設定から通常の停止を行い、終了を確認してください。");
        // Only confirmed death (including PID reuse with a different start time)
        // permits upgrading an unversioned owner. Matching live legacy owners refuse above.
        if (legacy) JsonFiles.Write(path, owner with { IdentityVersion = 1, Pid = 0,
            ProcessStartedUtc = default, ExecutablePath = "" });
    }

    private RuntimeState? LoadRuntime()
    {
        RuntimeState? local = State.Load();
        RuntimeState? recovered = RecoveryJournal.Recover(Config.PathName, Config.Load(), local);
        if (recovered is not null) recovered = ResolveLaunch(recovered);
        if (recovered is not null && recovered != local && !RecoveryJournal.ForeignOwner(recovered, State.PathName)) State.Save(recovered);
        return recovered;
    }

    private RuntimeState SaveRuntime(RuntimeState state)
    {
        state = RecoveryJournal.PrepareSave(Config.PathName, State.PathName, state, State.Load());
        if (RecoveryDestination.Resolved(state.ActiveConfig))
            RecoveryJournal.Save(Config.PathName, state);
        State.Save(state);
        return state;
    }

    private void EnsureCacheProvenance(DbxConfig config, RuntimeState? before)
    {
        CacheInspection scan = PendingCacheInspector.InspectStopped(config.CacheDir, () => preparation?.CheckCancellation());
        if (!scan.CompleteScan) throw StartupFailure.ForUser(new PendingUploadsException(scan.Detail), "cache_scan_incomplete", config.CacheDir,
            "キャッシュを最後まで走査できないため起動しません。", "元の設定・状態とキャッシュを残し、場所と読み取り権限を確認してください。");
        if (scan.Clean) return;
        bool known = before is not null && before.ConfigPath.Length > 0 &&
            RecoveryDestination.Resolved(before.ActiveConfig) && (before.RclonePid > 0 ||
                before.StopDisposition == StopDisposition.ClearedByUser ||
                before.LaunchRecoveryProof == "launch_parent_exited_before_pid" && ParentStopped(before));
        if (!known)
            throw StartupFailure.ForUser(new PendingUploadsException("\u72b6\u614b\u3068\u56de\u5fa9\u306e\u8a18\u9332\u304c\u7121\u3044\u30ad\u30e3\u30c3\u30b7\u30e5\u306b\u3001\u672a\u30a2\u30c3\u30d7\u30ed\u30fc\u30c9\u307e\u305f\u306f\u4e0d\u660e\u306a\u30d5\u30a1\u30a4\u30eb\u304c\u3042\u308a\u307e\u3059\u3002\u521d\u3081\u3066\u306e\u5229\u7528\u3068\u3057\u3066\u306f\u8d77\u52d5\u3057\u307e\u305b\u3093\u3002\u5143\u306e\u8a2d\u5b9a\u3068\u72b6\u614b\u3092\u5fa9\u5143\u3057\u3066\u304f\u3060\u3055\u3044\u3002"), "cache_provenance_unknown", config.CacheDir, "\u72b6\u614b\u3068\u56de\u5fa9\u306e\u8a18\u9332\u304c\u7121\u3044\u30ad\u30e3\u30c3\u30b7\u30e5\u306b\u3001\u672a\u30a2\u30c3\u30d7\u30ed\u30fc\u30c9\u307e\u305f\u306f\u4e0d\u660e\u306a\u30d5\u30a1\u30a4\u30eb\u304c\u3042\u308a\u307e\u3059\u3002\u521d\u3081\u3066\u306e\u5229\u7528\u3068\u3057\u3066\u306f\u8d77\u52d5\u3057\u307e\u305b\u3093\u3002\u5143\u306e\u8a2d\u5b9a\u3068\u72b6\u614b\u3092\u5fa9\u5143\u3057\u3066\u304f\u3060\u3055\u3044\u3002", "元の設定と状態を復元し、残ったキャッシュをそのまま保持してください。");
        if (before!.StopDisposition != StopDisposition.ClearedByUser || before.RecoveryRequired)
            EnsureRecoveryDestination(before with { RecoveryRequired = true }, config);
    }

    private static string? OrdinaryPath(string? path) => path is null ? null :
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    internal static void RequireRescueDirectoryChange(DbxConfig before, DbxConfig after)
    {
        if (!RescuePaths.Same(before.RescueDir, after.RescueDir) && RescueStore.HasUnacknowledged(before))
            throw new PendingUploadsException("確認済みでない退避記録があります。先に元の保存先で内容を確認し、確認済みにしてから rescueDir を変更してください。");
    }

    private IReadOnlyList<RescueRecord> PrepareCache(DbxConfig config, RuntimeState? before)
    {
        preparation?.Update("キャッシュの走査");
        CacheInspection scan = PendingCacheInspector.InspectStopped(config.CacheDir, () => preparation?.CheckCancellation());
        if (!scan.CompleteScan) throw StartupFailure.ForUser(new PendingUploadsException(scan.Detail), "cache_scan_incomplete", config.CacheDir,
            "キャッシュを最後まで走査できないため起動しません。", "元の設定・状態とキャッシュを残し、場所と読み取り権限を確認してください。");
        var records = RescueStore.Pending(config, before?.RescueRecords);
        foreach (var record in records.Where(r => r.RecordStatus == "verified" && !r.MoveCompleted))
        {
            preparation?.CheckCancellation();
            _ = RescueStore.ReconcileMoved(record);
        }
        int completed = 0;
        preparation?.Update("退避", completed, scan.Entries.Count);
        foreach (var entry in scan.Entries)
        {
            preparation?.Update("退避", completed++, scan.Entries.Count);
            // A record is only a resumable plan for an item still found in this cache.
            // Corrupt/foreign/completed records never control startup.
            RescueRecord? plan = records.FirstOrDefault(r => r.RecordStatus == "verified" && !r.MoveCompleted &&
                RescuePaths.Same(r.OriginalPath, entry.DataPath) && RescuePaths.Same(r.OriginalMetadataPath, entry.MetadataPath));
            if (plan is not null) { RescueStore.FinishMove(plan); continue; }
            if (!entry.MetadataValid || entry.Dirty == true && !entry.CompleteData)
                RescueStore.Rescue(config, entry, RescueStore.CloudPath(before, entry),
                    RecoveryCacheNames.PreservationReason(entry.Relative) ?? entry.Problem ?? "ファイル本体が途中までのため退避しました。");
        }
        preparation?.Update("退避", scan.Entries.Count, scan.Entries.Count);
        return RescueStore.Pending(config, before?.RescueRecords);
    }

    private static void EnsurePortAvailable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.ExclusiveAddressUse = true; listener.Start(); }
        finally { listener.Stop(); }
    }

    private static string WaitForImage(Process process)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (process.HasExited) throw new IOException("rclone exited before its process image was available.");
            process.Refresh();
            try { return ProcessIdentity.ImagePath(process); }
            catch (System.ComponentModel.Win32Exception) { /* Image query may not be ready yet; never use a loader module. */ }
            Thread.Sleep(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(5));
        throw new TimeoutException("rclone process image was not available.");
    }

    private static void WriteFilter(string path, string content)
    {
        if (File.Exists(path)) File.Copy(path, path + ".bak-" + Guid.NewGuid().ToString("N"));
        string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(pending, content, new UTF8Encoding(false));
        File.Move(pending, path, overwrite: true);
    }

    private static void LogEvent(DbxConfig config, string message, bool requireControlGate = true)
    {
        int Append()
        {
            if (!MountLogStore.IsLocalPath(config.LogDir)) return 0;
            try
            {
                Directory.CreateDirectory(config.LogDir);
                File.AppendAllText(Path.Combine(config.LogDir, "supervisor.log"), DateTime.UtcNow.ToString("O") + " " + message + "\n", new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.WriteLine("Application event recording is unavailable."); }
            return 0;
        }
        if (requireControlGate) ControlGate.Execute(Append); else Append();
    }
}
