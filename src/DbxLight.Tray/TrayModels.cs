using System.Text.Json;
using System.Text.RegularExpressions;
using DbxLight.Core;

namespace DbxLight.Tray;

public enum TrayState { Running, Uploading, Attention, Failed, Preparing }
public sealed record QueueItem(long Id, string Name, long Size, int Tries, bool Uploading);
public sealed record QueueAge(TimeSpan Elapsed, bool PresentAtStartup);
public sealed record MountIdentity(int Pid, DateTime StartedUtc);
public sealed record TraySnapshot(DbxStatus Status, IReadOnlyList<QueueItem> Queue, IReadOnlyList<string> Errors,
    bool QueueKnown, string? Error, int RestartCount = 0, DbxConfig? ActiveConfig = null,
    MountIdentity? Identity = null, bool Confirming = false, IReadOnlyList<string>? StartupRecords = null,
    IReadOnlyList<RescueRecord>? Rescued = null, bool PendingCacheClean = false, bool LogReadSucceeded = false);

public sealed class QueueAgeTracker
{
    private readonly Dictionary<(int Pid, long Id), (DateTimeOffset First, bool Startup)> firstSeen = [];
    private bool firstObservation = true;
    public int TrackedCount => firstSeen.Count;

    // Unknown/failed observations never erase tracking or consume the first successful observation.
    public void Observe(int pid, IReadOnlyList<QueueItem> queue, DateTimeOffset now)
    {
        var ids = queue.Select(q => (pid, q.Id)).ToHashSet();
        foreach (var old in firstSeen.Keys.Where(k => !ids.Contains(k)).ToArray()) firstSeen.Remove(old);
        foreach (var key in ids) firstSeen.TryAdd(key, (now, firstObservation));
        firstObservation = false;
    }

    public QueueAge? Oldest(DateTimeOffset now) => firstSeen.Count == 0 ? null :
        firstSeen.Values.Select(v => new QueueAge(now > v.First ? now - v.First : TimeSpan.Zero, v.Startup))
            .OrderByDescending(v => v.Elapsed).ThenByDescending(v => v.PresentAtStartup).First();
}

public sealed class RestartTracker
{
    private int previous;
    public bool Observe(int count)
    {
        bool restarted = count > previous;
        previous = count;
        return restarted;
    }
}

public sealed class DownloadNoticeTracker
{
    public static string Key(MountIdentity? identity, DownloadActivity activity) =>
        "download:" + identity?.Pid + ":" + identity?.StartedUtc.Ticks + ":" + activity.Episode;
    private MountIdentity? previousIdentity;
    private int previousEpisode;
    public bool Observe(MountIdentity? identity, DownloadActivity? activity)
    {
        if (identity != previousIdentity) { previousIdentity = identity; previousEpisode = 0; }
        if (activity is not { Exceeded: true } || activity.Episode <= previousEpisode) return false;
        previousEpisode = activity.Episode;
        return true;
    }
}

public sealed class NotificationThrottle
{
    private readonly Dictionary<string, DateTimeOffset> sent = new(StringComparer.Ordinal);
    public bool Allow(string content, DateTimeOffset now)
    {
        if (sent.TryGetValue(content, out var previous) && now - previous < TimeSpan.FromMinutes(10)) return false;
        sent[content] = now;
        foreach (string key in sent.Where(p => now - p.Value >= TimeSpan.FromMinutes(10) && p.Key != content).Select(p => p.Key).ToArray())
            sent.Remove(key);
        return true;
    }
}

public static class TrayPresentation
{
    public static TimeSpan PollInterval(DbxStatus status, bool waitingToStop = false) =>
        TimeSpan.FromSeconds(waitingToStop || status.UploadsQueued > 0 || status.UploadsInProgress > 0 ? 5 : 30);
    public static string StopWaiting(long queued) => $"アップロード待ちが {queued} 件あります。アップロードが終わるまで待ちます";
    public const string ForceExplanation = "同じ設定で起動し直すと、停止中のキャッシュを走査します。問題のあるファイルは退避して残し、問題のない未アップロードのファイルは起動直前に照合します。\n退避したファイルは、保存先で内容を確認し、必要な内容をドライブの元の場所へ自分で戻してください。";
    public static bool ReadOnly(DbxConfig config) => !config.WriteAllowedRemotes.Contains(config.Remote, StringComparer.Ordinal);
    public static TrayState State(TraySnapshot snapshot, bool preparing = false)
    {
        if (preparing || snapshot.Confirming) return TrayState.Preparing;
        if (!snapshot.Status.Running || !snapshot.Status.Ready) return TrayState.Failed;
        if (snapshot.Status.ErroredFiles > 0 || snapshot.Queue.Any(q => q.Tries > 0)) return TrayState.Failed;
        if (snapshot.Rescued?.Any(r => !r.Acknowledged) == true) return TrayState.Attention;
        if (snapshot.Status.DownloadActivity?.Exceeded == true) return TrayState.Attention;
        if (snapshot.Error is not null || snapshot.Errors.Count > 0) return TrayState.Attention;
        if (snapshot.Status.UploadsInProgress > 0 || snapshot.Status.UploadsQueued > 0) return TrayState.Uploading;
        return TrayState.Running;
    }
    public static string Tooltip(TraySnapshot snapshot, DbxConfig config, bool preparing = false)
    {
        if (snapshot.Confirming) return "DbxLight 確認中";
        if (snapshot.Status.Running) config = snapshot.ActiveConfig ?? config;
        string text = preparing ? "DbxLight 準備中" : !snapshot.Status.Running ? "DbxLight 停止中" :
            snapshot.Status.ErroredFiles > 0 ? $"失敗 {snapshot.Status.ErroredFiles} 件" :
            snapshot.Queue.Any(q => q.Tries > 0) ? $"失敗 {snapshot.Queue.Count(q => q.Tries > 0)} 件" :
            snapshot.Status.DownloadActivity is { Exceeded: true } downloads ? $"ダウンロードが多い {downloads.Files} 件" :
            snapshot.Status.UploadsQueued > 0 ? $"アップロード待ち {snapshot.Status.UploadsQueued} 件" :
            snapshot.Status.UploadsInProgress > 0 ? $"アップロード中 {snapshot.Status.UploadsInProgress} 件" :
            snapshot.Error is not null ? "DbxLight 状態を確認できません" : "DbxLight 稼働中";
        return text + (ReadOnly(config) ? " (読み取り専用)" : "");
    }
    public static string Permission(TraySnapshot snapshot, DbxConfig config)
    {
        if (snapshot.Confirming) return "確認中";
        if (snapshot.Status.Running) config = snapshot.ActiveConfig ?? config;
        return ReadOnly(config) ? "読み取り専用" : "書き込み可";
    }
    public static string Headline(TraySnapshot snapshot, DbxConfig config)
    {
        if (snapshot.Status.Preparation is { } preparing) return "DbxLight 起動の準備中: " + preparing.Stage;
        string text = State(snapshot) switch
        {
            TrayState.Running => "稼働中",
            TrayState.Uploading => "アップロード中",
            TrayState.Attention => "注意が必要です",
            TrayState.Failed => snapshot.Status.Running ? "失敗があります" : "停止中",
            _ => snapshot.Confirming ? "確認中" : "準備中"
        };
        if (snapshot.Status.Running) config = snapshot.ActiveConfig ?? config;
        return "DbxLight " + text + (snapshot.Confirming ? "" : " (" + Permission(snapshot, config) + ")");
    }
    public static string Bytes(long? bytes) => bytes is null ? "確認できません" :
        bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : $"{bytes:N0} バイト";
    public static string QueueAgeExplanation(TraySnapshot snapshot, QueueAge? age)
    {
        if (snapshot.Status.Preparation is not null) return "起動の準備中は、アップロード待ちの件数と時間を確認できません。";
        if (snapshot.Status.RecoveryRequired) return "\u56de\u5fa9\u524d\u306e\u30a2\u30c3\u30d7\u30ed\u30fc\u30c9\u5f85\u3061\u306e\u4ef6\u6570\u3068\u6642\u9593\u306f\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002";
        bool empty = snapshot.QueueKnown && snapshot.Queue.Count == 0 && snapshot.Error is null &&
            !snapshot.Status.ProcessUncertain && snapshot.Status.StatusError is null &&
            snapshot.Status.UploadsQueued == 0 && snapshot.Status.UploadsInProgress == 0;
        return "\u3044\u3061\u3070\u3093\u53e4\u3044\u30a2\u30c3\u30d7\u30ed\u30fc\u30c9\u5f85\u3061: " +
            (empty || snapshot.QueueKnown && age is not null ? Age(age) : "\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093") +
            (empty ? "" : "\n\u8868\u793a\u3057\u305f\u6642\u9593\u3088\u308a\u524d\u304b\u3089\u5f85\u3063\u3066\u3044\u305f\u53ef\u80fd\u6027\u304c\u3042\u308a\u307e\u3059\u3002");
    }

    public static string Age(QueueAge? age) => age is null ? "アップロード待ちはありません" :
        (age.PresentAtStartup ? "起動時から " : "初めて確認してから ") + $"{Math.Floor(age.Elapsed.TotalMinutes):0} 分";
}

public static class SnapshotReader
{
    public static TraySnapshot Read(MountController controller)
    {
        var observation = controller.ReadRuntime();
        DbxStatus status = observation.Status;
        if (status.Preparation is not null) return new(status, [], [], false, null, Rescued: []);
        RuntimeState? state = observation.Runtime;
        IReadOnlyList<QueueItem> queue = [];
        string? error = status.StatusError;
        bool known = false;
        if (status.Running && state is not null && state.RclonePid == status.RclonePid)
        {
            try
            {
                using var rc = new RcClient(state);
                JsonElement items = rc.Call("vfs/queue").GetProperty("queue");
                queue = items.EnumerateArray().Select(q => new QueueItem(StatusReader.Number(q, "id"),
                    q.GetProperty("name").GetString() ?? "", StatusReader.Number(q, "size"),
                    checked((int)StatusReader.Number(q, "tries")), q.GetProperty("uploading").GetBoolean())).ToArray();
                known = true;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidDataException)
            { error = e.Message; }
        }
        bool pendingCacheClean = state is not null && status.Running && status.Ready && !status.RecoveryRequired &&
            !status.ProcessUncertain && status.StatusError is null && status.UploadsQueued == 0 && status.UploadsInProgress == 0 &&
            PendingCacheInspector.Observe(state.ActiveConfig.CacheDir).Clean;
        LogTail logs = ErrorLogTail.ReadDetails(status.LogCapture.Captured ? status.LogCapture.Directory : null);
        return new(status, queue, logs.Errors, known, error,
            state?.RestartCount ?? 0, state?.ActiveConfig, IdentityOf(state), StartupRecords: logs.StartupRecords, Rescued: controller.RescueList(), PendingCacheClean: pendingCacheClean, LogReadSucceeded: logs.ReadSucceeded);
    }

    public static MountIdentity? IdentityOf(RuntimeState? state) => state is null ? null : new(state.RclonePid, state.ProcessStartedUtc);
    public static bool IsCurrentForSession(MountController controller, TraySnapshot snapshot, DbxConfig displayedConfig)
    {
        try
        {
            return ControlGate.TryExecute(() => !snapshot.Confirming && IsCurrent(controller, snapshot) &&
                JsonSerializer.Serialize(controller.State.Load()?.ActiveConfig, JsonFiles.Options) == JsonSerializer.Serialize(snapshot.ActiveConfig, JsonFiles.Options) &&
                JsonSerializer.Serialize(controller.Config.Load(), JsonFiles.Options) == JsonSerializer.Serialize(displayedConfig, JsonFiles.Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or TimeoutException)
        { return false; }
    }

    public static bool IsCurrent(MountController controller, TraySnapshot snapshot)
    {
        RuntimeState? current = controller.State.Load();
        return snapshot.Identity == IdentityOf(current) &&
            snapshot.Status.RecoveryRequired == (current?.RecoveryRequired ?? false) &&
            snapshot.Status.StopDisposition == current?.StopDisposition.ToString() &&
            (current is null || current.ConfigPath.Equals(controller.Config.PathName, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record LogTail(IReadOnlyList<string> Errors, IReadOnlyList<string> StartupRecords, bool ReadSucceeded = false);

public static class ErrorLogTail
{
    public const string ProviderError = "rclone \u306e\u51e6\u7406\u3067\u30a8\u30e9\u30fc\u304c\u8a18\u9332\u3055\u308c\u3066\u3044\u307e\u3059\u3002\u72b6\u614b\u3068\u63a5\u7d9a\u5148\u3092\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002";
    public const string StartupNotice = "\u8d77\u52d5\u6642\u306e\u30ea\u30f3\u30af\u6a5f\u80fd\u306e\u78ba\u8a8d\u3067\u3059\uff08\u5185\u5bb9\u306e\u30c0\u30a6\u30f3\u30ed\u30fc\u30c9\u30fb\u4fdd\u5b58\u3067\u306f\u3042\u308a\u307e\u305b\u3093\uff09\u3002";
    private static readonly object Sync = new();
    private static readonly Regex StartupProbe = new(@"^(?:\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? )?ERROR : symlinks not supported without the --links flag: /$", RegexOptions.CultureInvariant);
    public static IReadOnlyList<string> Read(string? logDir) => ReadDetails(logDir).Errors;
    public static LogTail ReadDetails(string? logDir)
    {
        if (logDir is null) return new([], []);
        lock (Sync)
        {
            var errors = new List<string>(); var startup = new List<string>();
            try
            {
                MountLogStore.ValidateFolder(logDir);
                var files = new List<string> { Path.Combine(logDir, "mount.log") };
                // Read newest first, at most one previous rotation, and never a whole unbounded file.
                for (int i = 0; i < files.Count; i++)
                {
                    string path = files[i];
                    PrivateStateFiles.ValidateExisting(path);
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long length = stream.Length;
                        bool partial = length > 256 * 1024;
                        int count = (int)Math.Min(length, 256 * 1024);
                        stream.Seek(length - count, SeekOrigin.Begin);
                        byte[] buffer = new byte[count];
                        stream.ReadExactly(buffer);
                        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(buffer));
                        if (partial) _ = reader.ReadLine();
                        var errorTail = new Queue<string>(); var startupTail = new Queue<string>();
                        int lines = 0;
                        while (lines++ < 32768 && reader.ReadLine() is { } line)
                            if (line.Contains("ERROR", StringComparison.Ordinal))
                            {
                                bool probe = StartupProbe.IsMatch(line);
                                var tail = probe ? startupTail : errorTail;
                                tail.Enqueue(probe ? StartupNotice : ProviderError);
                                while (tail.Count > 20) tail.Dequeue();
                            }
                        errors.AddRange(errorTail.Reverse()); startup.AddRange(startupTail.Reverse());
                    }
                    if (i == 0 && errors.Count < 20 && MountLogStore.PreviousFile(logDir) is { } previous) files.Add(previous);
                }
                return new(errors.Take(20).Reverse().ToArray(), startup.Take(20).Reverse().ToArray(), true);
            }
            catch { return new(errors.Take(20).Reverse().ToArray(), startup.Take(20).Reverse().ToArray(), false); }
        }
    }
}
