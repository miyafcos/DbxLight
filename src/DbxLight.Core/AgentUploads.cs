using System.Diagnostics;
using System.Text.Json;

namespace DbxLight.Core;

public sealed record AgentUploadVerification(long LocalSize, DateTimeOffset LocalModifiedUtc,
    long CloudSize, DateTimeOffset CloudModifiedUtc, DateTimeOffset VerifiedUtc);
public sealed record AgentUploadItem(string Path, string State, bool Verified, string Message, AgentUploadVerification? Verification = null);
public sealed record AgentWaitResult(bool Ok, string Code, string Message, IReadOnlyList<AgentUploadItem> Items,
    double WaitedSeconds, DbxStatus? Status, int SkippedUnsafe = 0)
{
    public string RescueScope => "退避したファイルはアップロードされておらず、wait --all の対象に入りません。dbxl rescue list --json で確認してください。";
}
public sealed record AgentWaitOutcome(int ExitCode, AgentWaitResult Result);
internal sealed record UploadQueueEntry(string Path, bool Uploading, int Tries);

public sealed partial class AgentMetadata
{
    internal Action<string>? WaitCheckpoint { get; init; }
    public AgentWaitOutcome Wait(IReadOnlyList<string> paths, bool all = false, double timeoutSeconds = 120)
    {
        if (all == (paths.Count > 0) || paths.Count > 1000 || !double.IsFinite(timeoutSeconds) || timeoutSeconds is < 0 or > 86400)
            throw new ArgumentException("Use wait <file>... or wait --all; at most 1000 paths; timeout=0..86400 seconds.");
        var timer = Stopwatch.StartNew();
        unsafeFolders.Clear();
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string relative = Relative(path, allowRoot: false); EnsureVisible(relative);
            targets[PathKey(relative)] = relative;
        }
        var items = targets.Values.Select(p => new AgentUploadItem(p, "unknown", false, "Not verified yet.")).ToList();
        AgentWaitOutcome Finish(int exit, string code, string message) => new(exit,
            new(exit == 0, code, message, items, Math.Round(timer.Elapsed.TotalSeconds, 3), Status, SkippedUnsafe));
        if (rc is null) return Finish(6, "not_running", "The mount is not running.");
        if (!Config.WriteAllowedRemotes.Contains(Config.Remote, StringComparer.Ordinal))
            return Finish(5, "read_only", "New saves are not permitted in this read-only mount; tell a person.");
        string metaPath;
        try { metaPath = CacheMetadataPath(); }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        { return Finish(3, "unknown", "Cache metadata could not be inspected: " + e.Message); }
        do
        {
            try
            {
                UpdateStatus();
                List<UploadQueueEntry> queue = ReadUploadQueue();
                CacheInspection observed = PendingCacheInspector.Observe(Config.CacheDir);
                bool unknownCacheNames = !observed.CompleteScan;
                if (all)
                {
                    foreach (UploadQueueEntry entry in queue) AddTarget(entry.Path);
                    foreach (CacheEntry entry in observed.Entries)
                    {
                        if (!MountPaths.IsSameOrBelow(WithoutExtendedPrefix(entry.MetadataPath), WithoutExtendedPrefix(metaPath)))
                        { if (!entry.MetadataValid || entry.Dirty != false) unknownCacheNames = true; continue; }
                        string cachedRelative = Path.GetRelativePath(WithoutExtendedPrefix(metaPath), WithoutExtendedPrefix(entry.MetadataPath)).Replace('\\', '/');
                        if (!CacheNames.TryDecode(cachedRelative, out string relative))
                        { unknownCacheNames = true; continue; }
                        if (!entry.MetadataValid || entry.Dirty != false)
                            try { AddTarget(relative); }
                            catch (ArgumentException) { unknownCacheNames = true; }
                    }
                }
                items = [];
                foreach (string relative in targets.Values)
                {
                    UploadQueueEntry? pending = queue.Find(q => PathKey(q.Path) == PathKey(relative));
                    if (pending is not null) { items.Add(QueueItem(pending)); continue; }
                    if (timer.Elapsed.TotalSeconds > timeoutSeconds && items.Count > 0)
                    { items.Add(new(relative, "unknown", false, "Time limit reached before verification.")); continue; }
                    try
                    {
                        AgentEntry? local = MetadataStat(relative), cloud = MetadataStat(relative, cloud: true);
                        DateTimeOffset verifiedUtc = DateTimeOffset.UtcNow;
                        WaitCheckpoint?.Invoke("after-cloud:" + relative);
                        AgentEntry? localAfter = MetadataStat(relative);
                        bool verified = SameFile(local, cloud) && SameFile(local, localAfter) &&
                            IsDirty(PendingCacheInspector.Observe(Config.CacheDir), metaPath, relative) == false;
                        AgentUploadVerification? verification = verified ? new(local!.Size!.Value, local.ModifiedUtc!.Value,
                            cloud!.Size!.Value, cloud.ModifiedUtc!.Value, verifiedUtc) : null;
                        items.Add(new(relative, verified ? "uploaded" : "unknown", verified,
                            verified ? "Cloud size and modification time match the observed file version." : "Cloud size/time or cache state cannot confirm this file.", verification));
                        WaitCheckpoint?.Invoke("after-verified:" + relative);
                    }
                    catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or JsonException)
                    { items.Add(new(relative, "unknown", false, "Cloud metadata could not be verified: " + e.Message)); }
                }
                // Keep a deterministic boundary after the last queue read: saves there
                // must be caught by the final status, pair inspection and every target's metadata.
                List<UploadQueueEntry> after = ReadUploadQueue();
                WaitCheckpoint?.Invoke("after-final-queue");
                UpdateStatus();
                CacheInspection finalCache = PendingCacheInspector.Observe(Config.CacheDir);
                bool queueChanged = Status.UploadsQueued is null || Status.UploadsInProgress is null ||
                    Status.UploadsQueued + Status.UploadsInProgress > after.Count;
                for (int i = 0; i < items.Count; i++)
                {
                    AgentUploadItem item = items[i];
                    UploadQueueEntry? pending = after.Find(q => PathKey(q.Path) == PathKey(item.Path));
                    if (pending is not null) { items[i] = QueueItem(pending); continue; }
                    if (!item.Verified) continue;
                    AgentEntry? current = MetadataStat(item.Path);
                    if (queueChanged || !MatchesVerification(current, item.Verification) ||
                        IsDirty(finalCache, metaPath, item.Path) != false)
                        items[i] = new(item.Path, "unknown", false, "The file version or pending state changed; verification must be repeated.");
                }
                if (items.Any(i => i.State == "failed") || Status.ErroredFiles > 0)
                    return Finish(4, "upload_failed", "An upload failed; tell a person and retain the saved file.");
                bool noGlobalPending = !all || (!unknownCacheNames && Status.UploadsQueued == 0 && Status.UploadsInProgress == 0 && after.Count == 0 &&
                    finalCache.Clean);
                if (items.All(i => i.Verified) && noGlobalPending)
                    return Finish(0, "uploaded", "Cloud metadata verified; no target remains in the upload queue.");
                if (all && items.Count == 0) items.Add(new("*", "unknown", false, "Pending work cannot be identified."));
            }
            catch (AgentCommandException e) { return Finish(e.ExitCode, e.Code, e.Message); }
            catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
            { return Finish(4, "failed", "Upload verification failed: " + e.Message); }
            if (timer.Elapsed.TotalSeconds >= timeoutSeconds) break;
            Thread.Sleep((int)Math.Min(200, Math.Max(1, (timeoutSeconds - timer.Elapsed.TotalSeconds) * 1000)));
        } while (true);
        return Finish(3, items.Any(i => i.State == "unknown") ? "unknown" : "timeout",
            "The files are not confirmed uploaded within the time limit.");

        void AddTarget(string path)
        {
            string relative = Relative(path, allowRoot: false); EnsureVisible(relative);
            targets[PathKey(relative)] = relative;
            if (targets.Count > 1000)
            {
                items = targets.Values.Take(1000).Select(p => new AgentUploadItem(p, "unknown", false, "Not verified yet.")).ToList();
                throw new AgentCommandException(3, "too_many", "More than 1000 pending files; wait on smaller explicit groups from the listed items, then repeat --all.", Status);
            }
        }
    }

    private void UpdateStatus()
    {
        var latest = controller.ReadAgentRuntime(); Status = latest.Status;
        EnsureSafe();
        if (!Status.Running) throw new AgentCommandException(6, "not_running", "The mount stopped while waiting.", Status);
        if (latest.Runtime?.RclonePid != Runtime?.RclonePid || latest.Runtime?.ProcessStartedUtc != Runtime?.ProcessStartedUtc ||
            JsonSerializer.Serialize(latest.Runtime?.ActiveConfig, JsonFiles.Options) != JsonSerializer.Serialize(Runtime?.ActiveConfig, JsonFiles.Options))
            throw new AgentCommandException(3, "mount_changed", "The active mount changed; tell a person and check again.", Status);
    }

    private static bool SameFile(AgentEntry? left, AgentEntry? right) => left is { Type: "file", Size: not null, ModifiedUtc: not null }
        && right is { Type: "file", Size: not null, ModifiedUtc: not null } && left.Size == right.Size && left.ModifiedUtc == right.ModifiedUtc;

    private static string PathKey(string path) => path.Normalize().ToUpperInvariant();

    private List<UploadQueueEntry> ReadUploadQueue()
    {
        JsonElement result = rc!.Call("vfs/queue");
        if (!result.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array)
            throw new AgentCommandException(3, "queue_unknown", "The upload queue is unavailable; tell a person.", Status);
        return queue.EnumerateArray().Select(q => new UploadQueueEntry(
            q.GetProperty("name").GetString() ?? throw new InvalidDataException("Queue path is missing."),
            q.GetProperty("uploading").GetBoolean(), q.GetProperty("tries").GetInt32())).ToList();
    }

    private static AgentUploadItem QueueItem(UploadQueueEntry item) => new(item.Path,
        item.Uploading ? "uploading" : item.Tries > 0 ? "failed" : "queued", false,
        item.Tries > 0 ? "Upload attempts have failed." : "Still in the upload queue.");

    private string CacheMetadataPath()
    {
        string path = rc!.Call("vfs/stats").GetProperty("diskCache").GetProperty("pathMeta").GetString()
            ?? throw new InvalidDataException("Cache metadata path is missing.");
        if (!MountPaths.IsSameOrBelow(WithoutExtendedPrefix(path), WithoutExtendedPrefix(Config.CacheDir)))
            throw new InvalidDataException("Cache metadata is outside the active cache directory.");
        return path;
    }

    private static bool MatchesVerification(AgentEntry? item, AgentUploadVerification? verification) =>
        item is { Type: "file", Size: not null, ModifiedUtc: not null } && verification is not null &&
        item.Size == verification.LocalSize && item.ModifiedUtc == verification.LocalModifiedUtc;

    private bool? IsDirty(CacheInspection inspection, string metaPath, string relative)
    {
        string? metadata = CacheNames.PathIn(metaPath, relative);
        string? data = cacheDataPath is null ? null : CacheNames.PathIn(cacheDataPath, relative);
        if (metadata is null || data is null || !inspection.CompleteScan) return null;
        bool EqualPath(string left, string right) => WithoutExtendedPrefix(left).Equals(WithoutExtendedPrefix(right), StringComparison.OrdinalIgnoreCase);
        CacheEntry? pair = inspection.Entries.FirstOrDefault(e => EqualPath(e.MetadataPath, metadata) || EqualPath(e.DataPath, data));
        // Only a completed scan proving that neither half exists is an uncached file.
        if (pair is null) return false;
        return pair.MetadataValid && EqualPath(pair.MetadataPath, metadata) && EqualPath(pair.DataPath, data) ? pair.Dirty : null;
    }

    private AgentEntry? MetadataStat(string relative, bool cloud = false)
    {
        // A single-file OS stat can open a VFS cache entry on Windows. Enumerating
        // its parent obtains attributes without opening that mounted file.
        string parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
        if (!cloud && (relative.Length > 0 || rc is not null || ChildrenReader is not null))
            return relative.Length == 0 ? new("", "", "dir", null, null, false, false) :
                Children(parent).FirstOrDefault(e => PathKey(e.Path) == PathKey(relative));
        JsonElement item;
        if (rc is not null)
        {
            item = rc.Call("operations/stat", new
            {
                fs = Config.Remote, remote = relative,
                opt = new { noMimeType = true, showHash = false },
                _filter = RcloneFilters.RemoteOptions(Config)
            }).GetProperty("item");
        }
        else item = RunLsJson(relative, stat: true);
        if (item.ValueKind == JsonValueKind.Null) return null;
        if (relative.Length == 0) return new("", "", "dir", null, null, false, false);
        return ParseChildren([ReadItem(item)], parent, includeHidden: true).FirstOrDefault();
    }
}
