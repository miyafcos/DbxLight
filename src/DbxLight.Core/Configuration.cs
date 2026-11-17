using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

public sealed record TestOverrides
{
    public string? WriteBack { get; init; }
    public string? CachePollInterval { get; init; }
    public string? CacheMaxAgeOverride { get; init; }
}

public sealed record DbxConfig
{
    public string RclonePath { get; init; } = DefaultRclonePath();
    public string? RcloneConfigPath { get; init; }
    public string Remote { get; init; } = "";
    public string MountPoint { get; init; } = "T:";
    public bool NetworkMode { get; init; } = true;
    public string Retention { get; init; } = "1h";
    public string CacheDir { get; init; } = Path.Combine(LocalRoot, "cache");
    private string? rescueDir;
    public string RescueDir
    {
        get => rescueDir ?? Environment.GetEnvironmentVariable("DBXL_RESCUE_DIR") ?? DefaultRescueDirectory(CacheDir);
        init => rescueDir = value;
    }
    internal static string DefaultRescueDirectory(string cacheDir)
    {
        string full = Path.GetFullPath(cacheDir).TrimEnd(Path.DirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(full) ?? Path.GetPathRoot(full)!, Path.GetFileName(full) + "-rescue");
    }
    public string CacheMaxSize { get; init; } = "5G";
    public string CacheMinFreeSpace { get; init; } = "20G";
    public string DirCacheTime { get; init; } = "1h";
    public string PollInterval { get; init; } = "1m";
    public string[] HiddenPaths { get; init; } = [];
    public string[] ExcludePatterns { get; init; } = [];
    public string[] WriteAllowedRemotes { get; init; } = [];
    public int DownloadWindowSeconds { get; init; } = 300;
    public int DownloadFileThreshold { get; init; } = 100;
    public long DownloadByteThreshold { get; init; } = 2000000000;
    public int RcPort { get; init; } = 5601;
    public bool UpdateCheck { get; init; } = true;
    public string LogDir { get; init; } = Path.Combine(LocalRoot, "logs");
    public TestOverrides? TestOverrides { get; init; }

    public static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbxLight");
    public static string DefaultConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbxLight", "config.json");
    public static string DefaultStatePath => Path.Combine(LocalRoot, "state.json");

    private static string DefaultRclonePath()
    {
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages", "Rclone.Rclone_Microsoft.Winget.Source_8wekyb3d8bbwe", "rclone-v1.74.3-windows-amd64", "rclone.EXE");
        return File.Exists(installed) ? installed : "rclone.exe";
    }

    public DbxConfig Validate(bool forMount = false)
    {
        if (!new[] { "30m", "1h", "3h", "1d", "1w" }.Contains(Retention, StringComparer.Ordinal))
            throw new ArgumentException("retention must be 30m, 1h, 3h, 1d or 1w.");
        Require(RclonePath, nameof(RclonePath));
        Require(MountPoint, nameof(MountPoint));
        Require(CacheDir, nameof(CacheDir));
        Require(LogDir, nameof(LogDir));
        Require(RescueDir, nameof(RescueDir));
        if (!Path.IsPathFullyQualified(RescueDir)) throw new ArgumentException("rescueDir must be a fully qualified path.");
        if (RcloneConfigPath is not null) Require(RcloneConfigPath, nameof(RcloneConfigPath));
        if (!Path.IsPathFullyQualified(CacheDir)) throw new ArgumentException("cacheDir must be a fully qualified path so recovery cannot change with the working directory.");
        if (RcloneConfigPath is not null && !Path.IsPathFullyQualified(RcloneConfigPath))
            throw new ArgumentException("rcloneConfigPath must be a fully qualified path so recovery cannot change with the working directory.");
        if (Remote is null || (forMount && string.IsNullOrWhiteSpace(Remote)))
            throw new ArgumentException("remote is required before mounting.");
        if (Remote.Any(char.IsControl) || Remote.StartsWith('-'))
            throw new ArgumentException("remote contains an invalid character.");
        if (DownloadWindowSeconds is < 1 or > 86400 || DownloadFileThreshold is < 1 or > 20000 || DownloadByteThreshold <= 0)
            throw new ArgumentException("Download window=1..86400 seconds; files=1..20000; byte threshold must be positive.");
        if (RcPort is < 1024 or > 65535) throw new ArgumentException("rcPort must be between 1024 and 65535.");
        if (HiddenPaths is null || ExcludePatterns is null || WriteAllowedRemotes is null)
            throw new ArgumentException("Configuration arrays cannot be null.");
        foreach (string pattern in ExcludePatterns) Require(pattern, "excludePatterns entry");
        foreach (string remote in WriteAllowedRemotes) Require(remote, "writeAllowedRemotes entry");
        ValidateSize(CacheMaxSize, nameof(CacheMaxSize));
        ValidateSize(CacheMinFreeSpace, nameof(CacheMinFreeSpace));
        double dir = ParseDuration(DirCacheTime);
        double poll = ParseDuration(PollInterval);
        if (dir <= 0 || (poll != 0 && poll >= dir))
            throw new ArgumentException("dirCacheTime must be positive; pollInterval must be 0 or smaller than dirCacheTime.");
        if (TestOverrides is { } test)
        {
            string? root = Path.GetPathRoot(Remote);
            bool localFolder = Path.IsPathFullyQualified(Remote) && root is { Length: 3 }
                && char.IsAsciiLetter(root[0]) && root[1] == ':';
            if (!localFolder || string.IsNullOrWhiteSpace(RcloneConfigPath))
                throw new ArgumentException("testOverrides require a fully qualified local folder remote and an explicit rcloneConfigPath.");
            if (test.WriteBack is not null) _ = ParseDuration(test.WriteBack);
            if (test.CachePollInterval is not null && ParseDuration(test.CachePollInterval) <= 0)
                throw new ArgumentException("cachePollInterval must be positive.");
            if (test.CacheMaxAgeOverride is not null) _ = ParseDuration(test.CacheMaxAgeOverride);
        }
        bool driveRelativeRemote = Remote.Length >= 2 && char.IsAsciiLetter(Remote[0]) && Remote[1] == ':' && !Path.IsPathFullyQualified(Remote);
        if (forMount && (driveRelativeRemote || (!Remote.Contains(':') && !Path.IsPathFullyQualified(Remote))))
            throw new ArgumentException("remote must be a named remote or a fully qualified local path.");
        string mount = MountPaths.CanonicalMount(MountPoint);
        if (NetworkMode && !MountPaths.IsDriveRoot(mount))
            throw new ArgumentException("networkMode requires a drive letter mountPoint.");
        if (MountPaths.IsSameOrBelow(Path.GetFullPath(CacheDir), mount) ||
            MountPaths.IsSameOrBelow(Path.GetFullPath(LogDir), mount))
            throw new ArgumentException("Cache and logs must be outside the mount.");
        if (MountPaths.IsSameOrBelow(Path.GetFullPath(RescueDir), mount) ||
            MountPaths.IsSameOrBelow(Path.GetFullPath(RescueDir), Path.GetFullPath(CacheDir)))
            throw new ArgumentException("rescueDir must be outside the mount and cache.");
        return this with { HiddenPaths = HiddenFolders.Normalize(HiddenPaths).ToArray() };
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException(name + " must be nonempty and contain no control characters.");
    }

    private static void ValidateSize(string value, string name)
    {
        if (value is null || !Regex.IsMatch(value, @"^\d+(?:\.\d+)?(?:[kKmMgGtTpP](?:i?[bB])?|[bB])?$", RegexOptions.CultureInvariant))
            throw new ArgumentException(name + " must be a nonnegative size such as 5G or 64M.");
    }

    public static double ParseDuration(string value)
    {
        if (value == "0") return 0;
        if (value is null) throw new ArgumentException("Duration cannot be null.");
        var match = Regex.Match(value, @"^(\d+(?:\.\d+)?)(ms|s|m|h|d|w)$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException("Invalid duration: " + value);
        double n = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double factor = match.Groups[2].Value switch { "ms" => .001, "s" => 1, "m" => 60, "h" => 3600, "d" => 86400, "w" => 604800, _ => 0 };
        double result = n * factor;
        if (!double.IsFinite(result) || result > TimeSpan.MaxValue.TotalSeconds) throw new ArgumentException("Duration is too large.");
        return result;
    }
}

public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void Write<T>(string path, T value, bool backup = false)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (backup && File.Exists(full))
            File.Copy(full, full + ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
        string content = JsonSerializer.Serialize(value, Options) + "\n";
        string pending = full + ".pending-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(pending, content, new UTF8Encoding(false));
        File.Move(pending, full, overwrite: true);
    }

    public static T Read<T>(string path) where T : class
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);
        T result = doc.RootElement.Deserialize<T>(Options) ?? throw new InvalidDataException("JSON was null: " + path);
        if (result is RuntimeState state && !doc.RootElement.TryGetProperty("identityVersion", out _))
            return (T)(object)(state with { IdentityVersion = 0 });
        return result;
    }
}

public sealed class ConfigStore
{
    public string PathName { get; }
    private readonly DbxConfig? initialConfig;
    public ConfigStore(string? path = null, DbxConfig? initialConfig = null)
    {
        PathName = Path.GetFullPath(path ?? Environment.GetEnvironmentVariable("DBXL_CONFIG") ?? DbxConfig.DefaultConfigPath);
        this.initialConfig = initialConfig;
    }
    public DbxConfig Load() => ControlGate.Execute(() =>
    {
        CredentialSwitch.Recover(PathName);
        return (File.Exists(PathName) ? JsonFiles.Read<DbxConfig>(PathName) : initialConfig ?? new DbxConfig()).Validate();
    });
    public void Save(DbxConfig config) => ControlGate.Execute(() =>
    {
        config = config.Validate();
        if (File.Exists(PathName)) MountController.RequireRescueDirectoryChange(JsonFiles.Read<DbxConfig>(PathName), config);
        JsonFiles.Write(PathName, config, backup: true);
        return 0;
    });

    public static DbxConfig Set(DbxConfig config, string key, string value)
    {
        JsonObject node = JsonSerializer.SerializeToNode(config, JsonFiles.Options)!.AsObject();
        JsonObject target = node;
        string leaf = key;
        if (key.StartsWith("testOverrides.", StringComparison.Ordinal))
        {
            leaf = key[14..];
            target = node["testOverrides"] as JsonObject ?? new JsonObject { ["writeBack"] = null, ["cachePollInterval"] = null, ["cacheMaxAgeOverride"] = null };
            node["testOverrides"] = target;
        }
        if (!target.ContainsKey(leaf)) throw new ArgumentException("Unknown setting: " + key);
        if (leaf is "hiddenPaths" or "excludePatterns" or "writeAllowedRemotes")
            target[leaf] = JsonNode.Parse(value)?.AsArray() ?? throw new ArgumentException("An array is required.");
        else if (leaf is "downloadWindowSeconds" or "downloadFileThreshold") target[leaf] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        else if (leaf == "downloadByteThreshold") target[leaf] = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        else if (leaf == "rcPort") target[leaf] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        else if (leaf is "networkMode" or "updateCheck") target[leaf] = bool.Parse(value);
        else if (leaf == "testOverrides") throw new ArgumentException("Use testOverrides.writeBack, cachePollInterval or cacheMaxAgeOverride.");
        else target[leaf] = value == "null" && leaf is "rcloneConfigPath" or "writeBack" or "cachePollInterval" or "cacheMaxAgeOverride" ? null : JsonValue.Create(value);
        return node.Deserialize<DbxConfig>(JsonFiles.Options)!.Validate();
    }
}
