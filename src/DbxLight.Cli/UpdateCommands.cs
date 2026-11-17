using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Cli;

internal static class UpdateCommands
{
    internal static int Run(MountController controller, string[] operands, bool json)
    {
        if (operands.Length == 0) throw new ArgumentException("Usage: update check|download|apply|swap");
        var words = operands.Skip(1).ToList();
        string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var store = new UpdateStore(controller.State.PathName);
        void Done(object result, string text)
        {
            if (json) Program.WriteJson(new { ok = true, result }); else Console.WriteLine(text);
        }
        switch (operands[0])
        {
            case "check":
                RequireEmpty(words);
                using (var client = new UpdateClient(store))
                {
                    var result = client.CheckAsync(target).GetAwaiter().GetResult();
                    Done(result, result.UpdateAvailable ? "New version: " + result.LatestVersion : "Current version: " + result.CurrentVersion);
                }
                return 0;
            case "download":
                RequireEmpty(words);
                using (var client = new UpdateClient(store))
                {
                    var result = client.DownloadAsync(target).GetAwaiter().GetResult();
                    Done(result, result.Version + " " + result.Staging);
                }
                return 0;
            case "apply":
                string? zipPath = Option(words, "--from"), checksum = Option(words, "--sha256")?.ToLowerInvariant();
                bool yes = Flag(words, "--yes");
                RequireEmpty(words);
                if (checksum is not null && (zipPath is null || !UpdatePackage.IsHash(checksum))) throw new ArgumentException("--sha256 requires --from and a SHA-256 hex value.");
                if (!yes)
                {
                    Done(new { applied = false, confirmationRequired = true, target, from = zipPath, releasesUrl = UpdateSource.ReleasesUrl },
                        "Update files in " + target + (zipPath is null ? " from the latest release." : " from " + zipPath + ".") + " Use --yes to proceed.");
                    return 0;
                }
                if (!UpdatePackage.CanApply(target))
                    throw new InvalidOperationException("この場所では自動で更新できません\n新しいバージョンの zip を展開して入れ直してください。\n" + UpdateSource.ReleasesUrl);
                RequireStopped(controller, target);
                UpdateDownloadResult download;
                if (zipPath is null)
                {
                    using var client = new UpdateClient(store);
                    download = client.DownloadAsync(target).GetAwaiter().GetResult();
                }
                else
                {
                    var info = new FileInfo(zipPath);
                    if (info.Length > UpdateClient.MaximumZipBytes) throw new InvalidDataException("Release ZIP exceeds the size limit.");
                    byte[] data = File.ReadAllBytes(zipPath);
                    if (checksum is not null && UpdatePackage.Hash(data) != checksum) throw new InvalidDataException("Local ZIP SHA-256 does not match.");
                    download = UpdatePackage.Stage(data, store);
                }
                try
                {
                    int pid = UpdateLauncher.Swap(download, target, controller.State.PathName, Environment.ProcessId, false, []);
                    Done(new { scheduled = true, version = download.Version, staging = download.Staging, swapPid = pid, resultPath = store.ResultPath },
                        "Update scheduled. Final result: " + store.ResultPath);
                }
                catch { store.CompleteStage(download.Staging); throw; }
                return 0;
            case "swap":
                int separator = words.IndexOf("--");
                string[] forwarded = separator < 0 ? [] : words.Skip(separator + 1).ToArray();
                if (separator >= 0) words.RemoveRange(separator, words.Count - separator);
                string destination = Option(words, "--target") ?? throw new ArgumentException("--target is required.");
                string staging = Option(words, "--staging") ?? throw new ArgumentException("--staging is required.");
                string wait = Option(words, "--wait-pid") ?? throw new ArgumentException("--wait-pid is required.");
                string started = Option(words, "--wait-started-utc") ?? throw new ArgumentException("--wait-started-utc is required.");
                if (!DateTime.TryParseExact(started, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime waitStarted)) throw new ArgumentException("Invalid --wait-started-utc.");
                bool relaunch = Flag(words, "--relaunch");
                RequireEmpty(words);
                if (!int.TryParse(wait, out int waitPid) || waitPid < 0) throw new ArgumentException("Invalid --wait-pid.");
                var swapped = new UpdateSwap(store).Run(destination, staging, waitPid, relaunch, forwarded, waitStarted);
                if (json)
                {
                    if (swapped.Success) Program.WriteJson(new { ok = true, result = swapped });
                    else Program.WriteJson(new { ok = false, code = "failed", message = swapped.Reason, result = swapped });
                }
                else Console.WriteLine(swapped.Success ? "Updated to " + swapped.Version : swapped.Reason);
                return swapped.Success ? 0 : 1;
            default: throw new ArgumentException("Usage: update check|download|apply|swap");
        }
    }
    internal static void RequireStopped(MountController controller, string target)
    {
        const string message = "常駐のメニューから更新するか、常駐とマウントを終了してから実行してください。";
        // The per-state resident mutex also catches residents without a running mount.
        string identity = WindowsIdentity.GetCurrent().User!.Value + "|" + Path.GetFullPath(controller.State.PathName).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        if (Mutex.TryOpenExisting(@"Local\DbxLight.Tray." + hash, out var resident))
        {
            using (resident)
            {
                bool acquired;
                try { acquired = resident.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException(message);
                resident.ReleaseMutex();
            }
        }
        var state = controller.State.Load();
        if (state is not null)
        {
            if (state.LaunchPending || state.StopInProgress || state.UncertainProcess is not null)
                throw new InvalidOperationException(message);
            using var child = ProcessIdentity.Open(state);
            if (child is not null) throw new InvalidOperationException(message);
            if (state.SupervisorPid is { } pid)
            {
                try
                {
                    using var supervisor = System.Diagnostics.Process.GetProcessById(pid);
                    if (!supervisor.HasExited && supervisor.StartTime.ToUniversalTime() == state.SupervisorStartedUtc) throw new InvalidOperationException(message);
                }
                catch (ArgumentException) { }
            }
        }
        // A different state still cannot update a running resident in this installation.
        try { using var file = new FileStream(Path.Combine(target, "DbxLight.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException(message); }
    }
    private static bool Flag(List<string> words, string option)
    {
        bool found = words.Remove(option);
        if (words.Contains(option)) throw new ArgumentException("Duplicate option: " + option);
        return found;
    }
    private static string? Option(List<string> words, string option)
    {
        int index = words.IndexOf(option);
        if (index < 0) return null;
        if (index + 1 == words.Count || words[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(option + " requires a value.");
        string result = words[index + 1]; words.RemoveRange(index, 2);
        if (words.Contains(option)) throw new ArgumentException("Duplicate option: " + option);
        return result;
    }
    private static void RequireEmpty(List<string> words)
    {
        if (words.Count > 0) throw new ArgumentException("Unexpected update argument: " + words[0]);
    }
}
