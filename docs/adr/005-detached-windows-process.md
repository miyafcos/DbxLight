# 005 Windows の常駐プロセスの起動

Process.Start で出力をリダイレクトしても、rclone が親の別の出力ハンドルを継承し、start の呼び出し側が EOF を待ち続ける動きを確認した。
CreateProcessW に inheritHandles=false と CREATE_NO_WINDOW / CREATE_UNICODE_ENVIRONMENT を指定する。
コマンドラインは Windows の引用規則で組み、認証は専用の環境ブロックだけに入れる。
作成直後に実行イメージの情報がまだ取れない実測もあったため、短い待ちで取得し、失敗時には自分の作成ハンドルで停止する。
通常の停止対象は PID・時刻・実行ファイルを照合し、起動失敗の後始末にも第三者の PID を使わない。
