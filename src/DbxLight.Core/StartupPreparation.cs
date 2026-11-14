using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DbxLight.Core;

public sealed record PreparationProgress(string Stage, int Completed, int Total, DateTimeOffset StartedUtc, bool Stopping = false);
internal sealed record PreparationState(string Id, string ConfigPath, int Pid, DateTime ProcessStartedUtc, string ExecutablePath,
    bool Active, PreparationProgress Progress);
internal sealed class PreparationCancelledException(Exception? persistenceFailure = null)
    : OperationCanceledException("Startup preparation was cancelled.", persistenceFailure);
internal sealed class PreparationPersistenceException(Exception failure)
    : IOException("Startup preparation completion could not be saved.", failure);
internal sealed class RecoveryBudgetExceededException : Exception;
internal sealed class RecoveryBudget(TimeSpan time, int folders, Func<TimeSpan>? elapsedOverride = null)
{
    private readonly long began = Stopwatch.GetTimestamp();
    private readonly HashSet<string> listed = new(StringComparer.Ordinal);
    private TimeSpan Elapsed => elapsedOverride?.Invoke() ?? Stopwatch.GetElapsedTime(began);
    internal bool Expired => Elapsed >= time;
    internal int RemainingMilliseconds => Math.Max(1, (int)Math.Min(30000, (time - Elapsed).TotalMilliseconds));
    internal void Check() { if (Expired) throw new RecoveryBudgetExceededException(); }
    internal void Folder(string path)
    {
        Check();
        if (!listed.Contains(path) && listed.Count >= folders) throw new RecoveryBudgetExceededException();
        listed.Add(path);
    }
}

/// <summary>Private progress and cancellation files are read without the lifecycle mutex.</summary>
internal sealed class StartupPreparation : IDisposable
{
    private readonly string path;
    private PreparationState state;
    private readonly CancellationToken cancellation;
    internal RecoveryBudget Budget { get; }
    internal Action<string>? CompletionCheckpoint { get; set; }
    internal static readonly TimeSpan MaximumComparisonTime = TimeSpan.FromSeconds(120);
    internal const int MaximumComparisonFolders = 200;
    private static string PathFor(string statePath) => statePath + ".preparation.json";
    private static string CancelPath(string statePath, string id) => statePath + ".cancel-" + id + ".json";
    private static string MutexFor(string statePath) => "Local\\DbxLight.Preparation." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RescuePaths.Full(statePath).ToUpperInvariant())))[..20];
    private StartupPreparation(string statePath, string configPath, TimeSpan time, int folders, CancellationToken cancellation, Func<TimeSpan>? elapsedOverride)
    {
        path = statePath; this.cancellation = cancellation;
        using var self = Process.GetCurrentProcess();
        state = new(Guid.NewGuid().ToString("N"), configPath, self.Id, self.StartTime.ToUniversalTime(), ProcessIdentity.ImagePath(self), true,
            new("持ち主の確認", 0, 0, DateTimeOffset.UtcNow));
        Budget = new(time, folders, elapsedOverride);
        Save();
    }
    internal static StartupPreparation Begin(string statePath, string configPath, TimeSpan? time = null, int? folders = null,
        CancellationToken cancellation = default, Func<TimeSpan>? elapsedOverride = null) =>
        new(statePath, configPath, time ?? MaximumComparisonTime, folders ?? MaximumComparisonFolders, cancellation, elapsedOverride);
    internal void Update(string stage, int completed = 0, int total = 0)
    {
        CheckCancellation();
        state = state with { Progress = state.Progress with { Stage = stage, Completed = completed, Total = total } };
        Save();
    }
    internal void CheckCancellation()
    {
        if (cancellation.IsCancellationRequested || File.Exists(CancelPath(path, state.Id))) throw new PreparationCancelledException();
    }
    internal void SealForLaunch()
    {
        using var mutex = new Mutex(false, MutexFor(path)); Enter(mutex);
        try
        {
            CheckCancellation();
            state = state with { Progress = state.Progress with { Stage = "子の起動", Completed = 0, Total = 1 } };
            Save();
        }
        finally { mutex.ReleaseMutex(); }
    }
    private void Save() => PrivateJsonFile.Write(PathFor(path), state);
    internal static PreparationState? Read(string statePath, string configPath)
    {
        try
        {
            var value = PrivateJsonFile.Read<PreparationState>(PathFor(statePath));
            if (value is null || !value.Active || !RescuePaths.Same(value.ConfigPath, configPath) || !Guid.TryParseExact(value.Id, "N", out _)) return null;
            using var alive = ProcessIdentity.Open(value.Pid, value.ProcessStartedUtc, value.ExecutablePath);
            if (alive is null) return null;
            if (File.Exists(CancelPath(statePath, value.Id))) value = value with { Progress = value.Progress with { Stopping = true } };
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return null; }
    }
    internal static bool RequestStop(string statePath, string configPath)
    {
        using var mutex = new Mutex(false, MutexFor(statePath)); Enter(mutex);
        try
        {
            var value = Read(statePath, configPath);
            if (value is null || value.Progress.Stage == "子の起動") return false;
            PrivateJsonFile.Write(CancelPath(statePath, value.Id), new { preparationId = value.Id, requestedUtc = DateTimeOffset.UtcNow });
            return true;
        }
        finally { mutex.ReleaseMutex(); }
    }
    private static void Enter(Mutex mutex)
    {
        bool held; try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { held = true; }
        if (!held) throw new TimeoutException("Preparation state is busy.");
    }
    internal void Complete(bool launched = false)
    {
        if (!state.Active) return;
        bool cancelled = false;
        try
        {
            CompletionCheckpoint?.Invoke("before-completion");
            using var mutex = new Mutex(false, MutexFor(path)); Enter(mutex);
            Exception? completionFailure = null;
            try
            {
                if (!state.Active) return;
                // A successful launch has already sealed out external preparation stops.
                // Local stops then use the lifecycle gate and the normal live-child guard.
                if (!launched)
                    try { CheckCancellation(); }
                    catch (PreparationCancelledException) { cancelled = true; }
                state = state with { Active = false };
                CompletionCheckpoint?.Invoke("before-completion-save");
                Save();
            }
            catch (Exception error) { completionFailure = error; throw; }
            finally { StartupFailure.Cleanup(completionFailure, mutex.ReleaseMutex, path); }
            CompletionCheckpoint?.Invoke("after-completion");
        }
        catch (Exception error) when (cancelled || !launched &&
            (cancellation.IsCancellationRequested || File.Exists(CancelPath(path, state.Id))))
        {
            // Keep the accepted stop even if completion persistence or mutex handling fails.
            throw new PreparationCancelledException(error);
        }
        catch (Exception error) { throw new PreparationPersistenceException(error); }
        if (cancelled) throw new PreparationCancelledException();
    }
    public void Dispose() => Complete();
}
