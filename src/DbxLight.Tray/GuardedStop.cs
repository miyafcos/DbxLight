using DbxLight.Core;

namespace DbxLight.Tray;

public sealed record StopAttempt(bool Stopped, long? PendingCount, string Message);
public static class GuardedStop
{
    public static StopAttempt TryStop(MountController controller)
    {
        try { controller.Stop(); return new(true, 0, "停止しました。"); }
        catch (PendingUploadsException)
        {
            DbxStatus status = controller.Status();
            long? count = status.UploadsQueued is { } queued && status.UploadsInProgress is { } uploading ? queued + uploading : null;
            return new(false, count, count is { } n ? TrayPresentation.StopWaiting(n) :
                "上げ待ちを確認できないため、停止しません。接続が戻るまで待ちます。");
        }
    }
}

internal sealed class ResidentWindow : Form
{
    private bool shutdownReason;
    public ResidentWindow() { ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None; }
    public event EventHandler? CloseRequested;
    public Func<SessionEndDecision>? QuerySessionEnd { get; set; }
    public Action<bool>? SessionEndChanged { get; set; }
    public void ClearShutdownReason()
    {
        if (shutdownReason && IsHandleCreated)
        {
            if (!ShutdownBlockReasonDestroy(Handle)) System.Diagnostics.Trace.WriteLine("ShutdownBlockReasonDestroy failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            shutdownReason = false;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0010) { CloseRequested?.Invoke(this, EventArgs.Empty); return; }
        if (m.Msg == 0x0011) // WM_QUERYENDSESSION: return immediately; work runs in the background.
        {
            SessionEndDecision decision = QuerySessionEnd?.Invoke() ?? new(false, "");
            if (decision.Block)
            {
                shutdownReason = ShutdownBlockReasonCreate(Handle, decision.Reason);
                if (!shutdownReason) System.Diagnostics.Trace.WriteLine("ShutdownBlockReasonCreate failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            else ClearShutdownReason();
            m.Result = decision.Block ? IntPtr.Zero : new IntPtr(1);
            return;
        }
        if (m.Msg == 0x0016) // WM_ENDSESSION (including cancellation)
        {
            ClearShutdownReason();
            SessionEndChanged?.Invoke(m.WParam != IntPtr.Zero);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearShutdownReason();
        base.Dispose(disposing);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
