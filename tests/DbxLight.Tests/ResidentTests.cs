using System.Reflection;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class ResidentTests
{
    internal static Task<int>? Watch(ResidentContext resident) =>
        (Task<int>?)typeof(ResidentContext).GetField("supervisor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resident);

    public static void FaultRecovery()
    {
        using var fixture = new Unit5Fixture("u6j-fault-recovery");
        Unit6iTests.RunTray(fixture, async resident =>
        {
            Task<int> previous = Watch(resident)!;
            RuntimeState healthy = fixture.State.Load()!;
            File.WriteAllText(fixture.State.PathName, "{", new UTF8Encoding(false));
            try { await Until(() => previous.IsFaulted, "Resident watcher did not fault."); }
            finally { RuntimeSnapshots.Save(fixture.State, healthy); }
            await Until(() => resident.Phase is ResidentPhase.StopFailed or ResidentPhase.Stopped,
                "Fault completion did not revalidate the normal stop.");
            if (resident.Phase == ResidentPhase.StopFailed)
            {
                FormTests.Field<ToolStripMenuItem>(resident, "pause").PerformClick();
                await resident.MenuStopTask!;
            }
            TestRunner.Equal(ResidentPhase.Stopped, resident.Phase);
            using (var oldChild = ProcessIdentity.Open(healthy)) TestRunner.Assert(oldChild is null);
            await resident.StartMountedAsync();
            Task<int>? replacement = Watch(resident);
            TestRunner.Assert(replacement is not null && !ReferenceEquals(previous, replacement) && !replacement.IsCompleted,
                "Resume retained or rethrew the completed faulted watcher.");
            RuntimeState resumed = fixture.State.Load()!;
            TestRunner.Assert(healthy.RclonePid != resumed.RclonePid);
            TestRunner.Assert(!resumed.StopRequested && resumed.SupervisorPid == Environment.ProcessId);
            await resident.StartMountedAsync();
            string log = File.ReadAllText(Path.Combine(healthy.ActiveConfig.LogDir, "supervisor.log"), Encoding.UTF8);
            TestRunner.Equal(1, log.Split('\n').Count(line => line.Contains("Supervisor task failed:", StringComparison.Ordinal)));
            Console.WriteLine($"Recovered faulted resident watcher after normal-stop verification; new owned target PID={resumed.RclonePid}; failure records=1.");
        });
    }

    internal static async Task Until(Func<bool> condition, string message)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }

    internal static void Run(Unit3aTests.RcFixture fixture, Func<ResidentContext, Task> scenario)
    {
        Exception? failure = null;
        bool ran = false;
        RuntimeState original = fixture.State.Load()!;
        var thread = new Thread(() =>
        {
            try
            {
                var options = new AppOptions(fixture.Config.PathName, fixture.State.PathName,
                    RegistryRoot: RegistrationService.TestHive + "\\Unit3a-" + Guid.NewGuid().ToString("N"));
                using var instance = new SingleInstance(options.StatePath);
                using var context = new ResidentContext(options, instance, async resident =>
                {
                    ran = true;
                    try { await scenario(resident); }
                    catch (Exception e) { failure = e; }
                    finally
                    {
                        try
                        {
                            try { _ = fixture.State.Load(); }
                            catch (System.Text.Json.JsonException) { RuntimeSnapshots.Save(fixture.State, original); }
                            await resident.ShutdownOwnedAsync();
                        }
                        catch (Exception e) { failure ??= e; Application.ExitThread(); }
                    }
                });
                Application.Run(context);
            }
            catch (Exception e) { failure ??= e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        TestRunner.Assert(thread.Join(TimeSpan.FromSeconds(60)), "Resident scenario did not terminate.");
        TestRunner.Assert(ran, "Resident initialization did not reach the test callback.");
        if (failure is not null) throw new InvalidOperationException("Resident scenario failed.", failure);
    }
}
