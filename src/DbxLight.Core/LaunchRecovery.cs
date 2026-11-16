using System.Diagnostics;
using System.Text.Json.Serialization;

namespace DbxLight.Core;

[JsonConverter(typeof(JsonStringEnumConverter<StartupPhase>))]
public enum StartupPhase { None, Intent, RecordedSuspended, Resumed }

public sealed partial class MountController
{
    private RuntimeState ResolveLaunch(RuntimeState state)
    {
        if (!state.LaunchPending) return state;
        if (state.IdentityVersion != 1) throw new PendingUploadsException("古い起動マークの同一性を確認できません。手動の回復確認が必要です。");
        if (state.RclonePid <= 0)
        {
            // PID is recorded before ResumeThread. An intent's unrecorded child can
            // only be nonexistent or suspended, so a verified dead parent releases it.
            if (state.LaunchPhase == StartupPhase.Intent && ParentStopped(state))
                return FinishLaunchProof(state, "launch_parent_exited_before_pid", stopped: true);
            return state; // Missing/uncertain identity remains the existing manual recovery case.
        }
        if (state.LaunchPhase is not (StartupPhase.RecordedSuspended or StartupPhase.Resumed) ||
            !ProcessIdentity.CompleteRecord(state.RclonePid, state.ProcessStartedUtc, state.ExecutablePath) ||
            !ProcessIdentity.CompleteRecord(state.LaunchParentPid, state.LaunchParentStartedUtc, state.LaunchParentExecutablePath))
            throw new PendingUploadsException("起動マークの子プロセスの同一性または段階を確認できません。手動の回復確認が必要です。");
        using var child = ExplainOtherOwner(state, () => ProcessIdentity.Open(state));
        if (child is null) return FinishLaunchProof(state, "recorded_child_exited", stopped: true);
        if (state.LaunchPhase == StartupPhase.Resumed)
            return FinishLaunchProof(state, "resumed_child_identified", stopped: false);
        bool? suspended = ThreadsSuspended(child);
        if (suspended is null)
            throw new PendingUploadsException("記録した子プロセスのスレッドの状態を確認できません。起動マークを残します。");
        if (!suspended.Value)
            return FinishLaunchProof(state, "recorded_running_child_adopted", stopped: false);
        if (!ParentStopped(state))
            throw new PendingUploadsException("起動中の親プロセスが停止したと確認できないため、起動マークを残して断ります。");
        child.Kill();
        if (!child.WaitForExit(10000)) throw new PendingUploadsException("The recorded suspended child did not exit; retain the launch marker.");
        return FinishLaunchProof(state, "recorded_suspended_child_terminated", stopped: true);
    }

    private RuntimeState FinishLaunchProof(RuntimeState state, string proof, bool stopped)
    {
        RuntimeState resolved = state with { LaunchPending = false, LaunchRecoveryProof = proof,
            LaunchPhase = stopped ? StartupPhase.None : StartupPhase.Resumed,
            StoppedUtc = stopped ? DateTime.UtcNow : state.StoppedUtc, StopRequested = stopped || state.StopRequested,
            RecoveryRequired = stopped || state.RecoveryRequired,
            StopDisposition = stopped ? StopDisposition.StartupFailed : state.StopDisposition };
        resolved = SaveRuntime(resolved);
        string path = Path.Combine(state.ActiveConfig.CacheDir, "dbxl-owner.json");
        if (File.Exists(path))
        {
            CacheOwner owner = JsonFiles.Read<CacheOwner>(path);
            if (owner.IdentityVersion == 1 && owner.GenerationId == state.GenerationId && owner.GenerationNumber == state.GenerationNumber &&
                owner.LaunchParentPid == state.LaunchParentPid && owner.LaunchParentStartedUtc == state.LaunchParentStartedUtc &&
                owner.LaunchParentExecutablePath.Equals(state.LaunchParentExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                (owner.Pid == state.RclonePid || owner.Pid == 0 && owner.LaunchPending))
                JsonFiles.Write(path, stopped ? owner with { Pid = 0, ProcessStartedUtc = default, ExecutablePath = "",
                    LaunchPending = false, LaunchPhase = StartupPhase.None } : CacheOwnership(resolved, false));
        }
        return resolved;
    }

    internal static bool ParentStopped(RuntimeState state)
    {
        if (!ProcessIdentity.CompleteRecord(state.LaunchParentPid, state.LaunchParentStartedUtc, state.LaunchParentExecutablePath)) return false;
        try { using var parent = ProcessIdentity.Open(state.LaunchParentPid, state.LaunchParentStartedUtc, state.LaunchParentExecutablePath); return parent is null; }
        catch (ProcessIdentityUnknownException) { return false; }
    }

    internal static bool AllThreadsSuspended(Process process) => ThreadsSuspended(process) == true;

    private static bool? ThreadsSuspended(Process process)
    {
        try
        {
            var threads = process.Threads.Cast<ProcessThread>().ToArray();
            try { return threads.Length == 0 ? null : threads.All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended); }
            finally { foreach (var thread in threads) thread.Dispose(); }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }

    private CacheOwner CacheOwnership(RuntimeState state, bool pending) => new(state.RclonePid, state.ProcessStartedUtc, state.ExecutablePath,
        State.PathName, pending, 1, state.LaunchParentPid, state.LaunchParentStartedUtc,
        state.LaunchParentExecutablePath, state.LaunchPhase, state.GenerationId, state.GenerationNumber);
}
