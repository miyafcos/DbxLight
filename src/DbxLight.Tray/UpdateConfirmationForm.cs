namespace DbxLight.Tray;

internal sealed class UpdateConfirmationForm : Form
{
    internal UpdateConfirmationForm(string version, string mount)
    {
        Ui.Configure(this, "更新を確認", new Size(720, 280));
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var column = Ui.Column(2);
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(Ui.Label($"バージョン {version} に更新します。更新のあいだ、{mount} のマウントを停止します。開いているファイルは保存して閉じてください。アップロード待ちがあるときは、終わるまで待ってから更新します。"), 0, 0);
        Button update = Ui.Button("更新する", (_, _) => { DialogResult = DialogResult.OK; Close(); });
        Button cancel = Ui.Button("キャンセル", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        column.Controls.Add(Ui.Buttons(cancel, update), 0, 1);
        Controls.Add(column); AcceptButton = update; CancelButton = cancel;
    }
}
