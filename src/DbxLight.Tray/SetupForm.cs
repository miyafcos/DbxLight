using DbxLight.Core;

namespace DbxLight.Tray;

public sealed class SetupForm : DbxForm
{
    private static readonly string[] Retentions = ["30m", "1h", "3h", "1d", "1w"];
    private readonly SetupSession session;
    private readonly Label heading = Ui.Label("", true);
    private readonly Panel page = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TextBox message = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        BackColor = SystemColors.Window, BorderStyle = BorderStyle.None, Text = "最後の段で確定するまで、接続設定は保存されません。" };
    private readonly Button back, next, cancelLogin;
    private CancellationTokenSource operations = new();
    private ComboBox? drive, retention;
    private bool busy, closing, resourcesDisposed;
    public SetupSession Session => session;

    public SetupForm(SetupSession session)
    {
        this.session = session;
        Ui.Configure(this, "ログインと接続先", new Size(800, 600));
        var column = Ui.Column(4);
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(heading, 0, 0); column.Controls.Add(page, 0, 1); column.Controls.Add(message, 0, 2);
        back = Ui.Button("戻る", (_, _) =>
        {
            try { SaveOptions(); session.Back(); Render(); }
            catch (Exception e) { message.Text = Ui.Failure(e); }
        });
        next = Ui.Button("次へ", async (_, _) => await RunAsync(async () =>
        {
            SaveOptions();
            if (session.Step == SetupStep.Finish)
            {
                await Task.Run(session.Commit);
                DialogResult = DialogResult.OK; closing = true; Close();
            }
            else session.Next();
        }));
        cancelLogin = Ui.Button("操作を取り消す", (_, _) => { operations.Cancel(); session.CancelLogin(); });
        column.Controls.Add(Ui.Buttons(Ui.Button("閉じる", (_, _) => Close()), next, back, cancelLogin), 0, 3);
        Controls.Add(column);
        FormClosing += (_, e) =>
        {
            if (busy && !closing) { e.Cancel = true; closing = true; operations.Cancel(); session.CancelLogin(); }
        };
        session.CheckRequirements(); Render();
    }

    internal void Render()
    {
        foreach (Control old in page.Controls.Cast<Control>().ToArray()) old.Dispose();
        drive = retention = null;
        back.Enabled = !busy && session.Step > SetupStep.Requirements;
        next.Visible = session.Step != SetupStep.Login;
        next.Text = session.Step == SetupStep.Finish ? "読み取り専用で始める" : "次へ";
        next.Enabled = !busy && (session.Step != SetupStep.Scope || session.ScopeVerified);
        cancelLogin.Visible = busy;
        heading.Text = $"{(int)session.Step} / 5  " + (session.Step switch
        {
            SetupStep.Requirements => "必要なものの確認",
            SetupStep.Login => "Dropbox にログイン",
            SetupStep.Scope => "見える範囲を選ぶ",
            SetupStep.Options => "この PC での使い方",
            _ => "読み取り専用で始める"
        });
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        page.Controls.Add(body);
        switch (session.Step)
        {
            case SetupStep.Requirements: Requirements(body); break;
            case SetupStep.Login: Login(body); break;
            case SetupStep.Scope: Scope(body); break;
            case SetupStep.Options: Options(body); break;
            case SetupStep.Finish:
                body.Controls.Add(Ui.Label("読み取り専用で始めます。フォルダを見て、必要なファイルを開けます。"));
                body.Controls.Add(Ui.Label("ここでは書き込みを有効にしません。あとで設定のスイッチから変更できます。"));
                body.Controls.Add(Ui.Label("接続先：" + session.Draft.Remote));
                body.Controls.Add(Ui.Label("割り当て先：" + session.Draft.MountPoint + "   手元に残す時間：" + session.Draft.Retention));
                body.Controls.Add(Ui.Label("表示しないフォルダ：" + session.Draft.HiddenPaths.Length + " 件"));
                break;
        }
    }

    private void Requirements(FlowLayoutPanel body)
    {
        body.Controls.Add(Ui.Label("rclone と WinFsp を確認します。足りないものは下の命令で入れてください。"));
        foreach (string name in new[] { "rclone", "WinFsp" })
        {
            bool found = session.Checks.Any(c => c.Name == name && c.Passed);
            body.Controls.Add(Ui.Label(name + (found ? "：見つかりました" : "：見つかりません")));
            if (!found)
            {
                string command = name == "rclone" ? "winget install --id Rclone.Rclone -e" : "winget install --id WinFsp.WinFsp -e";
                body.Controls.Add(new TextBox { ReadOnly = true, Width = 640, Text = command });
                body.Controls.Add(Ui.Button("命令をコピー", (_, _) => Clipboard.SetText(command)));
            }
        }
        body.Controls.Add(Ui.Button("もう一度確認", (_, _) => { session.CheckRequirements(); Render(); }));
        next.Enabled = !busy && session.Checks.Count(c => c.Name is "rclone" or "WinFsp" && c.Passed) == 2;
    }

    private void Login(FlowLayoutPanel body)
    {
        body.Controls.Add(Ui.Label("既定のブラウザで Dropbox の認可を行います。制限時間は 5 分です。"));
        body.Controls.Add(Ui.Button("Dropbox にログイン", async (_, _) => await RunAsync(async () =>
        {
            bool overwrite = false;
            if (await session.NeedsOverwriteAsync(operations.Token))
            {
                if (MessageBox.Show(this, "専用の設定に dbxlight が既にあります。上書きしてログインし直しますか？", "ログインの確認",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                overwrite = true;
            }
            message.Text = "ブラウザで認可してください。操作を取り消すこともできます。";
            LoginResult result = await session.AuthorizeAsync(overwrite, operations.Token);
            if (result.Succeeded) await session.SelectScopeAsync(false, operations.Token);
        })));
        body.Controls.Add(Ui.Label("上級：すでにある rclone のリモートを使う"));
        var remotes = new ComboBox { Width = 440, DropDownStyle = ComboBoxStyle.DropDownList };
        body.Controls.Add(remotes);
        body.Controls.Add(Ui.Button("接続先の一覧を読む", async (_, _) =>
        {
            // Keep this page alive so the populated choice is not replaced by Render.
            if (busy) return;
            ResetOperations();
            busy = true; page.Enabled = false; back.Enabled = false;
            try
            {
                remotes.Items.Clear();
                remotes.Items.AddRange((await session.ExistingRemotesAsync(operations.Token)).Cast<object>().ToArray());
                if (remotes.Items.Count > 0) remotes.SelectedIndex = 0;
                message.Text = remotes.Items.Count == 0 ? "既定の rclone 設定に接続先がありません。" : "使う接続先を選び、「この接続先を使う」を押してください。";
            }
            catch (Exception e) { message.Text = Ui.Failure(e); }
            finally { busy = false; if (closing) Close(); else { page.Enabled = true; back.Enabled = true; } }
        }));
        body.Controls.Add(Ui.Button("この接続先を使う", async (_, _) => await RunAsync(async () =>
        {
            if (remotes.SelectedItem is not string name) throw new ArgumentException("一覧から接続先を選んでください。");
            await session.UseExistingRemoteAsync(name, operations.Token);
            await session.SelectScopeAsync(false, operations.Token);
        })));
    }

    private void Scope(FlowLayoutPanel body)
    {
        body.Controls.Add(Ui.Label("直下のフォルダを表示して、見える範囲を確認します。"));
        var own = new RadioButton { Text = "自分のフォルダ（" + session.Draft.Remote.TrimEnd('/') + "）", AutoSize = true, Checked = !session.TeamRoot };
        var team = new RadioButton { Text = "チームの最上位（" + session.Draft.Remote.TrimEnd('/') + "/）", AutoSize = true, Checked = session.TeamRoot };
        body.Controls.Add(own); body.Controls.Add(team);
        own.CheckedChanged += async (_, _) => { if (own.Checked && !busy) await RunAsync(() => session.SelectScopeAsync(false, operations.Token)); };
        team.CheckedChanged += async (_, _) => { if (team.Checked && !busy) await RunAsync(() => session.SelectScopeAsync(true, operations.Token)); };
        var folders = new ListBox { Width = 712, Height = 200, IntegralHeight = false };
        folders.Items.AddRange(session.Folders.Select(f => f.Name).Cast<object>().ToArray());
        if (folders.Items.Count == 0) folders.Items.Add(session.ScopeVerified ? "直下にフォルダはありません。" : "範囲を選ぶか「確認し直す」を押してください。");
        body.Controls.Add(folders);
        body.Controls.Add(Ui.Button("確認し直す", async (_, _) => await RunAsync(() => session.SelectScopeAsync(session.TeamRoot, operations.Token))));
    }

    private void Options(FlowLayoutPanel body)
    {
        body.Controls.Add(Ui.Label("割り当て先のドライブと、開いたファイルを手元に残す時間を選びます。"));
        body.Controls.Add(Ui.Label("割り当て先"));
        drive = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        drive.Items.AddRange(Enumerable.Range('D', 23).Select(c => ((char)c) + ":").Cast<object>().ToArray());
        drive.SelectedItem = session.Draft.MountPoint;
        if (drive.SelectedIndex < 0) drive.SelectedItem = "T:";
        body.Controls.Add(drive);
        body.Controls.Add(Ui.Label("手元に残す時間"));
        retention = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        retention.Items.AddRange(["30 分", "1 時間", "3 時間", "1 日", "1 週間"]);
        retention.SelectedIndex = Array.IndexOf(Retentions, session.Draft.Retention);
        body.Controls.Add(retention);
        body.Controls.Add(Ui.Button("表示しないフォルダを選ぶ", (_, _) =>
        {
            SaveOptions();
            using var hidden = new HiddenFoldersForm(session.BrowsingConfig, update =>
            {
                session.SetHidden(update(session.BrowsingConfig).HiddenPaths);
                return Task.CompletedTask;
            }, () => message.Text = "この窓の選択に取り込みました。最後の段で確定します。");
            hidden.ShowDialog(this);
        }));
        body.Controls.Add(Ui.Label("表示しないフォルダ：" + session.Draft.HiddenPaths.Length + " 件。最後の段でまとめて保存します。"));
    }

    private void SaveOptions()
    {
        if (session.Step == SetupStep.Options && drive?.SelectedItem is string selected && retention is { SelectedIndex: >= 0 })
            session.SetOptions(selected, Retentions[retention.SelectedIndex]);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || IsDisposed) return;
        // A cancelled operation can be retried without reusing a cancelled token.
        ResetOperations();
        busy = true; page.Enabled = false; back.Enabled = next.Enabled = false; cancelLogin.Visible = true;
        try { await action(); if (!IsDisposed) message.Text = session.Message; }
        catch (OperationCanceledException) { if (!IsDisposed) message.Text = "操作を取り消しました。もう一度始められます。"; }
        catch (Exception e) { if (!IsDisposed) message.Text = Ui.Failure(e).ReplaceLineEndings(Environment.NewLine); }
        finally
        {
            busy = false;
            if (closing) Close();
            else if (!IsDisposed) { page.Enabled = true; Render(); }
        }
    }

    private void ResetOperations()
    {
        if (!operations.IsCancellationRequested) return;
        operations.Dispose(); operations = new CancellationTokenSource();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; operations.Cancel(); session.Dispose(); operations.Dispose(); }
        base.Dispose(disposing);
    }
}
