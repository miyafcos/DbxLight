namespace DbxLight.Tray;

public class DbxForm : Form
{
    protected override bool ShowWithoutActivation => true;
}

internal static class Ui
{
    public static readonly Font AppFont = new("Yu Gothic UI", 9f);
    public static void Configure(Form form, string title, Size size)
    {
        form.Text = title;
        form.Font = AppFont;
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.ClientSize = size;
        form.MinimumSize = new Size(size.Width + 16, size.Height + 40);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Padding = new Padding(16);
        form.ShowIcon = false;
    }
    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 8, 8),
        Font = bold ? new Font(AppFont, FontStyle.Bold) : AppFont, TextAlign = ContentAlignment.MiddleLeft
    };
    public static Button Button(string text, EventHandler? click = null)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(104, 32), Margin = new Padding(8, 0, 0, 0) };
        if (click is not null) button.Click += click;
        return button;
    }
    public static FlowLayoutPanel Buttons(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        flow.Controls.AddRange(controls);
        return flow;
    }
    public static TableLayoutPanel Column(int rows)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Margin = Padding.Empty, Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }
    public static string Failure(Exception error) => DbxLight.Core.StartupFailure.Get(error) is not null ? DbxLight.Core.StartupFailure.PublicMessage(error) :
        error is DbxLight.Core.PendingUploadsException ? error.Message : DbxLight.Core.StartupFailure.PublicMessage(error);

}

internal enum StopChoice { Cancel, Wait, Force }
internal sealed class StopChoiceForm : Form
{
    public StopChoice Choice { get; private set; }
    public StopChoiceForm(long? count, bool exiting, string? reason = null)
    {
        Ui.Configure(this, exiting ? "DbxLight を終了" : "DbxLight を一時停止", new Size(720, reason is null ? 304 : 368));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        var column = Ui.Column(3);
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.Controls.Add(Ui.Label(count is { } n ? TrayPresentation.StopWaiting(n) :
            "件数は確認できません。接続が戻るまで停止を待ちます。"), 0, 0);
        var explanation = Ui.Label(TrayPresentation.ForceExplanation +
            (reason is null ? "" : "\n\n停止を見送った理由: " + reason));
        explanation.MaximumSize = new Size(680, 0);
        column.Controls.Add(explanation, 0, 1);
        Button cancel = Ui.Button("戻る", (_, _) => { Choice = StopChoice.Cancel; Close(); });
        Button force = Ui.Button(exiting ? "強制終了" : "強制的に一時停止", (_, _) => { Choice = StopChoice.Force; Close(); });
        Button wait = Ui.Button("上げ終わるまで待つ", (_, _) => { Choice = StopChoice.Wait; Close(); });
        column.Controls.Add(Ui.Buttons(cancel, force, wait), 0, 2);
        Controls.Add(column);
        CancelButton = cancel;
        AcceptButton = wait;
    }
}
