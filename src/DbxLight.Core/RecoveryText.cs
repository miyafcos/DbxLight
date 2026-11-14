using System.Globalization;

namespace DbxLight.Core;

/// <summary>Human-readable text is independent of persisted enum names and recovery decisions.</summary>
internal static class RecoveryText
{
    internal const string PreflightNotice = "前回止めたときに上げ残した物だけは、起動の直前 (30 秒以内) に雲の側と比べ、変わっていない物だけを上げます。変わっていた物や危ない物は、手元に取り出して残し、自動では戻しません。保存先で中身を確かめ、雲の側と見比べてから、必要な内容を自分で戻してください。";
    internal const string PreservationNotice = "止めている間に変わった物や危ない物は、取り出して残します。保存先で内容を確認し、必要な内容をドライブの元の場所へ自分で戻してください。";
    internal const string ConflictNotice = "このアプリは、公式の Dropbox アプリのような競合コピーを作りません。ほかの人と同じファイルを同じ時間帯に直すと、あとから保存した側の内容で上書きになり、先に保存した人の変更が失われることがあります。";
    internal const string UploadDelayNotice = "保存したファイルは、少し遅れて Dropbox に上がります。上がるまでの時間は、件数と大きさで変わります。";
    internal const string OfficialClientNotice = "公式の Dropbox アプリと、同じフォルダへ同時に書き込まないでください。";
    internal static string WriteNotice => string.Join('\n', ConflictNotice, UploadDelayNotice, PreflightNotice, OfficialClientNotice);
    private const string PendingPrefix = "\u307e\u3060\u4e0a\u304c\u3063\u3066\u3044\u306a\u3044\u30d5\u30a1\u30a4\u30eb\u304c ";
    private const string CleanPrefix = "\u30ad\u30e3\u30c3\u30b7\u30e5\u3092\u691c\u67fb\u3057\u3001";
    private const string UnknownPrefix = "\u30ad\u30e3\u30c3\u30b7\u30e5\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3067\u3057\u305f\u3002";

    internal static string CacheClean(int entries, string path) => CleanPrefix +
        $"\u307e\u3060\u4e0a\u304c\u3063\u3066\u3044\u306a\u3044\u30d5\u30a1\u30a4\u30eb\u306f 0 \u4ef6\u3067\u3057\u305f\uff08\u7ba1\u7406\u60c5\u5831 {entries} \u4ef6\u3092\u78ba\u8a8d\uff09\u3002\n\u691c\u67fb\u3057\u305f\u5834\u6240: {path}";
    internal static string CachePending(int files, string path) => PendingPrefix +
        $"{files} \u4ef6\u3001\u6b21\u306e\u5834\u6240\u306b\u6b8b\u3063\u3066\u3044\u307e\u3059\u3002\n{path}";
    internal static string CacheUnknown(string reason, string path) => UnknownPrefix +
        $"\u7406\u7531: {reason}\n\u691c\u67fb\u3057\u305f\u5834\u6240: {path}";

    internal static string Explain(RuntimeState state)
    {
        var lines = new List<string>
        {
            "\u56de\u5fa9\u304c\u307e\u3060\u3067\u3059\u3002" + StopReason(state),
            CacheExplanation(state.CacheInspection, state.ActiveConfig.CacheDir),
            "\u30ea\u30e2\u30fc\u30c8: " + state.ActiveConfig.Remote,
            "rclone \u306e\u8a2d\u5b9a\u30d5\u30a1\u30a4\u30eb: " + ShowPath(state.ActiveConfig.RcloneConfigPath),
            "\u30ad\u30e3\u30c3\u30b7\u30e5\u306e\u4fdd\u5b58\u5148: " + ShowPath(state.ActiveConfig.CacheDir)
        };
        lines.Add("回復がまだの間は、同じ名前の接続先の中身を別のアカウントへ付け替えないでください。設定ファイル内部の変更は検出できません。");
        if (state.RescueRecords.Length > 0)
            lines.Add($"取り出した物が {state.RescueRecords.Length} 件あります。一覧で理由と保存先を確認してください。");
        if (state.StopDisposition == StopDisposition.ProcessUnknown)
            lines.Add((state.UncertainProcess ?? new ProcessUncertainty(state.RclonePid, null)).Explain());
        else
        {
            lines.Add(RecoveryDestination.Resolved(state.ActiveConfig)
                ? "\u540c\u3058\u56de\u5fa9\u5148\u3067\u518d\u958b\u3057\u3001\u4e0a\u3052\u5f85\u3061\u3068\u4e0a\u3052\u3066\u3044\u308b\u9014\u4e2d\u304c\u4e21\u65b9 0 \u3067\u3001\u30ad\u30e3\u30c3\u30b7\u30e5\u306b\u3082\u672a\u9001\u4fe1\u304c\u306a\u3044\u3068\u78ba\u8a8d\u3059\u308b\u3068\u89e3\u3051\u307e\u3059\u3002"
                : "\u5143\u306e\u56de\u5fa9\u5148\u304c\u7d76\u5bfe\u30d1\u30b9\u3067\u8a18\u9332\u3055\u308c\u3066\u3044\u306a\u3044\u305f\u3081\u3001\u518d\u958b\u3067\u304d\u307e\u305b\u3093\u3002\u5143\u306e\u5834\u6240\u3092\u63a8\u6e2c\u305b\u305a\u3001\u8a18\u9332\u3092\u5fa9\u5143\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
            lines.Add("\u672a\u9001\u4fe1\u304c\u6b8b\u308b\u53ef\u80fd\u6027\u3092\u4e86\u627f\u3057\u3066\u56fa\u5b9a\u3092\u89e3\u9664\u3059\u308b\u5834\u5408\u306f\u3001CLI \u306e recovery clear --i-understand-pending-may-be-lost \u3092\u5b9f\u884c\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        }
        lines.Add(WriteNotice);
        return string.Join('\n', lines);
    }

    private static string StopReason(RuntimeState state) => state.StopDisposition switch
    {
        StopDisposition.Forced => "\u5f37\u5236\u3067\u6b62\u3081\u307e\u3057\u305f\u3002",
        StopDisposition.UnexpectedExit => "\u4e88\u671f\u305b\u305a\u7d42\u4e86\u3057\u307e\u3057\u305f\u3002",
        StopDisposition.StartupFailed => "\u8d77\u52d5\u306b\u5931\u6557\u3057\u307e\u3057\u305f\u3002",
        StopDisposition.ProcessUnknown => "\u30d7\u30ed\u30bb\u30b9\u306e\u72b6\u614b\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002",
        StopDisposition.StopUncertain when IsPending(state.CacheInspection) => "\u6b62\u3081\u305f\u3042\u3068\u306e\u78ba\u8a8d\u3067\u3001\u307e\u3060\u4e0a\u304c\u3063\u3066\u3044\u306a\u3044\u30d5\u30a1\u30a4\u30eb\u304c\u898b\u3064\u304b\u308a\u307e\u3057\u305f\u3002",
        StopDisposition.StopUncertain => "\u6b62\u3081\u305f\u3042\u3068\u306e\u78ba\u8a8d\u3092\u7d42\u3048\u3089\u308c\u307e\u305b\u3093\u3067\u3057\u305f\u3002",
        StopDisposition.Running => "\u4e0a\u3052\u7d42\u308f\u3063\u305f\u3053\u3068\u3092\u307e\u3060\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002",
        StopDisposition.Clean => "\u505c\u6b62\u5f8c\u306e\u78ba\u8a8d\u304c\u307e\u3060\u7d42\u308f\u3063\u3066\u3044\u307e\u305b\u3093\u3002",
        StopDisposition.ClearedByUser => "\u672a\u9001\u4fe1\u304c\u6b8b\u308b\u53ef\u80fd\u6027\u3092\u4e86\u627f\u3057\u3066\u56de\u5fa9\u306e\u56fa\u5b9a\u3092\u89e3\u9664\u3057\u307e\u3057\u305f\u3002",
        _ => "\u524d\u306e\u505c\u6b62\u72b6\u614b\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002"
    };

    private static string ShowPath(string? path) => path is null ? "\u65e2\u5b9a\u306e\u8a2d\u5b9a\u30d5\u30a1\u30a4\u30eb" :
        Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path + "\uff08\u5143\u306e\u7d76\u5bfe\u30d1\u30b9\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\uff09";

    private static bool TryLegacyCount(string text, string prefix, string suffix, out int count, out string tail)
    {
        count = 0; tail = "";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int end = text.IndexOf(suffix, prefix.Length, StringComparison.Ordinal);
        if (end < 0 || !int.TryParse(text.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out count)) return false;
        tail = text[(end + suffix.Length)..];
        return true;
    }

    private static bool IsPending(string? text) => text?.StartsWith(PendingPrefix, StringComparison.Ordinal) == true ||
        (text is not null && TryLegacyCount(text, "Dirty metadata: ", " pending files in ", out int count, out _) && count > 0);

    private static string CacheExplanation(string? text, string path)
    {
        if (text is null) return CacheUnknown("\u524d\u56de\u306e\u691c\u67fb\u7d50\u679c\u306f\u8a18\u9332\u3055\u308c\u3066\u3044\u307e\u305b\u3093\u3002", path);
        if (text.StartsWith(PendingPrefix, StringComparison.Ordinal) || text.StartsWith(CleanPrefix, StringComparison.Ordinal) || text.StartsWith(UnknownPrefix, StringComparison.Ordinal)) return text;
        if (TryLegacyCount(text, "Dirty metadata: ", " pending files in ", out int pending, out string tail))
            return CachePending(pending, tail.EndsWith('.') ? tail[..^1] : tail);
        if (TryLegacyCount(text, "Verified metadata: ", " clean entries.", out int entries, out _)) return CacheClean(entries, path);
        foreach (var reason in new (string Prefix, string Japanese)[]
        {
            ("Unknown metadata fields: ", "\u7ba1\u7406\u60c5\u5831\u306b\u3001\u5bfe\u5fdc\u3057\u3066\u3044\u306a\u3044\u9805\u76ee\u304c\u3042\u308a\u307e\u3059\u3002"),
            ("Unknown metadata time: ", "\u7ba1\u7406\u60c5\u5831\u306e\u65e5\u6642\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002"),
            ("Unknown metadata values: ", "\u7ba1\u7406\u60c5\u5831\u306e\u5024\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002"),
            ("Unknown metadata ranges: ", "\u7ba1\u7406\u60c5\u5831\u306e\u7bc4\u56f2\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002")
        })
            if (text.StartsWith(reason.Prefix, StringComparison.Ordinal)) return CacheUnknown(reason.Japanese + "\uff08" + text[reason.Prefix.Length..] + "\uff09", path);
        if (text == "Cache data and metadata do not match.") return CacheUnknown("\u30d5\u30a1\u30a4\u30eb\u3068\u7ba1\u7406\u60c5\u5831\u304c\u5bfe\u5fdc\u3057\u3066\u3044\u307e\u305b\u3093\u3002", path);
        if (text.StartsWith("Cache inspection is unknown:", StringComparison.Ordinal))
            return CacheUnknown("\u524d\u56de\u306f\u8aad\u307f\u53d6\u308a\u306b\u5931\u6557\u3057\u307e\u3057\u305f\u3002\u65e7\u5f62\u5f0f\u306e\u8a18\u9332\u304b\u3089\u8a73\u3057\u3044\u7406\u7531\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002", path);
        return CacheUnknown("\u524d\u56de\u306e\u691c\u67fb\u7d50\u679c\u306e\u8a18\u9332\u5f62\u5f0f\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002\u5143\u306e\u8a18\u9332\u306f\u4fdd\u6301\u3057\u3066\u3044\u307e\u3059\u3002", path);
    }
}
