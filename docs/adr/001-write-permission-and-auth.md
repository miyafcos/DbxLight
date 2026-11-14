# 001 書き込みの許可と rc の認証

remote と writeAllowedRemotes の完全一致だけを、RcloneArguments.Mount で判定する。
既定では必ず --read-only を渡す。部分一致や大文字小文字の違いで許可が広がることを避けるため。
rc の認証は起動ごとに乱数で作り、RCLONE_RC_USER / RCLONE_RC_PASS に渡す。
継承した RCLONE_* は外す。引数や config show に認証を出さず、継承したオプションで守りが変わることを防ぐ。
