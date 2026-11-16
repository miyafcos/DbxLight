using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DbxLight.Core;

internal sealed class CredentialLease : IDisposable
{
    private readonly SafeFileHandle directory;
    private readonly FileStream lease;
    internal CredentialLease(string keyPath)
    {
        RescuePaths.Ancestors(keyPath);
        PrivateStateFiles.EnsureParent(keyPath);
        directory = PrivateStateFiles.LockParent(keyPath);
        try
        {
            string path = keyPath + ".login-lock";
            PrivateStateFiles.ValidateExisting(path);
            lease = new FileInfo(path).Create(FileMode.OpenOrCreate, FileSystemRights.Read | FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.WriteThrough, PrivateStateFiles.FileSecurity());
        }
        catch { directory.Dispose(); throw; }
    }
    internal static CredentialLease? TryAcquire(string path)
    {
        try { return new(path); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }
    public void Dispose() { lease.Dispose(); directory.Dispose(); }
}

/// <summary>Journal contains paths and application settings only, never key material.</summary>
internal static partial class CredentialSwitch
{
    internal static Action<string>? Checkpoint { get; set; }
    private static string ConfigIdentity(string configPath) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RescuePaths.Full(configPath).ToUpperInvariant())))[..20];
    internal static string KeyPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "credentials-" + ConfigIdentity(configPath), "rclone.conf");
    internal static string SharedLegacyPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "credentials", "rclone.conf");
    internal static string LegacyPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "rclone.conf");
    internal static string Folder(string configPath)
    {
        string key = ConfigIdentity(configPath);
        return Path.Combine(Path.GetDirectoryName(configPath)!, ".dbxl-login-" + key);
    }
    private static bool Equal(DbxConfig a, DbxConfig b) => JsonSerializer.Serialize(a, JsonFiles.Options) == JsonSerializer.Serialize(b, JsonFiles.Options);
    internal static PendingUploadsException NeedsLogin() => new("\u8a8d\u8a3c\u60c5\u5831\u3068\u8a2d\u5b9a\u306e\u5207\u308a\u66ff\u3048\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002\u30de\u30a6\u30f3\u30c8\u3092\u958b\u59cb\u305b\u305a\u3001\u30ed\u30b0\u30a4\u30f3\u3057\u76f4\u3057\u3066\u304f\u3060\u3055\u3044\u3002");

    internal static void RefuseMount(string configPath)
    {
        if (File.Exists(TransactionPath(configPath)) || File.Exists(ReloginPath(configPath))) throw NeedsLogin();
    }
}
