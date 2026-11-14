namespace DbxLight.Tray;

public sealed partial class StatusForm : DbxForm
{
    private DbxLight.Core.DbxConfig displayedConfig = new();
    private readonly Label summary = Ui.Label("状態を読み込み中");
    private readonly TextBox recovery = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
        Name = "recovery", MinimumSize = new Size(0, 384), ScrollBars = ScrollBars.Vertical, Visible = false, BackColor = SystemColors.Window };
    private readonly Label oldest = Ui.Label("上げ待ちを読み込み中");
    private readonly DataGridView queue = new();
    private readonly Label queueHeading = Ui.Label("上げ待ちのファイル");
    private readonly TableLayoutPanel column = Ui.Column(8);
    private readonly DataGridView rescued = new();
    private readonly Label rescueHeading = Ui.Label("取り出して保全した物", true);
    private readonly TableLayoutPanel rescueSection = new() { ColumnCount = 1, RowCount = 3, AutoSize = true,
        Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly TextBox errors = new();
    private DbxLight.Core.MountLogInfo displayedLog = new();
    private readonly Button openLogs = Ui.Button("記録のフォルダを開く");
    private readonly Label startup = Ui.Label("");
    public StatusForm()
    {
        Ui.Configure(this, "DbxLight の状態", new Size(864, 608));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.RemoveAt(8);
        column.RowStyles[1].SizeType = SizeType.Absolute;
        column.Dock = DockStyle.None; column.Anchor = AnchorStyles.Top | AnchorStyles.Left; column.AutoSize = true; column.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        column.Controls.Add(summary, 0, 0);
        column.Controls.Add(recovery, 0, 1);
        column.Controls.Add(oldest, 0, 2);
        column.Controls.Add(queueHeading, 0, 3);
        queue.Dock = DockStyle.Fill; queue.ReadOnly = true; queue.AllowUserToAddRows = false;
        queue.AllowUserToDeleteRows = false; queue.RowHeadersVisible = false; queue.AutoGenerateColumns = false;
        queue.BackgroundColor = SystemColors.Window; queue.BorderStyle = BorderStyle.FixedSingle;
        queue.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        queue.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        queue.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        queue.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        queue.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "名前とパス", FillWeight = 60 });
        queue.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大きさ", FillWeight = 20 });
        queue.Columns.Add(new DataGridViewTextBoxColumn { Name = "tries", HeaderText = "試した回数", FillWeight = 20 });
        column.Controls.Add(queue, 0, 4);
        rescueSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rescueSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rescueSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rescueSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rescued.Name = "rescued"; rescued.Dock = DockStyle.Fill; rescued.ReadOnly = true; rescued.AllowUserToAddRows = false;
        rescued.AllowUserToDeleteRows = false; rescued.RowHeadersVisible = false; rescued.AutoGenerateColumns = false;
        rescued.BackgroundColor = SystemColors.Window; rescued.BorderStyle = BorderStyle.FixedSingle;
        rescued.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        rescued.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        rescued.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        rescued.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        rescued.Columns.Add(new DataGridViewTextBoxColumn { Name = "original", HeaderText = "元のパス", FillWeight = 24 });
        rescued.Columns.Add(new DataGridViewTextBoxColumn { Name = "reason", HeaderText = "理由と次にすること", FillWeight = 28 });
        rescued.Columns.Add(new DataGridViewTextBoxColumn { Name = "destination", HeaderText = "取り出し先", FillWeight = 32 });
        rescued.Columns.Add(new DataGridViewButtonColumn { Name = "open", HeaderText = "", Text = "保存先を開く", UseColumnTextForButtonValue = true, FillWeight = 16 });
        rescued.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != rescued.Columns["open"]!.Index ||
                rescued.Rows[e.RowIndex].Tag is not DbxLight.Core.RescueRecord item) return;
            try
            {
                string? folder = DbxLight.Core.RescueStore.OpenFolder(item, displayedConfig);
                if (folder is null) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                    { UseShellExecute = true, ArgumentList = { folder } });
            }
            catch (Exception error) { MessageBox.Show(this, Ui.Failure(error), "保存先を開けません"); }
        };
        rescueHeading.Visible = false; rescued.Visible = false;
        rescueSection.Controls.Add(rescueHeading, 0, 0);
        rescueSection.Controls.Add(rescued, 0, 1);
        rescueSection.Controls.Add(Ui.Label("失敗したファイルと記録の末尾 20 件", true), 0, 2);
        column.Controls.Add(rescueSection, 0, 5);
        errors.Multiline = true; errors.ReadOnly = true; errors.ScrollBars = ScrollBars.Both; errors.WordWrap = false;
        errors.Dock = DockStyle.Fill; errors.BackColor = SystemColors.Window;
        column.Controls.Add(errors, 0, 6);
        column.Controls.Add(startup, 0, 7);
        openLogs.Enabled = false;
        openLogs.Click += (_, _) =>
        {
            string? folder = DbxLight.Core.MountLogStore.OpenFolder(displayedLog);
            if (folder is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                { UseShellExecute = true, ArgumentList = { folder } });
        };
        var buttons = Ui.Buttons(Ui.Button("閉じる", (_, _) => Close()), openLogs);
        buttons.Dock = DockStyle.Bottom;
        viewport.Controls.Add(column);
        Controls.Add(viewport); Controls.Add(buttons);
        viewport.SizeChanged += (_, _) => LayoutContents();
        foreach (Control focusable in new Control[] { recovery, queue, errors, rescued })
            focusable.Enter += (_, _) => viewport.ScrollControlIntoView(focusable);
    }
    public void UpdateSnapshot(TraySnapshot snapshot, QueueAge? age, DbxLight.Core.DbxConfig config)
    {
        if (IsDisposed) return;
        displayedConfig = config;
        var s = snapshot.Status;
        displayedLog = s.LogCapture;
        openLogs.Enabled = DbxLight.Core.MountLogStore.OpenFolder(displayedLog) is not null;
        string Count(long? n) => n is { } value ? $"{value:N0} 件" : "確認できません";
        summary.Text = TrayPresentation.Headline(snapshot, config) + "\n" +
            $"上げ待ち {Count(s.UploadsQueued)}    上げている途中 {Count(s.UploadsInProgress)}    失敗 {Count(s.ErroredFiles)}\n" +
            $"キャッシュ {Count(s.CacheFiles)} / {TrayPresentation.Bytes(s.CacheBytes)}    rclone のメモリ {TrayPresentation.Bytes(s.PrivateMemoryBytes)}";
        if (s.Preparation is { } preparing)
            summary.Text += $"\n起動の準備: {preparing.Stage} {preparing.Completed}/{preparing.Total} 件　開始 {preparing.StartedUtc:O}" +
                (preparing.Stopping ? "　取り出しの区切りで止めます。" : "");
        if (s.UnregisteredCredentialFiles.Count > 0)
            summary.Text += "\n未登録の鍵の作業ファイルを残しています: " + string.Join("; ", s.UnregisteredCredentialFiles);
        recovery.Text = (s.RecoveryLocation ?? "").ReplaceLineEndings(Environment.NewLine);
        recovery.Visible = s.RecoveryRequired || s.ProcessUncertain;
        var extracted = (snapshot.Rescued ?? []).Where(r => !r.Acknowledged).ToArray();
        rescued.Rows.Clear();
        foreach (var item in extracted)
        {
            string detail = item.Reason + "\n" + item.NextAction;
            if (item.RecordStatus == "foreign") detail = item.Reason + $" {item.ForeignRecordCount} \u4ef6";
            string original = item.RecordStatus == "unknown" && item.RecordPath.Length > 0
                ? item.RecordPath : item.CloudPath ?? item.OriginalPath;
            int row = rescued.Rows.Add(original, detail, item.DestinationPath);
            if (item.RecordStatus != "verified") rescued.Rows[row].Cells["open"] =
                new DataGridViewTextBoxCell { Value = item.RecordStatus == "foreign" ? "" : "\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093" };
            rescued.Rows[row].Tag = item;
            rescued.Rows[row].Cells["destination"].ToolTipText = item.DestinationPath;
            rescued.Rows[row].Cells["original"].ToolTipText = original;
        }
        bool hasRescue = extracted.Length > 0;
        bool hasDetails = hasRescue;
        bool showQueue = !hasDetails || snapshot.Queue.Count > 0;
        queue.Visible = showQueue; queueHeading.Visible = showQueue;
        column.RowStyles[4].SizeType = SizeType.Absolute;
        column.RowStyles[4].Height = showQueue ? 160 * DisplayScale : 0;
        int foreignRecords = extracted.Sum(r => r.ForeignRecordCount);
        int pendingTotal = Math.Max(extracted.Length, s.RescueNotice.Unacknowledged + s.RescueNotice.UnknownRecords + s.RescueNotice.ForeignRecords);
        rescueHeading.Text = hasRescue && extracted.All(r => r.RecordStatus == "foreign")
            ? $"ほかの設定の物 {foreignRecords} 件" : pendingTotal > extracted.Length ? $"取り出して保全した物 全 {pendingTotal} 件のうち新しい {extracted.Length} 件（全件は rescue list --all）" : $"取り出して保全した物 {extracted.Length} 件（原本と記録は残します）";
        rescued.Visible = hasRescue; rescueHeading.Visible = hasDetails;
        rescueSection.RowStyles[1].Height = hasDetails ? 240 * DeviceDpi / 96f : 0;
        oldest.Text = TrayPresentation.QueueAgeExplanation(snapshot, age);
        queue.Rows.Clear();
        foreach (QueueItem item in snapshot.Queue)
        {
            int i = queue.Rows.Add(item.Name, TrayPresentation.Bytes(item.Size), item.Tries.ToString());
            if (item.Tries > 0)
            {
                queue.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(168, 35, 35);
                queue.Rows[i].Cells[0].Value = "失敗: " + item.Name;
            }
        }
        if (snapshot.Queue.Count == 0) queue.Rows.Add(snapshot.QueueKnown ? "上げ待ちはありません" : "上げ待ちを確認できません", "", "");
        var lines = snapshot.Queue.Where(q => q.Tries > 0).Select(q => $"失敗: {q.Name} (試した回数 {q.Tries})")
            .Concat(snapshot.Errors).ToList();
        if (snapshot.Error is not null) lines.Insert(0, "状態を確認できません: " + snapshot.Error);
        string logState = !s.LogCapture.Captured ? s.LogCapture.Reason + Environment.NewLine + "失敗の有無を確認できません。" :
            !snapshot.LogReadSucceeded ? "記録はありますが読めません（失敗の有無を確認できません）。" :
            s.LogCapture.Reason + Environment.NewLine +
                (snapshot.Errors.Count == 0 ? "読めた範囲に失敗の記録はありません。" : "読めた範囲の失敗の記録です。");
        errors.Text = logState + (lines.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, lines));
        startup.Text = snapshot.StartupRecords is { Count: > 0 } records
            ? "起動時の機能確認 (害なし)\n" + records[^1] : "";
        LayoutContents();
    }
}
