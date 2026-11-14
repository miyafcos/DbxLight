using System.Drawing;
using System.Windows.Forms;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    private static void WriteWarning() => FormTests.Sta(() =>
    {
        foreach (int percent in new[] { 100, 125, 150 })
        {
            using var form = new WriteConfirmationForm("sample:");
            var all = ControlsOf(form).ToArray();
            if (percent != 100)
            {
                var fonts = all.Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
                form.Scale(new SizeF(percent / 100f, percent / 100f));
                foreach (var f in fonts) f.Control.Font = new Font(f.Font.FontFamily, f.Font.Size * percent / 100f, f.Font.Style);
            }
            form.Show(); form.PerformLayout(); Application.DoEvents();
            foreach (string paragraph in new[] { RecoveryText.ConflictNotice, RecoveryText.UploadDelayNotice, RecoveryText.PreflightNotice, RecoveryText.OfficialClientNotice })
            {
                var label = all.OfType<Label>().Single(c => c.Text == paragraph);
                var measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.ClientSize.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                TestRunner.Assert(label.ClientSize.Height >= measured.Height - 2, "Clipped paragraph at " + percent);
                var bounds = new Rectangle(form.PointToClient(label.PointToScreen(Point.Empty)), label.Size);
                TestRunner.Assert(form.ClientRectangle.Contains(bounds), "Paragraph outside form at " + percent);
            }
            int buttonsBottom = all.OfType<Button>().Max(button => form.PointToClient(button.PointToScreen(Point.Empty)).Y + button.Height);
            TestRunner.Assert(form.ClientSize.Height - buttonsBottom <= Math.Ceiling(24 * percent / 100.0), "Excess empty space below buttons at " + percent);
            Console.WriteLine("J9 confirmation scale=" + percent + "; client=" + form.ClientSize.Width + "x" + form.ClientSize.Height +
                "; bottomGap=" + (form.ClientSize.Height - buttonsBottom));
            form.Close();
        }
    });
    private static IEnumerable<Control> ControlsOf(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in ControlsOf(child)) yield return descendant; }
    }
}
