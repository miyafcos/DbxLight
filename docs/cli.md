# dbxl CLI

読者は、使い方を確認する人と AI の道具。

CLI は確認の問いや窓を出さない。rclone の設定・FTP・SFTP のパスワードの問いも無効にする。
ファイルを開く前に名前の一覧で絞り、保存後は wait で雲への反映を確認する。

## 前の版から変わった点

snapshot list / metadata / fetch を追加した。マウントと独立した API の読み出しで、全一覧・cursor からの差分・存在照会・rev 指定取得を行う。途中の失敗は終了値0にしない。

回復は取り出して残す処理へ変わった。status.recoveryReuploads、rescue list の result.recoveredUploads、個別の送信段階・手元の内容ハッシュ・送信後の確認項目を削除した。旧状態や旧記録にこれらがあっても無視して読める。起動の印のOS起動時刻も使わない。

rescue list は未確認の result.items だけを返し、rescue acknowledge <id> で確認済みにできる。原本は残す。status.unregisteredCredentialFiles は登録のない鍵の作業ファイルの場所を返す。専用の鍵は設定ファイルごとの置き場へ分けた。

UTF-8とnullの扱い（次の2段落）は維持する。

--json と config show は UTF-8（BOM なし）で、日本語・全角記号をそのまま返す。JSON の制御文字と HTML の特殊文字（< > & など）はエスケープする。

フォルダの大きさ・更新時刻を取得できない場合は size / modifiedUtc を null で返す。以前の -1 と仮の 2000-01-01 は返さない。ファイルの0バイトと、取得できた日時はそのまま返す。

## 共通の指定

~~~text
dbxl [--config <設定JSON>] [--state <状態JSON>] <コマンド> [引数] [--json]
~~~

設定は --config、DBXL_CONFIG、%APPDATA%\DbxLight\config.json の順。状態は --state、DBXL_STATE、%LOCALAPPDATA%\DbxLight\state.json の順。
状態の親はアプリに新規作成させる本人専用フォルダを使う。既存の安全でない権限の親は拒否する。状態には認証があるため、中身を手引きや報告に転載しない。

マウントを使う新コマンドは雲の相対パス（例: 資料/原稿.txt）と、割り当て先内の完全な手元のパス（例: T:\資料\原稿.txt または T:/資料/原稿.txt）を受ける。
日本語・空白・長いパスは引数を引用する。根は "" または T:\。片側だけの根、割り当て先外、ドライブ相対、デバイスパス、相対の .. は拒否する。
resolve と stat は「Web のリンクをコピー」で得た HTTPS の Dropbox /home/ URL も受ける。口座と名前空間から Web 上の場所を確認できる場合は URL を返す。確認できない場合、相対パスや手元のパスを使うと webUrl は null になる。

--json は成功・失敗とも標準出力に JSON を一つだけ返す（複数行に整形する）。記録・打ち切りの案内は標準エラー。
--format json の agent-guide も同じ契約。--json は --format markdown より優先する。
失敗の共通項目は次のとおり。コマンドに応じ status / items / waitedSeconds / checks を付ける。

~~~json
{"ok": false, "code": "usage", "message": "説明"}
~~~

終了値はシェルの終了値を読む。例（見本のドライブと名前）:

~~~powershell
dbxl ls "T:\資料" --max 20 --json
dbxl find "原稿" --in "資料" --type file --json
dbxl stat "資料/原稿.txt" --json
# 選んだ1件を開いて保存したあと
dbxl wait "T:\資料\原稿.txt" --timeout 120 --json
$LASTEXITCODE
~~~

## 新コマンド

| コマンド | 動き・既定値 |
| --- | --- |
| snapshot list <雲の相対フォルダ> --entries <NDJSON> --cursor-out <cursor> [--cursor-file <cursor>] [--limit N] [--max-rps N] [--deadline-seconds N] --json | 再帰の全一覧、または cursor からの差分。全ページ成功時だけ cursor を確定 |
| snapshot metadata --paths-file <JSONL> --output <JSONL> [--max-rps N] [--deadline-seconds N] --json | パスごとの存在を API で照会 |
| snapshot fetch --plan <JSONL> --store <フォルダ> [--parallel N] [--failures <JSONL>] [--max-rps N] [--deadline-seconds N] --json | rev を指定して本体を取得し、長さとハッシュを照合 |
| ls <パス> [--recursive] [--depth N] [--max N] [--offset N] [--include-hidden] [--time-limit 秒] [--json] | 子の名前・種類・大きさ・時刻・キャッシュ有無。既定は深さ1、1000件。recursive は深さ32。depth は明示値を優先 |
| find <語> [--in <パス>] [--type file\|dir] [--depth N] [--max N] [--offset N] [--time-limit 秒] [--json] | 内容を読まず名前の部分一致。NFKC と invariant の大文字化で大小・全半角・結合文字を吸収。既定は根、深さ8、1000件 |
| stat <パスまたはURL> [--time-limit 秒] [--json] | 1件の一覧情報・キャッシュ有無・待ち/途中/失敗・Web URL。idle はキューにない意味で、上がったという確認ではない |
| wait <パス>... [--timeout <秒>] [--json] | 指定した閉じたファイルが上がるまで待つ。既定120秒、小数可。上限86400秒、1000パス |
| wait --all [--timeout <秒>] [--json] | 今のマウントの待ち・途中・未送信の管理情報から対象を集めて待つ。パスと --all は併用しない。1000件を超えたら3で返し、返った items を明示した小さい組に分けて待ち、そのあと --all を繰り返す |
| refresh [<パス>] [--json] | vfs/refresh で指定フォルダの一覧だけを取り直す。省略は根。再帰しない。待ちを捨てたり、ファイルのキャッシュを消したりしない |
| resolve <パスまたはURL> [--json] | localPath / remotePath / webUrl を互いに引く。存在の確認や共有リンクの作成はしない |
| agent-guide [--format markdown\|json] [--json] | 今の権限とドライブ文字だけを反映した40行以内の手引き。リモート名・フォルダ名・ユーザー名を設定から入れない |

ls / find の max は1〜10000、depth は1〜128、offset は0〜1000000。
表示しないフォルダは通常の ls / find / stat / wait / refresh / resolve では除外・拒否する。ls --include-hidden は明示したときだけ含める。除外パターンは引き続き使う。

稼働中の通常一覧は Windows のフォルダ列挙で、同じ VFS の一覧キャッシュを使う（source=mount）。全角の記号を含む名前も、Windows が見せる名前のまま name / path に返す。
明示した非表示の一覧だけは同じ rc から元のリモートへ向ける（source=rc-remote）。
停止中は別プロセスの rclone lsjson（source=lsjson）。マウント・rc・停止中の一覧は同じ除外パターンを大文字小文字を区別せずに適用する。動いている rc の失敗を別プロセスで黙って埋めない。

### 一覧と検索の JSON

ls は ok / source / entries / truncated / continuation / depth / depthLimited / status / skippedUnsafe / timeLimited。find は同じ項目に query を加える。
entries の形は次のとおり（日時は UTC。フォルダの size / modifiedUtc は、取得できなければ null）:

~~~json
{"name":"原稿.txt","path":"資料/原稿.txt","type":"file","size":1234,"modifiedUtc":"2026-01-01T00:00:00+00:00","cached":false,"hidden":false}
~~~

cached=true はキャッシュ本体が存在する意味。全内容を取得済みという保証はない。rclone v1.74.3 の Windows キャッシュで実測した名前の変換を使う。停止中や、変換を確認できない記号・末尾の空白やピリオド・曖昧な引用符の並びを含む場合は null。ディレクトリは false。
キャッシュ名の変換を確認できない場合、未送信の管理情報も unknown とし、wait は0を返さない。キューが空でも、管理情報の名前を確認できない wait --all は3。
一覧・stat・名前検索は割り当て先の内容を開かず、ハッシュや MIME の内容判定も要求しない。
Windows の単独ファイル照会がキャッシュ項目を作るため、稼働中の stat と wait の手元側は親フォルダの列挙から1件を選ぶ。列挙が返した属性・大きさ・時刻だけを使い、マウント内のファイルに追加の照会をしない。リンクや再解析ポイントはたどらない。
内部の列挙は長いパス形式を使い、260文字を超えるパスや末尾の空白・ピリオドを保つ。返った path を Windows で直接開く際も、必要なら `\\?\` 形式を使う。CLI には雲の相対パスを渡す。

skippedUnsafe は、区切り文字・制御文字・`.`・`..` など安全でない名前を結果から省いた件数。判定は従来と同じで、正常な項目は返す。標準エラーには件数とフォルダの相対パスだけを1行ずつ出し、省いた名前は出さない。
skippedUnsafe>0 のフォルダでは、一覧や stat に見当たらなくても「無い」と断言せず、人に知らせる。値は各コマンドで実際に確認したフォルダの合計で、未走査の範囲は含まない。

件数の上限で打ち切った場合は truncated=true、timeLimited=false、continuation={"offset":次の数,"hint":"続け方"}。
同じ引数の --offset をその数に置き換える。offset の上限に達した場合は continuation.offset=null とし、子フォルダへ範囲を絞る案内を hint に返す。find は1回10000項目の走査でも打ち切るため、entries が0件でも continuation があり得る。
depthLimited=true は境界のフォルダの中を未確認という意味。指定範囲の結果に truncated=false があっても、その先が無いとは判断しない。必要な --in / パスに絞って深さを増やす。
木を読む find / ls --recursive / ls --depth 2 以上は --time-limit <秒> で走査時間を指定する。小数可、既定45秒、範囲1〜3600秒。
時間が来たら確定した範囲を終了値0で返し、truncated=true / timeLimited=true と continuation を付ける。wait --timeout の時間切れ（3）とは区別する。
find の entries が0件でも続きがある。hint を読み、続きが前に進んだ場合は同じ引数の --offset を continuation.offset に置き換えて繰り返す。範囲が分かる場合は --in で絞る。
一階層の ls（明示 depth=1 を含む）と stat も --time-limit を受け付け、同じ範囲を検証するが、単一フォルダの読み込みを時間で切らない。
走査には offset までの読み直しも含む。一覧が温まったフォルダを再利用するため続きは速くなるが、読み直し自体が長い場合は同じ offset が返ることもある。
時間切れで新しい項目が0件かつ continuation.offset が指定した offset と同じ場合は、--time-limit を延ばすか、--in で範囲を狭めてから、その offset で続ける。
時間到達後は新しい読み込みを始めず、開始済みの最大4フォルダが終わるまで待つ。指定秒数は厳密な返却期限ではなく、一つの遅いフォルダや状態の確認時間が加わり得る。
ローカルで初回の各フォルダに180msの待ちを入れた測定では、1秒指定の打ち切りは1.082〜1.184秒で戻った（超過は最大0.184秒）。既読フォルダには待ちを入れず、続きの読み直しを確認した。雲の実環境の超過幅は未測定で、この数値を保証上限にはしない。

~~~powershell
dbxl find "原稿" --in "資料" --time-limit 45 --json
# timeLimited=true のとき、返った continuation.offset を使う（以下は見本）
dbxl find "原稿" --in "資料" --time-limit 45 --offset 120 --json
~~~

ls / find / stat は返却前（件数・時間による打ち切りも含む）に PID・起動時刻・動作中の設定を再確認する。入れ替わったら結果を返さず mount_changed（3）。列挙の起点と祖先も読み直すたびに確認し、再解析ポイントは拒否する。

offset は固定された一覧を前提とする。途中で一覧が変わると重複・飛びがあり得る。
再帰のフォルダ読み込みは最大4本を先読みする。名前順・深さ優先で結果を消費し、出力の max、offset、1回10000項目の検索上限を保つ。上限で止まる場合も、先読みした最大4フォルダの読み込みを終えてから返る。

### stat と wait の JSON

stat は ok / entry（上記）/ uploadState / queued / uploading / failed / webUrl / status / skippedUnsafe。
uploadState は idle / queued / uploading / failed / unknown。停止中は上げのフラグも null。失敗回数がある再試行中は uploading と failed の両方が true になる場合がある。

wait は成功・時間切れ・失敗とも ok / code / message / items / waitedSeconds / status / skippedUnsafe。
items は {"path":"資料/原稿.txt","state":"uploaded","verified":true,"message":"照合内容","verification":{"localSize":1234,"localModifiedUtc":"2026-01-01T00:00:00Z","cloudSize":1234,"cloudModifiedUtc":"2026-01-01T00:00:00Z","verifiedUtc":"2026-01-01T00:00:01Z"}} の配列。verification は照合した版と雲側を観測した時刻。未確認・待ち・途中の項目は null。
state は uploaded / queued / uploading / failed / unknown。停止や読み取り専用では unknown のまま返すことがある。

キューにないファイルも、割り当て先の一覧と雲の側の大きさ・更新時刻を照合し、未送信の管理情報と最後のキューを確認してから uploaded にする。--all はキャッシュ全体に不明な未送信があっても0にしない。
0 が保証するのは、各 verification.verifiedUtc に、その大きさ・更新時刻の版が雲側にあったことまで。返却時の最新内容・内容ハッシュ・その後の更新までは保証しない。確かめる途中で手元の版が変われば照合をやり直し、複数対象も返却前に全部読み直す。管理情報の読取不能・途中の形式・本体だけの組は unknown（3）。本体と管理情報の両方が無いことを走査で確認できた場合だけ、未キャッシュとして照合を続ける。
wait --all だけは、管理情報が無い本体で、稼働中の remote 内の見える名前に戻せ、前後のキュー・手元の一覧・雲に無く、起動後に更新されてから60秒以上経ち、前後の完全な走査で大きさ・更新時刻が同じ物を残りとして外し、0を返せる。JSON の remnantCount / remnants（先頭20件の path / bytes / modifiedUtc）/ remnantMessage に記録し、その本体は常駐を止めたときに回復の対象として残る。
終了値0で remnantCount が1以上なら、道具は残りの数と remnantMessage を人に伝える。残りはアップロード待ちではなく、中身は常駐を止めたときの回復の対象に残る。
時刻は厳密一致。照合できない、時刻精度が違う、存在が確認できない場合は unknown とし、0を返さない。
同じ大きさ・時刻で内容だけが変わることは、この照合では見分けられない。ファイルを閉じてから呼び、待機中は同じファイルへの保存を重ねない。
読み取り専用の wait は5。前から残る未送信が送られることはあるが、新しく保存できるという判断には使わない。
wait には ls・find が返した path の綴りのまま渡し、別の綴り（NFC と NFD の違いなど）で渡すと、キャッシュに項目が残っている間は確かめられず、3を返す。
常駐への問い合わせが時間切れになっても、指定した時間の中で確かめ直す。最後の周が時間切れで終わった場合、その時間切れが管理情報の読みなら3・unknown、状態の読みなら3・status_unknown・「The running mount cannot be verified.」、列（vfs/queue）の読みなら4・failedを返す。雲への問い合わせ（operations/stat）の時間切れは、その項目をunknownにして周を続け、時間を使い切れば3・unknownを返す。前の周に時間切れがあっても、最後の周が終わりまで進めば、その周の結果を返す。
rclone が重い間も wait は指定時間内で状態を読み直すため、その間はほかの命令が busy を受けやすくなる。
待ち時間には照合も含む。処理中の rc 要求は3秒の制限まで待つため、指定時間を少し超えて戻る場合がある。wait --all は残ったキャッシュの本体の候補を1件ずつ確かめるため、候補が多いと指定した時間を越えて返ることがある。

### refresh / resolve / agent-guide の JSON

refresh は ok / path / source（vfs/refresh）/ status。
resolve は ok / localPath / remotePath / webUrl / status / requiresExtendedPath。明示した rclone 設定に対する listremotes --long で Dropbox と確認できた場合だけ /home/ URL を返す。remote=名前:フォルダ ならフォルダをURLに足し、URL入力からはその分を引く。範囲外URLは拒否する。Dropbox 以外・種類不明では webUrl=null、URL入力は web_mapping_unknown（3）。設定パス未指定では種類を推測しない。
末尾が空白・ピリオドの成分がある場合は requiresExtendedPath=true とし、localPath を `\\\\?\\` 付きで返す。末尾は除去しない。
URL のホストは www.dropbox.com または dropbox.com、HTTPS の既定ポートのみ。クエリ・フラグメント・埋め込み区切りは拒否する。
共有リンクや別アカウントの対応を調べるものではない。

agent-guide の JSON は ok / code / message / mountPoint / readOnly / recoveryRequired / processUncertain / markdown。
回復・プロセス確認が未完了なら安全な説明の手引きを出し、ok=false、終了値3。人に知らせ、自動で解除しない。
ドライブ文字のないフォルダ割り当ては「未設定（ドライブ文字なし）」と表示し、フォルダの実名を出さない。

### パスとフォルダ共有

3 つのコマンドには、設定されたマウント先にある項目の絶対パスを渡します。
`path` はファイルとフォルダ、`share` はフォルダを受け付けます。`--config` または `DBXL_CONFIG` で接続先の設定を、`--state` または `DBXL_STATE` で稼働状態ファイルを選べます。
招待のプレビューと送信は、稼働中のマウント、起動時の Dropbox 口座と名前空間、選んだフォルダと親の表示を照合します。旧版で起動したネットワークドライブ、固定ドライブ文字、フォルダ割り当ては、現行版でマウントし直してから共有状態を読み直してください。
稼働中の `path` は、そのマウントが使う rclone の設定ファイルで確認します。停止中でも、同じ接続で確認済みの頭と認証ファイルの同一性を確かめられれば使えます。

| コマンド | 結果 |
| --- | --- |
| `dbxl path <パス> [--json]` | Slack などに貼るパス。JSON では `result.path` |
| `dbxl share show <パス> [--json]` | `result.shared` に直接共有済みか、`result.inherited` に上位フォルダから権限を引き継いでいるか、`result.members` にメンバーと権限。`result.connection` に調べた Dropbox 口座と接続先 |
| `dbxl share invite <パス> --to <メール> [--to <メール>] --access <権限> [--message <文>] [--json]` | 送信前の内容と `result.confirmation`。`result.wouldShareFolder` が true なら送信時に共有フォルダを作成。`result.executed` は false |

権限は `viewer`（閲覧のみ）または `editor`（編集できる）を指定します。`result.connection.namespaceKind` は `team_home`、`team_space`、`personal`、`explicit_namespace` のいずれかです。`result.connection.teamSpace` はチームルートを読んだ場合に true になります。
`share show` はマウントを照合できない場合も設定の接続先を読み、`result.connection.mountVerified` を false にします。

最初にプレビューの接続先、対象フォルダ、宛先の順と権限、一言を確認します。同じコマンドに `--execute --confirm <確認値>` を付けると Dropbox に招待を送り、`--json` を省略しても結果を JSON で返します。
確認値は稼働中のマウントと Dropbox 上のフォルダ、共有の状態、宛先の順、権限、一言に対応します。招待の直前に、元のパスから口座、名前空間、フォルダ、直接共有を確認し直します。`confirmation_required`（終了値 2）、`confirmation_changed` または `mount_unverified`（終了値 3）が出たら状況を確認し、プレビューからやり直してください。
`result.invited` は送信を確認できた宛先、`result.access` は権限、`result.sharedFolderCreated` は今回新しく共有フォルダにできたかを示します。
`result.sharedFolderState` は `existing`、`created`、`not_created`、`unknown` のいずれかです。
`result.invitationState` は `sent`、`not_sent`、`unknown` のいずれかです。
引数の解析、確認値の照合、送信計画の作成までに止まると `ok=false`、`code`、`message` を返します。
確認値を照合して再確認に進んだあとで止まると、送信前のマウント再照合も含め、確認できた進捗を `result` に返します。
`unknown` の場合は `share show` で状態を確認してから次の操作を決めてください。

終了値 2 は引数・設定・パスの誤りです。3 は認証不足（`missing_scope`）、時間切れ、確認中に対象が変わった場合などです。
4 はチームの制限（`team_policy`）、入れ子（`inside_shared_folder`、`contains_shared_folder`）、通信エラーなどです。

`dbxl set copyPathPrefix <頭>` は確認した Dropbox 口座に表示設定を保存します。空の値は自動に戻します。どちらも稼働中のマウントを続けたまま適用します。

### snapshot の読み出し

snapshot list / metadata / fetch は Dropbox の API を直接読み、マウント停止中も使える。設定は --config / DBXL_CONFIG の指定を使い、状態ファイルは開かない。
選んだ rclone リモートの `impersonate_admin` が非空の場合は、管理者実行を避けるため `unsupported_namespace`（終了値 2）で止まる。文字列 `false` も非空として扱う。空欄または省略は従来どおり進む。認証の切り替え途中・再ログインが必要な印があれば、回復の書き込みをせず auth_unavailable（3）で止まる。非表示や除外の設定にかかわらず指定した雲の範囲を読む。--json は必須。

~~~text
dbxl snapshot list "見本/数学" --entries "C:/sample/full.ndjson" --cursor-out "C:/sample/full.cursor.json" --json
dbxl snapshot list "見本/数学" --cursor-file "C:/sample/full.cursor.json" --entries "C:/sample/delta.ndjson" --cursor-out "C:/sample/delta.cursor.json" --json
dbxl snapshot metadata --paths-file "C:/sample/paths.jsonl" --output "C:/sample/metadata.jsonl" --json
dbxl snapshot fetch --plan "C:/sample/plan.jsonl" --store "C:/sample/store" --failures "C:/sample/failures.jsonl" --json
~~~

list の対象フォルダと metadata の path は設定の remote の根からの相対パスを受ける。list の根は空の引数。list の対象フォルダは従来どおり :・\\・制御文字・先頭の /・空の区切り・.・.. を拒否する。remote の prefix は、先頭と末尾の / を取り除いたあとに :・\\・制御文字・空の区切り・.・.. を拒否する（先頭の / はチームの根を選ぶ印）。metadata の path は : を使えるが、空文字・\\・制御文字・先頭の /・空の区切り・.・.. を拒否する。metadata は / から始まる API パスを組み立てるため、id:・rev:・ns: から始まる入力も文字どおりの名前として照会する。手元の出力は完全なローカルパスを指定する。出力の名前に rclone.conf（大文字小文字を問わない）は使えない。割り当て先、その UNC、ネットワーク、キャッシュ、退避先、ログ、設定・鍵・状態のフォルダとその実体の下、再解析ポイントを通る出力先は unsafe_destination（2）。--config / DBXL_CONFIG、--state / DBXL_STATE で選んだ場所も対象。rcloneConfigPath が無いときも、rclone config file で解決した設定ファイルの親フォルダとその実体を拒否する。従来の入力・出力検証を先に済ませ、その場所の解決後、認証照会と最初の出力の作成より前に出力先を再検査する。%APPDATA%\\rclone の拒否も保つ。再解析ポイントは実体を解決してから拒否する。公式の Dropbox のフォルダは判別しないため、呼ぶ側で同期外の置き場を選ぶ。

--max-rps は小数可、既定4、範囲0.01〜100。1コマンド全体の要求の開始間隔を制限する。fetch の --parallel は既定4、範囲1〜32。--deadline-seconds は全体の期限で、既定600秒、0より大きく86400秒以下。入力の検証・認証照会・待ち・全ページ・検証も期限に含める。429 の Retry-After は同じコマンドで共有し、期限に収まらなければ rate_limited（3）。429 が3回続いても3で止める。list の --limit は既定2000、範囲1〜2000。API は指定より少ない行を返すことがある。

DbxLight 自身のすべての要求には、接続の確立・送信から応答ヘッダまで60秒、本文の読み取り1回ごとの無通信60秒の上限がある。本文を1回読めたら無通信の時間を数え直し、本文全体の所要時間には上限を付けない。アカウント照会と token 更新後の確認にも同じ上限を使う。rclone の子の上限は30秒のまま。5xx と通信の失敗（接続の切断・この2つの上限による通信の時間切れ・応答本文の途中の切断）は、同じ要求を最大3回やり直す（初回を含め最大4要求、待ちは順に1秒・2秒・4秒）。本文が JSON でない5xxも同じ。全体の期限が先に来たら timeout（3）、Ctrl-C は cancelled（3）で、どちらもやり直さない。やり直しの待ちが全体の期限に収まらなければ、待たず timeout（3）で返す。回数を使い切れば remote_error / network_error（4）。download は新しい .partial に先頭から取り直し、途中の本体は再利用しない。取り消し、入力の誤り、hash 不一致、壊れた JSON、ローカルの書き込み失敗はこの再試行の対象外。retries は通信・5xxによる再試行の回数で、401の更新と429は含めない。

list は cursor-file なしで mode=full（recursive=true）、ありで mode=delta（continue のみ）。include_deleted=true なので、全一覧にも過去に消えた物の deleted 行が混じる。どちらも has_more=false まで読み、ページと行を受け取った順に NDJSON へ流す。差分は完全一覧としての不在証明には使えない。一覧は原子的な断面ではない。Dropbox API 上の大文字小文字だけの改名は差分に出ないため、定期的・明示的な全一覧のやり直しで字面を直す。rclone のマウントが一時名を経由して改名した場合は、その移動が差分に出ることがある。この動作を前提にAPIの制約を取り除かない。apiAtomicSnapshotGuaranteed と mountGeneration は出さない。cursor_reset（3）なら呼ぶ側が全一覧をやり直す。list の not_found は対象のフォルダが無いこと。差分（--cursor-file）で出たら、対象が消えた・移ったので、対象のパスを確かめ直して全一覧から作り直す。差分に pathLower が rootPathLower と同じ deleted 行が来たら、対象そのものの削除なので台帳を捨て、対象のパスを確かめ直して全一覧からやり直す。対象が消えたときに not_found と対象自身の deleted 行のどちらで知らされるかは Dropbox の側の動きによるため、呼ぶ側は両方を同じに扱う。

各行は schema=dbxl.snapshot.entry.v1 / seq / type（file・folder・deleted）/ name / pathLower / parentPathLower / id / pathDisplay。pathLower は選択した名前空間内の絶対パス。name は末尾の正しい字面で、親の関係と組み合わせて名前を作る。pathDisplay 全体を正しい祖先の字面と見なさない。file は size / clientModifiedUtc / serverModifiedUtc / rev / contentHash / downloadable も持つ。folder の本体の項目と deleted の id は null。deleted はパスを使う。対象フォルダ自身の行が含まれる場合もある。標準出力と cursor の rootPathLower が対象フォルダ自身のパス（remote の prefix を含む）を示し、名前空間の根なら空文字。相対パスはこれを基準に作る。空の全一覧でパスが得られない場合は、同じ5種類の API のうち metadata で対象フォルダを照会する。

台帳への適用と名前の再構築は呼ぶ側で行う。全一覧も差分も seq の順で適用する。

- file はその場所の項目を置き換え、以前の子孫をすべて除く。
- folder はその場所の項目を置き換え、子孫は保つ。
- deleted はその場所と子孫を除く。もともと無い場所なら何もしない。
- 親より子が先に届くことがある。親の行が未着でも子を保ち、あとから来る親に結び付ける。

これらは [Dropbox の list_folder の規則](https://www.dropbox.com/developers/documentation/http/documentation#files-list_folder) に従う。大文字小文字と差分については [公式の変更検出ガイド](https://www.dropbox.com/developers/reference/detecting-changes) も参照。知らない種類・壊れた応答 JSON は全体を不完全にする。

cursor のファイルは schema=dbxl.snapshot.cursor.v1 / bindingId / folder / cursor / createdUtc / rootPathLower / entries / entriesSha256。entries は対になる一覧の行数、entriesSha256 は一覧ファイルの全バイト（UTF-8・BOMなし・各行末LF）の SHA-256。呼ぶ側は一覧ファイルの存在・行数・ハッシュを照合してから台帳へ適用する。欠落・不一致なら cursor を採用せず全一覧をやり直す。これらの照合項目が無い古い cursor は invalid_cursor（2）なので全一覧を作り直す。bindingId は remote 名・remote 内の場所・アカウント・根の名前空間のハッシュで、中身は出さない。別の bindingId または対象フォルダの cursor は binding_changed（3）で拒否する。cursor 自体は標準出力へ出さない。

metadata の入力は UTF-8（先頭BOM可）の JSONL で、1行に {"path":"見本/原稿.md"}。出力は path / exists / type / id / observedUtc。明確な not_found だけ exists=false とし、権限・通信・不明の種類は全体を不完全にする。

fetch の plan は UTF-8（先頭BOM可）の JSONL で、1行に id / rev / cloudPath / size / contentHash の5項目が必須。id は id: で始まる文字列、rev は1〜128桁の16進文字列、cloudPath は空でない文字列で制御文字（U+0000〜U+001F・U+007F）を含まない、size は0以上の整数、contentHash は小文字64桁の16進文字列。cloudPath は結果へ写す目印で、取得には使わない。:・\\・.・..・空の区切りも使え、一覧の pathLower の先頭の / を外した物をそのまま入れてよい。取得の API パスは rev:<rev>、手元の本体の名前は contentHash から作り、cloudPath をどちらのパスの組み立てにも使わない。知らない項目は無視する。plan・paths-file・cursor-file は通信前に全行・形を検証し、壊れた JSON、必須項目の欠落・型違い、不正なパスがあれば要求0件で終了値2。plan と paths-file は検証時から終了まで同じ読み取り専用ハンドルを保持して、途中の差し替えを防ぐ。

rev:<rev> で取得し、応答の id・rev・長さ・content_hash と plan を照合する。流しながら Dropbox の4 MiBブロックによる content_hash と通常の SHA-256 を計算する。非 downloadable（Paper 等）は別形式へ変換せず失敗する。store 内の bindingId/objects/<contentHash>.blob に検証済み本体を置く。既存の本体は長さと内容のハッシュを読み直して一致したときだけ reused=true とする。壊れた既存本体は上書きしない。

fetch の結果は store/fetch-results.<識別子>.jsonl。各行は id / rev / cloudPath / size / contentHash / sha256 / localPath / reused で、並列処理の完了順。行別の失敗があっても期限内でほかの行を取り続け、1件でも取得・照合できなければ全体は不完全。標準出力の failed は失敗を記録した件数、failuresFile は確定した手元の失敗 JSONL。各行は planLine（1始まりの plan の物理行番号）/ id / rev / code / localPath。localPath は既存本体の長さ・hash 不一致のときその場所を示し、それ以外は null。取り消された取得も失敗を記録できれば数えるが、未着手行を全件列挙する保証はない。全体の code は、記録済みの失敗では先頭の planLine の理由を使う（全体の期限・I/O失敗等は優先する）。

--failures <手元のJSONL> で失敗の出力を指定できる。省略時は結果の JSONL と同じフォルダに fetch-failures.<結果と同じ識別子>.jsonl を置く（store の隣には置かない）。出力先の安全性と既存ファイルは通信前に検証する。失敗が無ければ failed=0 / failuresFile=null。失敗時にも失敗 JSONL は確定するが、成功の結果 JSONL は .partial のままで resultsFile=null。先に検証済みの本体は残し、次回の実行で reused になる。

既存の出力ファイルは destination_exists（2）で拒否する。一覧・結果・cursor は新しい出力名を選ぶ。cursor-file と cursor-out も別の名前にする。途中の本体と出力は .partial.<識別子> の名で残し、検証後に改名する。一覧・結果・失敗 JSONL・cursor・本体は確定前に FileStream.Flush(true) でディスクへ書き切る。list は全ページ成功後に一覧を確定し、その行数・SHA-256 を記した cursor を最後に確定する。呼ぶ側は終了値0と complete=true と、一覧・cursor の照合を確認する。Flush(true) はストレージ機器の障害まで保証しない。Ctrl-C は所有する子プロセスと取得を取り消して cancelled（3）。途中の物を自動削除しない。

標準出力は JSON を1つ。schema（dbxl.snapshot.list.v1 / metadata.v1 / fetch.v1）/ ok / code / mode / source=dropbox-api / bindingId / rootPathLower / complete / entries / files / folders / deleted / pages / requests / throttled / retries / planned / fetched / reused / verified / failed / bytes / startedUtc / observedThroughUtc / elapsedSeconds / entriesFile / cursorFile / resultsFile / failuresFile / partialFiles。使わない件数は0、対象外の rootPathLower や未確定の最終出力先はnull。引数や設定で RunAsync より前に失敗した場合は schema=dbxl.snapshot.v1 / mode=""。requests はアカウント照会と再試行を含む DbxLight 自身の API 要求数（rclone 子プロセスの要求は含めない）、throttled は429の回数。進みは標準エラーへ少量だけ出す。

token は rclone の設定からプロセス内だけで借りる。既定の設定場所は rclone config file で決める。選んだ remote の access token と期限だけを保持し、ほかの認証値を保存・表示しない。期限まで10秒以下・期限切れ・401では、rclone about <remote> --json の読み取り照会で更新を促して読み直す。期限まで10秒を超える token はそのまま使う。更新は同じ token の値と期限について1回で、並列の要求は1本の更新を待って結果を共有する。値と期限が両方同じままなら auth_unavailable（3）。長時間の取得で別の token が失効した場合は、その token について更新を行う。期限が無い、または零の期限の古い token は期限なしとして使い、401なら同じ更新の経路を使う。DbxLight は自分で更新せず、認証・状態・設定ファイルへ書かない。暗号化設定は encrypted_config（3）、dropbox 以外は unsupported_remote（2）、再現できない接続は unsupported_namespace（2または3）、借用できなければ auth_unavailable（3）。HTTP の生の失敗本文も表示しない。送り先は api.dropboxapi.com と content.dropboxapi.com の固定2ホストで、リダイレクトを追わない。

snapshot の0は指定した読み出しがすべてそろった意味で、検査合格や原子的な snapshot の保証ではない。内部の予期しない失敗は1。使い方・入力の形・設定なし・危険な出力先は2。時間切れ・rate_limited・cursor_reset・binding_changed・auth_unavailable・不完全は3。取得・内容不一致・リモートの失敗は4。snapshot には5・6を使わない。失敗の code は次の固定語で、サーバー本文や任意の例外文を含めない。

| 終了値 | code | 意味 |
| --- | --- | --- |
| 1 | internal_error | 予期しない内部の失敗 |
| 2 | usage / not_configured / invalid_config | 引数・数値・設定なし・設定の形 |
| 2 | input_unavailable / invalid_input / invalid_plan / invalid_cursor / unsafe_cloud_path | 入力を開けない・入力の形や必須項目・パスの誤り |
| 2 | unsafe_destination / destination_exists | 危険な出力先・既存の出力 |
| 2 | unsupported_remote | Dropbox 以外や再現できない remote |
| 2 / 3 | unsupported_namespace | 再現できない接続設定・名前空間 |
| 3 | auth_unavailable / encrypted_config / unsupported_config | 認証を借用・更新できない・鍵の設定を読めない |
| 3 | timeout / cancelled / rate_limited | 全体の期限・取り消し・要求制限 |
| 3 | cursor_reset / binding_changed | cursor の失効・接続または対象の変更 |
| 3 | unknown_entry / invalid_response / invalid_json / incomplete | 未知の種類・壊れた応答・読み出しの不完全 |
| 4 | io_error / network_error / redirect_refused / remote_error | ローカルI/O・通信・リダイレクト拒否・リモートの失敗 |
| 4 | not_found / not_file / restricted_content / permission_denied / not_downloadable | 指定した rev・パス・対象のフォルダが無いと Dropbox が明示したとき・ファイルでない・取得制限・権限・取得できない形式 |
| 4 | id_mismatch / rev_mismatch / size_mismatch / content_hash_mismatch | 指定した版・長さ・内容との不一致 |

形の合わない rev（実在の rev の形と違う rev。例: 16桁の0（0000000000000000））は、2026-11-15（日本時間）の実測で、やり直しを3回使い切って remote_error（4）、retries=3 になった。2026-11-15、試し用フォルダ内の実在するファイルの21桁の16進 rev の末尾4桁を変えた、形は正しいが実在しない rev は not_found（4）、retries=0 で返った。この1例は、履歴から消えた版を含むすべての rev の応答を保証しない。remote_error の行を取り直すときは、一覧を取り直して rev が現役かを確かめる。同じ rev を回数の上限なしにやり直さない。

## 終了値

| 値 | 新コマンド | 既存コマンド |
| --- | --- | --- |
| 0 | 成功。wait は照合済み。snapshot は指定した読み出しが全部そろった | 従来どおり。status は下記の注意がある |
| 1 | snapshot: 予期しない内部失敗 | 通常の失敗・使い方の誤り・doctor 不合格・run の見張り失敗 |
| 2 | 使い方・数値・マウント外のパスの誤り。snapshot は設定なし・危険な出力先も含む | 使わない |
| 3 | まだ・順番待ち（busy）・時間切れ・未確認・回復未完了・プロセス不明・非表示の拒否 | 上げ待ちや回復を理由に操作を断る。読むだけのコマンドの順番待ちは busy（status を除く） |
| 4 | 上げの失敗・処理失敗・存在を照合できない stat。snapshot は取得・内容不一致・リモートの失敗 | 使わない |
| 5 | wait: 読み取り専用で新しく保存できない | 使わない |
| 6 | wait / refresh: 動いていない | 使わない |

既存 status は稼働中で未準備なら1、それ以外は従来の0を保つ。回復未完了・プロセス不明でも終了値だけでは分からない場合がある。
必ず JSON の ok / recoveryRequired / processUncertain / statusError / ready も読む。マウントを使う新コマンドはその状態で3を返す。snapshot はマウントや状態ファイルを使わない。
recovery clear の確認引数不足は従来の1。終了値の変更で古い呼び出しを壊さない。

マウントを使うコマンドの失敗 code の固定値: usage / failed / busy / pending / recovery_required / process_uncertain / status_unknown / state_mismatch / hidden / not_running / read_only / timeout / unknown / too_many / mount_changed / queue_unknown / upload_failed / refresh_failed / not_found / check_failed / not_configured / web_mapping_unknown。
not_configured は設定の remote が空のとき。新コマンドは終了値2、status は従来の終了値を保ち ok=false / code=not_configured を返す。--config または DBXL_CONFIG を確認する。agent-guide は既定ドライブを表示せず、この確認手順を返す。
busy は別の DbxLight の操作による順番待ちで、確かめられなかった、という意味（成功でも失敗でもない）。wait は途中で順番待ちになると、その周で見えていた上げの失敗・上げ待ち・照らしの結果も答えに含めず、全部の項目を unknown・verified=false・verification=null で返す。wait --all は明示したパスが無いため items は空配列になる。数秒おいて同じコマンドを呼び直すと、その時点の状態が返る。上げの失敗が残っていれば upload_failed・終了値4を返す。ls・find・stat は、先に起きた通信などの失敗を、あとの順番待ちで置き換えない。読むだけのコマンドは終了値3、status は preparing と同じ終了値0で ok=false / code=busy と全項目を返し、不明値は null。操作側の45秒待ちと失敗の扱いは変わらない。
別の操作と重なった status --json は、止まっているときの回復待ち・プロセス不明でも、busy と不明の値（null）を返すことがある。操作が終わってから1回呼び直し、recoveryRequired・processUncertain があれば人に知らせる。
recovery_required でも running=true かつ processUncertain=false の wait・stat・status は、前の停止からの上げ残しの確認中で、完了すると自然に解けると案内する。wait は開始時と待機中のどちらでも同じ案内を返す。数秒おいて確認し、長く続くときは人に知らせる。停止中・プロセス不明の案内と、各コマンドの終了値は従来どおり。
code は分類、message は説明。message の文面で分岐しない。
not_found は ls / find --in / refresh / stat で、指定パスが今の一覧に見当たらない場合。終了値は4。名前を確かめるか、親フォルダを ls して確認する。非表示の範囲は従来どおり hidden（3）。停止中の refresh は not_running（6）。

## 既存コマンド

既存の操作・停止の守り・書き込みを許す完全一致の条件を保つ。AI の道具は停止・再接続・設定・強制・回復解除を人の指示なしに実行しない。

通常の停止は rclone の終了を25秒まで待ち、その間に終わらなければ停止の失敗（終了値1）を返す。アプリは rclone を終了させずに残し、強制には切り替えない。

| コマンド | 動き | --json の成功 |
| --- | --- | --- |
| help / --help / -h / 引数なし | 使い方 | ok / usage |
| start | 起動、準備待ち、既存 PID の再利用 | ok / result（pid / alreadyRunning） |
| run | 起動して見張る。最大3回の再起動。Ctrl+C は通常停止の守りを通る | 終了時に ok / code / message を一つ |
| stop [--force] | 通常は待ち・途中を確かめて停止。force は未送信を残す明示操作 | ok / result（pid / wasRunning） |
| restart | 通常停止を通して起動し直す | ok / result（pid / alreadyRunning） |
| status | 状態を返す | 下記の項目を根に置く |
| hide <パス> / unhide <パス> | 表示しない一覧を変更。動いていれば守りを通して再接続 | ok / result.hiddenPaths |
| hidden [--max N] [--offset N] | 表示しない一覧。既定1000件 | ok / result（hiddenPaths / truncated / continuation） |
| folders [パス] [--max N] [--offset N] | 表示しないフォルダも含む子フォルダ。既定1000件 | ok / result（folders / truncated / continuation） |
| set <key> <value> | 検証して保存。配列は JSON 配列。稼働中は通常停止の守りを通す | ok / result.saved |
| config show | 設定。rc の認証は出さない | ok / result（設定） |
| doctor | rclone、WinFsp、割り当て先、キャッシュを確認 | ok / code / message / checks（name / passed / detail） |
| recovery clear --i-understand-pending-may-be-lost | 未送信が残る可能性を了承し、停止済みの回復の固定を明示解除 | ok / result.cleared |

hidden / folders の上限値・続け方は ls と同じ。hide / unhide は設定を変えた結果の設定配列を返す。
config show は --json がなくても従来の設定 JSON を出す。
設定項目は README の「設定」の表。読み取り専用を外せるのは writeAllowedRemotes が remote と完全一致するときだけ。

## status --json の全項目

既存の項目名と値は保つ。null は未取得であり、0や問題なしという意味ではない。

| 項目 | 意味 |
| --- | --- |
| running / ready | 所有プロセスが稼働 / 割り当て先の準備完了 |
| rclonePid | 所有 rclone の PID |
| privateMemoryBytes / workingSetBytes | メモリ（バイト） |
| uploadsQueued / uploadsInProgress / erroredFiles | 待ち / 途中 / キャッシュの失敗数 |
| cacheFiles / cacheBytes | 手元キャッシュの件数 / バイト |
| oldestQueuedAgeSeconds / oldestQueuedAgeReason | 最古の待ち時間。rclone に投入時刻がないため秒数は null、理由を返す |
| queueSupported | vfs/queue の一覧が取得できたか |
| recoveryRequired | 回復がまだなら true |
| stopDisposition | Unknown / Running / Clean / Forced / UnexpectedExit / StartupFailed / ProcessUnknown / StopUncertain / ClearedByUser |
| processUncertain / uncertainProcess | 状態確認不能の印 / {pid, name}。名前を得られない場合は name=null |
| recoveryLocation | 回復先と条件の説明。未完了時に残す |
| statusError | 状態を確認できない理由 |
| ok / code / message | 機械向けの判定。失敗時に code / message を加える |
| downloadActivity | 大量取得の観測（以下）。停止中は null |
| rescueFiles | 確認済みでない取り出し記録の件数。不明・ほかの設定も含み、送信の確認数ではない |
| unregisteredCredentialFiles | 登録のない鍵の作業ファイルの場所。本文は読まず、そのまま残す |
| readOnly / readOnlySource | 起動時に実際に渡した --read-only の有無 / active_arguments。停止中は現在の設定で起動した場合の値 / configured。旧記録で引数を確認できない・プロセス不明は null / unknown |

## 大量に取得しているとき

core/stats と core/transferred の取得方向の情報を使い、雲からキャッシュへの転送を数える。上げと内容を調べない一覧は数えない。
設定 downloadWindowSeconds=300、downloadFileThreshold=100、downloadByteThreshold=2000000000 が既定。set または設定 JSON で変更できる（窓は1〜86400秒、件数は1〜20000、バイト数は正の整数）。
100件は rc の完了履歴上限に合わせた既定値。監視は取得を止めず、状態とトレイの通知で知らせる。

downloadActivity は known / files / bytes / windowSeconds / fileThreshold / byteThreshold / exceeded / episode / historyLimited / reason。
件数か容量がしきい値以上になると exceeded=true。同じ連続した超過では1度通知し、窓の範囲から外れて落ち着いたあと再び超過すると次の episode となる。
観測は状態照会時に行い、常駐の通常ポーリングを使う。CLI だけの場合は status または新コマンドの状態照会時に記録する。
rc の完了履歴は直近100件なので、照会の間に大量転送された場合の数は下限になる。historyLimited=true を見て、少ない数を根拠に取得が少ないとは判断しない。
観測不能は known=false。ファイルを自分で開いて監視の数を増やす処理はない。

## AI の道具からの使い分け

先に ls / find で候補を絞り、必要な1件だけを開く。保存後は wait の0を待つ。非0なら雲への反映を確認できていないと伝える。
見えるはずのファイルが無ければ refresh してから再確認する。表示しない範囲が必要なら人に知らせる。
読み取り専用では新しく保存できない。前からの未送信は送られる場合がある。ログオフ・シャットダウン時も、未送信・途中・回復未完了・不明な状態は通常と同じ停止確認を通す。読み取り専用でキューとキャッシュが空と確認でき、表示した設定とマウント世代が現在も同じ場合だけ、直ちに許可する。
回復未完了・プロセス不明は人に知らせる。AI の道具が自分で解除・設定変更・停止を行わない。
貼り付け用は agent-guide、ひな形は integrations/claude-code/SKILL.md と integrations/AGENTS.snippet.md。PC の設定へ自動で入れる処理はない。

公開仕様: [rclone RC](https://rclone.org/rc/)、[lsjson](https://rclone.org/commands/rclone_lsjson/)。

## 取り出した物の確認

~~~text
dbxl rescue list [--all | --max N] [--json]
dbxl rescue acknowledge <id> [--json]
dbxl set rescueDir "D:\Sample\rescue"
~~~

rescue list は未確認の取り出し記録を返す。JSON は ok=true / result.items の配列。元のパス cloudPath（不明なら originalPath）、reason、destinationPath、size、modifiedUtc、completeData、moveCompleted、recordStatus、recordProblem、nextAction を読む。recordStatus=unknown は、実際に読もうとした recordPath とアプリが決めた理由を示す。foreign は、ほかの設定の物を件数 foreignRecordCount だけでまとめ、元のパスや保存先を返さない。

アプリは取り出した物を雲へ戻さない。nextAction に従い、利用者が保存先からドライブの元の場所へ戻す。雲側の更新で取り出した物は、両方を開いて見比べてから必要な内容を戻す。部分本体は管理情報とともに内容を確認・復元してから戻す。取り出した原本と記録は消さない。

rescue acknowledge は検証できる取り出し済み記録を確認済みにして一覧から下げる。成功は0、使い方の誤りや確認不能は1。内容を消したり雲へ送ったりしない。取り出し途中の確認済み操作は断る。recovery clear は従来の確認引数を要求し、キャッシュの持ち主と停止を確かめてから回復の固定を解く。検証不能な取り出し記録はその操作で変更しない。

rescueDir は未指定ならキャッシュの隣の <キャッシュ名>-rescue。同じボリュームでキャッシュの外に置く。完全修飾のパスだけ指定し、割り当て先・キャッシュ内は拒否する。本人だけの権限で本体・管理情報・記録を残す。確認済みでない記録が残る間は、rescueDir の変更を保存前に断る。先に現在の保存先で内容を確認し、確認済みにする。

取り出し記録は通知と一覧だけに使う。破損・場所の不一致・ほかの設定の記録があっても、それだけで起動・停止・接続変更・書き込みのスイッチ・ログアウト・回復解除を止めない。判断の材料はキャッシュの走査と持ち主の記録。取り出し途中は次の停止中の走査が残った側を見つけ、検証できる計画だけ同じ先へ続ける。

名前（フォルダ名を含む）に全角の記号（：？＊｜＜＞＼＂）か「‛」（U+201B）を含む物は、止めたときに上げ残っていると、自動では上げ直さず、取り出して残します。rclone がこれらの名前を、キャッシュの中で「‛」を付けた形で持つためです。止める前に上がり終えた物には関係しません。

## 起動前の走査と鮮度

管理情報の破損、対の欠落、途中の未送信本体、拾えない・対応不明の名前、雲側の変化、比較材料のない物は、キャッシュの外へ取り出して残す。健全な完全な対で拾える名前だけ、起動直前に雲側の元の指紋と照らす。空の指紋は新規保存として、雲側の不在を照らす。両方にある大きさ・時刻・ハッシュだけを比べ、比較要素0は不明として保全する。Dropboxのハッシュ名はdropboxへそろえる。

このアプリは、公式の Dropbox アプリのような競合コピーを作りません。ほかの人と同じファイルを同じ時間帯に直すと、あとから保存した側の内容で上書きになり、先に保存した人の変更が失われることがあります。 保存したファイルは、少し遅れて Dropbox 上へアップロードされます。アップロードされるまでの時間は、件数と大きさで変わります。 前回止めたときに未アップロードだったファイルだけは、起動の直前 (30 秒以内) に Dropbox 上の状態と比べ、変わっていないファイルだけをアップロードします。変わっていたファイルや問題のあるファイルは、ローカルに退避して残し、自動では戻しません。保存先で中身を確かめ、Dropbox 上と見比べてから、必要な内容を自分で戻してください。 公式の Dropbox アプリと、同じフォルダへ同時に書き込まないでください。

全件の照合は親フォルダ単位の一覧で行う。取り出し・記録の保存・引数や鍵の準備を先に済ませる。最後の照合の後は持ち主の記録保存と子の作成・再開だけを行い、それも鮮度の時間へ含める。30秒を超えた場合は全件を照らし直す（再照合3回まで）。それでも超過する場合は残る対象全件を「数が多い・遅いので、自動ではアップロードしません」として残す。状態ファイルと両台帳には recoveryComparedUtc（最古）、recoveryComparedFiles、childResumedUtc、recoveryComparisonAgeSeconds を残す。再開直後に親が落ち、後で子を引き取った場合、失われた再開時刻と秒数を推測して埋めない。

雲側へ問い合わせられない・照合が時間切れの場合は、照合対象を動かさず起動を断る。run は間をあけて再試行し、照合待ちは異常終了の再起動回数へ数えない。所有・走査全体・元の回復先が不明な場合も起動しない。記録のない旧キャッシュの未送信・不明は、元の設定と状態の復元を案内する。

起動後に Dirty がキューにないだけで、開いている保存を取り出したり止めたりしない。ドライブでの普通の保存は rclone が通常の経路で送る。読み取り専用でも、以前からの未送信が送られることがある。wait の保証は上記のメタデータ照合まで。

状態ファイルと両台帳は起動ごとの乱数ID・通番・改訂通番で選び、古い状態ファイルから生きた新しい世代を停止・変更しない。起動印は親・子のPID・開始時刻・exeの同一性で扱う。PID未記録なら親の停止で解く。記録済みで動いている子は同じ子として引き取り、status/stopを使える。全スレッドが停止したままの子は、親の停止を確認して終了・作り直す。同一性の項目が欠けた旧印は推測で解かず、元の設定・状態を保って手動の確認を行う。

## ログインの鍵と出力

専用の鍵は、設定ファイルの完全パスから決めた識別を含む credentials-<識別>/rclone.conf に置く。同じ親フォルダの設定A/Bでも鍵を共有しない。古い版の置き場（設定ファイルと同じフォルダの rclone.conf と credentials/rclone.conf）の物は移さず、消さず、書き換えない。旧場所を指す設定は外部管理のconfとしてそのまま使える。再ログイン時に新しい専用先へ切り替え、許可を空にする。旧ファイルは未登録の物として存在と場所だけを表示する。既存の親フォルダの権限は変えない。

認可とフォルダの確認には本人専用の作業用confを使う。本番は確定まで変えず、確定後の書き込み許可は空にする。ログアウトは作業用confのdbxlightだけをrcloneで外し、設定と一緒に確定する。外の利用者管理のconfは書き換えない。外のconfの中身をほかの道具が付け替えた場合は検出できない。

鍵の作業用と控えは、作成前に登録した正確なパスだけを空にして片づける。旧CleanupPathsのように、作成後に名前で見つけた物は所有証拠にしない。登録のない同型ファイルは残し、status.unregisteredCredentialFilesと状態窓に場所だけ出す。本文は読まない。

停止中のls/find/statも含め、rcloneの生のstderrは破棄する。stdoutは必要な名前・大きさ・時刻などだけを抽出する。解析失敗はアプリの定型理由で返し、元の出力を例外に付けない。マウントの INFO は設定別の本人専用フォルダへ保存し、アプリ定義の起動・停止の記録と分ける。以前の診断ログを表示する場合も生の行を出さず、アプリの定型理由へ変える。

## 取り出しの知らせ

start / restart の result.rescueNotice、run の rescueNotice、status の rescueNotice は unacknowledged（未確認の取り出し件数）/ unknownRecords（確認できない記録件数）/ foreignRecords（ほかの設定の件数）/ hint（rescue list の案内）を返す。文字の結果にも件数と案内を出す。--json は知らせも同じ1つのJSON値の項目に入れる。

ok=true と終了値0は、開始・状態照会の操作が成功した意味。取り出した物を全部上げた意味にはならない。unacknowledged または unknownRecords が0でなければ dbxl rescue list --json で理由と保存先を確認する。

取り出した物は上がっておらず、wait --all の対象に入らない。wait の文字の結果とJSONの rescueScope にもこの範囲を表示する。

## 手元の INFO 記録

start / restart の result.logCapture と status.logCapture は captured / recording / reason / directory / bytes / startedUtc を返す。記録の本文は返さない。captured は記録先を用意できた印で、本文の読取り成功やその後の全書込みの成功の証明ではない。recording はその記録を設定した mount の稼働中を示す。bytes は現在の mount.log の大きさ。記録を用意できないときも起動を続け、captured=false と「ログを記録できていません」を出す。

置き場は logDir/rclone-<設定の識別>/mount.log。フォルダは作成時から本人だけの権限で、子に引き継がれる。mount.log も先に本人だけの権限で作り、既存先の権限を緩めない。再解析ポイント・公開された権限の先は使わない。rclone へ --log-file <上記のファイル> --log-level INFO --log-file-max-size 10M --log-file-max-backups 4 --log-file-max-age 30d を渡す。圧縮は使わない。回し替え・古い物の削除は rclone だけが行う。アプリは記録フォルダ内の物を削除・移動・書換えしない。DEBUG・-vv・dump の任意引数は渡せない。RCLONE_* 環境変数は子へ渡さない（アプリが決めるRC認証を除く）。

状態窓は現在の mount.log と、足りなければ直前の回し替えの1本だけを短く読む。読み・書き・削除を許す共有で開き、すぐ閉じる。「ログを記録していません。」「ログはありますが読めません（失敗の有無を確認できません）。」「読めた範囲に失敗のログはありません。」を分ける。本文は表示せずアプリの定型文へ分類する。古い版が起動ごとに作った記録・補助JSONは読まずそのまま残す。記録用の常駐の子は無く、常駐から起動する子は rclone mount だけ。

起動失敗は failureClass と logDirectory を返す。状態窓も同じ分類を使う。

## 長い起動の準備

準備の走査・取り出し・照合の段階、処理済み件数/対象件数、開始時刻を preparation.stage / completed / total / startedUtc で返す。status は制御の排他を2秒で取れないとき、この進みを返す。JSONは ok=false / code=preparing で、状態照会の終了値は従来どおり0。rescueCountsKnown=false のとき、保全件数を0と判断しない。停止要求があれば preparation.stopping=true。

stop は準備中に result.preparationCancellationRequested=true と、1件の取り出しの区切りで止める案内を返す。進行中の本体・管理情報の対を完了してから止まり、子は起動しない。start は停止要求を受けて止まると pending（3）。run は停止要求に従って終わる。次の起動では残りから進める。

準備の完了確定とstopの取消し受付は同じ排他で決める。取消しを先に受け付けていれば、準備が別の例外で終わっても停止番号への反映を試みる。準備完了・停止番号・回復記録の保存が失敗した場合も、受け付けた取消しを保持してrunの再試行を止める。保存失敗を含む場合はpending（3）とfailureClass=preparation_cancel_persistence_failedで理由を返す。最後の所有解除の保存も失敗した場合は、その理由を併記し、同じpending（3）と専用のfailureClassを保つ。回復の記録と未送信は残る。保存先と記録を確認して停止をやり直す。完了確定が先なら通常停止の道で番号を増やす。

取り消しの受付より先に準備の保存が失敗し、その後に保存先が直って完了・停止番号の保存が成功しても、pending（3）とfailureClass=preparation_cancel_persistence_failedで先の失敗を知らせる。通信・子の起動だけの失敗の後に受け付けた取り消しは、従来どおり正常に終わる。

状態・起動準備・回復記録の読取りや保存が失敗した run は、初回再試行・自動再起動をやめて知らせる。保存先を確認し、再開・start・run の明示操作でやり直す。

run の初回照合が通信の失敗になった場合は5秒後に再試行する。そのRunが最初の排他内で控えた停止番号より後のstopがあれば再試行をやめる。過去の停止は新しい明示起動を妨げない。番号がない旧状態は0と読み、時計では比較しない。監視の終了・初回起動失敗・待機後の自動停止では、まず状態を読む。生きた子の後片づけが要る場合だけ、同じ制御の排他内で持ち主のPID・開始時刻、世代ID・番号、子のPID・開始時刻を照合し、自分の物を通常停止する。所有を取得しなかった失敗も、別のrunの準備や子には停止・取消しを出さない。別の入口が動かしていれば、このトレイは停止済みとし、既存の知らせと状態の窓で案内する。

トレイは準備中・自動再起動待ち中の一時停止も受け付ける。初回の後片づけと停止・終了処理が終わるまで再開は受け付けない。上げ待ちの確認窓は拒否結果の件数と理由を示し、不明なら「件数は確認できません」と出す。待つ／戻るの直後に同じ監視が生きていれば監視を続け、終了済みなら上記の世代照合を通して停止・終了を再確認する。確認できなければ停止失敗／終了失敗からやり直す。外のstopで初回準備を取り消した場合も、後片づけ後に停止済みを確定する。Windows終了時の停止成功後に安全確認が失敗しても前の段階には戻さない。接続窓終了後の設定読取り中も新しい起動を受け付けない。[トレイの段階の表](tray-states.md)を参照。

照合の全体上限は120秒・200フォルダ。helperにも残り時間を渡し、上限を超えた残りは「数が多い・遅いので、自動ではアップロードしません」で取り出す。同じ親の名前の鍵は再利用し、鍵を作る区切りと各対象の照合でも、残り時間と停止要求を確認する。すでに照合した物にも通常の30秒の鮮度を適用する。通常の通信失敗で比較自体ができない場合は、従来どおり対象を動かさず起動を断る。

rescue list は新しい未確認50件まで。--max N（1〜10000）で件数、--all で未確認の全件を表示する。result.truncated=true なら指定件数の先にも残る。--max 10000でも残件を判定し、文字の出力には残りがあるときだけrescue list --allの案内を付ける。確認済みの原本と記録は残し、通常の起動・状態照会では確認済みの記録本文を読み直さない。確認済み索引が壊れたときは本文を再確認する。

rescueDir を別のボリュームへ明示指定した場合は、その場所を使う。移動に時間がかかることがあり、途中の1件を動かしている間は停止の区切りを待つ。設定窓にもこの時間の違いを表示する。

名前の同一性は fsinfo の CaseInsensitive から選ぶ。不正な一覧・重複は一覧全体を比べられない物として取り出す。区別しない接続先では親と葉の対象ごとに、NFKCの後にß/ẞをSS、ı/İをIへ写し、ToUpperInvariantで大文字化した鍵で曖昧な候補を探す。鍵が同じでも字面またはASCIIの大小差だけで同名と確かめられなければ取り出す。規則不明の対象は字面どおりの相手があるときだけ照合し、無ければ取り出す。関係のないUnicode名は対象の照合を妨げない。区別する接続先は字面だけで探す。

起動失敗は、アプリが定義した理由を failureClass / message / location / nextAction に返す。本人専用でない保存先は private_storage_required で、既存の場所を使わず、まだ存在しない場所を指定すると本人だけの権限で作る手順を示す。cache_owner_unknown / cache_owner_running / cache_scan_incomplete は、元の設定・状態・キャッシュを保った確認を案内する。文字の結果と状態窓にも分類と場所と対処を出す。外から来た例外の本文は表示せず、型のある印を付けたアプリ定義の理由だけを使う。

run は終了時に取り出し記録を読み直す。準備取消しはcode=cancelled / preparationCancelled=trueで区別し、終了値は0。rescueCountsKnown=falseならrescueNoticeの件数はnullで未確認とし、0件に置き換えない。自動再起動の準備取消しも同じ。文字の結果も終了時の件数とrescue listの案内を出す。

## バージョンと更新

| コマンド | 動き |
| --- | --- |
| version [--json] / --version [--json] | 現在の major.minor.patch を表示 |
| update check [--json] | このリポジトリの Releases を匿名で確認。更新の有無にかかわらず確認成功は0、確認失敗は1 |
| update download [--json] | 新しい版を取得・検証して staging に置く。常駐が使う |
| update apply [--from <zip>] [--sha256 <hex>] --yes [--json] | 常駐と選択したマウントの停止後に入れ替えを予約する |
| update swap --target <folder> --staging <folder> --wait-pid <pid> --wait-started-utc <UTC> [--relaunch] -- <args...> | 内部用。通常は直接呼ばない |

version の JSON は次の形。+ の後ろの印は version に含めず commit に分ける。

~~~json
{"ok":true,"result":{"version":"0.1.0","commit":"<コミットの印>"}}
~~~

update check の JSON:

~~~json
{"ok":true,"result":{"currentVersion":"0.1.0","latestVersion":"0.1.1","updateAvailable":true,"canApply":true,"releasesUrl":"<このリポジトリの Releases のページ>","unchanged":false}}
~~~

latestVersion は対象の版がなければ null。unchanged=true は ETag による304を保存済みの結果として読んだ場合。
canApply は、実行中のフォルダに release.json と掲載ファイルがあり、書き込めるという判定。開発ビルドでも確認はできるが、入れ替えには配布 zip を展開した置き方が必要。

update download の result は version・staging・releasesUrl。zip と .sha256、あれば API の digest、大きさ、全ファイルの manifest を照合する。
状態ファイルの親の update フォルダを使う。設定・状態の指定は共通の --config と --state に従う。

apply は --yes がないと何もしない。result.applied=false・confirmationRequired=true と対象の説明を返す。
--from はローカルの zip を使い、通信しない。--sha256 は64桁で指定し、英字の大小を区別しない。ローカルでは前の版へ戻すこともできる。
常駐またはマウントが残る場合は1を返す。常駐のメニューから更新するか、終了してから実行する。
引数の誤りは既存の通常コマンドと同じ終了値1・code=usage。JSON の失敗は ok=false と code・message を返す。

実行中の CLI のロックを解くため、apply は新 CLI を切り離して起動し、自身が終了したあとに入れ替える。JSON の形:

~~~json
{"ok":true,"result":{"scheduled":true,"version":"0.1.1","staging":"<staging の場所>","swapPid":1234,"resultPath":"<update/last-result.json の場所>"}}
~~~

終了値0と scheduled=true は予約の成功。完了は resultPath の success=true を確認する。
最終結果には success・version・stage・reason・rolledBack・backupPath・finishedUtc・read が入る。
stage は失敗時に verify・target・wait・locked・swap・rollback のいずれか、成功時は null。verify は staging の検証、target は移動前の配置・書き込み・旧ファイル・管理外の同名ファイルやフォルダ・backup の場所の問題、wait は終了待ち、locked は排他で開けない場合、swap はファイルの移動開始後に失敗して戻した場合、rollback は戻しにも失敗した場合を表す。reason は例外の文を記録として残すが、常駐の通知には出さない。
失敗時に rolledBack=true なら旧ファイルに戻っている。backupPath は、成功したときは前の版を残した backup フォルダ、戻しに失敗したときは前の版のファイルがある場所を示す。戻せたときは null。復元にも失敗した場合はこの場所を使って手で戻す。常駐からの更新では、結果を次の起動で一度通知する。
掲載のないファイルとフォルダ、既存の登録は保つ。

~~~powershell
dbxl version
dbxl update check --json
# 人が決め、常駐とマウントを終了したあと
dbxl update apply --yes --json
# ローカルに保管した版へ戻す
dbxl update apply --from "D:\packages\DbxLight-0.1.0-win-x64.zip" --yes --json
~~~

このリポジトリの Releases にアクセスできない場合、404も確認失敗として返す。トークンによる回避は行わない。
自動確認は updateCheck=true（既定）で、起動の2分後と前回の試行の24時間後のうち遅い時刻に行う。自動の失敗は通知しない。
手動の確認は24時間の制限を受けず、通知済みの更新も毎回知らせる。設定画面で自動確認だけを変える保存はマウントを止めず、次の確認時刻に反映する。自動での入れ替えは行わない。
.NET 8 デスクトップ ランタイム、rclone、WinFsp は zip に含まない。検証の限界と戻し方は [ADR 006](adr/006-self-update.md) を参照。
