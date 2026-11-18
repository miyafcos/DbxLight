namespace DbxLight.Core;

internal sealed class FolderOperationException(string code, string message, int exitCode, bool outcomeUnknown = false)
    : Exception(message)
{
    internal string Code { get; } = code;
    internal int ExitCode { get; } = exitCode;
    internal bool OutcomeUnknown { get; } = outcomeUnknown;

    internal static FolderOperationException For(string code, bool outcomeUnknown = false)
    {
        (int exit, string message) = code switch
        {
            "not_configured" => (2, "Dropbox の接続先を設定してください。"),
            "invalid_config" => (2, "DbxLight の設定を確認してください。"),
            "unsupported_remote" or "unsupported_namespace" => (2, "この接続先には対応していません。通常の Dropbox リモートを選んでください。"),
            "copy_path_prefix_unknown" => (3, DropboxCopyPaths.PrefixHelp),
            "encrypted_config" or "unsupported_config" => (3, "この rclone 設定では認証を利用できません。DbxLight からログインしてください。"),
            "missing_scope" => (3, "Dropbox の共有に必要な権限がありません。もう一度ログインしてください。"),
            "auth_unavailable" => (3, "Dropbox の認証を確認できません。もう一度ログインしてください。"),
            "expired_access_token" or "auth_refresh_unavailable" => (3, "Dropbox の認証を更新できませんでした。接続を確認し、失敗が続く場合はログインし直してください。"),
            "mount_unverified" => (3, "稼働中のマウントと Dropbox の接続先を確認できません。旧版でマウントしたドライブやフォルダは、現行版でマウントし直してから共有状態を読み直してください。"),
            "web_location_unknown" => (3, "Dropbox の Web 上の場所を確認できません。Dropbox の画面で対象の場所を開いてください。"),
            "selection_unverified" => (3, "表示されたフォルダと Dropbox 上の対象が一致するか確認できません。マウントし直して、表示を更新してからフォルダを選び直してください。"),
            "not_visible" => (3, "選んだフォルダをマウント内で確認できません。エクスプローラーで表示されるフォルダを選んでください。"),
            "ambiguous_name" => (2, "Dropbox 上の名前を特定できません。Dropbox の画面で対象の場所と名前を確認してください。"),
            "selection_limit" => (2, "選択した項目が多すぎるか、パスの合計が長すぎます。選択を分けてコピーしてください。"),
            "confirmation_required" => (2, "招待の確認を先に実行し、返った確認値を --confirm に指定してください。"),
            "confirmation_changed" => (3, "確認値が現在の招待内容と一致しません。共有状態と招待先を確認し直してください。"),
            "not_found" => (4, "Dropbox にフォルダが見つかりません。エクスプローラーで場所を確認してください。"),
            "not_folder" => (2, "フォルダを指定してください。"),
            "namespace_root" => (2, "Dropbox の最上位は指定できません。その中のフォルダを選んでください。"),
            "inside_shared_folder" => (4, "上位のフォルダが共有済みです。共有フォルダを入れ子にできません。共有する場所を見直してください。"),
            "contains_shared_folder" => (4, "このフォルダの下に共有済みのフォルダがあります。共有フォルダを入れ子にできません。共有する場所を見直してください。"),
            "invalid_email" => (2, "メールアドレスの形式を確認してください。"),
            "already_member" => (4, "すでにメンバーまたは招待済みの相手がいます。共有の一覧を確かめ、招待先から外してください。"),
            "team_policy" => (4, "チームの設定により共有できません。チームの管理者に共有先と設定を確認してください。"),
            "permission_denied" => (4, "このフォルダを共有する権限がありません。フォルダの所有者またはチームの管理者に確認してください。"),
            "email_unverified" => (4, "Dropbox に登録したメールアドレスの確認が必要です。Dropbox で確認を済ませてください。"),
            "sharing_changed" => (3, "接続先またはフォルダの状態が変わりました。共有の状態と招待先を確認し直してください。"),
            "network_error" => (4, "Dropbox との通信に失敗しました。接続と現在の共有状態を確認してください。"),
            "timeout" => (3, "Dropbox の応答を時間内に確認できませんでした。現在の共有状態を確認してください。"),
            "cancelled" => (3, "操作を中止しました。現在の共有状態を確認してください。"),
            "rate_limited" => (3, "Dropbox が要求を制限しています。時間をおいて共有状態を確認してください。"),
            "invalid_response" => (4, "Dropbox の応答を確認できませんでした。現在の共有状態を確認してください。"),
            _ => (4, "Dropbox で処理できませんでした。Dropbox の共有画面で状態と権限を確認してください。")
        };
        return new(code, message, exit, outcomeUnknown);
    }
}
