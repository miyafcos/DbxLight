using System.Diagnostics;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit9Tests
{
    internal static void RunUnit10bRecovery(TestRunner runner)
    {
        runner.Test("10b P2: an old state-only rewrite at the same revision is repaired", LegacyStateOnly);
        runner.Test("10b P2: a newer journal without proof retains the earlier proof for the same child", LegacyNewerJournal);
        runner.Test("10b P2: saving a stale same-child state carries the latest mount proof", StaleSameChildSave);
        runner.Test("10b P2: legacy removal from all three records preserves copy and preview without shell writes", LegacyAllRecords);
        runner.Test("10b P2: proof does not cross a child, generation, connection, or contradictory device", LegacyProofBoundary);
        runner.Test("10b P2: an occupied proof path does not replace an unrelated file or break status", OccupiedProofPath);
    }

    private static (Fixture Fixture, RuntimeState Live) ProofFixture()
    {
        var f = new Fixture();
        using Process process = Process.GetCurrentProcess();
        var live = f.Controller.State.Load()! with
        {
            GenerationId = Guid.NewGuid().ToString("N"),
            RclonePid = process.Id,
            ExecutablePath = ProcessIdentity.ImagePath(process),
            ProcessStartedUtc = process.StartTime.ToUniversalTime()
        };
        return (f, live);
    }

    // 0.1.0 deserializes those three properties as unmapped and emits no corresponding fields.
    private static RuntimeState WithoutMountProof(RuntimeState live) =>
        live with { MountDevice = null, MountDropboxBinding = null, MountDropboxCursor = null };

    private static void AssertMountProof(RuntimeState expected, RuntimeState actual)
    {
        TestRunner.Equal(expected.GenerationId, actual.GenerationId);
        TestRunner.Equal(expected.RclonePid, actual.RclonePid);
        TestRunner.Equal(expected.ProcessStartedUtc, actual.ProcessStartedUtc);
        TestRunner.Equal(expected.MountDevice, actual.MountDevice);
        TestRunner.Equal(expected.MountDropboxBinding, actual.MountDropboxBinding);
        TestRunner.Equal(expected.MountDropboxCursor, actual.MountDropboxCursor);
    }

    private static void LegacyStateOnly()
    {
        var (f, live) = ProofFixture();
        RuntimeSnapshots.Save(f.Controller.State, live);
        var saved = f.Controller.State.Load()!;
        f.Controller.State.Save(WithoutMountProof(saved));
        var recovered = RecoveryJournal.Recover(f.ConfigPath, f.Config, f.Controller.State.Load())!;
        AssertMountProof(saved, recovered);
        TestRunner.Equal(saved.RevisionNumber, recovered.RevisionNumber);
    }

    private static void LegacyNewerJournal()
    {
        var (f, live) = ProofFixture();
        RuntimeSnapshots.Save(f.Controller.State, live);
        var saved = f.Controller.State.Load()!;
        PrivateJsonFile.Write(RecoveryJournal.CachePath(f.Config.CacheDir), WithoutMountProof(saved) with
        { RevisionNumber = saved.RevisionNumber + 1, RestartCount = 12 });
        var recovered = RecoveryJournal.Recover(f.ConfigPath, f.Config, saved)!;
        TestRunner.Equal(saved.RevisionNumber + 1, recovered.RevisionNumber);
        TestRunner.Equal(12, recovered.RestartCount);
        AssertMountProof(saved, recovered);
    }

    private static void StaleSameChildSave()
    {
        var (f, live) = ProofFixture();
        RuntimeSnapshots.Save(f.Controller.State, live);
        var saved = f.Controller.State.Load()!;
        var stale = WithoutMountProof(saved) with { RestartCount = 12 };
        var prepared = RecoveryJournal.PrepareSave(f.ConfigPath, f.StatePath, stale, saved);
        TestRunner.Equal(saved.RevisionNumber + 1, prepared.RevisionNumber);
        TestRunner.Equal(12, prepared.RestartCount);
        AssertMountProof(saved, prepared);

        var otherChild = RecoveryJournal.PrepareSave(f.ConfigPath, f.StatePath,
            stale with { ProcessStartedUtc = stale.ProcessStartedUtc.AddTicks(1) }, saved);
        TestRunner.Assert(otherChild.MountDevice is null && otherChild.MountDropboxBinding is null && otherChild.MountDropboxCursor is null);
    }

    private static void LegacyAllRecords()
    {
        var (f, live) = ProofFixture();
        using var rc = new FolderTestRc(live, f.Config.Remote, f.MountedConfig, runtimeStatus: true);
        var controller = new MountController(f.Controller.Config, f.Controller.State);
        RuntimeSnapshots.Save(controller.State, live with { RcPort = rc.Port });
        var initial = controller.ReadRuntime();
        TestRunner.Assert(initial.Status is { Ready: true, DownloadActivity.Known: true });
        var saved = initial.Runtime!;
        AssertMountProof(live, saved);
        _ = Success(f.Cli("path", f.LocalFolder));

        var old = WithoutMountProof(saved) with { RevisionNumber = saved.RevisionNumber + 1 };
        RecoveryJournal.Save(f.ConfigPath, old);
        controller.State.Save(old);
        var files = new[] { f.StatePath, RecoveryJournal.CachePath(f.Config.CacheDir), RecoveryJournal.ConfigPath(f.ConfigPath) };
        byte[][] before = files.Select(File.ReadAllBytes).ToArray();
        var options = new AppOptions(f.ConfigPath, f.StatePath);
        var shellCopy = ShellCopyServer.CopyTextAsync(options, [f.LocalFolder], f.Open, f.Controller.FolderMountInspector)
            .GetAwaiter().GetResult();
        TestRunner.Assert(shellCopy.EndsWith(RelativeFolder, StringComparison.Ordinal));
        _ = Success(f.Cli("path", f.LocalFolder));
        _ = Success(f.Cli(Invitation(f.LocalFolder)));
        TestRunner.Assert(files.Select(File.ReadAllBytes).Zip(before).All(pair => pair.First.SequenceEqual(pair.Second)),
            "The copy and preview paths changed a runtime file.");
        AssertReadOnly(f.Api);
        AssertMountProof(saved, controller.ReadRuntime().Runtime!);
        foreach (string path in files) AssertMountProof(saved, PrivateJsonFile.Read<RuntimeState>(path)!);
    }

    private static void OccupiedProofPath()
    {
        var (f, live) = ProofFixture();
        using var rc = new FolderTestRc(live, f.Config.Remote, f.MountedConfig, runtimeStatus: true);
        var controller = new MountController(f.Controller.Config, f.Controller.State);
        RuntimeSnapshots.Save(controller.State, live with { RcPort = rc.Port });
        string path = f.StatePath + ".mount-proof.json";
        PrivateJsonFile.Write(path, new { sample = true });
        byte[] before = File.ReadAllBytes(path);
        var observation = controller.ReadRuntime();
        TestRunner.Assert(observation.Status.Ready);
        AssertMountProof(live, observation.Runtime!);
        TestRunner.Assert(File.ReadAllBytes(path).SequenceEqual(before));
    }

    private static void LegacyProofBoundary()
    {
        var (f, live) = ProofFixture();
        using var rc = new FolderTestRc(live, f.Config.Remote, f.MountedConfig, runtimeStatus: true);
        var controller = new MountController(f.Controller.Config, f.Controller.State);
        RuntimeSnapshots.Save(controller.State, live with { RcPort = rc.Port });
        var saved = controller.ReadRuntime().Runtime!;
        var stripped = WithoutMountProof(saved);
        foreach (RuntimeState replacement in new[] {
            stripped with { GenerationId = Guid.NewGuid().ToString("N"), GenerationNumber = saved.GenerationNumber + 1 },
            stripped with { ProcessStartedUtc = saved.ProcessStartedUtc.AddTicks(1) },
            stripped with { ExecutablePath = saved.ExecutablePath + ".other.exe" },
            stripped with { ActiveConfig = saved.ActiveConfig with { HiddenPaths = ["other"] } },
            stripped with { MountDevice = @"\Device\Volume{different}" },
            stripped with { StopInProgress = true }
        })
        {
            controller.State.Save(replacement);
            TestRunner.Assert(f.Mount.Observe(f.Config) is not { DropboxBinding: not null } &&
                f.Mount.Observe(f.Config) is not { DropboxCursor: not null });
        }
    }
}
