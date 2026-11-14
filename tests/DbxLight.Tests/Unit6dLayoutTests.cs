using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6dTests
{
    private static IEnumerable<Control> FormControls(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(FormControls(c)));
    private static void SmallStatusWindow() => FormTests.Sta(() =>
    {
        foreach (int percent in new[] { 100, 125, 150 })
        {
            using var form = new StatusForm { WorkAreaOverride = new Rectangle(0, 0, 1366, 700),
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
            var record = new RescueRecord { CloudPath = "Sample/saved.txt", DestinationPath = @"D:\Sample\rescue\saved.txt",
                Reason = "Sample preserved body" };
            form.UpdateSnapshot(new TraySnapshot(new DbxStatus { RecoveryRequired = true,
                RecoveryLocation = string.Join("\n", Enumerable.Repeat("Sample recovery explanation", 8)), RescueFiles = 1 },
                [], [], true, null, Rescued: [record], StartupRecords: ["Sample harmless probe"]), null, new DbxConfig());
            if (percent != 100)
            {
                var controls = FormControls(form).Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
                form.Scale(new SizeF(percent / 100f, percent / 100f));
                foreach (var item in controls) item.Control.Font = new Font(item.Font.FontFamily, item.Font.Size * percent / 100f, item.Font.Style);
            }
            form.Show(); form.FitToWorkArea(); form.PerformLayout(); Application.DoEvents();
            TestRunner.Assert(form.Width <= 1366 && form.Height <= 700, "Status window exceeds the work area.");
            var viewport = FormTests.Field<Panel>(form, "viewport");
            TestRunner.Assert(viewport.VerticalScroll.Visible, "Long status content cannot scroll.");
            foreach (Button button in FormControls(form).OfType<Button>())
            {
                Rectangle bounds = new(form.PointToClient(button.PointToScreen(Point.Empty)), button.Size);
                TestRunner.Assert(form.ClientRectangle.Contains(bounds), "Close button is outside the window.");
                TestRunner.Assert(button.GetPreferredSize(button.Size).Height <= button.Height + 2, "Button text is clipped.");
            }
            form.ScrollToEnd(); form.PerformLayout(); Application.DoEvents();
            var startup = FormTests.Field<Label>(form, "startup");
            Rectangle last = new(viewport.PointToClient(startup.PointToScreen(Point.Empty)), startup.Size);
            TestRunner.Assert(last.Bottom <= viewport.ClientSize.Height + 2 && last.Top >= 0,
                $"The final status content cannot be reached: last={last}; viewport={viewport.ClientSize}; scroll={viewport.AutoScrollPosition}; max={viewport.VerticalScroll.Maximum}; large={viewport.VerticalScroll.LargeChange}.");
            Console.WriteLine($"S7 workArea=1366x700 scale={percent}% form={form.Width}x{form.Height}; scrolling and close button reachable.");
            form.Close();
        }
    });
}
