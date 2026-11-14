using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6cTests
{
    private static void ParentIntentRecovery()
    {
        using var f = new Unit5Fixture("u6e-parent-intent");
        using var parent = Process.GetCurrentProcess();
        RuntimeState intent = GenerationFixture(f, 1, f.State.PathName, 0) with
        {
            ProcessStartedUtc = default, ExecutablePath = "", StopDisposition = StopDisposition.StartupFailed, RecoveryRequired = true,
            LaunchPending = true, StoppedUtc = null, StopRequested = false,
            LaunchParentPid = parent.Id, LaunchParentStartedUtc = parent.StartTime.ToUniversalTime(), LaunchParentExecutablePath = ProcessIdentity.ImagePath(parent),
            LaunchPhase = StartupPhase.Intent
        };
        f.State.Save(intent); RecoveryJournal.Save(f.Store.PathName, intent);
        string ownerPath = Path.Combine(f.Config.CacheDir, "dbxl-owner.json");
        CacheOwner Owner(RuntimeState value) => new(0, default, "", f.State.PathName,
            LaunchPending: true, IdentityVersion: 1, LaunchParentPid: value.LaunchParentPid,
            LaunchParentStartedUtc: value.LaunchParentStartedUtc, LaunchParentExecutablePath: value.LaunchParentExecutablePath,
            LaunchPhase: StartupPhase.Intent, GenerationId: value.GenerationId, GenerationNumber: value.GenerationNumber);
        JsonFiles.Write(ownerPath, Owner(intent));
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.ClearRecovery(true));
        TestRunner.Assert(f.State.Load()!.LaunchPending && JsonFiles.Read<CacheOwner>(ownerPath).LaunchPending);
        RuntimeState missing = f.State.Load()! with { LaunchParentExecutablePath = "" };
        RuntimeSnapshots.Save(f.State, missing);
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        TestRunner.Assert(f.State.Load()!.LaunchPending);
        // A changed creation time proves that this is not the recorded parent.
        RuntimeState stopped = f.State.Load()! with { LaunchParentExecutablePath = intent.LaunchParentExecutablePath,
            LaunchParentStartedUtc = intent.LaunchParentStartedUtc.AddSeconds(-1) };
        RuntimeSnapshots.Save(f.State, stopped); JsonFiles.Write(ownerPath, Owner(stopped));
        f.Start();
        TestRunner.Equal("launch_parent_exited_before_pid", f.State.Load()!.LaunchRecoveryProof);
        TestRunner.Assert(!f.State.Load()!.LaunchPending && !JsonFiles.Read<CacheOwner>(ownerPath).LaunchPending);
        Console.WriteLine("E4 live/incomplete parent refused; different parent start time released PID-zero intent without WMI.");
    }

    private static void SuspendedLaunchRecovery()
    {
        using var f = new Unit5Fixture("u6c-suspended-launch");
        var info = RcloneArguments.ProcessInfo(Environment.ProcessPath!, ["--unit6a-startup-child", f.Root, "before-resume"]);
        using var parent = Process.Start(info) ?? throw new IOException("Cannot start the launch crash parent.");
        Task<string> output = parent.StandardOutput.ReadToEndAsync(), error = parent.StandardError.ReadToEndAsync();
        RuntimeState? recorded = null;
        try
        {
            string checkpoint = Path.Combine(f.Root, "checkpoint.json");
            Unit5Fixture.Wait(() => File.Exists(checkpoint), "No recorded suspended-child checkpoint.", 20);
            recorded = JsonFiles.Read<RuntimeState>(checkpoint);
            TestRunner.Assert(recorded.LaunchParentStartedUtc != default && recorded.LaunchParentPid == parent.Id && recorded.LaunchPhase == StartupPhase.RecordedSuspended);
            parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000));
            using (var child = ProcessIdentity.Open(recorded)) TestRunner.Assert(child is not null && MountController.AllThreadsSuspended(child));
            TestRunner.Assert(!Directory.Exists(f.MountRoot));
            f.Start();
            RuntimeState replacement = f.State.Load()!;
            TestRunner.Assert(replacement.RclonePid != recorded.RclonePid);
            TestRunner.Equal("recorded_suspended_child_terminated", replacement.LaunchRecoveryProof);
            using var old = ProcessIdentity.Open(recorded); TestRunner.Assert(old is null);
            Console.WriteLine($"R7 recorded suspended PID={recorded.RclonePid}; dead parent verified; exact image/start/thread suspension proved; child terminated; replacement PID={replacement.RclonePid} mounted normally.");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000)); }
            _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
            if (recorded is not null)
            {
                using var remaining = ProcessIdentity.Open(recorded);
                if (remaining is not null) { remaining.Kill(); TestRunner.Assert(remaining.WaitForExit(10000)); }
            }
        }
    }

    private static void RunningLaunchMarker()
    {
        using var f = new Unit5Fixture("u6e-running-launch-marker"); f.Start();
        RuntimeState original = f.State.Load()!;
        using var parent = Process.GetCurrentProcess();
        RuntimeSnapshots.Save(f.State, original with { LaunchPending = true, LaunchPhase = StartupPhase.RecordedSuspended,
            LaunchParentPid = parent.Id, LaunchParentStartedUtc = parent.StartTime.ToUniversalTime().AddSeconds(-1),
            LaunchParentExecutablePath = ProcessIdentity.ImagePath(parent) });
        TestRunner.Assert(f.Controller.Status() is { Running: true, Ready: true });
        TestRunner.Equal(original.RclonePid, f.Controller.Start().Pid);
        TestRunner.Equal("recorded_running_child_adopted", f.State.Load()!.LaunchRecoveryProof);
        TestRunner.Assert(!f.State.Load()!.LaunchPending);
        f.Controller.Stop();
        Console.WriteLine("E4 running child + suspended marker: exact child adopted; status/start/stop work without a second child.");
    }
}
