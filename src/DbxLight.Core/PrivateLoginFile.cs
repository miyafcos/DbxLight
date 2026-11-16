using System.Security.AccessControl;

namespace DbxLight.Core;

/// <summary>Authorization changes only a registered working copy until settings and key commit together.</summary>
internal sealed class PrivateLoginFile : IDisposable
{
    private readonly ConfigStore config;
    private readonly CredentialLease lease;
    private KeyTransaction transaction;
    private bool committed, disposed;
    internal string WorkingPath => transaction.WorkingPath;

    internal PrivateLoginFile(ConfigStore config, string keyPath, Action<string>? created = null)
    {
        this.config = config;
        DbxConfig before = config.Load();
        CredentialSwitch.BeginAllowed(config.PathName);
        lease = new CredentialLease(keyPath);
        transaction = new KeyTransaction { ConfigPath = config.PathName, KeyPath = keyPath,
            WorkingPath = keyPath + ".work-" + Guid.NewGuid().ToString("N"),
            BackupPath = keyPath + ".pending-" + Guid.NewGuid().ToString("N"),
            HadOriginal = File.Exists(keyPath), HadConfig = File.Exists(config.PathName), Before = before };
        try
        {
            PrivateStateFiles.ValidateExisting(keyPath);
            CredentialSwitch.Register(config.PathName, keyPath);
            CredentialSwitch.WriteTransaction(config.PathName, transaction); // Location precedes any key copy.
            Empty(WorkingPath, created, requireNew: true);
            if (transaction.HadOriginal) File.Copy(keyPath, WorkingPath, overwrite: true);
            CredentialSwitch.FlushOpaque(WorkingPath);
        }
        catch
        {
            try { CredentialSwitch.RecoverHeld(config.PathName, transaction); }
            finally { lease.Dispose(); }
            throw;
        }
    }

    internal static void Empty(string path, Action<string>? created = null, bool requireNew = false)
    {
        RescuePaths.Ancestors(path);
        PrivateStateFiles.EnsureParent(path);
        using var parent = PrivateStateFiles.LockParent(path);
        PrivateStateFiles.ValidateExisting(path);
        using FileStream stream = new FileInfo(path).Create(requireNew ? FileMode.CreateNew : FileMode.Create, FileSystemRights.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough, PrivateStateFiles.FileSecurity());
        created?.Invoke(path);
        stream.Flush(true);
    }

    internal static void RefuseActiveChange(string? configPath)
    {
        if (configPath is not null && CredentialSwitch.LeaseActive(configPath))
            throw new InvalidOperationException("\u30ed\u30b0\u30a4\u30f3\u3068\u63a5\u7d9a\u5148\u306e\u753b\u9762\u3092\u9589\u3058\u3066\u304b\u3089\u3001\u30de\u30a6\u30f3\u30c8\u3092\u958b\u59cb\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
    }

    internal DbxConfig Commit(MountController controller, DbxConfig expected, DbxConfig after) => ControlGate.Execute(() =>
    {
        if (disposed || committed || controller.Config.PathName != config.PathName) throw new InvalidOperationException("Credential change is not available.");
        controller.PrepareConnectionChange(expected);
        after = (after with { WriteAllowedRemotes = [] }).Validate();
        if (after.RcloneConfigPath is not null && !RescuePaths.Same(after.RcloneConfigPath, transaction.KeyPath)) throw CredentialSwitch.NeedsLogin();
        PrivateStateFiles.ValidateExisting(WorkingPath);
        if (transaction.HadOriginal)
        {
            Empty(transaction.BackupPath, requireNew: true);
            File.Copy(transaction.KeyPath, transaction.BackupPath, overwrite: true);
            CredentialSwitch.FlushOpaque(transaction.BackupPath);
        }
        transaction = transaction with { Stage = "Prepared", Before = expected, After = after };
        CredentialSwitch.WriteTransaction(config.PathName, transaction);
        CredentialSwitch.Checkpoint?.Invoke("switch-marker");
        File.Move(WorkingPath, transaction.KeyPath, overwrite: true);
        CredentialSwitch.Checkpoint?.Invoke("switch-conf");
        transaction = transaction with { Stage = "KeyInstalled" };
        CredentialSwitch.WriteTransaction(config.PathName, transaction);
        CredentialSwitch.Checkpoint?.Invoke("switch-before-settings");
        DbxConfig saved = controller.ApplyConnectionChange(expected, _ => after);
        CredentialSwitch.Checkpoint?.Invoke("switch-settings");
        transaction = transaction with { Stage = "ConfigInstalled" };
        CredentialSwitch.WriteTransaction(config.PathName, transaction);
        CredentialSwitch.Checkpoint?.Invoke("switch-installed");
        CredentialSwitch.Scrub(config.PathName, transaction.BackupPath);
        CredentialSwitch.Scrub(config.PathName, WorkingPath);
        // Only paths registered before their creation are eligible for cleanup.
        CredentialSwitch.CleanupKnown(config.PathName, uncertainOrphans: false);
        File.Delete(CredentialSwitch.TransactionPath(config.PathName));
        CredentialSwitch.ClearRelogin(config.PathName);
        committed = true;
        return saved;
    });

    public void Dispose()
    {
        if (disposed) return;
        try
        {
            if (!committed)
            {
                var durable = PrivateJsonFile.Read<KeyTransaction>(CredentialSwitch.TransactionPath(config.PathName));
                if (durable is not null) CredentialSwitch.RecoverHeld(config.PathName, durable);
            }
            disposed = true;
        }
        finally { lease.Dispose(); }
    }
}
