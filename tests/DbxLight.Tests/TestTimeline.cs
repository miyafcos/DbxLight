using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class TestTimeline
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Starts = [];
    internal static void Mark(string root, string stage, object? detail = null)
    {
        lock (Gate)
        {
            if (!Starts.TryGetValue(root, out long start)) Starts[root] = start = Stopwatch.GetTimestamp();
            string row = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds, stage, detail });
            File.AppendAllText(Path.Combine(root, "r11-timeline.jsonl"), row + "\n", new UTF8Encoding(false));
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    internal static void Dialog(string root, Form dialog, string stage, object? detail = null) =>
        Mark(root, stage, new { result = dialog.DialogResult.ToString(), foreground = GetForegroundWindow() == dialog.Handle,
            dialog.Visible, dialog.Focused, choice = dialog is StopChoiceForm stop ? stop.Choice.ToString() : null, detail });
}
