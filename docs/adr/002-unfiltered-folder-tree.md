# 002 表示しないフォルダも取得する一覧

rclone v1.74.3 をローカルフォルダでマウントして実測した。
hidden を除外した状態で operations/list は visible だけを返し、別プロセスの lsjson --dirs-only は hidden / visible の両方を返した。
FolderBrowser.List は絞り込みなしの別プロセスで lsjson を実行し、設定から表示しない印を付ける。
一覧の対象は指定場所の直下だけ。認証設定の内容は読まず、設定ファイルのパスだけを rclone に渡す。
証跡は _report/evidence/probe.txt。
