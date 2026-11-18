using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
public sealed record RuntimeState
{
    public int RclonePid { get; init; }
    public MountLogInfo LogCapture { get; init; } = new();
    public int IdentityVersion { get; init; } = 1;
    public int LaunchParentPid { get; init; }
    public DateTime LaunchParentStartedUtc { get; init; }
    public string LaunchParentExecutablePath { get; init; } = "";
    public StartupPhase LaunchPhase { get; init; }
    public string? LaunchRecoveryProof { get; init; }
    public bool LaunchPending { get; init; }
    public bool? StartedReadOnly { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MountDevice { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MountDropboxBinding { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MountDropboxCursor { get; init; }
    public string GenerationId { get; init; } = "";
    public long GenerationNumber { get; init; }
    public long RevisionNumber { get; init; }
    public string OwnerStatePath { get; init; } = "";
    public int RecoveryJournalVersion { get; init; }
    public DateTimeOffset? RecoveryRevisionUtc { get; init; }
    public string[] RescueRecords { get; init; } = [];
    public DateTimeOffset? RecoveryComparedUtc { get; init; }
    public int RecoveryComparedFiles { get; init; }
    public double? RecoveryComparisonAgeSeconds { get; init; }
    public DateTimeOffset? ChildResumedUtc { get; init; }
    public string? CacheDataRoot { get; init; }
    public string? CacheMetadataRoot { get; init; }
    public DateTime ProcessStartedUtc { get; init; }
    public string ExecutablePath { get; init; } = "";
    public int RcPort { get; init; }
    public string RcUser { get; init; } = "";
    public string RcPassword { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public DbxConfig ActiveConfig { get; init; } = new();
    public bool StopInProgress { get; init; }
    public bool StopRequested { get; init; }
    public long StopSequence { get; init; }
    public StopDisposition StopDisposition { get; init; } = StopDisposition.Unknown;
    public bool RecoveryRequired { get; init; } = true;
    public ProcessUncertainty? UncertainProcess { get; init; }
    public DownloadWatchState? DownloadWatch { get; init; }
    public string? CacheInspection { get; init; }
    public DateTime? StoppedUtc { get; init; }
    public int? SupervisorPid { get; init; }
    public DateTime? SupervisorStartedUtc { get; init; }
    public string? SupervisorExecutablePath { get; init; }
    public int RestartCount { get; init; }
    public override string ToString() => $"RuntimeState pid={RclonePid} [authentication redacted]";
}

public sealed class StateStore
{
    public string PathName { get; }
    private readonly Action<string>? created;
    public StateStore(string? path = null, Action<string>? onCreated = null)
    {
        PathName = Path.GetFullPath(path ?? Environment.GetEnvironmentVariable("DBXL_STATE") ?? DbxConfig.DefaultStatePath);
        created = onCreated;
    }
    public RuntimeState? Load() => ControlGate.Execute(() =>
    {
        if (!File.Exists(PathName)) return null;
        using var directory = PrivateStateFiles.LockParent(PathName);
        PrivateStateFiles.ValidateExisting(PathName);
        return JsonFiles.Read<RuntimeState>(PathName);
    });
    internal void EnsurePrivateDirectory() => PrivateStateFiles.EnsureParent(PathName);
    public void Save(RuntimeState state) => ControlGate.Execute(() => { SaveLocked(state); return 0; });

    private void SaveLocked(RuntimeState state)
    {
        EnsurePrivateDirectory();
        using var directory = PrivateStateFiles.LockParent(PathName);
        PrivateStateFiles.ValidateExisting(PathName);
        string pending = PathName + ".pending-" + Guid.NewGuid().ToString("N");
        // ACL is supplied to CreateFile by the BCL; create and write use this single stream/handle.
        using (FileStream stream = new FileInfo(pending).Create(FileMode.CreateNew, FileSystemRights.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough, PrivateStateFiles.FileSecurity()))
        {
            created?.Invoke(pending);
            byte[] content = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(state, JsonFiles.Options) + "\n");
            stream.Write(content); stream.Flush(flushToDisk: true);
        }
        File.Move(pending, PathName, overwrite: true);
    }

}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
internal sealed record CacheOwner(int Pid, DateTime ProcessStartedUtc, string ExecutablePath, string StatePath, bool LaunchPending = false, int IdentityVersion = 0,
    int LaunchParentPid = 0, DateTime LaunchParentStartedUtc = default,
    string LaunchParentExecutablePath = "", StartupPhase LaunchPhase = StartupPhase.None, string GenerationId = "", long GenerationNumber = 0);

internal sealed record ProcessIdentityQueries(Func<Process, string> Name, Func<Process, bool> Exited,
    Func<Process, DateTime> StartedUtc, Func<Process, string> Image)
{
    internal static readonly ProcessIdentityQueries System = new(p => p.ProcessName, p => p.HasExited,
        p => p.StartTime.ToUniversalTime(), ProcessIdentity.ImagePath);
}

public static class ProcessIdentity
{
    public static Process? Open(RuntimeState state)
    {
        if (state.RclonePid <= 0 && (state.LaunchPending || state.StopDisposition is StopDisposition.StartupFailed or StopDisposition.ProcessUnknown && state.StoppedUtc is null))
            throw new ProcessIdentityUnknownException("Launch ownership has not been resolved; retain recovery state.",
                identity: new ProcessUncertainty(state.RclonePid, null));
        return Open(state.RclonePid, state.ProcessStartedUtc, state.ExecutablePath, ProcessIdentityQueries.System, trustedRecord: state.IdentityVersion == 1);
    }

    public static Process? Open(int pid, DateTime startedUtc, string executablePath) =>
        Open(pid, startedUtc, executablePath, ProcessIdentityQueries.System);

    internal static Process? Open(int pid, DateTime startedUtc, string executablePath, ProcessIdentityQueries query, bool legacySupervisor = false, bool trustedRecord = true)
    {
        if (pid <= 0) return null;
        Process? process = null;
        string? observedName = null;
        try
        {
            process = Process.GetProcessById(pid);
            string? expectedName = !trustedRecord || startedUtc == default ? null : RecordedName(executablePath);
            if (expectedName is not null)
            {
                string name = query.Name(process);
                observedName = string.IsNullOrWhiteSpace(name) ? null : name;
                if (string.IsNullOrWhiteSpace(name)) throw new NotSupportedException("Process name is unavailable.");
                bool matches = name.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
                if (!matches) { process.Dispose(); return null; }
            }
            else
            {
                try { observedName = query.Name(process); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
            if (!query.Exited(process))
            {
                if (startedUtc == default)
                    throw new ProcessIdentityUnknownException("Saved process start time is missing; retain recovery state.",
                        identity: new ProcessUncertainty(pid, observedName));
                if (query.StartedUtc(process) != startedUtc) { process.Dispose(); return null; }
                if (!trustedRecord) throw new ProcessIdentityUnknownException("Legacy process identity is not trusted; retain recovery state.", identity: new ProcessUncertainty(pid, observedName));
                if (legacySupervisor) return process; // Old records have a PID/start time but no executable path.
                string image = query.Image(process);
                if (string.Equals(image, executablePath, StringComparison.OrdinalIgnoreCase)) return process;
                throw new ProcessIdentityUnknownException("Process executable identity does not match the saved record; retain recovery state.");
            }
        }
        catch (ArgumentException) when (process is null) { }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            bool exited = false;
            try { exited = process is not null && query.Exited(process); }
            catch (Exception queryError) when (queryError is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
            process?.Dispose();
            if (exited) return null;
            var identity = new ProcessUncertainty(pid, observedName);
            throw new ProcessIdentityUnknownException(identity.Explain(), e, identity);
        }
        process?.Dispose();
        return null;
    }
    internal static bool CompleteRecord(int pid, DateTime startedUtc, string executablePath) =>
        pid > 0 && startedUtc != default && RecordedName(executablePath) is not null;

    private static string? RecordedName(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (index == 0 && path.Length >= 2 && path[1] == ':' && part.Length == 2 && char.IsAsciiLetter(part[0])) continue;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        }
        string file = Path.GetFileName(path);
        if (file.Length == 0 || file.EndsWith('.') || file.EndsWith(' ') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (!Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        string name = Path.GetFileNameWithoutExtension(file);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    internal static Process? OpenSupervisor(RuntimeState state)
    {
        if (state.SupervisorPid is not { } pid || state.SupervisorStartedUtc is not { } started) return null;
        return Open(pid, started, state.SupervisorExecutablePath ?? "", ProcessIdentityQueries.System,
            legacySupervisor: state.SupervisorExecutablePath is null, trustedRecord: state.IdentityVersion == 1);
    }

    public static string ImagePath(Process process)
    {
        var image = new StringBuilder(32768);
        uint size = (uint)image.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, image, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return image.ToString();
    }
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);

}

internal static class ControlGate
{
    // A busy control operation is uncertainty; shutdown queries never wait on the UI thread.
    public static bool TryExecute(Func<bool> action)
    {
        using var mutex = new Mutex(false, "Local\\DbxLight.Control.v1");
        bool held;
        try { held = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) return false;
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    public static bool TryExecute<T>(Func<T> action, TimeSpan timeout, out T result)
    {
        using var mutex = new Mutex(false, "Local\\DbxLight.Control.v1");
        bool held;
        try { held = mutex.WaitOne(timeout); } catch (AbandonedMutexException) { held = true; }
        if (!held) { result = default!; return false; }
        try { result = action(); return true; } finally { mutex.ReleaseMutex(); }
    }

    // A synchronous API keeps the thread-affine Mutex on the acquiring thread.
    public static T Execute<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, "Local\\DbxLight.Control.v1");
        bool held;
        try { held = mutex.WaitOne(TimeSpan.FromSeconds(45)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new ControlBusyException();
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }
}
