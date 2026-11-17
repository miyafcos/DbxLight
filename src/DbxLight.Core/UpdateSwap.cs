using System.Diagnostics;
using System.Globalization;

namespace DbxLight.Core;

public sealed class UpdateSwap
{
    private readonly UpdateStore store;
    internal Action<string>? Checkpoint { get; set; }
    internal Action<string, string[]>? RelaunchOverride { get; set; }
    internal Func<int, TimeSpan, bool>? WaitOverride { get; set; }
    internal Func<Process, DateTime>? WaitStartedUtcOverride { get; set; }
    internal TimeSpan WaitLimit { get; set; } = TimeSpan.FromSeconds(120);
    public UpdateSwap(UpdateStore store) => this.store = store;
    public UpdateResult Run(string target, string staging, int waitPid, bool relaunch, string[] arguments, DateTime? waitStartedUtc = null)
    {
        target = Path.GetFullPath(target); staging = Path.GetFullPath(staging);
        string version = UpdateVersion.Current, phase = "verify";
        string? backup = null, reason = null;
        bool rolledBack = false, changed = false, success = false, rollbackFailed = false, backupRootCreated = false, backupCreated = false;
        var moved = new List<string>();
        var installed = new List<string>();
        var locks = new List<FileStream>();
        FileStream? lease = null;
        ReleaseManifest? old = null;
        byte[]? oldManifestBytes = null;
        try
        {
            if (MountPaths.IsSameOrBelow(staging, target) || MountPaths.IsSameOrBelow(target, staging))
                throw new InvalidDataException("Install and staging directories must be separate.");
            if (!UpdateCleanup.IsStage(Path.GetFileName(staging)) || !Path.GetDirectoryName(staging)!.Equals(Path.GetFullPath(store.Root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staging must belong to this update state directory.");
            Directory.CreateDirectory(store.Root);
            lease = new FileStream(staging + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var incoming = UpdatePackage.ReadManifest(staging, out byte[] incomingBytes); version = incoming.Version;
            UpdatePackage.ValidateDirectory(staging, incoming, exact: true);
            phase = "target";
            if (!UpdatePackage.CanApply(target)) throw new InvalidDataException("This installation cannot be updated automatically.");
            Checkpoint?.Invoke("validated");
            phase = "wait";
            if (!Wait(waitPid, waitStartedUtc)) throw new TimeoutException("The process did not exit within the update wait limit.");
            phase = "target";
            old = UpdatePackage.ReadManifest(target, out oldManifestBytes);
            UpdatePackage.ValidateDirectory(target, old, exact: false);
            var oldNames = old.Files.Select(f => f.Name).Append("release.json").ToArray();
            var incomingNames = incoming.Files.Select(f => f.Name).Append("release.json").ToArray();
            foreach (string name in oldNames.Union(incomingNames, StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.Combine(target, name);
                if (Directory.Exists(path)) throw new IOException("A release file is occupied by a directory: " + name);
                if (!File.Exists(path)) continue;
                if (!oldNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("A new release file would replace an unmanaged file: " + name);
                phase = "locked";
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete));
                phase = "target";
            }
            Checkpoint?.Invoke("exclusive");
            string backupRoot = Path.Combine(target, "backup");
            if (Directory.Exists(backupRoot)) UpdatePackage.RequirePlain(backupRoot);
            backupRootCreated = !Directory.Exists(backupRoot);
            backup = Path.Combine(backupRoot, old.Version + "-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture));
            if (Directory.Exists(backup)) throw new IOException("The update backup already exists.");
            Directory.CreateDirectory(backup); backupCreated = true;
            foreach (string name in oldNames)
            {
                File.Move(Path.Combine(target, name), Path.Combine(backup, name));
                moved.Add(name); changed = true; phase = "swap";
            }
            Checkpoint?.Invoke("backed-up");
            foreach (string name in incomingNames)
            {
                Checkpoint?.Invoke("before-create:" + name);
                using (var output = new FileStream(Path.Combine(target, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    installed.Add(name); // Only this successful CreateNew grants rollback ownership.
                    Checkpoint?.Invoke("created:" + name);
                    using var input = File.OpenRead(Path.Combine(staging, name));
                    input.CopyTo(output);
                }
                Checkpoint?.Invoke("installed:" + name);
            }
            Checkpoint?.Invoke("verify");
            UpdatePackage.ValidateDirectory(target, incoming, exact: false);
            if (!File.ReadAllBytes(Path.Combine(target, "release.json")).SequenceEqual(incomingBytes))
                throw new InvalidDataException("Installed release manifest differs from the verified manifest.");
            success = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            reason = error.Message;
            if (phase == "locked" && (error is not IOException || (error.HResult & 0xffff) is not (32 or 33))) phase = "target";
            if (phase == "target" && error is IOException && (error.HResult & 0xffff) is 32 or 33) phase = "locked";
            foreach (var handle in locks) handle.Dispose(); locks.Clear();
            if (changed)
            {
                try
                {
                    Checkpoint?.Invoke("rollback");
                    foreach (string name in installed) File.Delete(Path.Combine(target, name));
                    foreach (string name in moved) File.Move(Path.Combine(backup!, name), Path.Combine(target, name));
                    UpdatePackage.ValidateDirectory(target, old!, exact: false);
                    if (!File.ReadAllBytes(Path.Combine(target, "release.json")).SequenceEqual(oldManifestBytes!))
                        throw new InvalidDataException("Restored release manifest does not match.");
                    rolledBack = true;
                }
                catch (Exception restore) when (restore is not OutOfMemoryException)
                { reason += " Rollback failed: " + restore.Message; phase = "rollback"; rollbackFailed = true; }
            }
            if (!rollbackFailed && backupCreated)
            {
                try
                {
                    if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup!).Any())
                    { Directory.Delete(backup!); backup = null; }
                    string backupRoot = Path.Combine(target, "backup");
                    if (backupRootCreated && Directory.Exists(backupRoot) && !Directory.EnumerateFileSystemEntries(backupRoot).Any()) Directory.Delete(backupRoot);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { reason += " Empty backup cleanup failed: " + cleanup.Message; }
            }
        }
        finally { foreach (var handle in locks) handle.Dispose(); lease?.Dispose(); }
        var result = new UpdateResult(success, version, reason, rolledBack, backup, DateTimeOffset.UtcNow, Stage: success ? null : phase);
        void Persist()
        {
            try { store.WriteResult(result); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { result = result with { Reason = (result.Reason is null ? "" : result.Reason + " ") + "Result recording failed: " + error.Message }; }
        }
        Persist();
        try { store.CompleteStage(staging); }
        catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(error); }
        // An unchanged target can always be restarted. Only failed rollback forbids it.
        if (relaunch && !rollbackFailed)
        {
            try
            {
                if (RelaunchOverride is { } launch) launch(Path.Combine(target, "DbxLight.exe"), arguments);
                else UpdateLauncher.Start(Path.Combine(target, "DbxLight.exe"), arguments, target);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { result = result with { Reason = (result.Reason is null ? "" : result.Reason + " ") + "Relaunch failed: " + error.Message }; Persist(); }
        }
        return result;
    }
    private bool Wait(int pid, DateTime? started)
    {
        if (pid <= 0) return true;
        if (WaitOverride is { } wait) return wait(pid, WaitLimit);
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return true; }
        using (process)
        {
            if (started is { } expected)
            {
                DateTime actual;
                try { actual = WaitStartedUtcOverride is { } read ? read(process) : process.StartTime.ToUniversalTime(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (OriginalWaitProcessEnded(pid, expected)) return true;
                    throw;
                }
                if (actual != expected.ToUniversalTime()) return true;
            }
            if (pid == Environment.ProcessId) throw new InvalidOperationException("The swap process cannot wait for itself.");
            return process.WaitForExit((int)WaitLimit.TotalMilliseconds);
        }
    }
    private static bool OriginalWaitProcessEnded(int pid, DateTime expected)
    {
        Process current;
        try { current = Process.GetProcessById(pid); }
        catch (ArgumentException) { return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
        using (current)
        {
            try { return current.HasExited || current.StartTime.ToUniversalTime() != expected.ToUniversalTime(); }
            catch (Exception error) when (error is not OutOfMemoryException) { return false; }
        }
    }

}

public static class UpdateLauncher
{
    public static int Start(string executable, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var child = DetachedProcess.CreateSuspended(info);
        int pid = child.Process.Id; child.ResumeRecorded(); return pid;
    }
    public static int Swap(UpdateDownloadResult download, string target, string statePath, int waitPid, bool relaunch, string[] arguments)
    {
        using var process = Process.GetProcessById(waitPid);
        var words = new List<string> { "--state", statePath, "update", "swap", "--target", target, "--staging", download.Staging,
            "--wait-pid", waitPid.ToString(CultureInfo.InvariantCulture), "--wait-started-utc", process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
        if (relaunch) words.Add("--relaunch");
        words.Add("--"); words.AddRange(arguments);
        return Start(Path.Combine(download.Staging, "dbxl.exe"), words);
    }
}
