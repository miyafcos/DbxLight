using System.Reflection;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6hTests
{
    private static void EarlyPauseResumePolling()
    {
        using var f = new Unit5Fixture("u6h-early-pause-poll", writeBack: "5m");
        Exception? failure = null; bool reached = false;
        RuntimeState? owned = null;
        var thread = new Thread(() =>
        {
            try
            {
                var options = new AppOptions(f.Store.PathName, f.State.PathName, RegistryRoot: RegistrationService.TestHive + "\\U6h-" + Guid.NewGuid().ToString("N"));
                using var instance = new SingleInstance(options.StatePath);
                using var context = new ResidentContext(options, instance);
                var dispatcher = FormTests.Field<ResidentWindow>(context, "dispatcher");
                var poll = FormTests.Field<System.Windows.Forms.Timer>(context, "poll");
                context.StartupAwaitCheckpoint = stage =>
                {
                    if (stage == "initial-config" && !reached)
                    {
                        reached = true;
                        FormTests.Field<ToolStripMenuItem>(context, "pause").PerformClick();
                        _ = Scenario();
                    }
                    return Task.CompletedTask;
                };
                async Task TickUntil(Func<bool> condition)
                {
                    TestRunner.Assert(poll.Enabled, "Resident polling did not start.");
                    poll.Interval = 60000;
                    typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(poll, [EventArgs.Empty]);
                    await ResidentTests.Until(() => !FormTests.Field<bool>(context, "refreshing") && condition(), "Controlled polling tick did not publish state.");
                }
                async Task Scenario()
                {
                    try
                    {
                        await context.MenuStopTask!;
                        TestRunner.Assert(poll.Enabled);
                        await context.StartMountedAsync();
                        owned = context.Controller.State.Load()!;
                        f.Write("poll.txt", "polling resumed content");
                        await Task.Run(() => Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 1, "Queued polling file missing."));
                        await TickUntil(() => context.Snapshot.Status.UploadsQueued == 1 && context.Snapshot.Queue.Count == 1);
                        bool choseWait = false;
                        EventHandler chooseWait = (_, _) =>
                        {
                            if (Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault() is not { } choice) return;
                            choseWait = true;
                            ((Button)choice.AcceptButton!).PerformClick();
                        };
                        Application.Idle += chooseWait;
                        try
                        {
                            FormTests.Field<ToolStripMenuItem>(context, "pause").PerformClick();
                            await context.MenuStopTask!;
                        }
                        finally { Application.Idle -= chooseWait; }
                        TestRunner.Assert(choseWait, "Wait choice was not presented.");
                        TestRunner.Equal(ResidentPhase.WaitingToPause, context.Phase);
                        using (var rc = new RcClient(owned))
                        {
                            var queue = rc.Call("vfs/queue").GetProperty("queue").EnumerateArray().Single();
                            rc.Call("vfs/queue-set-expiry", new { id = queue.GetProperty("id").GetInt64(), expiry = -1 });
                        }
                        await Task.Run(() => Unit5Fixture.Wait(() => f.Controller.Status().UploadsQueued == 0 && File.Exists(Path.Combine(f.RemoteRoot, "poll.txt")), "Local upload did not drain."));
                        await TickUntil(() => context.Phase == ResidentPhase.Stopped && ResidentTests.Watch(context) is null);
                        await TickUntil(() => !context.Snapshot.Status.Running);
                        TestRunner.Equal("polling resumed content", File.ReadAllText(Path.Combine(f.RemoteRoot, "poll.txt")));
                    }
                    catch (Exception e) { failure = e; }
                    finally
                    {
                        try { await context.ShutdownOwnedAsync(); }
                        catch (Exception e) { failure ??= e; context.ExitThread(); }
                    }
                }
                using var guard = new System.Windows.Forms.Timer { Interval = 45000 };
                guard.Tick += (_, _) => { failure ??= new TimeoutException("Early resume scenario hung."); context.ExitThread(); };
                guard.Start(); Application.Run(context);
            }
            catch (Exception e) { failure ??= e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try
        {
            TestRunner.Assert(thread.Join(TimeSpan.FromSeconds(60)));
            if (failure is not null) throw new InvalidOperationException("Polling resume failed.", failure);
            TestRunner.Assert(reached && owned is not null);
            using var child = ProcessIdentity.Open(owned!); TestRunner.Assert(child is null);
        }
        finally { f.Controller.Stop(true); }
    }
}
