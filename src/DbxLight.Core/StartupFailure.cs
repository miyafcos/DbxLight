namespace DbxLight.Core;

public sealed record StartupFailureInfo(string Classification, string Message, string? LogDirectory, string? Location = null, string? NextAction = null);
public static class StartupFailure
{
    private const string Key = "DbxLight.StartupFailure";
    private const string CleanupKey = "DbxLight.CleanupFailures";
    internal static IReadOnlyList<Exception> CleanupFailures(Exception error) =>
        error.Data[CleanupKey] as List<Exception> ?? [];
    internal static void AddCleanupFailure(Exception primary, Exception cleanup, string location = "")
    {
        if (primary.Data[CleanupKey] is not List<Exception> failures)
            primary.Data[CleanupKey] = failures = [];
        failures.Add(cleanup);
        if (Get(primary) is null)
            ForUser(primary, primary.Data["PreparationCancelled"] is true ? "preparation_cancel_persistence_failed" : "local_record_failed", location,
                "\u8d77\u52d5\u3068\u5f8c\u7247\u3065\u3051\u3092\u5b8c\u4e86\u3067\u304d\u307e\u305b\u3093\u3067\u3057\u305f\u3002\u81ea\u52d5\u3067\u306f\u518d\u8a66\u884c\u3057\u307e\u305b\u3093\u3002",
                "\u624b\u5143\u306e\u8a18\u9332\u3092\u6b8b\u3057\u3001\u4fdd\u5b58\u5148\u3092\u78ba\u8a8d\u3057\u3066\u304b\u3089\u660e\u793a\u306e\u64cd\u4f5c\u3067\u3084\u308a\u76f4\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        if (Get(primary) is { } info)
            primary.Data[Key] = info with { Message = info.Message + "\n\u5f8c\u7247\u3065\u3051\u3082\u5b8c\u4e86\u3067\u304d\u307e\u305b\u3093\u3067\u3057\u305f\u3002\u8a18\u9332\u306e\u4fdd\u5b58\u5148\u3092\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002" };
    }
    internal static T RetryableChild<T>(T error) where T : Exception
    { error.Data["DbxLight.RetryableChild"] = true; return error; }
    internal static bool CanRetry(Exception error) =>
        (error is RecoveryComparisonException || error.Data["DbxLight.RetryableChild"] is true) && CleanupFailures(error).Count == 0;

    internal static void Cleanup(Exception? primary, Action cleanup, string location)
    {
        try { cleanup(); }
        catch (PreparationCancelledException accepted) when (primary is not null && primary is not PreparationCancelledException)
        {
            // An accepted stop outranks an in-flight connection failure.
            accepted.Data["DbxLight.PriorFailure"] = primary;
            if (!CanRetry(primary)) AddCleanupFailure(accepted, primary, location);
            foreach (var error in CleanupFailures(primary)) AddCleanupFailure(accepted, error, location);
            throw;
        }
        catch (PreparationCancelledException accepted) when (primary is PreparationCancelledException)
        {
            if (accepted.InnerException is not null) AddCleanupFailure(primary, accepted.InnerException, location);
            foreach (var error in CleanupFailures(accepted)) AddCleanupFailure(primary, error, location);
        }
        catch (Exception error) when (primary is not null) { AddCleanupFailure(primary, error, location); }
    }
    public static StartupFailureInfo? Get(Exception error) => error.Data[Key] as StartupFailureInfo;
    internal static T ForUser<T>(T error, string classification, string location, string reason, string nextAction) where T : Exception
    {
        // Only call sites defining the reason can mark it safe. Never inspect exception text.
        error.Data[Key] = new StartupFailureInfo(classification, reason + "\n場所: " + location + "\n次にすること: " + nextAction, null, location, nextAction);
        return error;
    }
    internal static UnauthorizedAccessException PrivateStorage(string location, string reason) => ForUser(
        new UnauthorizedAccessException(reason), "private_storage_required", location, reason,
        "そのフォルダを使わず、まだ存在しない場所を指定してください。アプリが本人だけの権限で作ります。");
    public static string PublicMessage(Exception error) => Get(error) is { } own ? "[" + own.Classification + "] " + own.Message :
        "処理を完了できませんでした。設定と手元の記録を確認してください。";
    internal static void Annotate(Exception error, DbxConfig? config, string configPath)
    {
        string? folder = null;
        try { if (config is not null) folder = MountLogStore.Folder(configPath, config); } catch { }
        if (Get(error) is { } own)
        { error.Data[Key] = own with { LogDirectory = folder }; return; }
        string code = config is not null && string.IsNullOrWhiteSpace(config.Remote) ? "remote_not_configured" :
            config is not null && (Path.IsPathFullyQualified(config.RclonePath) && !File.Exists(config.RclonePath) ||
                error is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 }) ? "rclone_not_found" :
            error is TimeoutException ? "timeout" :
            config is not null && !MountPaths.IsAvailable(config.MountPoint) ? "mount_point_in_use" : "other";
        string description = code switch
        {
            "remote_not_configured" => "設定に接続先がありません。",
            "rclone_not_found" => "rclone が見つかりません。",
            "mount_point_in_use" => "ドライブの文字または割り当て先が使用中です。",
            "timeout" => "起動の準備が時間切れになりました。",
            _ => "起動を完了できませんでした。設定と手元の記録を確認してください。"
        };
        error.Data[Key] = new StartupFailureInfo(code, description + " 記録のフォルダ: " + (folder ?? "記録を取れていません。"), folder);
    }
}
