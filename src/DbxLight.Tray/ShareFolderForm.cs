using DbxLight.Core;

namespace DbxLight.Tray;

internal sealed class ShareFolderForm : DbxForm
{
    private readonly string localPath;
    private readonly TextBox path = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, Height = 48,
        ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
    private readonly Label state = Ui.Label("Dropbox の共有状態を確認しています…", true);
    private readonly ListView members = new() { Dock = DockStyle.Fill, View = View.Details,
        FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, AccessibleName = "共有メンバー" };
    private readonly TextBox recipients = new() { Name = "inviteEmails", Multiline = true, Height = 58, ScrollBars = ScrollBars.Vertical,
        AccessibleName = "メールアドレス", PlaceholderText = "複数の宛先は改行またはカンマで区切ります" };
    private readonly ComboBox access = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "権限" };
    private readonly TextBox message = new() { Name = "inviteMessage", AccessibleName = "招待に添える一言" };
    private readonly Label result = Ui.Label("");
    private readonly Button inviteButton, refreshButton;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<CancellationToken, Task<ShareFolderInfo>> load;
    private readonly Func<ShareFolderInfo, FolderInvitePlan, CancellationToken, Task<(FolderInviteResult Result, FolderOperationException? Error)>> send;
    private ShareFolderInfo? info;
    private bool busy, sending;

    internal ShareFolderForm(string localPath, Func<CancellationToken, Task<ShareFolderInfo>> load,
        Func<ShareFolderInfo, FolderInvitePlan, CancellationToken, Task<(FolderInviteResult Result, FolderOperationException? Error)>> send)
    {
        this.localPath = localPath;
        this.load = load;
        this.send = send;
        Ui.Configure(this, "このフォルダを共有", new Size(740, 540));
        path.Text = localPath;
        path.AccessibleName = "対象フォルダ";
        var column = Ui.Column(6);
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(path, 0, 0);
        column.Controls.Add(state, 0, 1);
        members.Columns.Add("メンバー", 218);
        members.Columns.Add("メールアドレス", 284);
        members.Columns.Add("権限", 155);
        column.Controls.Add(members, 0, 2);

        var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 10, 0, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        access.Items.AddRange([FolderSharing.AccessLabel("viewer"), FolderSharing.AccessLabel("editor")]);
        access.SelectedIndex = 0;
        (string Label, Control Input)[] rows = [("メールアドレス", recipients), ("権限", access), ("一言（任意）", message)];
        for (int i = 0; i < rows.Length; i++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows[i].Input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            fields.Controls.Add(Ui.Label(rows[i].Label), 0, i);
            fields.Controls.Add(rows[i].Input, 1, i);
        }
        column.Controls.Add(fields, 0, 3);
        result.MaximumSize = new Size(660, 0);
        column.Controls.Add(result, 0, 4);

        inviteButton = Ui.Button("招待する", async (_, _) => await InviteAsync());
        inviteButton.Name = "previewInvitation";
        refreshButton = Ui.Button("状態を更新", async (_, _) => await LoadAsync());
        var close = Ui.Button("閉じる", (_, _) => Close());
        column.Controls.Add(Ui.Buttons(close, inviteButton, refreshButton), 0, 5);
        Controls.Add(column);
        CancelButton = close;
        UpdateButtons();
        Shown += async (_, _) => await LoadAsync();
        FormClosing += (_, e) => { if (sending) e.Cancel = true; };
        Resize += (_, _) => result.MaximumSize = new Size(Math.Max(1, column.ClientSize.Width - 8), 0);
    }

    private async Task LoadAsync(string? outcome = null)
    {
        busy = true;
        info = null;
        UpdateButtons();
        state.Text = "Dropbox の共有状態を確認しています…";
        path.Text = localPath;
        members.Items.Clear();
        result.Text = outcome ?? "";
        try
        {
            var loaded = await load(lifetime.Token);
            if (!IsDisposed) Present(loaded, outcome);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!IsDisposed)
            {
                state.Text = "共有状態を確認できませんでした。";
                result.Text = Combine(outcome, e is FolderOperationException ? e.Message :
                    FolderOperationException.For("remote_error").Message);
            }
        }
        finally
        {
            busy = false;
            if (!IsDisposed) UpdateButtons();
        }
    }

    private void Present(ShareFolderInfo loaded, string? outcome)
    {
        info = loaded;
        if (loaded.CopyPath is { } shown) path.Text = shown;
        var status = loaded.View.Status;
        string shareState = status.Shared ? "共有済み" + (status.Access is { } own ? "　自分の権限  " + FolderSharing.AccessLabel(own) : "") :
            status.Inherited ? "上位フォルダから共有の権限を引き継いでいます。" : "未共有";
        state.Text = shareState + "\n接続先 " + loaded.View.Target.Account.ConnectionLabel;
        members.BeginUpdate();
        foreach (var member in status.Members)
        {
            string name = member.Kind == "invitee" ? member.Name + "（招待済み）" : member.Name;
            members.Items.Add(new ListViewItem([name, member.Email ?? "", FolderSharing.AccessLabel(member.Access)]));
        }
        members.EndUpdate();
        string? mountError = loaded.Mount is null ? FolderOperationException.For("mount_unverified").Message : null;
        result.Text = Combine(outcome, Combine(mountError, loaded.PathError));
    }

    private async Task InviteAsync()
    {
        if (busy || info is not { CopyPath: { } copyPath, Mount: not null } selected) return;
        string? outcome = null;
        busy = true;
        UpdateButtons();
        try
        {
            var request = FolderInviteRequest.Create(recipients.Text.Split(['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), access.SelectedIndex == 0 ? "viewer" : "editor", message.Text);
            var plan = FolderSharing.Plan(selected.View, copyPath, request);
            using var confirmation = new ShareInvitationConfirmation(plan);
            if (confirmation.ShowDialog(this) != DialogResult.OK) return;
            sending = true;
            result.Text = "Dropbox に招待を送っています…";
            var (sent, error) = await send(selected, plan, lifetime.Token);
            outcome = Combine(sent.Progress, error?.Message);
            if (error is null) { recipients.Clear(); message.Clear(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result.Text = e is FolderOperationException or ArgumentException ? e.Message : FolderOperationException.For("remote_error").Message;
            if (e is FolderOperationException { Code: "sharing_changed" }) info = null;
        }
        finally
        {
            sending = busy = false;
            if (!IsDisposed) UpdateButtons();
        }
        if (outcome is not null) await LoadAsync(outcome);
    }

    private void UpdateButtons()
    {
        inviteButton.Enabled = !busy && info is { CopyPath: not null, Mount: not null };
        refreshButton.Enabled = !busy;
    }

    private static string Combine(string? first, string? second) =>
        string.Join(Environment.NewLine, new[] { first, second }.Where(text => !string.IsNullOrEmpty(text)));

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class ShareInvitationConfirmation : Form
{
    internal ShareInvitationConfirmation(FolderInvitePlan plan)
    {
        Ui.Configure(this, "招待する相手の確認", new Size(692, 438));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        var column = Ui.Column(4);
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(Ui.Label("このフォルダに招待する相手と権限を確認してください。", true), 0, 0);
        var lines = new List<string> { "フォルダ", plan.Path, "接続先  " + plan.Target.Account.ConnectionLabel,
            "", "権限  " + FolderSharing.AccessLabel(plan.Request.Access), "", "招待する相手" };
        lines.AddRange(plan.Request.To);
        if (plan.Request.Message is { } followUp) lines.AddRange(["", "招待に添える一言", followUp]);
        if (plan.Inherited) lines.AddRange(["", FolderSharing.InheritedNotice]);
        else if (plan.WouldShareFolder) lines.AddRange(["", "このフォルダを共有フォルダにします。"]);
        column.Controls.Add(new TextBox
        {
            Text = string.Join(Environment.NewLine, lines), Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, TabStop = false, AccessibleName = "招待の内容"
        }, 0, 1);
        column.Controls.Add(Ui.Label("「招待する」を押すと、相手に Dropbox の招待メールが届きます。"), 0, 2);
        Button cancel = Ui.Button("やめる");
        cancel.DialogResult = DialogResult.Cancel;
        Button send = Ui.Button("招待する");
        send.DialogResult = DialogResult.OK;
        column.Controls.Add(Ui.Buttons(send, cancel), 0, 3);
        Controls.Add(column);
        AcceptButton = CancelButton = cancel;
        Shown += (_, _) => cancel.Select();
    }
}
