using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private static void RunLaunchRecovery(Action<string, string, Action> test)
    {
        test("E4", "parent death after ResumeThread and before resumed record adopts the same child for status and stop", () => LaunchCrash("after-resume"));
        test("E4", "parent death before PID record permits a stopped-cache rescan and pending recovery", () => LaunchCrash("before-record"));
        test("E4", "old launch markers missing child identity remain refused", MissingLaunchIdentity);
    }

    private static void LaunchCrash(string phase)
    {
        using var f = new Unit5Fixture("u6e-launch-" + phase, writeBack: "5m");
        Pending(f);
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        using var parent = Process.Start(RcloneArguments.ProcessInfo(Environment.ProcessPath!, ["--unit6a-startup-child", f.Root, phase]))!;
        Task<string> output = parent.StandardOutput.ReadToEndAsync(), error = parent.StandardError.ReadToEndAsync();
        RuntimeState? checkpoint = null;
        try
        {
            string path = Path.Combine(f.Root, "checkpoint.json");
            Unit5Fixture.Wait(() => File.Exists(path), "Launch crash checkpoint not reached.", 30);
            checkpoint = JsonFiles.Read<RuntimeState>(path);
            RuntimeState durable = JsonFiles.Read<RuntimeState>(f.State.PathName);
            TestRunner.Equal(phase == "before-record" ? 0 : checkpoint.RclonePid, durable.RclonePid);
            TestRunner.Assert(durable.LaunchPending);
            if (phase == "before-record")
            {
                using var child = ProcessIdentity.Open(checkpoint);
                TestRunner.Assert(child is not null && MountController.AllThreadsSuspended(child));
            }
            parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000));
            if (phase == "before-record")
                Unit5Fixture.Wait(() => { using var child = ProcessIdentity.Open(checkpoint); return child is null; }, "Never-resumed job child survived its parent.");
            else
            {
                Unit5Fixture.Wait(() => f.Controller.Status() is { Running: true, Ready: true }, "Running recorded child was not adopted.");
                TestRunner.Equal(checkpoint.RclonePid, f.Controller.Status().RclonePid);
                TestRunner.Equal("recorded_running_child_adopted", f.State.Load()!.LaunchRecoveryProof);
                TestRunner.Assert(f.State.Load()!.ChildResumedUtc is null && f.State.Load()!.RecoveryComparisonAgeSeconds is null,
                    "Adoption invented a missing resume receipt.");
            }
            f.Start();
            TestRunner.Assert(phase == "before-record" ? f.State.Load()!.RclonePid != checkpoint.RclonePid : f.State.Load()!.RclonePid == checkpoint.RclonePid);
            Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.RemoteRoot, "saved.txt")) &&
                File.ReadAllText(Path.Combine(f.RemoteRoot, "saved.txt")) == "complete saved body", "Pending body did not recover normally.");
            f.Controller.Stop();
            TestRunner.Assert(!f.Controller.Status().Running);
            Console.WriteLine($"E4 real crash phase={phase}; status/start/stop succeeded; complete pending body retained/uploaded; no duplicate child.");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000)); }
            _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
            if (checkpoint is not null)
            {
                using var child = ProcessIdentity.Open(checkpoint);
                if (child is not null) { child.Kill(); TestRunner.Assert(child.WaitForExit(10000)); }
            }
        }
    }

    private static void MissingLaunchIdentity()
    {
        using var f = new Unit5Fixture("u6e-launch-old");
        f.Start(); f.Controller.Stop();
        RuntimeState old = f.State.Load()!;
        RuntimeState marker = old with { LaunchPending = true, LaunchPhase = StartupPhase.RecordedSuspended };
        foreach (RuntimeState missing in new[] { marker with { ProcessStartedUtc = default },
            marker with { ExecutablePath = "" }, marker with { LaunchParentStartedUtc = default },
            marker with { LaunchParentExecutablePath = "" }, marker with { IdentityVersion = 0 } })
        {
            RuntimeSnapshots.Save(f.State, missing);
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.ClearRecovery(true));
            TestRunner.Assert(f.State.Load()!.LaunchPending);
        }
        RuntimeSnapshots.Save(f.State, old); // Restore this fixture's stopped owner for disposal only.
    }
}
