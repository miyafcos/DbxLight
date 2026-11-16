using System.Security.AccessControl;
using System.Text.Json.Serialization;

namespace DbxLight.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record RescueRecord
{
    [JsonIgnore] internal string? ReadPath { get; init; }
    [JsonIgnore] internal DbxConfig? TrustedConfig { get; init; }
    [JsonIgnore] internal string? ValidationProblem { get; init; }
    [JsonIgnore] internal bool IsForeign { get; init; }
    [JsonIgnore] internal int ForeignCount { get; init; }
    public string? OwnerId { get; init; }
    public int ForeignRecordCount => ForeignCount;
    public string RecordStatus => IsForeign ? "foreign" : TrustedConfig is null ? "unknown" : "verified";
    public string? RecordProblem => ValidationProblem;
    public string Id { get; init; } = "";
    public string RecordPath { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public string OriginalMetadataPath { get; init; } = "";
    public string DestinationPath { get; init; } = "";
    public string MetadataDestinationPath { get; init; } = "";
    public string? CloudPath { get; init; }
    public string CacheRelativePath { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset RescuedUtc { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? ModifiedUtc { get; init; }
    public bool HadData { get; init; }
    public bool HadMetadata { get; init; }
    public bool CompleteData { get; init; }
    public bool MoveCompleted { get; init; }
    public bool Conflict { get; init; }
    public DbxConfig Origin { get; init; } = new();
    public bool NotificationShown { get; init; }
    public bool Acknowledged { get; init; }
    public string NextAction => RecordStatus == "unknown" ? "記録の場所と退避したファイル本体を確認してください。" :
        IsForeign ? "元の設定で退避先を確認してください。" :
        !CompleteData ? "保存先のファイル本体と管理情報から内容を確認・復元し、ドライブの元の場所へ自分で戻してください。" :
        Conflict ? "保存先とドライブの元の場所を開いて見比べ、必要な内容を自分で戻してください。" :
        "保存先からドライブの元の場所へ自分で戻してください。";
}

public sealed record RescueNotice(int Unacknowledged = 0, int UnknownRecords = 0, int ForeignRecords = 0)
{
    public string Hint => "退避したファイルはアップロードされていません。dbxl rescue list --json で理由と保存先を確認してください。";
}

internal sealed record AcknowledgedRescues(string[] Paths, Dictionary<string, string> Stamps);
internal static class RescueStore
{
    internal static Action<string>? RecordReadCheckpoint { get; set; }
    private static string AcknowledgedPath(DbxConfig config) => Path.Combine(config.RescueDir, ".dbxl-records", "acknowledged.index");
    private static HashSet<string> Acknowledged(DbxConfig config)
    {
        try
        {
            RescuePaths.Ancestors(AcknowledgedPath(config));
            var index = PrivateJsonFile.Read<AcknowledgedRescues>(AcknowledgedPath(config));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in index?.Paths ?? [])
            {
                if (!RescuePaths.Same(Path.GetDirectoryName(path)!, Path.Combine(config.RescueDir, ".dbxl-records")) ||
                    !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) throw new IOException();
                var file = new FileInfo(path);
                string stamp = file.Exists ? file.Length + "|" + file.LastWriteTimeUtc.Ticks : "missing";
                if (index!.Stamps is not null && index.Stamps.TryGetValue(path, out string? saved) && saved == stamp) paths.Add(path);
            }
            return paths;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return new(StringComparer.OrdinalIgnoreCase); }
    }
    internal static IReadOnlyList<RescueRecord> Pending(DbxConfig config, IEnumerable<string>? remembered = null) =>
        List(config, remembered, includeAcknowledged: false);
    internal static IReadOnlyList<RescueRecord> Pending(RuntimeState state) => Pending(state.ActiveConfig, state.RescueRecords);
    internal static RescueNotice Notice(IReadOnlyList<RescueRecord> records) => new(
        records.Count(r => !r.IsForeign && r.RecordStatus == "verified" && !r.Acknowledged),
        records.Count(r => r.RecordStatus == "unknown"), records.Sum(r => r.IsForeign ? Math.Max(1, r.ForeignCount) : 0));
    internal static string Owner(DbxConfig config)
    {
        if (config.Remote is null || config.Remote.Any(char.IsControl)) throw new IOException(RescuePaths.Unknown);
        string identity = RescuePaths.Full(config.CacheDir).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() + "\n" +
            config.Remote + "\n" + (config.RcloneConfigPath is null ? "<default>" : RescuePaths.Full(config.RcloneConfigPath).ToUpperInvariant());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    }

    internal static IReadOnlyList<RescueRecord> Summarize(IReadOnlyList<RescueRecord> records)
    {
        var own = records.Where(r => !r.IsForeign).ToList();
        int count = records.Count(r => r.IsForeign);
        if (count > 0) own.Add(new RescueRecord { IsForeign = true, ForeignCount = count, Origin = null!,
            Reason = "\u307b\u304b\u306e\u8a2d\u5b9a\u306e\u30d5\u30a1\u30a4\u30eb" });
        return own;
    }

    internal static IReadOnlyList<RescueRecord> List(DbxConfig config, IEnumerable<string>? remembered = null, bool includeAcknowledged = true)
    {
        var paths = new HashSet<string>(remembered ?? [], StringComparer.OrdinalIgnoreCase);
        string records = Path.Combine(config.RescueDir, ".dbxl-records");
        try
        {
            RescuePaths.Ancestors(config.RescueDir);
            _ = File.GetAttributes(config.RescueDir); PendingCacheInspector.RefuseReparse(config.RescueDir);
            try
            {
                _ = File.GetAttributes(records); PendingCacheInspector.RefuseReparse(records);
                foreach (string path in Directory.EnumerateFiles(records, "*.json")) paths.Add(path);
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return [Unknown(records)];
        }
        if (!includeAcknowledged) paths.ExceptWith(Acknowledged(config));
        return paths.Select(p => Read(p, config)).Where(r => includeAcknowledged || !r.Acknowledged).OrderByDescending(r => r.RescuedUtc).ToArray();
    }

    internal static RescueRecord Read(string path, DbxConfig config)
    {
        RescueRecord? record = null;
        try
        {
            string actual = RescuePaths.Full(path);
            if (!RescuePaths.Same(Path.GetDirectoryName(actual)!, Path.Combine(config.RescueDir, ".dbxl-records")))
                throw new IOException(RescuePaths.Unknown);
            RescuePaths.Ancestors(actual);
            RecordReadCheckpoint?.Invoke(actual);
            record = PrivateJsonFile.Read<RescueRecord>(actual) ?? throw new IOException(RescuePaths.Unknown);
            if (record.Origin is null || !RescuePaths.Same(record.Origin.RescueDir, config.RescueDir))
                throw new IOException(RescuePaths.Unknown);
            string owner = Owner(record.Origin);
            if (record.OwnerId is not null && record.OwnerId != owner) throw new IOException(RescuePaths.Unknown);
            RescuePaths.Require(record, actual, record.Origin, checkPresence: false);
            if (owner != Owner(config))
                return record with { ReadPath = actual, RecordPath = actual, OwnerId = owner, IsForeign = true,
                    ForeignCount = 1, TrustedConfig = null, ValidationProblem = null };
            RescuePaths.Require(record, actual, config);
            return record with { ReadPath = actual, RecordPath = actual, OwnerId = owner, IsForeign = false,
                TrustedConfig = config, ValidationProblem = null };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Unknown(path, e is RescuePresenceException ? e.Message : null);
        }
    }

    private static RescueRecord Unknown(string path, string? problem = null) => new() { RecordPath = path, Origin = null!,
        ValidationProblem = problem ?? RescuePaths.Unknown, Reason = problem ?? RescuePaths.Unknown };

    internal static void RequireVerified(RescueRecord record)
    {
        if (record.ReadPath is null || record.TrustedConfig is null) throw new PendingUploadsException(RescuePaths.Unknown);
        try { RescuePaths.Require(record, record.ReadPath, record.TrustedConfig); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new PendingUploadsException(RescuePaths.Unknown); }
    }

    internal static string? OpenFolder(RescueRecord record, DbxConfig config)
    {
        try
        {
            if (record.ReadPath is null) return null;
            var current = Read(record.ReadPath, config);
            RequireVerified(current);
            string folder = Path.GetDirectoryName(current.DestinationPath)!;
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal static IReadOnlyList<RescueRecord> List(RuntimeState state) => List(state.ActiveConfig, state.RescueRecords);
    internal static bool HasUnacknowledged(DbxConfig config) => Pending(config).Any();
    internal static Action<string, RescueRecord>? MoveCheckpoint { get; set; }
    internal static Action? CompletionSaveCheckpoint { get; set; }

    internal static RescueRecord Rescue(DbxConfig config, CacheEntry entry, string? cloudPath, string reason, bool conflict = false)
    {
        string id = Guid.NewGuid().ToString("N");
        string relative = SafeRelative(cloudPath is null ? "unmapped/" + entry.Relative.Replace('\\', '/') : RecoveryCacheNames.EncodeForDisk(cloudPath));
        string folder = Path.Combine(config.RescueDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + id[..6]);
        string records = Path.Combine(config.RescueDir, ".dbxl-records");
        PrivateStateFiles.EnsureParent(Path.Combine(config.RescueDir, "private-directory-check"));
        var record = new RescueRecord
        {
            Id = id, RecordPath = Path.Combine(records, id + ".json"), OriginalPath = entry.DataPath,
            OriginalMetadataPath = entry.MetadataPath, CacheRelativePath = entry.Relative, CloudPath = cloudPath,
            DestinationPath = Path.Combine(folder, relative.Replace('/', '\\')), MetadataDestinationPath = Path.Combine(records, id + ".metadata"),
            Reason = reason, RescuedUtc = DateTimeOffset.UtcNow, Size = entry.HasData ? new FileInfo(entry.DataPath).Length : 0,
            ModifiedUtc = entry.ModTime, HadData = entry.HasData, HadMetadata = entry.HasMetadata,
            CompleteData = entry.CompleteData, Conflict = conflict, Origin = config, OwnerId = Owner(config)
        };
        record = record with { ReadPath = record.RecordPath, TrustedConfig = config };
        Save(record); // Plan is durable before either move; restart resumes an interrupted pair.
        return FinishMove(record);
    }

    internal static RescueRecord FinishMove(RescueRecord record)
    {
        RequireVerified(record);
        void Move(bool present, string source, string target)
        {
            if (!present) return;
            PrivateStateFiles.EnsureParent(target);
            if (File.Exists(target))
            {
                PrivateStateFiles.ValidateExisting(target);
                if (File.Exists(source)) throw new IOException("Both rescue source and target exist; retain both: " + source);
                return;
            }
            PendingCacheInspector.RefuseReparse(source);
            RestrictBody(source);
            File.Move(source, target); // Never overwrite or delete a body.
            RestrictBody(target);
            PrivateStateFiles.ValidateExisting(target);
        }
        Move(record.HadData, record.OriginalPath, record.DestinationPath);
        MoveCheckpoint?.Invoke("after-data-move", record);
        Move(record.HadMetadata, record.OriginalMetadataPath, record.MetadataDestinationPath);
        MoveCheckpoint?.Invoke("after-metadata-move", record);
        record = record with { MoveCompleted = true };
        Save(record);
        return record;
    }

    // Called only after stopped-cache ownership has been established.
    internal static RescueRecord ReconcileMoved(RescueRecord record)
    {
        RequireVerified(record);
        if (record.MoveCompleted || File.Exists(record.OriginalPath) || File.Exists(record.OriginalMetadataPath)) return record;
        if (record.HadData && !File.Exists(record.DestinationPath) || record.HadMetadata && !File.Exists(record.MetadataDestinationPath)) return record;
        var completed = record with { MoveCompleted = true };
        Save(completed);
        return completed;
    }

    private static void RestrictBody(string path)
    {
        // Do not request WRITE_OWNER on an existing rclone file: the owner can set its
        // DACL without that privilege. A same-owner, protected DACL is sufficient.
        var file = new FileInfo(path);
        if (!PrivateStateFiles.CurrentSid.Equals(file.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(System.Security.Principal.SecurityIdentifier))))
            throw StartupFailure.PrivateStorage(path, "退避元が本人の所有ではないため動かせません。");
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(PrivateStateFiles.CurrentSid, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(acl);
    }

    internal static void Save(RescueRecord record)
    {
        RequireVerified(record);
        if (record.MoveCompleted) CompletionSaveCheckpoint?.Invoke();
        PrivateJsonFile.Write(record.ReadPath!, record);
        if (record.Acknowledged)
        {
            var paths = Acknowledged(record.TrustedConfig!); paths.Add(record.ReadPath!);
            PrivateJsonFile.Write(AcknowledgedPath(record.TrustedConfig!), new AcknowledgedRescues(paths.ToArray(), paths.ToDictionary(path => path, path => new FileInfo(path).Length + "|" + File.GetLastWriteTimeUtc(path).Ticks, StringComparer.OrdinalIgnoreCase)));
        }
    }
    internal static void Acknowledge(RuntimeState state)
    {
        var records = List(state).Where(r => r.RecordStatus == "verified" && r.MoveCompleted).ToArray();
        foreach (var record in records) Save(record with { Acknowledged = true });
    }
    internal static string? CloudPath(RuntimeState? state, CacheEntry entry)
    {
        if (state?.CacheDataRoot is not { } root || !MountPaths.IsSameOrBelow(Path.GetFullPath(entry.DataPath), Path.GetFullPath(root))) return null;
        string encoded = Path.GetRelativePath(root, entry.DataPath).Replace('\\', '/');
        return RecoveryCacheNames.TryDecode(encoded, out string relative) && !Path.IsPathRooted(relative) && !relative.Contains('\\') ? relative : null;
    }
    internal static string SafeRelative(string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Split('/').Any(p => p is "" or "." or ".." ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith(' ') || p.EndsWith('.')))
            throw new IOException("Cannot safely map the rescue name: " + relative);
        return relative;
    }
}
