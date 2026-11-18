using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

public sealed record DownloadEvent(string Id, string FileKey, DateTimeOffset At, long Bytes);
public sealed record DownloadCompleted(string Id, DateTimeOffset At);
public sealed record DownloadActive(string FileKey, long Bytes);
public sealed record DownloadWatchState
{
    public DownloadEvent[] Events { get; init; } = [];
    public DownloadCompleted[] Completed { get; init; } = [];
    public DownloadActive[] Active { get; init; } = [];
    public DateTimeOffset ObservedUtc { get; init; }
    public DateTimeOffset? LimitedUntil { get; init; }
    public long TotalTransfers { get; init; }
    public bool Exceeded { get; init; }
    public int Episode { get; init; }
}
public sealed record DownloadActivity(bool Known, long Files, long Bytes, int WindowSeconds, int FileThreshold,
    long ByteThreshold, bool Exceeded, int Episode, bool HistoryLimited, string Reason);
public sealed record DownloadObservation(DownloadWatchState? State, DownloadActivity Activity)
{
    internal DownloadProgress? Progress { get; init; }
}

internal sealed record DownloadFile(string FileKey, string Name, long Bytes, long? Size,
    double? RemainingSeconds, double? ElapsedSeconds);
internal sealed record DownloadProgress(string SourceFs, IReadOnlyList<DownloadFile> Files);

/// <summary>Observe RC transfer metadata only. Never opens content or controls a transfer.</summary>
public static class DownloadMonitor
{
    public static DownloadObservation Read(RuntimeState state, DateTimeOffset now)
    {
        try
        {
            using var rc = new RcClient(state);
            string fs = rc.Call("vfs/stats").GetProperty("fs").GetString() ?? throw new InvalidDataException("Missing VFS identity.");
            JsonElement completed = rc.Call("core/transferred");
            JsonElement stats = rc.Call("core/stats");
            return Observe(state.DownloadWatch, state.ActiveConfig, fs, completed, stats, now);
        }
        catch (Exception e) when (Unavailable(e))
        {
            DbxConfig config = state.ActiveConfig;
            return new(null, new(false, 0, 0, config.DownloadWindowSeconds, config.DownloadFileThreshold,
                config.DownloadByteThreshold, false, state.DownloadWatch?.Episode ?? 0, true, "RC download statistics are unavailable."));
        }
    }

    public static DownloadObservation Observe(DownloadWatchState? previous, DbxConfig config, string fs,
        JsonElement completed, JsonElement stats, DateTimeOffset now)
    {
        if (previous is not null && now < previous.ObservedUtc) now = previous.ObservedUtc;
        DateTimeOffset cutoff = now.AddSeconds(-config.DownloadWindowSeconds);
        var events = (previous?.Events ?? []).Where(e => e.At > cutoff).ToList();
        var seen = (previous?.Completed ?? []).Where(e => e.At > cutoff).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var finishedBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        var finishedTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var unknownBaseline = new HashSet<string>(StringComparer.Ordinal);
        var recent = completed.GetProperty("transferred");
        if (recent.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unknown transfer history.");
        bool missingDirection = false;
        foreach (JsonElement item in recent.EnumerateArray())
        {
            if (!IsContentTransfer(item)) continue;
            if (!item.TryGetProperty("srcFs", out _) && !item.TryGetProperty("dstFs", out _)) { missingDirection = true; continue; }
            if (!IsDownload(item, fs)) continue;
            DateTimeOffset stamp = Time(item, "completed_at") ??
                (item.TryGetProperty("timestamp", out var timestamp) ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.GetInt64()) :
                throw new InvalidDataException("A completed transfer has no timestamp."));
            if (stamp <= cutoff) continue;
            string name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Transfer has no name.");
            string started = item.TryGetProperty("started_at", out var start) ? start.GetString() ?? "" : "";
            string id = Hash(name + "\n" + started + "\n" + stamp.ToString("O", CultureInfo.InvariantCulture));
            if (seen.ContainsKey(id)) continue;
            seen[id] = new(id, stamp);
            string fileKey = Hash(name.Normalize().ToUpperInvariant());
            finishedBytes[fileKey] = finishedBytes.GetValueOrDefault(fileKey) + Math.Max(0, StatusReader.Number(item, "bytes"));
            if (!finishedTimes.TryGetValue(fileKey, out var before) || stamp > before) finishedTimes[fileKey] = stamp;
            if (Time(item, "started_at") is not { } startedAt || startedAt < cutoff) unknownBaseline.Add(fileKey);
        }
        DownloadFile[] transfers = ActiveTransfers(fs, stats, out bool activeDirectionMissing);
        missingDirection |= activeDirectionMissing;
        var active = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (DownloadFile file in transfers)
            active[file.FileKey] = active.GetValueOrDefault(file.FileKey) + file.Bytes;
        long total = StatusReader.Number(stats, "transfers");
        var priorActive = (previous?.Active ?? []).ToDictionary(e => e.FileKey, e => e.Bytes, StringComparer.Ordinal);
        bool gap = previous is null || now - previous.ObservedUtc > TimeSpan.FromSeconds(config.DownloadWindowSeconds);
        bool counterReset = false;
        foreach (string key in finishedBytes.Keys.Concat(active.Keys).Distinct(StringComparer.Ordinal))
        {
            long currentBytes = finishedBytes.GetValueOrDefault(key) + active.GetValueOrDefault(key);
            long baseline = priorActive.GetValueOrDefault(key);
            if (gap && active.ContainsKey(key) || !priorActive.ContainsKey(key) && unknownBaseline.Contains(key))
            { baseline = currentBytes; counterReset = true; }
            if (currentBytes < baseline) counterReset = true;
            long delta = Math.Max(0, currentBytes - baseline);
            if (delta > 0 || finishedBytes.ContainsKey(key) || !priorActive.ContainsKey(key))
            {
                DateTimeOffset at = !active.ContainsKey(key) && finishedTimes.TryGetValue(key, out var finished) && finished < now ? finished : now;
                events.Add(new(Guid.NewGuid().ToString("N"), key, at, delta));
            }
        }
        bool limited = counterReset || events.Count > 20000 || seen.Count > 20000 || recent.GetArrayLength() >= 100 ||
            total - (previous?.TotalTransfers ?? 0) > recent.GetArrayLength();
        DateTimeOffset? limitedUntil = limited ? now.AddSeconds(config.DownloadWindowSeconds) : previous?.LimitedUntil;
        bool historyLimited = limitedUntil > now || missingDirection;
        DownloadEvent[] retained = events.OrderBy(e => e.At).ThenBy(e => e.Id, StringComparer.Ordinal).TakeLast(20000).ToArray();
        long files = retained.Select(e => e.FileKey).Distinct(StringComparer.Ordinal).LongCount();
        long bytes = retained.Sum(e => e.Bytes);
        bool exceeded = files >= config.DownloadFileThreshold || bytes >= config.DownloadByteThreshold;
        int episode = (previous?.Episode ?? 0) + (exceeded && previous?.Exceeded != true ? 1 : 0);
        var next = new DownloadWatchState { Events = retained,
            Completed = seen.Values.OrderBy(e => e.At).TakeLast(20000).ToArray(),
            Active = active.Select(e => new DownloadActive(e.Key, e.Value)).ToArray(), ObservedUtc = now, LimitedUntil = limitedUntil,
            TotalTransfers = total, Exceeded = exceeded, Episode = episode };
        return new(next, new(!missingDirection, files, bytes, config.DownloadWindowSeconds, config.DownloadFileThreshold,
            config.DownloadByteThreshold, exceeded, episode, historyLimited,
            missingDirection ? "Transfer direction is unavailable; counts are incomplete." : historyLimited ?
            "RC history is bounded; these counts are a lower bound." : "RC download transfer statistics."))
        { Progress = Progress(fs, transfers) };
    }

    // The resident uses the same direction test on the lightweight poll. No state is written here.
    internal static DownloadProgress? ReadProgress(RuntimeState state, string fs)
    {
        try
        {
            using var rc = new RcClient(state.RcPort, new(state.RcUser, state.RcPassword), TimeSpan.FromSeconds(1));
            return Progress(fs, ActiveTransfers(fs, rc.Call("core/stats"), out _));
        }
        catch (Exception e) when (Unavailable(e)) { return null; }
    }

    private static bool Unavailable(Exception error) => error is HttpRequestException or OperationCanceledException or
        JsonException or InvalidOperationException or KeyNotFoundException or IOException or ArgumentException or OverflowException;

    private static DownloadFile[] ActiveTransfers(string fs, JsonElement stats, out bool missingDirection)
    {
        missingDirection = false;
        if (!stats.TryGetProperty("transferring", out var transfers) || transfers.ValueKind != JsonValueKind.Array)
            return [];

        var files = new List<DownloadFile>();
        foreach (JsonElement item in transfers.EnumerateArray())
        {
            if (!IsContentTransfer(item)) continue;
            if (!item.TryGetProperty("srcFs", out _) && !item.TryGetProperty("dstFs", out _))
            {
                missingDirection = true;
                continue;
            }
            if (!IsDownload(item, fs)) continue;
            string name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Transfer has no name.");
            long bytes = Math.Max(0, StatusReader.Number(item, "bytes"));
            long? size = item.TryGetProperty("size", out var total) && total.ValueKind == JsonValueKind.Number &&
                total.TryGetInt64(out long length) && length >= 0 ? length : null;
            double? speed = PositiveNumber(item, "speed");
            double? currentSpeed = PositiveNumber(item, "speedAvg") ?? speed;
            double? remaining = size is null ? null : bytes >= size ? 0 :
                Number(item, "eta") ?? (size.Value - bytes) / currentSpeed;
            files.Add(new(Hash(name.Normalize().ToUpperInvariant()), WindowsName(name), bytes, size, remaining, bytes / speed));
        }
        return files.ToArray();
    }

    private static DownloadProgress Progress(string fs, IEnumerable<DownloadFile> transfers) =>
        new(fs, transfers.GroupBy(file => file.Name, StringComparer.Ordinal)
            .Select(group => group.MaxBy(file => file.Bytes)!)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    private static double? Number(JsonElement item, string field) =>
        item.TryGetProperty(field, out var number) && number.ValueKind == JsonValueKind.Number &&
        number.TryGetDouble(out double value) && double.IsFinite(value) && value >= 0 ? value : null;

    private static double? PositiveNumber(JsonElement item, string field) => Number(item, field) is > 0 and var value ? value : null;

    private static string WindowsName(string name) => new(name.Select(c =>
        c == '/' ? '\\' : MountPaths.WinFspCharacters.Contains(c) ? (char)(c + 0xf000) : c).ToArray());

    private static bool IsDownload(JsonElement item, string fs)
    {
        if (!item.TryGetProperty("srcFs", out var source) || source.ValueKind != JsonValueKind.String ||
            !NormalizeFs(source.GetString()!).Equals(NormalizeFs(fs), StringComparison.OrdinalIgnoreCase)) return false;
        if (item.TryGetProperty("dstFs", out var destination) && !string.IsNullOrEmpty(destination.GetString())) return false;
        if (item.TryGetProperty("checked", out var check) && check.GetBoolean()) return false;
        return !item.TryGetProperty("what", out var what) || what.GetString() == "transferring";
    }
    private static bool IsContentTransfer(JsonElement item) =>
        !(item.TryGetProperty("checked", out var check) && check.GetBoolean()) &&
        (!item.TryGetProperty("what", out var what) || what.GetString() == "transferring");
    private static string NormalizeFs(string value)
    {
        value = value.Replace('\\', '/').TrimEnd('/');
        return value.StartsWith("//?/", StringComparison.Ordinal) ? value[4..] : value;
    }
    private static DateTimeOffset? Time(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var stamp) && stamp.Year > 1970 ? stamp : null;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
