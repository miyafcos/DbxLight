namespace DbxLight.Core;

// rclone can stage its replacement next to the config. Keep both under the
// private application root and retain the exclusive lease until the helper exits.
internal sealed class SnapshotRenewalFile : IDisposable
{
    private static string Root => Path.Combine(DbxConfig.LocalRoot, "renewal");
    private readonly CredentialLease lease;
    internal string ConfigPath { get; }

    internal SnapshotRenewalFile()
    {
        ConfigPath = Path.Combine(Root, Guid.NewGuid().ToString("N"), "config");
        string guard = Path.Combine(Root, ".guard");
        RescuePaths.Ancestors(guard);
        PrivateStateFiles.EnsureParent(guard);
        using var root = PrivateStateFiles.LockParent(guard);
        lease = new CredentialLease(ConfigPath);
    }

    public void Dispose()
    {
        try { Scrub(ConfigPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        finally { lease.Dispose(); }
        RemoveEmptyDirectory(ConfigPath);
    }

    internal static void RecoverAbandoned()
    {
        string guard = Path.Combine(Root, ".guard");
        if (!Directory.Exists(Root)) return;
        try
        {
            RescuePaths.Ancestors(guard);
            using var root = PrivateStateFiles.LockParent(guard);
            foreach (string directory in Directory.EnumerateDirectories(Root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                string path = Path.Combine(directory, "config");
                try
                {
                    if (!File.Exists(path + ".login-lock")) continue;
                    using (var owned = CredentialLease.TryAcquire(path))
                    {
                        if (owned is null) continue;
                        Scrub(path);
                    }
                    RemoveEmptyDirectory(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Scrub(string path)
    {
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!))
        {
            if (file.Equals(path + ".login-lock", StringComparison.OrdinalIgnoreCase)) continue;
            PrivateStateFiles.ValidateExisting(file);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(0);
                stream.Flush(true);
            }
            File.Delete(file);
        }
    }

    private static void RemoveEmptyDirectory(string path)
    {
        try
        {
            // A second cleanup may have taken the lease after it was released.
            File.Delete(path + ".login-lock");
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
