using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class ReconnectTests
{
    public static void DelayedRead()
    {
        string root = Path.Combine(@"D:\dbxl-test", "run-u3a-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string remote = Path.Combine(root, "remote"); Directory.CreateDirectory(remote);
        string empty = Path.Combine(root, "empty.conf"); File.WriteAllText(empty, "", new UTF8Encoding(false));
        string drive = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var config = new ConfigStore(Path.Combine(root, "config.json"));
        var state = new StateStore(Path.Combine(root, "runtime", "state.json"));
        var controller = new MountController(config, state);
        config.Save(new DbxConfig { Remote = remote, RcloneConfigPath = empty, MountPoint = drive,
            RcPort = port, CacheDir = Path.Combine(root, "cache"), LogDir = Path.Combine(root, "logs"),
            CacheMaxSize = "8M", CacheMinFreeSpace = "16M", PollInterval = "0", DirCacheTime = "1m" });
        var owned = new Dictionary<int, RuntimeState>();
        Exception? failure = null;
        bool ran = false;
        void Track()
        {
            RuntimeState? current = state.Load();
            if (current is not null && current.ConfigPath == config.PathName && current.ActiveConfig.Remote == remote)
                owned[current.RclonePid] = current;
        }
        var thread = new Thread(() =>
        {
            try
            {
                var options = new AppOptions(config.PathName, state.PathName,
                    RegistryRoot: RegistrationService.TestHive + "\\Display-" + Guid.NewGuid().ToString("N"));
                using var instance = new SingleInstance(state.PathName);
                using var context = new ResidentContext(options, instance, async resident =>
                {
                    ran = true;
                    try
                    {
                        Track(); RuntimeState before = state.Load()!;
                        object logGate = typeof(ErrorLogTail).GetField("Sync", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                        Task? stale = null, change = null;
                        bool confirming = false;
                        Monitor.Enter(logGate);
                        try
                        {
                            stale = resident.RefreshAsync();
                            await Task.Delay(300);
                            TestRunner.Assert(!stale.IsCompleted, "The old read was not delayed at the log gate.");
                            change = resident.ApplyConfigAsync(c => c with { WriteAllowedRemotes = [c.Remote] });
                            confirming = TrayPresentation.Tooltip(resident.Snapshot, resident.Config).Contains("\u78ba\u8a8d\u4e2d", StringComparison.Ordinal);
                            await ResidentTests.Until(() =>
                            {
                                Track(); RuntimeState? current = state.Load();
                                return current is not null && current.RclonePid != before.RclonePid && controller.Status().Ready;
                            }, "Reconnection did not produce a new ready PID.");
                        }
                        finally { Monitor.Exit(logGate); }
                        if (change is not null) await change;
                        if (stale is not null) await stale;
                        RuntimeState after = state.Load()!;
                        Console.WriteLine($"Display transition: old PID={before.RclonePid}, new PID={after.RclonePid}, displayed PID={resident.Snapshot.Status.RclonePid}, confirming={confirming}.");
                        TestRunner.Assert(confirming && resident.Snapshot.Status.RclonePid == after.RclonePid &&
                            resident.Snapshot.ActiveConfig is { } active && !TrayPresentation.ReadOnly(active),
                            "An old generation overwrote the new mount or the transition asserted an old permission.");
                    }
                    catch (Exception e) { failure = e; }
                    finally
                    {
                        Track();
                        try { await resident.ShutdownOwnedAsync(); }
                        catch (Exception e) { failure ??= e; Application.ExitThread(); }
                    }
                });
                Application.Run(context);
            }
            catch (Exception e) { failure ??= e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start(); TestRunner.Assert(thread.Join(TimeSpan.FromSeconds(60)), "Display test did not terminate.");
            TestRunner.Assert(ran, "Display test callback was not reached.");
            if (failure is not null) throw new InvalidOperationException("Display test failed.", failure);
        }
        finally
        {
            Track();
            foreach (RuntimeState record in owned.Values)
            {
                using Process? process = ProcessIdentity.Open(record);
                if (process is not null) { process.Kill(); TestRunner.Assert(process.WaitForExit(10000)); }
                using Process? remaining = ProcessIdentity.Open(record);
                TestRunner.Assert(remaining is null, "Owned display-test rclone remained alive.");
            }
            long bytes = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            TestRunner.Assert(bytes <= 50L * 1024 * 1024);
            Console.WriteLine($"Display-test owned PIDs={string.Join(',', owned.Keys)}: exited; run bytes={bytes}; artifacts={root}");
        }
    }
}
