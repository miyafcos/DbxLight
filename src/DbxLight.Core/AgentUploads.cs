using System.Diagnostics;
using System.Text.Json;

namespace DbxLight.Core;

public sealed record AgentUploadVerification(long LocalSize, DateTimeOffset LocalModifiedUtc,
    long CloudSize, DateTimeOffset CloudModifiedUtc, DateTimeOffset VerifiedUtc);
public sealed record AgentUploadItem(string Path, string State, bool Verified, string Message, AgentUploadVerification? Verification = null);
public sealed record AgentCacheRemnant(string Path, long Bytes, DateTimeOffset ModifiedUtc);
public sealed record AgentWaitResult(bool Ok, string Code, string Message, IReadOnlyList<AgentUploadItem> Items,
    double WaitedSeconds, DbxStatus? Status, int SkippedUnsafe = 0)
{
    public int RemnantCount { get; init; }
    public IReadOnlyList<AgentCacheRemnant> Remnants { get; init; } = [];
    public string RemnantMessage => RemnantCount == 0 ? "" : "名前の変更の途中で残ったキャッシュの本体はアップロード待ちではなく、常駐を止めたときに回復の対象として残ります。";
    public string RescueScope => "退避したファイルはアップロードされておらず、wait --all の対象に入りません。dbxl rescue list --json で確認してください。";
}
public sealed record AgentWaitOutcome(int ExitCode, AgentWaitResult Result);
internal sealed record UploadQueueEntry(string Path, bool Uploading, int Tries);

public sealed partial class AgentMetadata
{
    internal Func<string, object?, JsonElement>? WaitRcCallTestOverride { get; init; }
    internal Func<double>? WaitElapsedTestOverride { get; init; }
    internal Action<int>? WaitDelayTestOverride { get; init; }
    internal const int RemnantMinimumAgeSeconds = 60;
    internal Func<DateTimeOffset>? WaitUtcNow { get; init; }
    internal Action<string>? WaitCheckpoint { get; init; }
    public AgentWaitOutcome Wait(IReadOnlyList<string> paths, bool all = false, double timeoutSeconds = 120)
    {
        if (all == (paths.Count > 0) || paths.Count > 1000 || !double.IsFinite(timeoutSeconds) || timeoutSeconds is < 0 or > 86400)
            throw new ArgumentException("Use wait <file>... or wait --all; at most 1000 paths; timeout=0..86400 seconds.");
        var timer = Stopwatch.StartNew();
        double ElapsedSeconds() => WaitElapsedTestOverride?.Invoke() ?? timer.Elapsed.TotalSeconds;
        JsonElement Call(string command, object? body = null) =>
            WaitRcCallTestOverride?.Invoke(command, body) ?? rc!.Call(command, body);
        unsafeFolders.Clear();
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string relative = Relative(path, allowRoot: false); EnsureVisible(relative);
            targets[PathKey(relative)] = relative;
        }
        var items = targets.Values.Select(p => new AgentUploadItem(p, "unknown", false, "Not verified yet.")).ToList();
        var remnants = new List<AgentCacheRemnant>();
        AgentWaitOutcome Finish(int exit, string code, string message) => new(exit,
            new(exit == 0, code, message, items, Math.Round(timer.Elapsed.TotalSeconds, 3), Status, SkippedUnsafe) { RemnantCount = remnants.Count, Remnants = remnants.Take(20).ToArray() });
        if (rc is null) return Finish(6, "not_running", "The mount is not running.");
        if (!Config.WriteAllowedRemotes.Contains(Config.Remote, StringComparer.Ordinal))
            return Finish(5, "read_only", "New saves are not permitted in this read-only mount; tell a person.");
        string metaPath = "";
        bool metadataKnown = false, retrying = false;
        (int Exit, string Code, string Message)? lastRcTimeout = null;
        do
        {
            bool metadataPhase = !metadataKnown, statusPhase = false;
            try
            {
                remnants.Clear();
                items = targets.Values.Select(p => new AgentUploadItem(p, "unknown", false, "Not verified yet.")).ToList();
                // Retry the status/identity guard even when the initial metadata read timed out.
                if (retrying && metadataPhase) { statusPhase = true; UpdateStatus(wait: true); statusPhase = false; }
                if (!metadataKnown) { metaPath = CacheMetadataPath(Call); metadataKnown = true; }
                metadataPhase = false;
                statusPhase = true; UpdateStatus(wait: true); statusPhase = false;
                List<UploadQueueEntry> queue = ReadUploadQueue(Call);
                CacheInspection observed = PendingCacheInspector.Observe(Config.CacheDir);
                bool unknownCacheNames = !observed.CompleteScan;
                var remnantCandidates = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
                var absent = new HashSet<string>(StringComparer.Ordinal);
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
                        if (entry.HasData && !entry.HasMetadata)
                        {
                            try
                            {
                                string visible = Relative(relative, allowRoot: false); EnsureVisible(visible);
                                if (cacheDataPath is null || EqualCachePath(entry.DataPath, CacheNames.PathIn(cacheDataPath, visible)) != true)
                                    unknownCacheNames = true;
                                else remnantCandidates[PathKey(visible)] = entry;
                            }
                            catch (ArgumentException)
                            { unknownCacheNames = true; }
                        }
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
                    bool candidate = all && remnantCandidates.ContainsKey(PathKey(relative));
                    if (ElapsedSeconds() > timeoutSeconds && items.Count > 0 && !candidate)
                    { items.Add(new(relative, "unknown", false, "Time limit reached before verification.")); continue; }
                    try
                    {
                        AgentEntry? local = candidate ? RemnantLocalStat(relative) : MetadataStat(relative);
                        bool cloudAbsent = candidate && RemnantCloudAbsent(relative, Call);
                        AgentEntry? cloud = candidate ? null : MetadataStat(relative, cloud: true, Call);
                        DateTimeOffset verifiedUtc = DateTimeOffset.UtcNow;
                        WaitCheckpoint?.Invoke("after-cloud:" + relative);
                        AgentEntry? localAfter = candidate ? RemnantLocalStat(relative) : MetadataStat(relative);
                        if (candidate && local is null && cloudAbsent && localAfter is null) absent.Add(PathKey(relative));
                        bool verified = !candidate && SameFile(local, cloud) && SameFile(local, localAfter) &&
                            IsDirty(PendingCacheInspector.Observe(Config.CacheDir), metaPath, relative) == false;
                        AgentUploadVerification? verification = verified ? new(local!.Size!.Value, local.ModifiedUtc!.Value,
                            cloud!.Size!.Value, cloud.ModifiedUtc!.Value, verifiedUtc) : null;
                        items.Add(new(relative, verified ? "uploaded" : "unknown", verified,
                            verified ? "Cloud size and modification time match the observed file version." : "Cloud size/time or cache state cannot confirm this file.", verification));
                        WaitCheckpoint?.Invoke("after-verified:" + relative);
                    }
                    catch (Exception e) when ((e is IOException or HttpRequestException or OperationCanceledException or JsonException) || candidate && e is UnauthorizedAccessException)
                    { items.Add(new(relative, "unknown", false, "Cloud metadata could not be verified: " + e.Message)); }
                }
                // Keep a deterministic boundary after the last queue read: saves there
                // must be caught by the final status, pair inspection and every target's metadata.
                List<UploadQueueEntry> after = ReadUploadQueue(Call);
                WaitCheckpoint?.Invoke("after-final-queue");
                statusPhase = true; UpdateStatus(wait: true); statusPhase = false;
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
                if (all && observed.CompleteScan && finalCache.CompleteScan)
                {
                    DateTimeOffset now = WaitUtcNow?.Invoke() ?? DateTimeOffset.UtcNow;
                    foreach ((string key, CacheEntry first) in remnantCandidates)
                    {
                        CacheEntry? last = finalCache.Entries.FirstOrDefault(e => EqualCachePath(e.DataPath, first.DataPath) == true);
                        if (!absent.Contains(key) || last is not { HasData: true, HasMetadata: false } ||
                            first.ObservedBytes is not { } bytes || first.ObservedModifiedUtc is not { } modified ||
                            last.ObservedBytes != bytes || last.ObservedModifiedUtc != modified ||
                            now - modified < TimeSpan.FromSeconds(RemnantMinimumAgeSeconds) ||
                            Runtime is null || Runtime.ProcessStartedUtc >= modified.UtcDateTime ||
                            queue.Any(q => PathKey(q.Path) == key) || after.Any(q => PathKey(q.Path) == key)) continue;
                        string relative = targets[key];
                        try { if (RemnantLocalStat(relative) is not null) continue; }
                        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or JsonException or UnauthorizedAccessException or AgentCommandException)
                        { continue; }
                        remnants.Add(new(relative, bytes, modified));
                    }
                    var remnantKeys = remnants.Select(r => PathKey(r.Path)).ToHashSet(StringComparer.Ordinal);
                    items.RemoveAll(i => remnantKeys.Contains(PathKey(i.Path)));
                    foreach (string key in remnantKeys) targets.Remove(key);
                }
                if (items.Any(i => i.State == "failed") || Status.ErroredFiles > 0)
                    return Finish(4, "upload_failed", "An upload failed; tell a person and retain the saved file.");
                bool noGlobalPending = !all || (!unknownCacheNames && Status.UploadsQueued == 0 && Status.UploadsInProgress == 0 && after.Count == 0 &&
                    (finalCache.Clean || finalCache.CompleteScan && finalCache.Entries.All(e =>
                        e.MetadataValid && e.Dirty == false || remnants.Any(r => EqualCachePath(e.DataPath, CacheNames.PathIn(cacheDataPath!, r.Path)) == true))));
                if (items.All(i => i.Verified) && noGlobalPending)
                    return Finish(0, "uploaded", "Cloud metadata verified; no target remains in the upload queue.");
                lastRcTimeout = null;
                if (all && items.Count == 0) items.Add(new("*", "unknown", false, "Pending work cannot be identified."));
            }
            catch (OperationCanceledException error) when (IsRcRequestTimeout(error))
            {
                lastRcTimeout = statusPhase
                    ? (3, "status_unknown", "The running mount cannot be verified.")
                    : metadataPhase ? (3, "unknown", "Cache metadata could not be inspected: " + error.Message)
                    : (4, "failed", "Upload verification failed: " + error.Message);
                // No receipt, queue or remnant proof from an incomplete round survives it.
                remnants.Clear();
                items = targets.Values.Select(p => new AgentUploadItem(p, "unknown", false, "Not verified yet.")).ToList();
                WaitCheckpoint?.Invoke(metadataPhase ? "rc-timeout:metadata" : "rc-timeout:loop");
                retrying = true;
            }
            catch (AgentCommandException e) { return Finish(e.ExitCode, e.Code, e.Message); }
            catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
            { return metadataPhase ? Finish(3, "unknown", "Cache metadata could not be inspected: " + e.Message)
                : Finish(4, "failed", "Upload verification failed: " + e.Message); }
            if (ElapsedSeconds() >= timeoutSeconds) break;
            int interval = (int)Math.Min(200, Math.Max(1, (timeoutSeconds - ElapsedSeconds()) * 1000));
            if (WaitDelayTestOverride is { } delay) delay(interval); else Thread.Sleep(interval);
        } while (true);
        if (lastRcTimeout is { } timedOut) return Finish(timedOut.Exit, timedOut.Code, timedOut.Message);
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

    private static bool? EqualCachePath(string left, string? right, bool normalizeUnicode = false)
    {
        if (right is null) return null;
        try
        {
            string a = NormalizeCachePath(left), b = NormalizeCachePath(right);
            if (normalizeUnicode) { a = a.Normalize(); b = b.Normalize(); }
            return a.Equals(b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }

    private static string NormalizeCachePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(WithoutExtendedPrefix(path)));

    private AgentEntry? RemnantLocalStat(string relative)
    {
        try { return MetadataStat(relative); }
        // Only a definite absent parent is absence. Other IO and access errors remain unknown.
        catch (DirectoryNotFoundException)
        {
            string parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            if (parent.Length == 0) throw; // A missing mount is not an absent parent inside it.
            try { _ = File.GetAttributes(Path.Combine(MountPaths.CanonicalMount(Config.MountPoint), parent.Replace('/', '\\'))); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
            throw new IOException("The mounted parent cannot be confirmed absent.");
        }
    }

    private bool RemnantCloudAbsent(string relative, Func<string, object?, JsonElement>? call = null) => (call ?? rc!.Call)("operations/stat", new
    {
        fs = Config.Remote, remote = relative,
        opt = new { noMimeType = true, showHash = false },
        _filter = RcloneFilters.RemoteOptions(Config)
    }).GetProperty("item").ValueKind == JsonValueKind.Null;

    private static bool IsRcRequestTimeout(Exception error) =>
        error is OperationCanceledException { InnerException: TimeoutException };

    private void UpdateStatus(bool wait = false)
    {
        var latest = controller.ReadAgentRuntime(); Status = latest.Status;
        // StatusReader intentionally exposes an error string, not its original exception.
        // Recognize only HttpClient's timeout message here; other status failures stay immediate.
        if (wait && Status is { Running: true, RecoveryRequired: false, ProcessUncertain: false, StatusError: { } error } &&
            error.StartsWith("The request was canceled due to the configured HttpClient.Timeout of ", StringComparison.Ordinal) &&
            error.EndsWith(" seconds elapsing.", StringComparison.Ordinal))
        {
            // A known generation change is terminal even when that generation's RC timed out.
            if (latest.Runtime?.RclonePid != Runtime?.RclonePid || latest.Runtime?.ProcessStartedUtc != Runtime?.ProcessStartedUtc ||
                JsonSerializer.Serialize(latest.Runtime?.ActiveConfig, JsonFiles.Options) != JsonSerializer.Serialize(Runtime?.ActiveConfig, JsonFiles.Options))
                throw new AgentCommandException(3, "mount_changed", "The active mount changed; tell a person and check again.", Status);
            throw new TaskCanceledException(error, new TimeoutException(error));
        }
        EnsureSafe();
        if (!Status.Running) throw new AgentCommandException(6, "not_running", "The mount stopped while waiting.", Status);
        if (latest.Runtime?.RclonePid != Runtime?.RclonePid || latest.Runtime?.ProcessStartedUtc != Runtime?.ProcessStartedUtc ||
            JsonSerializer.Serialize(latest.Runtime?.ActiveConfig, JsonFiles.Options) != JsonSerializer.Serialize(Runtime?.ActiveConfig, JsonFiles.Options))
            throw new AgentCommandException(3, "mount_changed", "The active mount changed; tell a person and check again.", Status);
    }

    private static bool SameFile(AgentEntry? left, AgentEntry? right) => left is { Type: "file", Size: not null, ModifiedUtc: not null }
        && right is { Type: "file", Size: not null, ModifiedUtc: not null } && left.Size == right.Size && left.ModifiedUtc == right.ModifiedUtc;

    private static string PathKey(string path) => path.Normalize().ToUpperInvariant();

    private List<UploadQueueEntry> ReadUploadQueue(Func<string, object?, JsonElement>? call = null)
    {
        JsonElement result = (call ?? rc!.Call)("vfs/queue", null);
        if (!result.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array)
            throw new AgentCommandException(3, "queue_unknown", "The upload queue is unavailable; tell a person.", Status);
        return queue.EnumerateArray().Select(q => new UploadQueueEntry(
            q.GetProperty("name").GetString() ?? throw new InvalidDataException("Queue path is missing."),
            q.GetProperty("uploading").GetBoolean(), q.GetProperty("tries").GetInt32())).ToList();
    }

    private static AgentUploadItem QueueItem(UploadQueueEntry item) => new(item.Path,
        item.Uploading ? "uploading" : item.Tries > 0 ? "failed" : "queued", false,
        item.Tries > 0 ? "Upload attempts have failed." : "Still in the upload queue.");

    private string CacheMetadataPath(Func<string, object?, JsonElement>? call = null)
    {
        string path = (call ?? rc!.Call)("vfs/stats", null).GetProperty("diskCache").GetProperty("pathMeta").GetString()
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
        try
        {
            string? metadata = CacheNames.PathIn(metaPath, relative);
            string? data = cacheDataPath is null ? null : CacheNames.PathIn(cacheDataPath, relative);
            if (metadata is null || data is null || !inspection.CompleteScan ||
                EqualCachePath(metadata, metadata) != true || EqualCachePath(data, data) != true) return null;
            CacheEntry? pair = null;
            foreach (CacheEntry entry in inspection.Entries)
            {
                bool? metaMatch = EqualCachePath(entry.MetadataPath, metadata), dataMatch = EqualCachePath(entry.DataPath, data);
                if (metaMatch is null || dataMatch is null) return null;
                if (metaMatch != true && dataMatch != true) continue;
                if (pair is not null) return null; // More than one normalized pair cannot identify a file.
                pair = entry;
            }
            if (pair is null)
            {
                // A Unicode spelling alias is uncertainty, never proof of an uncached file.
                if (inspection.Entries.Any(e => EqualCachePath(e.MetadataPath, metadata, normalizeUnicode: true) != false ||
                    EqualCachePath(e.DataPath, data, normalizeUnicode: true) != false)) return null;
                return false; // A completed scan found neither half nor any possible alias.
            }
            return pair.MetadataValid && EqualCachePath(pair.MetadataPath, metadata) == true &&
                EqualCachePath(pair.DataPath, data) == true ? pair.Dirty : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }

    private AgentEntry? MetadataStat(string relative, bool cloud = false, Func<string, object?, JsonElement>? call = null)
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
            item = (call ?? rc.Call)("operations/stat", new
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
