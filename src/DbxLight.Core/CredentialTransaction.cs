using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
internal sealed record KeyRegistry
{
    public int Version { get; init; } = 2;
    public string ConfigPath { get; init; } = "";
    public string[] Keys { get; init; } = [];
    public string[] RegisteredPaths { get; init; } = [];
}
internal sealed record KeyTransaction
{
    public int Version { get; init; } = 1;
    public string ConfigPath { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public string WorkingPath { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public string Stage { get; init; } = "Authorizing";
    public bool HadOriginal { get; init; }
    public bool HadConfig { get; init; }
    public DbxConfig Before { get; init; } = new();
    public DbxConfig? After { get; init; }
}

internal static partial class CredentialSwitch
{
    internal static string TransactionPath(string configPath) => Path.Combine(Folder(configPath), "switch.json");
    internal static string RegistryPath(string configPath) => Path.Combine(Folder(configPath), "files.json");
    internal static string ReloginPath(string configPath) => Path.Combine(Folder(configPath), "relogin-required.json");
    private static bool StageKnown(string stage) => stage is "Authorizing" or "Prepared" or "KeyInstalled" or "ConfigInstalled" or "RollingBack" or "RolledBack";

    internal static void Recover(string configPath)
    {
        SnapshotRenewalFile.RecoverAbandoned();
        RecoverLogin(configPath);
    }

    private static KeyRegistry Registry(string configPath)
    {
        string path = RegistryPath(configPath);
        RescuePaths.Ancestors(path);
        var value = PrivateJsonFile.Read<KeyRegistry>(path) ?? new KeyRegistry { ConfigPath = configPath };
        if (value.Version is not (1 or 2) || !RescuePaths.Same(value.ConfigPath, configPath) || value.Keys is null) throw NeedsLogin();
        // Version 1 discovered temporary paths by name after creation: that is not ownership evidence.
        if (value.Version == 1) value = value with { Version = 2, RegisteredPaths = [] };
        foreach (string key in value.Keys) ValidateKey(configPath, key);
        if (value.RegisteredPaths is null) throw NeedsLogin();
        // Old placements are external files. Legacy registry entries cannot grant
        // mutation authority over either their key or their temporary files.
        value = value with { RegisteredPaths = value.RegisteredPaths.Where(path =>
            path.StartsWith(KeyPath(configPath) + ".", StringComparison.OrdinalIgnoreCase)).ToArray() };
        foreach (string pending in value.RegisteredPaths)
        {
            string full = RescuePaths.Full(pending);
            bool valid = new[] { KeyPath(configPath) }.Any(key =>
                Regex.IsMatch(full, "^" + Regex.Escape(key) + @"\.(?:work|pending)-[0-9a-f]{32}$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (!valid) throw NeedsLogin();
        }
        return value;
    }
    internal static void ValidateKey(string configPath, string path)
    {
        RescuePaths.Ancestors(path);
        if (!Path.GetFileName(RescuePaths.Full(path)).Equals("rclone.conf", StringComparison.OrdinalIgnoreCase) ||
            RescuePaths.Same(Path.GetDirectoryName(path)!, Path.GetDirectoryName(configPath)!)) throw NeedsLogin();
    }
    internal static void Register(string configPath, string keyPath)
    {
        ValidateKey(configPath, keyPath);
        if (!RescuePaths.Same(keyPath, KeyPath(configPath))) throw NeedsLogin();
        var registry = Registry(configPath);
        PrivateJsonFile.Write(RegistryPath(configPath), registry with { Keys = registry.Keys.Append(keyPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
    }
    private static void ValidateTransaction(string configPath, KeyTransaction tx)
    {
        if (tx.Version != 1 || !RescuePaths.Same(tx.ConfigPath, configPath) || tx.Before is null) throw NeedsLogin();
        ValidateKey(configPath, tx.KeyPath);
        if (!RescuePaths.Same(tx.KeyPath, KeyPath(configPath))) throw NeedsLogin();
        if (!Registry(configPath).Keys.Any(k => RescuePaths.Same(k, tx.KeyPath))) throw NeedsLogin();
        foreach (var part in new[] { (Path: tx.WorkingPath, Kind: ".work-"), (Path: tx.BackupPath, Kind: ".pending-") })
        {
            string prefix = tx.KeyPath + part.Kind;
            if (!part.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(part.Path[prefix.Length..], "N", out _)) throw NeedsLogin();
            RescuePaths.Ancestors(part.Path);
        }
        if (!StageKnown(tx.Stage)) throw NeedsLogin();
        if (tx.Stage != "Authorizing" && (tx.After is null || tx.After.WriteAllowedRemotes.Length != 0 ||
            tx.After.RcloneConfigPath is not null && !RescuePaths.Same(tx.After.RcloneConfigPath, tx.KeyPath)))
            throw NeedsLogin();
    }
    private static void RegisterTemporaryPaths(string configPath, IEnumerable<string> paths, bool validatedLegacyTransaction = false)
    {
        var registry = Registry(configPath);
        var registered = registry.RegisteredPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string full = RescuePaths.Full(path);
            if (registered.Contains(full)) continue;
            if (File.Exists(full) && !validatedLegacyTransaction) throw NeedsLogin();
            registered.Add(full);
        }
        PrivateJsonFile.Write(RegistryPath(configPath), registry with { RegisteredPaths = registered.ToArray() });
    }

    internal static void WriteTransaction(string configPath, KeyTransaction tx)
    {
        RegisterTemporaryPaths(configPath, [tx.WorkingPath, tx.BackupPath]); // Durable before either file can be created.
        if (tx.Stage == "Authorizing") Checkpoint?.Invoke("temporary-registered");
        PrivateJsonFile.Write(TransactionPath(configPath), tx);
    }
    internal static bool LeaseActive(string keyPath)
    {
        string path = keyPath + ".login-lock";
        RescuePaths.Ancestors(path);
        if (!File.Exists(path)) return false;
        PrivateStateFiles.ValidateExisting(path);
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return false; }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return true; }
    }
    internal static void BeginAllowed(string configPath)
    {
        foreach (string key in Registry(configPath).Keys)
            if (LeaseActive(key)) throw new InvalidOperationException("\u30ed\u30b0\u30a4\u30f3\u753b\u9762\u3092\u9589\u3058\u3066\u304b\u3089\u3084\u308a\u76f4\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        if (File.Exists(TransactionPath(configPath)) && !File.Exists(ReloginPath(configPath))) throw NeedsLogin();
    }

    // Only exact pre-registered paths are eligible. Contents are never read, hashed or parsed.
    internal static void Scrub(string configPath, string path, bool installedRollback = false)
    {
        RescuePaths.Ancestors(path);
        if (RescuePaths.Same(path, LegacyPath(configPath)) || RescuePaths.Same(path, SharedLegacyPath(configPath))) throw NeedsLogin();
        var registry = Registry(configPath);
        bool temporary = registry.RegisteredPaths.Any(p => RescuePaths.Same(p, path));
        if (!temporary && !(installedRollback && registry.Keys.Any(k => RescuePaths.Same(k, path)))) throw NeedsLogin();
        if (!File.Exists(path)) { Unregister(); return; }
        using var parent = PrivateStateFiles.LockExistingParent(path);
        PrivateStateFiles.ValidateExisting(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        { stream.SetLength(0); stream.Flush(true); }
        File.Delete(path);
        Unregister();
        void Unregister()
        {
            if (temporary) PrivateJsonFile.Write(RegistryPath(configPath), Registry(configPath) with
                { RegisteredPaths = Registry(configPath).RegisteredPaths.Where(p => !RescuePaths.Same(p, path)).ToArray() });
        }
    }
    private static string[] TemporaryFiles(string key)
    {
        string directory = Path.GetDirectoryName(key)!;
        RescuePaths.Ancestors(directory);
        if (!Directory.Exists(directory)) return [];
        string pattern = "^" + Regex.Escape(Path.GetFileName(key)) + @"\.(?:work|pending)-[0-9a-f]{32}$";
        return Directory.EnumerateFiles(directory, Path.GetFileName(key) + ".*")
            .Where(p => Regex.IsMatch(Path.GetFileName(p), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
    }
    private static IEnumerable<string> KnownKeys(string configPath) => Registry(configPath).Keys.Append(KeyPath(configPath))
        .Append(LegacyPath(configPath)).Append(SharedLegacyPath(configPath)).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> UnregisteredFiles(string configPath)
    {
        var registered = Registry(configPath).RegisteredPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Enumeration is used only to report names. No contents, ACL changes or deletion.
        return KnownKeys(configPath).SelectMany(TemporaryFiles).Where(p => !registered.Contains(p))
            .Concat(new[] { LegacyPath(configPath), SharedLegacyPath(configPath) }.Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void CleanupKnown(string configPath, bool uncertainOrphans)
    {
        var registry = Registry(configPath);
        foreach (string file in registry.RegisteredPaths)
        {
            string key = KnownKeys(configPath).First(k => file.StartsWith(k + ".", StringComparison.OrdinalIgnoreCase));
            if (LeaseActive(key)) continue;
            if (uncertainOrphans && File.Exists(file)) RequireRelogin(configPath);
            Scrub(configPath, file);
        }
    }

    internal static void RequireRelogin(string configPath)
    {
        PrivateJsonFile.Write(ReloginPath(configPath), new { version = 1, reason = "credential_switch_unknown" });
        if (File.Exists(configPath))
        {
            DbxConfig current = JsonFiles.Read<DbxConfig>(configPath).Validate();
            if (current.WriteAllowedRemotes.Length > 0) JsonFiles.Write(configPath, current with { WriteAllowedRemotes = [] }, backup: true);
        }
    }
    internal static void ClearRelogin(string configPath)
    {
        string path = ReloginPath(configPath);
        RescuePaths.Ancestors(path);
        if (File.Exists(path)) { PrivateStateFiles.ValidateExisting(path); File.Delete(path); }
    }
    private static void RecoverLogin(string configPath)
    {
        string path = TransactionPath(configPath);
        RescuePaths.Ancestors(path);
        if (!File.Exists(path)) { CleanupKnown(configPath, uncertainOrphans: true); return; }
        KeyTransaction tx;
        try
        {
            tx = PrivateJsonFile.Read<KeyTransaction>(path) ?? throw NeedsLogin();
            ValidateTransaction(configPath, tx);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            RequireRelogin(configPath);
            CleanupKnown(configPath, uncertainOrphans: false);
            return; // Preserve the unknown marker; only a new authorized login/logout may replace it.
        }
        using CredentialLease? lease = CredentialLease.TryAcquire(tx.KeyPath);
        if (lease is null) return;
        RecoverHeld(configPath, tx);
        CleanupKnown(configPath, uncertainOrphans: true);
    }

    internal static void RecoverHeld(string configPath, KeyTransaction tx)
    {
        ValidateTransaction(configPath, tx);
        // The legacy transaction itself records these exact paths before creation.
        RegisterTemporaryPaths(configPath, [tx.WorkingPath, tx.BackupPath], validatedLegacyTransaction: true);
        void Write(KeyTransaction value) { tx = value; WriteTransaction(configPath, value); }
        if (tx.Stage == "Authorizing")
        {
            Scrub(configPath, tx.WorkingPath); Scrub(configPath, tx.BackupPath);
            File.Delete(TransactionPath(configPath));
            return; // The production key and application configuration were never changed.
        }
        DbxConfig current = File.Exists(configPath) ? JsonFiles.Read<DbxConfig>(configPath).Validate() : !tx.HadConfig ? tx.Before : throw NeedsLogin();
        if (!Equal(current, tx.Before) && !Equal(current, tx.After!))
        { RequireRelogin(configPath); Scrub(configPath, tx.WorkingPath); Scrub(configPath, tx.BackupPath); return; }
        if (tx.Stage == "ConfigInstalled")
        {
            if (!File.Exists(tx.KeyPath)) { RequireRelogin(configPath); return; }
            PrivateStateFiles.ValidateExisting(tx.KeyPath);
            JsonFiles.Write(configPath, tx.After!, backup: true);
            Scrub(configPath, tx.WorkingPath); Scrub(configPath, tx.BackupPath);
            File.Delete(TransactionPath(configPath)); ClearRelogin(configPath);
            return;
        }
        if (tx.Stage != "RolledBack")
        {
            Write(tx with { Stage = "RollingBack" });
            if (tx.HadOriginal)
            {
                if (!File.Exists(tx.BackupPath)) { RequireRelogin(configPath); return; }
                PrivateStateFiles.ValidateExisting(tx.BackupPath);
                PrivateLoginFile.Empty(tx.WorkingPath);
                File.Copy(tx.BackupPath, tx.WorkingPath, overwrite: true);
                FlushOpaque(tx.WorkingPath);
                File.Move(tx.WorkingPath, tx.KeyPath, overwrite: true);
            }
            else Scrub(configPath, tx.KeyPath, installedRollback: true);
            if (tx.HadConfig || File.Exists(configPath)) JsonFiles.Write(configPath, tx.Before, backup: true);
            Write(tx with { Stage = "RolledBack" });
        }
        Scrub(configPath, tx.WorkingPath); Scrub(configPath, tx.BackupPath);
        File.Delete(TransactionPath(configPath));
    }

    internal static void FlushOpaque(string path)
    {
        PrivateStateFiles.ValidateExisting(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        file.Flush(true);
    }
}
