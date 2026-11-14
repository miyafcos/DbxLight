namespace DbxLight.Tray;

public sealed partial class StatusForm
{
    private readonly Panel viewport = new StatusViewport { Name = "statusViewport", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
    private bool arranging;
    internal Rectangle? WorkAreaOverride { get; set; }
    private float DisplayScale => DeviceDpi / 96f * Font.Size / Ui.AppFont.Size;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToWorkArea();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToWorkArea();
    }
    internal void FitToWorkArea()
    {
        Rectangle area = WorkAreaOverride ?? Screen.FromControl(this).WorkingArea;
        MinimumSize = Size.Empty;
        MaximumSize = area.Size;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        LayoutContents();
        int footer = Controls.Cast<Control>().Where(c => c.Dock == DockStyle.Bottom).Sum(c => c.Height);
        int desiredHeight = column.GetPreferredSize(new Size(column.Width, 0)).Height + footer + Padding.Vertical + viewport.Padding.Vertical + column.Margin.Vertical + Height - ClientSize.Height;
        Height = Math.Min(area.Height, Math.Max(Height, desiredHeight));
        MinimumSize = new Size(Math.Min((int)(640 * DisplayScale), area.Width), Math.Min((int)(360 * DisplayScale), area.Height));
        if (StartPosition != FormStartPosition.Manual)
            Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
        LayoutContents();
    }
    private void LayoutContents()
    {
        if (arranging || column.RowStyles.Count < 8) return;
        arranging = true;
        try
        {
            float scale = DisplayScale;
            int textHeight = recovery.Visible ? TextRenderer.MeasureText(recovery.Text, recovery.Font,
                new Size(Math.Max(1, viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 16), int.MaxValue),
                // Native multiline edit metrics can exceed TextRenderer by a few pixels after DPI scaling.
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 20 : 0;
            column.RowStyles[1].Height = recovery.Visible ? Math.Max(Math.Max(recovery.MinimumSize.Height, 384 * scale), textHeight) : 0;
            column.RowStyles[4].Height = queue.Visible ? 160 * scale : 0;
            column.RowStyles[6].Height = 120 * scale;
            rescueSection.RowStyles[1].Height = rescued.Visible ? 240 * scale : 0;
            int width = Math.Max(1, viewport.ClientSize.Width - (viewport.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
            column.MinimumSize = new Size(width, 0);
            column.MaximumSize = new Size(width, 0);
            column.Width = width;
            column.PerformLayout();
        }
        finally { arranging = false; }
    }
    internal void ScrollToHistory() => viewport.AutoScrollPosition = new Point(0, rescueSection.Top);
    internal void ScrollToEnd() => viewport.AutoScrollPosition = new Point(0, viewport.VerticalScroll.Maximum);
}

internal sealed class StatusViewport : Panel
{
    // Preserve the user's scroll position when polling updates or layout runs.
    // Focus changes explicitly scroll the relevant content into view.
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
}
