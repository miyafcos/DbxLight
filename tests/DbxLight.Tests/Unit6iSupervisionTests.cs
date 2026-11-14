using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6iTests
{
    private static void StopRefusalKeepsWatch(bool wait, bool externalStop = false, bool exit = false)
    {
        using var f = new Unit5Fixture("u6i-refused-" + wait, writeBack: "5m");
        var replacement = new TaskCompletionSource<RuntimeState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new List<RuntimeState>();
        bool chose = false; EventHandler? choose = null;
        RunTray(f, async c =>
        {
            try
            {
                Task<int> watch = ResidentTests.Watch(c)!;
                await c.MenuStopTask!;
                TestRunner.Assert(chose, "Stop choice was not shown.");
                TestRunner.Equal(wait ? exit ? ResidentPhase.WaitingToExit : ResidentPhase.WaitingToPause : ResidentPhase.Watching, c.Phase);
                if (externalStop) FormTests.Field<System.Windows.Forms.Timer>(c, "poll").Interval = 60000;
                TestRunner.Assert(!watch.IsCompleted, "Stop refusal ended supervision.");
                RuntimeState first = f.State.Load()!;
                await Task.Run(() => KillOwned(first));
                RuntimeState next = await replacement.Task;
                TestRunner.Assert(next.RclonePid != first.RclonePid);
                TestRunner.Assert(ReferenceEquals(watch, ResidentTests.Watch(c)) && !watch.IsCompleted);
                if (wait)
                {
                    await Task.Run(() => Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Restarted queue missing."));
                    using (var rc = new RcClient(next))
                    {
                        var queue = rc.Call("vfs/queue").GetProperty("queue").EnumerateArray().Single();
                        rc.Call("vfs/queue-set-expiry", new { id = queue.GetProperty("id").GetInt64(), expiry = -1 });
                    }
                    await Task.Run(() => Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 0 &&
                        File.Exists(Path.Combine(f.RemoteRoot, "pending.txt")), "Restarted upload did not drain."));
                    if (externalStop)
                    {
                        await Task.Run(() => f.Controller.Stop());
                        await ResidentTests.Until(() => exit
                            ? c.Phase == ResidentPhase.Exiting && !FormTests.Field<System.Windows.Forms.Timer>(c, "poll").Enabled
                            : c.Phase == ResidentPhase.Stopped, "External stop left an unobserved wait.");
                    }
                    else await c.RefreshAsync();
                    TestRunner.Equal(exit ? ResidentPhase.Exiting : ResidentPhase.Stopped, c.Phase);
                    TestRunner.Assert(!c.Controller.Status().Running);
                    TestRunner.Equal("pending startup content", File.ReadAllText(Path.Combine(f.RemoteRoot, "pending.txt")));
                }
                else TestRunner.Equal(ResidentPhase.Watching, c.Phase);
            }
            finally { if (choose is not null) Application.Idle -= choose; }
        }, (c, execute) =>
        {
            var dispatcher = FormTests.Field<ResidentWindow>(c, "dispatcher");
            c.Controller.StartupCheckpoint = (stage, state) =>
            {
                if (stage != "mount-ready") return;
                owned.Add(state);
                if (owned.Count > 1) { replacement.TrySetResult(state); return; }
                f.Write("pending.txt", "pending startup content");
                Unit5Fixture.Wait(() => c.Controller.Status().UploadsQueued == 1, "Startup queue missing.");
                using var delivered = new ManualResetEventSlim();
                dispatcher.BeginInvoke((Action)(() =>
                {
                    choose = (_, _) =>
                    {
                        if (Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is not { } choice) return;
                        chose = true;
                        if (wait) ((Button)choice.AcceptButton!).PerformClick();
                        else ((Button)choice.CancelButton!).PerformClick();
                    };
                    Application.Idle += choose;
                    if (exit) FormTests.Field<ToolStripMenuItem>(c, "exit").PerformClick();
                    else Pause(c).PerformClick();
                    _ = execute();
                    delivered.Set();
                }));
                TestRunner.Assert(delivered.Wait(TimeSpan.FromSeconds(4)));
            };
        }, autoStart: false);
        foreach (var state in owned) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
    }
}
