using System.Diagnostics;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    internal static int StartupChild(string root, string phase)
    {
        var controller = new MountController(new ConfigStore(Path.Combine(root, "config.json")),
            new StateStore(Path.Combine(root, "runtime", "state.json")));
        controller.StartupCheckpoint = (at, state) =>
        {
            if (at != phase) return;
            JsonFiles.Write(Path.Combine(root, "checkpoint.json"), state);
            Thread.Sleep(45000);
        };
        controller.Start();
        return 0;
    }

    private static void KnownCreationFailure()
    {
        using var f = new Unit5Fixture("u6a-no-child");
        f.Store.Save(f.Config with { RclonePath = Path.Combine(f.Root, "does-not-exist.exe") });
        TestRunner.Throws<System.ComponentModel.Win32Exception>(() => f.Controller.Start());
        RuntimeState failed = f.State.Load()!;
        TestRunner.Assert(failed.RclonePid == 0 && !failed.LaunchPending && failed.StoppedUtc is not null && failed.RecoveryRequired);
        f.Controller.ClearRecovery(true);
        TestRunner.Assert(!f.State.Load()!.RecoveryRequired);
        Console.WriteLine("P6 CreateProcess=false: no child; known stopped failure retained; explicit clear works without weakening unresolved launch refusal.");
    }

    private static void IncompleteLaunch()
    {
        using var f = new Unit3cTests.LockoutMount("u6a-intent");
        RuntimeSnapshots.Save(f.State, f.Clean with { RclonePid = 0, LaunchPending = true, StoppedUtc = null,
            RecoveryRequired = true, StopDisposition = StopDisposition.StartupFailed });
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.ClearRecovery(true));
        f.Config.Save(f.Config.Load() with { MountPoint = "W:", RcPort = 54871 });
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        TestRunner.Assert(f.Controller.Status().ProcessUncertain);
    }

    private static void SuspendedOwnership()
    {
        foreach (string phase in new[] { "before-record", "after-record" })
        {
            using var f = new Unit5Fixture("u6a-" + phase);
            var info = RcloneArguments.ProcessInfo(Environment.ProcessPath!, ["--unit6a-startup-child", f.Root, phase]);
            using Process parent = Process.Start(info) ?? throw new IOException("Could not start test parent.");
            Task<string> output = parent.StandardOutput.ReadToEndAsync(), error = parent.StandardError.ReadToEndAsync();
            RuntimeState? own = null;
            try
            {
                string checkpoint = Path.Combine(f.Root, "checkpoint.json");
                Unit5Fixture.Wait(() => File.Exists(checkpoint), "No startup checkpoint.", 20);
                own = JsonFiles.Read<RuntimeState>(checkpoint);
                if (phase == "before-record")
                {
                    TestRunner.Equal(0, JsonFiles.Read<RuntimeState>(f.State.PathName).RclonePid);
                    TestRunner.Assert(!Directory.Exists(f.MountRoot), "Unrecorded suspended child executed.");
                }
                parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000));
                if (phase == "before-record")
                {
                    Unit5Fixture.Wait(() => { using var p = ProcessIdentity.Open(own); return p is null; }, "Unrecorded job child survived parent crash.");
                    f.Start();
                    TestRunner.Assert(f.State.Load()!.RclonePid != own.RclonePid);
                    TestRunner.Equal("launch_parent_exited_before_pid", f.State.Load()!.LaunchRecoveryProof);
                    Console.WriteLine("P6 before PID write: parent killed; child never resumed and exited; intent released and restart succeeded.");
                }
                else
                {
                    Unit5Fixture.Wait(() => Directory.Exists(f.MountRoot), "Recorded child did not mount.");
                    using (var p = ProcessIdentity.Open(own)) TestRunner.Assert(p is not null, "Recorded child was lost.");
                    TestRunner.Equal(own.RclonePid, f.Controller.Start().Pid);
                    TestRunner.Throws<InvalidOperationException>(() => f.Controller.ClearRecovery(true));
                    f.Controller.Stop(true);
                    Console.WriteLine("P6 after PID+owner write: parent killed; next start found exact PID=" + own.RclonePid);
                }
            }
            finally
            {
                if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(10000); }
                if (own is not null)
                {
                    using Process? child = ProcessIdentity.Open(own);
                    if (child is not null) { child.Kill(); TestRunner.Assert(child.WaitForExit(10000)); }
                    using Process? remaining = ProcessIdentity.Open(own); TestRunner.Assert(remaining is null);
                }
                _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
            }
        }
    }
}
