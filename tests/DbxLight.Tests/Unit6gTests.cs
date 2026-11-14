using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6gTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        RunFailures(Test);
        RunNames(Test);
        RunNameMarks(Test);
        RunCacheNames(Test);
        RunNotices(Test);
        RunPagination(Test);
        foreach (string stage in new[] { "initial-config", "before-doctor", "pair", "after-final-comparison", "before-resume", "after-resume" })
        foreach (bool exit in new[] { false, true })
            Test("J1", "tray menu stops startup at " + stage + "; exit=" + exit, () => MenuStartupStop(stage, exit));
    }
    private static void MenuStartupStop(string stage, bool exit)
    {
        using var f = new Unit5Fixture("u6g-menu-" + stage + "-" + exit, writeBack: "5m");
        if (stage == "pair")
        {
            f.Start(); f.Write("one.txt", "one saved"); f.Write("two.txt", "two saved");
            Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == 2, "Pending menu pairs missing.");
            f.Controller.Stop(true);
            foreach (var entry in PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Where(e => e.Dirty == true))
                File.WriteAllText(entry.MetadataPath, "{");
        }
        Exception? failure = null; bool clicked = false; int resumed = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var options = new AppOptions(f.Store.PathName, f.State.PathName, RegistryRoot: RegistrationService.TestHive + "\\U6g-" + Guid.NewGuid().ToString("N"));
                using var instance = new SingleInstance(options.StatePath);
                using var context = new ResidentContext(options, instance);
                var dispatcher = FormTests.Field<ResidentWindow>(context, "dispatcher");
                void Click()
                {
                    if (clicked) return;
                    clicked = true;
                    if (exit) FormTests.Field<NotifyIcon>(context, "tray").ContextMenuStrip!.Items[^1].PerformClick();
                    else FormTests.Field<ToolStripMenuItem>(context, "pause").PerformClick();
                    _ = Finish(context.MenuStopTask ?? throw new InvalidOperationException("Menu did not request stop."));
                }
                async Task Finish(Task stop)
                {
                    try { await stop; }
                    catch (Exception e) { failure ??= e; }
                    finally { context.ExitThread(); }
                }
                void ClickFromWorker()
                {
                    var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    dispatcher.BeginInvoke((Action)(() => { try { Click(); delivered.SetResult(); } catch (Exception e) { delivered.SetException(e); } }));
                    if (!delivered.Task.Wait(TimeSpan.FromSeconds(4))) throw new TimeoutException("Menu injection not delivered.");
                    delivered.Task.GetAwaiter().GetResult();
                }
                context.StartupAwaitCheckpoint = checkpoint => { if (checkpoint == stage) Click(); return Task.CompletedTask; };
                context.Controller.StartupCheckpoint = (checkpoint, _) =>
                {
                    if (checkpoint == "after-resume") Interlocked.Increment(ref resumed);
                    if (checkpoint == stage) ClickFromWorker();
                };
                if (stage == "pair") RescueStore.MoveCheckpoint = (checkpoint, _) => { if (checkpoint == "after-data-move" && !clicked) ClickFromWorker(); };
                using var guard = new System.Windows.Forms.Timer { Interval = 45000 };
                guard.Tick += (_, _) => { failure ??= new TimeoutException("Menu startup scenario did not finish."); context.ExitThread(); };
                guard.Start();
                try { Application.Run(context); }
                finally { RescueStore.MoveCheckpoint = null; }
                TestRunner.Assert(clicked, "Menu checkpoint was not reached.");
                TestRunner.Assert(context.MenuStopTask is { IsCompletedSuccessfully: true }, "Menu stop did not complete.");
            }
            catch (Exception e) { failure ??= e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try
        {
            TestRunner.Assert(thread.Join(TimeSpan.FromSeconds(60)), "Menu test thread did not exit.");
            if (failure is not null) throw new InvalidOperationException("Menu startup stop failed.", failure);
            TestRunner.Assert(!f.Controller.Status().Running);
            TestRunner.Equal(stage == "after-resume" ? 1 : 0, resumed);
            if (stage == "pair")
            {
                TestRunner.Equal(1, f.Controller.RescueList().Count(r => r.MoveCompleted));
                TestRunner.Equal(1, PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Count(e => !e.MetadataValid));
            }
            if (f.State.Load() is { } state) { using var child = ProcessIdentity.Open(state); TestRunner.Assert(child is null); }
            Console.WriteLine($"J1 menu={ (exit ? "exit" : "pause") }; stage={stage}; resumed={resumed}; owned mount not running.");
        }
        finally { RescueStore.MoveCheckpoint = null; f.Controller.Stop(true); }
    }
}
