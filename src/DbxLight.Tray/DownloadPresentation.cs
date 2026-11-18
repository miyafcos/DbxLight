using DbxLight.Core;

namespace DbxLight.Tray;

internal static class DownloadPresentation
{
    internal static bool HasDownloads(TraySnapshot snapshot) => Files(snapshot).Count > 0;

    internal static IReadOnlyList<DownloadFile> Files(TraySnapshot snapshot) =>
        snapshot is { Confirming: false, Status: { Running: true, Ready: true, ProcessUncertain: false,
            DownloadProgress.Files: { } files } } ? files : [];

    internal static string Tooltip(IReadOnlyList<DownloadFile> files)
    {
        const string prefix = "ダウンロード中: ";
        string amount = Amount(files.Sum(f => (decimal)f.Bytes),
            files.All(f => f.Size.HasValue) ? files.Sum(f => (decimal)f.Size!.Value) : null);
        string item = files.Count == 1 ? ShortName(files[0], 127 - prefix.Length - amount.Length - 1) : $"{files.Count} 件";
        return prefix + item + " " + amount;
    }

    internal static string ShortName(DownloadFile file, int limit)
    {
        string name = file.Name[(file.Name.LastIndexOf('\\') + 1)..];
        if (name.Length <= limit) return name;
        int end = limit - 1;
        if (char.IsHighSurrogate(name[end - 1])) end--;
        return name[..end] + "…";
    }

    internal static string Amount(decimal bytes, decimal? size)
    {
        decimal largest = Math.Max(bytes, size ?? 0);
        var (unit, scale) = largest switch
        {
            >= 1L << 40 => ("TB", 1L << 40),
            >= 1L << 30 => ("GB", 1L << 30),
            >= 1L << 20 => ("MB", 1L << 20),
            >= 1L << 10 => ("KB", 1L << 10),
            _ => ("バイト", 1L)
        };
        return $"{bytes / scale:0.#} / {(size is { } total ? $"{total / scale:0.#}" : "不明")} {unit}";
    }

    internal static string Remaining(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value)) return "確認できません";
        if (value <= 0) return "まもなく完了";
        if (value >= 86400) return "24 時間以上";
        int wholeSeconds = (int)Math.Ceiling(value);
        if (wholeSeconds < 60) return $"約 {wholeSeconds} 秒";
        return $"約 {wholeSeconds / 60} 分 {wholeSeconds % 60} 秒";
    }

    internal static int PollDelay(TimeSpan untilFullRefresh, bool downloading)
    {
        double milliseconds = untilFullRefresh.TotalMilliseconds;
        return Math.Max(1, (int)Math.Ceiling(downloading ? Math.Min(milliseconds, 1000) : milliseconds));
    }
}

internal sealed class LongDownloadNoticeTracker
{
    private const long MinimumSize = 8L * 1024 * 1024;
    private const double MinimumElapsed = 3;
    private const double MinimumRemaining = 3;
    private readonly Dictionary<string, DateTimeOffset> firstSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> announced = new(StringComparer.Ordinal);
    private MountIdentity? previousIdentity;
    private DateTimeOffset nextNotice;

    internal DownloadFile? Observe(TraySnapshot snapshot, DateTimeOffset now)
    {
        if (previousIdentity != snapshot.Identity)
        {
            previousIdentity = snapshot.Identity;
            firstSeen.Clear();
            announced.Clear();
            nextNotice = default;
        }
        if (snapshot.Confirming || !snapshot.Status.Running || !snapshot.Status.Ready ||
            snapshot.Status.DownloadProgress is not { } progress) return null;

        var keys = progress.Files.Select(f => f.FileKey).ToHashSet(StringComparer.Ordinal);
        foreach (string key in firstSeen.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            firstSeen.Remove(key);
            announced.Remove(key);
        }
        foreach (DownloadFile file in progress.Files) firstSeen.TryAdd(file.FileKey, now);

        if (now < nextNotice || TrayPresentation.State(snapshot) != TrayState.Downloading ||
            snapshot.Status.DownloadActivity is { } activity && (activity.Exceeded ||
                progress.Files.Count >= activity.FileThreshold || progress.Files.Sum(f => (decimal)f.Bytes) >= activity.ByteThreshold))
            return null;

        foreach (DownloadFile file in progress.Files)
        {
            double elapsed = Math.Max(file.ElapsedSeconds ?? 0, (now - firstSeen[file.FileKey]).TotalSeconds);
            if (file.Size is not { } size || size < MinimumSize || file.Bytes >= size || elapsed < MinimumElapsed ||
                file.RemainingSeconds is < MinimumRemaining || !announced.Add(file.FileKey)) continue;
            nextNotice = now.AddSeconds(8);
            return file;
        }
        return null;
    }

    internal static string Key(MountIdentity? identity, DownloadFile file) =>
        $"long-download:{identity?.Pid}:{identity?.StartedUtc.Ticks}:{file.FileKey}";
}
