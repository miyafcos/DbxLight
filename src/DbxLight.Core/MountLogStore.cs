using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

public sealed record MountLogInfo(bool Captured = false, bool Recording = false, string Reason = "\u30ed\u30b0\u3092\u8a18\u9332\u3057\u3066\u3044\u307e\u305b\u3093\u3002",
    string? Directory = null, long Bytes = 0, DateTimeOffset? StartedUtc = null);

/// <summary>rclone owns log writing and rotation. The application only prepares and reads.</summary>
internal static class MountLogStore
{
    internal static void RequireLocal(string path)
    {
        string full = RescuePaths.Full(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Diagnostic storage must be local.");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
            !(drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || drive.DriveFormat.Equals("ReFS", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Diagnostic storage must support local private files.");
    }
    internal static bool IsLocalPath(string path)
    {
        try { RequireLocal(path); return true; }
        catch { return false; }
    }
    internal static Action? BeforeCreate { get; set; }
    internal static string Folder(string configPath, DbxConfig config) => Path.Combine(config.LogDir, "rclone-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RescuePaths.Full(configPath).ToUpperInvariant())))[..20]);
    internal static string FilePath(MountLogInfo info) => Path.Combine(info.Directory!, "mount.log");
    internal static MountLogInfo Prepare(string configPath, DbxConfig config)
    {
        try
        {
            string folder = Folder(configPath, config), path = Path.Combine(folder, "mount.log");
            RequireLocal(path); RescuePaths.Ancestors(path); PrivateStateFiles.EnsureParent(path);
            using var parent = PrivateStateFiles.LockParent(path);
            BeforeCreate?.Invoke();
            PrivateStateFiles.ValidateExisting(path);
            if (!File.Exists(path))
            {
                using var created = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.ReadWrite | FileShare.Delete,
                    4096, FileOptions.None, PrivateStateFiles.FileSecurity());
            }
            // Check writability without truncating or adding to an existing log.
            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            return new(true, true, "INFO \u306e\u30ed\u30b0\u3092\u8a18\u9332\u3057\u3066\u3044\u307e\u3059\u3002", folder, new FileInfo(path).Length, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Diagnostics must never prevent starting or stopping, including unexpected failures.
            return new(false, false, "\u30ed\u30b0\u3092\u8a18\u9332\u3067\u304d\u3066\u3044\u307e\u305b\u3093\u3002\u6a29\u9650\u307e\u305f\u306f\u4fdd\u5b58\u5148\u306e\u7a7a\u304d\u3092\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        }
    }
    internal static MountLogInfo Read(RuntimeState state)
    {
        var saved = state.LogCapture;
        if (!saved.Captured || saved.Directory is null) return saved;
        try
        {
            string expected = Folder(state.ConfigPath, state.ActiveConfig);
            if (!RescuePaths.Same(saved.Directory, expected)) throw new IOException();
            ValidateFolder(saved.Directory);
            string path = FilePath(saved);
            PrivateStateFiles.ValidateExisting(path);
            bool recording = state.StoppedUtc is null && state.StopDisposition == StopDisposition.Running;
            return saved with { Recording = recording, Bytes = new FileInfo(path).Length,
                Reason = recording ? "INFO \u306e\u30ed\u30b0\u3092\u8a18\u9332\u3057\u3066\u3044\u307e\u3059\u3002" : "\u3053\u306e\u8d77\u52d5\u306e\u30ed\u30b0\u3092\u4fdd\u5b58\u3057\u307e\u3057\u305f\u3002" };
        }
        catch { return saved with { Recording = false, Reason = "\u30ed\u30b0\u306f\u3042\u308a\u307e\u3059\u304c\u8aad\u3081\u307e\u305b\u3093\u3002\u5931\u6557\u306e\u6709\u7121\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002" }; }
    }
    internal static void ValidateFolder(string folder)
    {
        string path = Path.Combine(folder, "mount.log");
        RequireLocal(path); RescuePaths.Ancestors(path);
        using var parent = PrivateStateFiles.LockParent(path);
    }
    internal static string? OpenFolder(MountLogInfo info)
    {
        if (!info.Captured || info.Directory is null) return null;
        try { ValidateFolder(info.Directory); return info.Directory; }
        catch { return null; }
    }
    // Only rclone's timestamped rotation names, never previous application session logs.
    private static readonly Regex Rotated = new(@"^mount-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}\.\d{3}\.log$", RegexOptions.CultureInvariant);
    internal static string? PreviousFile(string folder) => new DirectoryInfo(folder).EnumerateFiles("mount-*.log")
        .Where(f => Rotated.IsMatch(f.Name)).OrderByDescending(f => f.Name, StringComparer.Ordinal).Select(f => f.FullName).FirstOrDefault();
}
