using DbxLight.Core;

namespace DbxLight.Tray;

public sealed class SettingsForm : DbxForm
{
    private static readonly string[] Retentions = ["30m", "1h", "3h", "1d", "1w"];
    private readonly ComboBox retention = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox drive = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox remote = new(), cacheLimit = new(), cacheDir = new();
    private readonly CheckBox autoStart = new() { Text = "Windows の起動時に始める", AutoSize = true };
    private string? noticeMount;
    private readonly TextBox shellNotice = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
        MinimumSize = new Size(0, 88), ScrollBars = ScrollBars.Vertical, Visible = false, BackColor = SystemColors.Window };
    private readonly Label permission = Ui.Label("", true);
    private readonly TextBox result = new() { Text = "変更したあとに「適用」を押してください。", Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, BorderStyle = BorderStyle.None };
    private readonly Button apply;
    private readonly RegistrationService registration;
    private readonly string executable;
    private readonly AppOptions options;
    private readonly Func<Func<DbxConfig, DbxConfig>, Task> save;
    private bool originalAutoStart;
    private DbxConfig currentConfig;
    private readonly Func<DbxConfig, bool, bool, Task>? writeAccess;
    private readonly CheckBox writeEnabled = new() { Text = "書き込みを有効にする", AutoSize = true, AutoCheck = false };

    public SettingsForm(DbxConfig config, RegistrationService registration, string executable, AppOptions options,
        Func<Func<DbxConfig, DbxConfig>, Task> save, Func<Task>? reauthenticate = null, Func<Task>? logout = null, Func<DbxConfig, bool, bool, Task>? writeAccess = null)
    {
        currentConfig = config; this.writeAccess = writeAccess;
        this.registration = registration; this.executable = executable; this.options = options; this.save = save;
        Ui.Configure(this, "DbxLight の設定", new Size(832, 752));
        var column = Ui.Column(9);
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var explanation = Ui.Label("開いたファイルを手元に残す時間と、使うドライブを選びます。\n上げ待ちがある間は、設定を変更できません。\n取り出し先をキャッシュと別のボリュームにすると、移動に時間がかかることがあります。");
        column.Controls.Add(permission, 0, 0);
        column.Controls.Add(explanation, 0, 1);
        column.Controls.Add(Ui.Label("remote を変えても、書き込みを許すリモートの一覧になければ読み取り専用です。"), 0, 2);
        var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Margin = Padding.Empty };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        retention.Items.AddRange(["30 分", "1 時間", "3 時間", "1 日", "1 週間"]);
        retention.SelectedIndex = Array.IndexOf(Retentions, config.Retention);
        drive.Items.AddRange(Enumerable.Range('D', 23).Select(c => ((char)c) + ":").Cast<object>().ToArray());
        if (!drive.Items.Contains(config.MountPoint)) drive.Items.Add(config.MountPoint);
        drive.SelectedItem = config.MountPoint;
        remote.Text = config.Remote; cacheLimit.Text = config.CacheMaxSize; cacheDir.Text = config.CacheDir;
        (string Title, Control Input)[] rows =
        [
            ("雲の置き場 (remote)", remote), ("手元に残す時間", retention), ("キャッシュの上限 (例: 5G)", cacheLimit),
            ("キャッシュの置き場", cacheDir), ("割り当て先", drive), ("自動起動", autoStart)
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows[i].Input.Dock = DockStyle.Fill; rows[i].Input.Margin = new Padding(0, 8, 0, 8);
            fields.Controls.Add(Ui.Label(rows[i].Title), 0, i); fields.Controls.Add(rows[i].Input, 1, i);
        }
        originalAutoStart = registration.IsAutoStart();
        autoStart.Checked = originalAutoStart;
        apply = Ui.Button("適用", async (_, _) => await ApplyAsync());
        Button loginAgain = Ui.Button("ログインし直す", async (_, _) =>
        {
            if (reauthenticate is not null) await reauthenticate();
        });
        Button signOut = Ui.Button("ログアウト");
        loginAgain.Enabled = reauthenticate is not null; signOut.Enabled = logout is not null;
        signOut.Click += async (_, _) =>
        {
            if (logout is null || MessageBox.Show(this,
                "割り当てを止めて、接続先から外します。専用のログインで使った鍵も消します。",
                "ログアウト", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            signOut.Enabled = loginAgain.Enabled = apply.Enabled = false;
            try { await logout(); remote.Text = ""; result.Text = "ログアウトしました。"; }
            catch (Exception e) { if (!IsDisposed) result.Text = Ui.Failure(e).ReplaceLineEndings(Environment.NewLine); }
            finally { if (!IsDisposed) { signOut.Enabled = true; loginAgain.Enabled = reauthenticate is not null; apply.Enabled = true; } }
        };
        column.Controls.Add(Ui.Buttons(signOut, loginAgain), 0, 3);
        writeEnabled.Click += async (_, _) => await ChangeWriteAccessAsync();
        column.Controls.Add(writeEnabled, 0, 4);
        column.Controls.Add(fields, 0, 5);
        column.Controls.Add(shellNotice, 0, 6);
        column.Controls.Add(result, 0, 7);
        column.Controls.Add(Ui.Buttons(Ui.Button("閉じる", (_, _) => Close()), apply), 0, 8);
        Controls.Add(column);
        AcceptButton = apply;
        UpdateSnapshot(new(new(), [], [], false, null), config);
    }
    public void UpdateSnapshot(TraySnapshot snapshot, DbxConfig config)
    {
        if (IsDisposed) return;
        currentConfig = config;
        DbxConfig active = snapshot.Status.Running ? snapshot.ActiveConfig ?? config : config;
        writeEnabled.Checked = active.WriteAllowedRemotes.Contains(active.Remote, StringComparer.Ordinal);
        writeEnabled.Enabled = writeAccess is not null && !snapshot.Confirming && !string.IsNullOrWhiteSpace(config.Remote);
        permission.Text = "\u73fe\u5728\u306e\u5272\u308a\u5f53\u3066: " + TrayPresentation.Permission(snapshot, config)
            + (!snapshot.Confirming && !snapshot.Status.Running ? " (\u505c\u6b62\u4e2d\u306e\u8a2d\u5b9a)" : "")
            + (!snapshot.Confirming && TrayPresentation.ReadOnly(active) ? "\n新しい保存はできません。前から残っている未送信は送られることがあります。" : "");
        if (noticeMount != config.MountPoint)
        {
            noticeMount = config.MountPoint;
            shellNotice.Text = registration.ShellRegistrationNotice(config, executable, options).ReplaceLineEndings(Environment.NewLine);
            shellNotice.Visible = shellNotice.Text.Length > 0;
            if (shellNotice.Text.Length > 0 && ClientSize.Height < 820)
            {
                ClientSize = new Size(ClientSize.Width, 820);
                MinimumSize = new Size(MinimumSize.Width, 860);
            }
        }
    }

    private async Task ChangeWriteAccessAsync()
    {
        if (writeAccess is null) return;
        DbxConfig target = currentConfig;
        bool enable = !writeEnabled.Checked, confirmed = false;
        if (enable)
        {
            using var confirmation = new WriteConfirmationForm(target.Remote);
            if (confirmation.ShowDialog(this) != DialogResult.OK) return;
            confirmed = true;
        }
        writeEnabled.Enabled = apply.Enabled = false;
        try
        {
            await writeAccess(target, enable, confirmed);
            result.Text = enable ? "書き込みを有効にしました。" : "読み取り専用に戻しました。";
        }
        catch (Exception e) { if (!IsDisposed) result.Text = Ui.Failure(e).ReplaceLineEndings(Environment.NewLine); }
        finally
        {
            if (!IsDisposed) { writeEnabled.Enabled = !string.IsNullOrWhiteSpace(currentConfig.Remote); apply.Enabled = true; }
        }
    }

    private async Task ApplyAsync()
    {
        apply.Enabled = false;
        try
        {
            string newRemote = remote.Text.Trim(), newCache = cacheDir.Text.Trim(), limit = cacheLimit.Text.Trim();
            string newDrive = (string?)drive.SelectedItem ?? throw new ArgumentException("割り当て先を選んでください。");
            string keep = Retentions[retention.SelectedIndex];
            await save(c => c with { Remote = newRemote, Retention = keep, CacheDir = newCache, CacheMaxSize = limit, MountPoint = newDrive });
            // Registration is changed only after the guarded config update succeeds.
            if (autoStart.Checked != originalAutoStart)
            {
                registration.SetAutoStart(autoStart.Checked, executable, options);
                originalAutoStart = autoStart.Checked;
            }
            result.Text = "設定を適用しました。";
        }
        catch (Exception e) { if (!IsDisposed) result.Text = Ui.Failure(e).ReplaceLineEndings(Environment.NewLine); }
        finally { if (!IsDisposed) apply.Enabled = true; }
    }
}
