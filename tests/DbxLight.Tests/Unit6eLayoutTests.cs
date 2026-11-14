using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private static IEnumerable<Control> AllControls(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(AllControls(c)));

    private static void LargeStatusContent() => FormTests.Sta(() =>
    {
        foreach (int percent in new[] { 100, 125, 150 })
        {
            using var form = new StatusForm { WorkAreaOverride = new Rectangle(0, 0, 1600, 1400),
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
            var config = new DbxConfig { Remote = "sample:", CacheDir = @"D:\Sample\cache", RescueDir = @"D:\Sample\rescue" };
            var record = new RescueRecord { TrustedConfig = config, CloudPath = "Sample/saved.txt", CompleteData = true, MoveCompleted = true,
                DestinationPath = @"D:\Sample\rescue\saved.txt", Reason = "Sample preserved body" };
            var runtime = new RuntimeState { ActiveConfig = config, RecoveryRequired = true, StopDisposition = StopDisposition.Forced,
                CacheInspection = RecoveryText.CachePending(1, config.CacheDir) };
            form.UpdateSnapshot(new TraySnapshot(new DbxStatus { RecoveryRequired = true, RecoveryLocation = RecoveryDestination.Explain(runtime), RescueFiles = 1 },
                [], [], true, null, Rescued: [record], StartupRecords: ["Sample startup notice"]), null, config);
            if (percent != 100)
            {
                var controls = AllControls(form).Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
                form.Scale(new SizeF(percent / 100f, percent / 100f));
                foreach (var item in controls) item.Control.Font = new Font(item.Font.FontFamily, item.Font.Size * percent / 100f, item.Font.Style);
            }
            form.Show(); form.FitToWorkArea(); form.PerformLayout(); Application.DoEvents();
            TestRunner.Assert(form.Width <= 1600 && form.Height <= 1400);
            TestRunner.Assert(form.Height > 608 * percent / 100, "Large status window kept the old fixed initial height.");
            var viewport = FormTests.Field<Panel>(form, "viewport");
            if (percent == 100) TestRunner.Assert(!viewport.VerticalScroll.Visible, "Large screen still hides fitting content behind outer scrolling.");
            foreach (Button button in AllControls(form).OfType<Button>())
            {
                Rectangle bounds = new(form.PointToClient(button.PointToScreen(Point.Empty)), button.Size);
                TestRunner.Assert(form.ClientRectangle.Contains(bounds), "Footer button is outside the form.");
            }
            var recovery = FormTests.Field<TextBox>(form, "recovery");
            int lastY = recovery.GetPositionFromCharIndex(Math.Max(0, recovery.TextLength - 1)).Y;
            TestRunner.Assert(lastY + recovery.Font.Height + 4 <= recovery.ClientSize.Height, "Recovery explanation is clipped.");
            Console.WriteLine($"E8 large workArea=1600x1400 scale={percent}% form={form.Width}x{form.Height}; content height and footer verified.");
            form.Close();
        }
    });

    private static void CurrentRecoveryText()
    {
        using var f = new Unit5Fixture("u6e-text");
        string guide = AgentGuide.Create(f.Controller).Markdown;
        TestRunner.Assert(guide.Contains(RecoveryText.PreflightNotice, StringComparison.Ordinal));
        TestRunner.Assert(guide.Contains("rescue list", StringComparison.Ordinal) && guide.Contains("利用者", StringComparison.Ordinal));
        TestRunner.Assert(guide.Split('\n').Length <= 40);
        FormTests.Sta(() =>
        {
            using var form = new WriteConfirmationForm("sample:");
            string text = string.Join("\n", AllControls(form).Select(c => c.Text));
            TestRunner.Assert(text.Contains(RecoveryText.PreflightNotice, StringComparison.Ordinal));
            TestRunner.Assert(text.Contains("自分で戻してください", StringComparison.Ordinal));
            TestRunner.Assert(text.Contains("競合コピーを作りません", StringComparison.Ordinal) && !text.Contains("数秒", StringComparison.Ordinal));
        });
    }
}
