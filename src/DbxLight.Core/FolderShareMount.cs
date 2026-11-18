using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DbxLight.Core;

internal sealed record FolderMountObservation(string Device, string RcloneConfigPath);
internal sealed record FolderMountIdentity(string ConfigPath, string StatePath, string Generation, long Number,
    int Pid, DateTime StartedUtc, string Device, string RcloneConfigPath, string? DropboxBinding, string? DropboxCursor)
{
    internal bool Matches(DropboxFolderAccount account) => DropboxBinding == FolderShareMount.BindAccount(account, RcloneConfigPath);
}

internal enum FolderMountPresence { Unknown, Stopped, Running }
internal readonly record struct FolderMountConnection(FolderMountPresence Presence, FolderMountIdentity? Identity = null,
    string? StoppedGeneration = null, long StopSequence = 0);

internal sealed class FolderShareMount(string configPath, string statePath,
    Func<RuntimeState, FolderMountObservation?>? inspect = null)
{
    internal string ConfigPath => configPath;

    internal FolderMountIdentity? Observe(DbxConfig config) => ObserveConnection(config).Identity;

    internal async Task<FolderMountConnection> ReadBoundConnectionAsync(DbxConfig config, CancellationToken token)
    {
        var connection = await Task.Run(() => ObserveConnection(config), token).WaitAsync(token).ConfigureAwait(false);
        if (connection.Presence == FolderMountPresence.Unknown || connection.Identity is { DropboxBinding: null })
            throw FolderOperationException.For("mount_unverified");
        return connection;
    }

    private FolderMountConnection ObserveConnection(DbxConfig config)
    {
        try
        {
            var state = PrivateJsonFile.Read<RuntimeState>(statePath);
            if (state is null) return ConfirmStopped(config);
            if (!OwnsState(state)) return default;
            if (!IsRunning(state, config)) return ConfirmStopped(config, state);
            state = MountProof.Retain(state);

            var observed = (inspect ?? Inspect)(state);
            if (observed is null || state.MountDevice is { } launchDevice && !SamePath(launchDevice, observed.Device))
                return default;
            if (state.MountDevice is null && !LegacyNetworkDevice(state.ActiveConfig, observed.Device))
                return default;

            if (config.RcloneConfigPath is { } configured && !SamePath(Path.GetFullPath(configured), observed.RcloneConfigPath))
                return default;
            return new(FolderMountPresence.Running, new(configPath, statePath, state.GenerationId, state.GenerationNumber,
                state.RclonePid, state.ProcessStartedUtc, observed.Device, observed.RcloneConfigPath,
                state.MountDropboxBinding, state.MountDropboxCursor));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return default; }
    }

    private FolderMountConnection ConfirmStopped(DbxConfig config, RuntimeState? state = null)
    {
        if (state is not null)
        {
            if (state.StopDisposition is StopDisposition.Running or StopDisposition.Unknown or StopDisposition.ProcessUnknown ||
                state.LaunchPending || state.StopInProgress || state.UncertainProcess is not null ||
                state.StoppedUtc is null && !(state.StopRequested && state.StopDisposition == StopDisposition.Clean))
                return default;
            using var process = ProcessIdentity.Open(state);
            if (process is not null) return default;
        }
        if (StartupPreparation.Read(statePath, configPath) is not null || ProbeDevice(config.MountPoint) is not (true, null))
            return default;
        return new(FolderMountPresence.Stopped, StoppedGeneration: state?.GenerationId, StopSequence: state?.StopSequence ?? 0);
    }

    private bool OwnsState(RuntimeState state) => SamePath(state.ConfigPath, configPath) &&
        (SamePath(state.OwnerStatePath, statePath) ||
         state.OwnerStatePath.Length == 0 && state.GenerationNumber == 0 && state.RclonePid == 0 &&
         state.StopRequested && state.StopDisposition == StopDisposition.Clean);

    private bool IsRunning(RuntimeState state, DbxConfig config) =>
        OwnsState(state) && state.GenerationId.Length > 0 && state.GenerationNumber > 0 && !state.LaunchPending &&
        state.StopDisposition == StopDisposition.Running && !state.StopRequested && !state.StopInProgress &&
        state.StoppedUtc is null && !state.RecoveryRequired && state.UncertainProcess is null &&
        SameConnection(config, state.ActiveConfig);

    private RuntimeState? ReadRunningState(DbxConfig config)
    {
        try
        {
            var state = PrivateJsonFile.Read<RuntimeState>(statePath);
            return state is not null && IsRunning(state, config) ? MountProof.Retain(state) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    internal async Task CheckAsync(DbxConfig expected, FolderMountIdentity identity, string relative, bool checkCopyPath,
        CancellationToken token)
    {
        var current = CheckSettings(expected, checkCopyPath);
        await RequireVisibleAsync(current, relative, token).ConfigureAwait(false);
        var observed = await Task.Run(() => Observe(current), token).WaitAsync(token).ConfigureAwait(false);
        if (observed != identity) throw FolderOperationException.For("sharing_changed");
    }

    internal IDisposable? CheckStillCurrent(DbxConfig expected, FolderMountIdentity identity, bool checkCopyPath)
    {
        var current = CheckSettings(expected, checkCopyPath);
        if (ReadRunningState(current) is not { } state || state.GenerationId != identity.Generation ||
            state.GenerationNumber != identity.Number || state.RclonePid != identity.Pid ||
            state.ProcessStartedUtc != identity.StartedUtc || state.MountDropboxBinding != identity.DropboxBinding ||
            state.MountDropboxCursor != identity.DropboxCursor ||
            state.MountDevice is { } device && !SamePath(device, identity.Device))
            throw FolderOperationException.For("sharing_changed");

        if (inspect is not null)
        {
            var observed = inspect(state);
            if (!SamePath(observed?.Device, identity.Device) || !SamePath(observed?.RcloneConfigPath, identity.RcloneConfigPath))
                throw FolderOperationException.For("sharing_changed");
            return null;
        }

        System.Diagnostics.Process? process = null;
        try
        {
            process = ProcessIdentity.Open(state);
            if (process is not null && SamePath(Device(current.MountPoint), identity.Device) && !process.HasExited)
            {
                var verified = process;
                process = null;
                return verified;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ProcessIdentityUnknownException) { }
        finally { process?.Dispose(); }
        throw FolderOperationException.For("sharing_changed");
    }

    private DbxConfig CheckSettings(DbxConfig expected, bool checkCopyPath)
    {
        var current = DropboxFolderClient.ReadConfiguration(configPath);
        if (!SameConnection(expected, current) || checkCopyPath && (expected.CopyPathPrefix != current.CopyPathPrefix ||
            expected.CopyPathBinding != current.CopyPathBinding))
            throw FolderOperationException.For("sharing_changed");
        return current;
    }

    internal static Task RequireVisibleAsync(DbxConfig config, string relative, CancellationToken token, bool folderOnly = true) =>
        Task.Run(() => RequireVisible(config, relative, token, folderOnly), token).WaitAsync(token);

    internal static void RequireVisible(DbxConfig config, string relative, CancellationToken token, bool folderOnly = true)
    {
        token.ThrowIfCancellationRequested();
        if (HiddenFolders.IsHidden(relative, config.HiddenPaths))
            throw FolderOperationException.For("not_visible");

        // The mounted VFS has already applied the complete rclone filter. Keep checking
        // names after the first match to reject a case/Unicode collision in that parent.
        string parent = MountMetadata.Extended(MountPaths.CanonicalMount(config.MountPoint));
        string[] components = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            for (int i = 0; i < components.Length; i++)
                parent = VisibleChild(parent, components[i], folderOnly || i < components.Length - 1, token);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw FolderOperationException.For("not_visible");
        }
    }

    internal static string VisibleChild(string parent, string component, bool folder, CancellationToken token, Action? visit = null)
    {
        token.ThrowIfCancellationRequested();
        var matches = new FileSystemEnumerable<(string Path, FileAttributes Attributes)>(parent,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes), new EnumerationOptions { AttributesToSkip = 0 });
        matches.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
        {
            token.ThrowIfCancellationRequested();
            visit?.Invoke();
            return entry.FileName.Equals(component, StringComparison.OrdinalIgnoreCase) ||
                entry.FileName.ToString().Normalize().Equals(component, StringComparison.OrdinalIgnoreCase);
        };
        string? found = null;
        foreach (var item in matches)
        {
            if (found is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                folder && !item.Attributes.HasFlag(FileAttributes.Directory))
                throw FolderOperationException.For("not_visible");
            found = item.Path;
        }
        return found ?? throw FolderOperationException.For("not_visible");
    }

    private static bool SameConnection(DbxConfig left, DbxConfig right) =>
        SamePath(MountPaths.CanonicalMount(left.MountPoint), MountPaths.CanonicalMount(right.MountPoint)) &&
        DropboxCopyPaths.SameConnection(left, right) &&
        left.NetworkMode == right.NetworkMode &&
        SamePath(Path.GetFullPath(left.CacheDir), Path.GetFullPath(right.CacheDir)) &&
        left.HiddenPaths.SequenceEqual(right.HiddenPaths, StringComparer.OrdinalIgnoreCase) &&
        left.ExcludePatterns.SequenceEqual(right.ExcludePatterns, StringComparer.Ordinal);

    private static bool SamePath(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static FolderMountObservation? Inspect(RuntimeState state)
    {
        using var process = ProcessIdentity.Open(state);
        if (process is null) return null;
        using var rc = new RcClient(state);
        if (StatusReader.Number(rc.Call("core/pid"), "pid") != process.Id) return null;
        JsonElement stats = rc.Call("vfs/stats");
        string? remote = DropboxFolderClient.Text(stats, "fs");
        JsonElement disk = DropboxFolderClient.Object(stats, "diskCache");
        string? cache = DropboxFolderClient.Text(disk, "path");
        if (!MatchesVfsRemote(state.ActiveConfig.Remote, remote) ||
            cache is null || !MountPaths.IsSameOrBelow(OrdinaryPath(cache), state.ActiveConfig.CacheDir))
            return null;
        string? device = Device(state.ActiveConfig.MountPoint);
        if (device is null) return null;
        // Ask the live process for the path only. Its default can differ from this CLI's.
        string? rcloneConfig = RcloneConfigPath(rc);
        return rcloneConfig is null ? null : new(device, rcloneConfig);
    }

    internal static string? RcloneConfigPath(RcClient rc)
    {
        string? path = DropboxFolderClient.Text(rc.Call("config/paths"), "config");
        return path is not null && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
    }

    internal static bool MatchesVfsRemote(string configured, string? observed)
    {
        int colon = configured.IndexOf(':');
        return observed is not null && colon > 0 && observed.IndexOf(':') == colon &&
            configured.AsSpan(0, colon).SequenceEqual(observed.AsSpan(0, colon)) &&
            configured.AsSpan(colon + 1).Trim('/').SequenceEqual(observed.AsSpan(colon + 1).Trim('/'));
    }

    internal static string BindAccount(DropboxFolderAccount account, string rcloneConfigPath) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            account.AccountId, account.NamespaceId, account.HomeNamespaceId, account.RootNamespaceId,
            Config = Path.GetFullPath(rcloneConfigPath).ToUpperInvariant()
        })));

    private static string OrdinaryPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private static bool LegacyNetworkDevice(DbxConfig config, string device) => config.NetworkMode &&
        device.EndsWith(@"\server\" + RcloneArguments.NetworkVolumeName(config), StringComparison.OrdinalIgnoreCase);

    internal static string? Device(string mount) => ProbeDevice(mount).Device;

    private static (bool Known, string? Device) ProbeDevice(string mount)
    {
        string root = MountPaths.CanonicalMount(mount);
        var buffer = new StringBuilder(32768);
        if (MountPaths.IsDriveRoot(root))
        {
            if (QueryDosDevice(root[..2], buffer, buffer.Capacity) == 0)
                return (Marshal.GetLastWin32Error() == 2 && !Directory.Exists(root), null);
        }
        else
        {
            using var directory = OpenDirectory(MountMetadata.Extended(root), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (directory.IsInvalid) return (Marshal.GetLastWin32Error() is 2 or 3, null);
            uint size = GetFinalPathNameByHandle(directory, buffer, (uint)buffer.Capacity, 0xA); // NT volume, opened name
            if (size == 0 || size >= buffer.Capacity) return default;
        }
        string value = buffer.ToString().TrimEnd('\\');
        // Do not accept a local disk, SUBST, or a Windows network redirector in place of the WinFsp volume.
        return (true, value.StartsWith(@"\Device\Volume{", StringComparison.OrdinalIgnoreCase) ? value : null);
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int capacity);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle directory, StringBuilder path, uint capacity, uint flags);
}
