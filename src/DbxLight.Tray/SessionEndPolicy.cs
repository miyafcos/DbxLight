using DbxLight.Core;

namespace DbxLight.Tray;

public sealed record SessionEndDecision(bool Block, string Reason);

public static class SessionEndPolicy
{
    public static SessionEndDecision Decide(DbxConfig config, DbxStatus status, bool stoppedClean, bool permissionUncertain,
        bool pendingCacheClean = false)
    {
        if (permissionUncertain || status.ProcessUncertain || status.RecoveryRequired || status.StatusError is not null || status.ErroredFiles > 0)
            return new(true, "DbxLight: 未アップロードのファイルの確認がまだです。状態と回復先を確認してください。");
        if (status.UploadsQueued > 0 || status.UploadsInProgress > 0)
            return new(true, "DbxLight: アップロード待ちを送信しています。終わるまでお待ちください。");
        if (stoppedClean) return new(false, "");
        bool readOnly = status.Running ? status.ReadOnly == true : TrayPresentation.ReadOnly(config);
        bool noPending = status.UploadsQueued == 0 && status.UploadsInProgress == 0 &&
            status.ErroredFiles == 0 && status.QueueSupported && status.Ready && pendingCacheClean;
        if (readOnly && noPending) return new(false, "");
        if (status.UploadsQueued is null || status.UploadsInProgress is null || !status.QueueSupported || !pendingCacheClean)
            return new(true, "DbxLight: 未アップロードのファイルの確認がまだです。状態と回復先を確認してください。");
        return new(true, "DbxLight: アップロード待ちを再確認し、安全な停止を確認しています。");
    }
}
