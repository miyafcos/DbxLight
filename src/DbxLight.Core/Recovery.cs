using System.Text.Json.Serialization;

namespace DbxLight.Core;

[JsonConverter(typeof(JsonStringEnumConverter<StopDisposition>))]
public enum StopDisposition { Unknown, Running, Clean, Forced, UnexpectedExit, StartupFailed, ProcessUnknown, StopUncertain, ClearedByUser }

public sealed record ProcessUncertainty(int Pid, string? Name)
{
    public string Explain() => $"PID {Pid} \u306e\u30d7\u30ed\u30bb\u30b9\uff08" +
        (Name is null ? "\u540d\u524d\u3092\u53d6\u5f97\u3067\u304d\u307e\u305b\u3093" : "\u540d\u524d: " + Name) +
        "\uff09\u306e\u72b6\u614b\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002\n" +
        "\u3053\u306e\u30d7\u30ed\u30bb\u30b9\u304c\u7d42\u4e86\u3059\u308b\u304b Windows \u3092\u518d\u8d77\u52d5\u3057\u305f\u3042\u3068\u3001\u8d77\u52d5\u3057\u76f4\u3057\u3066\u304f\u3060\u3055\u3044\u3002";

    internal static ProcessUncertainty From(Exception error, int fallbackPid)
    {
        if (error is ProcessIdentityUnknownException { Identity: { } identity }) return identity;
        string? name = null;
        try { using var process = System.Diagnostics.Process.GetProcessById(fallbackPid); name = process.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return new(fallbackPid, name);
    }
}

public static class RecoveryDestination
{
    public static bool Resolved(DbxConfig config)
    {
        bool driveRelative = config.Remote.Length >= 2 && char.IsAsciiLetter(config.Remote[0]) && config.Remote[1] == ':' && !Path.IsPathFullyQualified(config.Remote);
        return Path.IsPathFullyQualified(config.CacheDir) &&
            (config.RcloneConfigPath is null || Path.IsPathFullyQualified(config.RcloneConfigPath)) &&
            !driveRelative && (config.Remote.Contains(':') || Path.IsPathFullyQualified(config.Remote));
    }
    public static bool Matches(DbxConfig active, DbxConfig proposed) =>
        active.Remote.Equals(proposed.Remote, StringComparison.Ordinal) &&
        SamePath(active.RcloneConfigPath, proposed.RcloneConfigPath) && SamePath(active.CacheDir, proposed.CacheDir);

    private static bool SamePath(string? a, string? b) => a is null || b is null ? a == b :
        Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string Explain(RuntimeState state) => RecoveryText.Explain(state);

}

public sealed class ProcessIdentityUnknownException(string message, Exception? inner = null, ProcessUncertainty? identity = null)
    : InvalidOperationException(message, inner)
{
    public ProcessUncertainty? Identity { get; } = identity;
}
