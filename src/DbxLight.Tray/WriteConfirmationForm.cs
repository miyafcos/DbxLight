namespace DbxLight.Tray;

public sealed class WriteConfirmationForm : DbxForm
{
    public WriteConfirmationForm(string remote)
    {
        Ui.Configure(this, "書き込みを有効にする", new Size(820, 320));
        MinimumSize = new Size(MinimumSize.Width, 0);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var column = Ui.Column(7);
        for (int i = 0; i < 7; i++) column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(Ui.Label("現在の接続先：" + remote, true), 0, 0);
        column.Controls.Add(Ui.Label(DbxLight.Core.RecoveryText.ConflictNotice), 0, 1);
        column.Controls.Add(Ui.Label(DbxLight.Core.RecoveryText.UploadDelayNotice), 0, 2);
        column.Controls.Add(Ui.Label(DbxLight.Core.RecoveryText.PreflightNotice), 0, 3);
        column.Controls.Add(Ui.Label(DbxLight.Core.RecoveryText.OfficialClientNotice), 0, 4);
        column.Controls.Add(Ui.Label("有効にすると、いったん止めて同じ接続先につなぎ直します。"), 0, 5);
        Button cancel = Ui.Button("戻る", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        Button confirm = Ui.Button("確認して書き込みを有効にする", (_, _) => { DialogResult = DialogResult.OK; Close(); });
        column.Controls.Add(Ui.Buttons(cancel, confirm), 0, 6);
        Controls.Add(column); CancelButton = cancel;
        Shown += (_, _) =>
        {
            int contentHeight = column.GetPreferredSize(new Size(ClientSize.Width - Padding.Horizontal, 0)).Height;
            ClientSize = new Size(ClientSize.Width, contentHeight + Padding.Vertical);
        };
    }
}
