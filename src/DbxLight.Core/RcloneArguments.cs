using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DbxLight.Core;

public sealed record RcCredentials(string User, string Password)
{
    public static RcCredentials Create() => new(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    public override string ToString() => "RcCredentials [redacted]";
}

public static class RcloneArguments
{
    public static IReadOnlyList<string> Mount(DbxConfig config, string filterFile, string? logFile = null)
    {
        config = config.Validate(forMount: true);
        var args = new List<string> { "mount", config.Remote, config.MountPoint, "--ask-password=false", "--ftp-ask-password=false", "--sftp-ask-password=false" };
        // This is the only write-permission gate. Exact, ordinal matching is deliberate.
        if (!config.WriteAllowedRemotes.Contains(config.Remote, StringComparer.Ordinal)) args.Add("--read-only");
        Add(args, "--vfs-cache-mode", "full");
        Add(args, "--vfs-cache-max-age", config.TestOverrides?.CacheMaxAgeOverride ?? config.Retention);
        Add(args, "--vfs-cache-max-size", config.CacheMaxSize);
        Add(args, "--vfs-cache-min-free-space", config.CacheMinFreeSpace);
        Add(args, "--cache-dir", Path.GetFullPath(config.CacheDir));
        Add(args, "--dir-cache-time", config.DirCacheTime);
        Add(args, "--poll-interval", config.PollInterval);
        args.AddRange(RcloneFilters.Arguments(config, filterFile));
        if (config.NetworkMode)
        {
            args.Add("--network-mode");
            Add(args, "--volname", NetworkVolumeName(config));
        }
        args.Add("--rc");
        Add(args, "--rc-addr", "127.0.0.1:" + config.RcPort);
        if (logFile is not null)
        {
            Add(args, "--log-file", logFile);
            Add(args, "--log-level", "INFO");
            Add(args, "--log-file-max-size", "10M");
            Add(args, "--log-file-max-backups", "4");
            Add(args, "--log-file-max-age", "30d");
        }
        if (config.RcloneConfigPath is not null) Add(args, "--config", Path.GetFullPath(config.RcloneConfigPath));
        if (config.TestOverrides?.WriteBack is { } writeBack) Add(args, "--vfs-write-back", writeBack);
        if (config.TestOverrides?.CachePollInterval is { } poll) Add(args, "--vfs-cache-poll-interval", poll);
        return args;
    }

    internal static string NetworkVolumeName(DbxConfig config)
    {
        string identity = Path.GetFullPath(config.CacheDir) + "|" + MountPaths.CanonicalMount(config.MountPoint);
        return "DbxLight-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
    }

    private static void Add(List<string> args, string key, string value) { args.Add(key); args.Add(value); }

    public static ProcessStartInfo ProcessInfo(string executable, IEnumerable<string> arguments, RcCredentials? credentials = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        // Keep inherited rclone options from changing config, filtering, authentication or write permission.
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        if (credentials is not null)
        {
            info.Environment["RCLONE_RC_USER"] = credentials.User;
            info.Environment["RCLONE_RC_PASS"] = credentials.Password;
        }
        return info;
    }
}
