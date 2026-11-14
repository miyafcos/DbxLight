namespace DbxLight.Core;

public sealed record AgentGuideResult(bool Ok, string Code, string Message, string MountPoint, bool ReadOnly,
    bool RecoveryRequired, bool ProcessUncertain, string Markdown);

public static class AgentGuide
{
    public static AgentGuideResult Create(MountController controller)
    {
        var observed = controller.ReadAgentRuntime();
        DbxStatus status = observed.Status;
        DbxConfig config = ((status.Running || status.RecoveryRequired || status.ProcessUncertain)
            ? observed.Runtime?.ActiveConfig : null) ?? controller.Config.Load();
        config = config.Validate();
        if (string.IsNullOrWhiteSpace(config.Remote))
            return new(false, "not_configured", "Remote is not configured. Check --config or DBXL_CONFIG.",
                "未設定", true, status.RecoveryRequired, status.ProcessUncertain,
                "# DbxLight を AI の道具から使う\n設定がまだ無い。--config か環境変数 DBXL_CONFIG を確かめる。\n割り当て先は未設定。人に設定を確認する。\n");
        string root = MountPaths.CanonicalMount(config.MountPoint);
        string mount = MountPaths.IsDriveRoot(root) ? root[..2].ToUpperInvariant() : "未設定（ドライブ文字なし）";
        bool readOnly = !config.WriteAllowedRemotes.Contains(config.Remote, StringComparer.Ordinal);
        bool safe = !status.RecoveryRequired && !status.ProcessUncertain && status.StatusError is null && (!status.Running || status.Ready);
        string code = safe ? "guide" : status.ProcessUncertain ? "process_uncertain" : status.RecoveryRequired ? "recovery_required" : "status_unknown";
        string markdown = string.Join('\n', new[]
        {
            "# DbxLight を AI の道具から使う",
            "割り当て先: " + mount,
            "権限: " + (readOnly ? "読み取り専用。新しく保存できない。" : "書き込み可。保存後に上がったことを確認する。"),
            safe ? "操作前に dbxl status --json で稼働状態と回復状態を確認する。" : "回復またはプロセスの確認がまだ。人に知らせ、自分で解除しない。",
            "- フォルダの中身を再帰で検索しない。先に dbxl ls / dbxl find で名前から当たりを付け、要るファイルだけ開く。",
            "- rg、grep、再帰読み取りを割り当て先全体にかけない。内容の検索は選んだファイルだけにする。",
            "- ファイルを閉じてから dbxl wait <パス> --json を呼び、待っている間に同じファイルを保存し直さない。0 は中身の一致までは保証しない。終了値 0 を確認してから「保存した」と報告する。",
            "- wait が 0 以外なら、雲へ上がったことを確認できていないと報告し、手元の保存を保つ。",
            "- busy は未確認（成功でも失敗でもない）。数秒おいて同じコマンドを呼び直す。",
            "- 見えるはずのファイルが無ければ、無いと言う前に dbxl refresh <フォルダ> --json をして再度見る。",
            "- 読み取り専用では新しく保存できない。保存先を手元の別の場所にするか、人に知らせる。前から手元に残る未送信が送られることはある。",
            "- 表示しないフォルダは通常の一覧・検索で見えない。必要なら人に知らせ、自分で設定を変えない。",
            "- 回復がまだ・プロセス不明のときは人に知らせる。回復の解除、強制操作を自分で行わない。",
            "- 取り出した物は dbxl rescue list --json で理由と保存先を確認して人へ伝える。必要な内容は利用者が保存先からドライブの元の場所へ戻す。名前によっては自動で上げ直さず取り出す。くわしくは docs/cli.md。",
            "- " + RecoveryText.WriteNotice.Replace('\n', ' '),
            "- 止める・つなぎ直す・設定を変える・強制のコマンドは、人に頼まれたときだけ使う。",
            "- 稼働中の一覧・検索・stat は全角の記号も Windows が見せる名前のまま返す。返った path を使う。",
            "- skippedUnsafe>0 は安全でない名前を省いた件数。そのフォルダで「無い」と断言せず、人に知らせる。",
            "- truncated=true は continuation の案内に従う（offset があれば --offset）。depthLimited=true なら必要なフォルダへ絞って深さを増やす。",
            "- timeLimited=true なら、同じ引数に continuation.offset の --offset を付けて続ける。範囲が分かるなら --in で絞る。",
            "| コマンド | 用途 |",
            "| --- | --- |",
            "| ls <パス> --json | 中身を落とさず一覧（既定 1 階層・1000 件） |",
            "| find <語> --in <パス> --json | 名前だけを検索（既定 深さ 8・1000 件） |",
            "| stat <パス> --json | 大きさ・時刻・キャッシュ・上げの状態 |",
            "| wait <パス> --json / wait --all --json | 保存後に上がったことを照合 |",
            "| refresh <フォルダ> --json | 一覧を取り直す |",
            "| resolve <パス> --json | 手元・雲の相対パス・Web URL の対応 |",
            "| agent-guide / status --json | この手引き / 状態（ok と回復の項目も読む） |",
            "",
            "| 終了値 | 新コマンドの意味 |",
            "| --- | --- |",
            "| 0 | 成功。wait は雲側の情報を照合済み |",
            "| 2 | 使い方の誤り |",
            "| 3 | まだ・時間切れ・未確認・回復未完了・プロセス不明 |",
            "| 4 | 処理または上げの失敗 |",
            "| 5 | 読み取り専用で新しく保存できない |",
            "| 6 | 動いていない |",
            "",
            "既存コマンドは通常失敗 1、上げ待ちの拒否 3。status は終了値だけで判断せず、JSON の ok・recoveryRequired・processUncertain も読む。"
        });
        return new(safe, code, safe ? "Agent guidance generated." : "Verification is incomplete; tell a person.",
            mount, readOnly, status.RecoveryRequired, status.ProcessUncertain, markdown);
    }
}
