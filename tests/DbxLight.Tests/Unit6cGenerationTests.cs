using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6cTests
{
    private static void StaleStateGeneration()
    {
        using var f = new Unit5Fixture("u6c-state-generations", writeBack: "5m");
        f.Start(); f.Write("pending.txt", "generation B must recover this");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Dirty missing.");
        f.Controller.Stop(true);
        RuntimeState stale = f.State.Load()!;
        f.State.Save(stale with { RecoveryRevisionUtc = DateTimeOffset.UtcNow.AddYears(10) });
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        var stateB = new StateStore(Path.Combine(f.Root, "state-B", "state.json"));
        var controllerB = new MountController(f.Store, stateB);
        RuntimeState? running = null;
        try
        {
            controllerB.Start();
            Unit5Fixture.Wait(() => !controllerB.Status().RecoveryRequired, "B did not finish recovery.", 30);
            running = stateB.Load()!;
            TestRunner.Assert(running.GenerationNumber > stale.GenerationNumber && running.GenerationId != stale.GenerationId);
            TestRunner.Assert(Guid.TryParseExact(running.GenerationId, "N", out _));
            foreach (string path in new[] { RecoveryJournal.CachePath(f.Config.CacheDir), RecoveryJournal.ConfigPath(f.Store.PathName) })
            {
                RuntimeState copy = PrivateJsonFile.Read<RuntimeState>(path)!;
                TestRunner.Equal(running.GenerationId, copy.GenerationId);
                TestRunner.Equal(running.GenerationNumber, copy.GenerationNumber);
                TestRunner.Equal(running.RevisionNumber, copy.RevisionNumber);
            }
            string[] paths = [f.State.PathName, stateB.PathName, RecoveryJournal.CachePath(f.Config.CacheDir), RecoveryJournal.ConfigPath(f.Store.PathName), f.Store.PathName];
            var before = paths.ToDictionary(p => p, File.ReadAllBytes);
            DbxStatus statusA = f.Controller.Status();
            TestRunner.Assert(statusA.Running && statusA.Ready && statusA.ProcessUncertain);
            TestRunner.Equal(running.RclonePid, statusA.RclonePid);
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Stop());
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Stop(true));
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.PrepareConnectionChange(f.Store.Load()));
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.UpdateConfig(c => c with { Retention = "3h" }));
            TestRunner.Throws<PendingUploadsException>(() => RecoveryJournal.PrepareSave(f.Store.PathName, f.State.PathName, stale, f.State.Load()));
            foreach (string path in paths) TestRunner.Assert(before[path].AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Stale state wrote a newer generation: " + path);
            using var process = ProcessIdentity.Open(running); TestRunner.Assert(process is not null);
            TestRunner.Equal("generation B must recover this", File.ReadAllText(Path.Combine(f.RemoteRoot, "pending.txt")));
            Console.WriteLine($"R3 A={stale.GenerationNumber}/{stale.GenerationId}; B={running.GenerationNumber}/{running.GenerationId}; A future timestamp ignored; status sees B PID={running.RclonePid}; stop/force/connection/settings/stale-save refused; all records unchanged.");
        }
        finally
        {
            controllerB.Stop(true);
            if (running is not null) { using var remaining = ProcessIdentity.Open(running); TestRunner.Assert(remaining is null); }
        }
    }

    private static RuntimeState GenerationFixture(Unit5Fixture f, long number, string owner, int pid = int.MaxValue) => new()
    {
        GenerationId = Guid.NewGuid().ToString("N"), GenerationNumber = number, RevisionNumber = 1,
        OwnerStatePath = owner, RecoveryJournalVersion = 1, RecoveryRevisionUtc = DateTimeOffset.UtcNow,
        ConfigPath = f.Store.PathName, ActiveConfig = f.Config, RclonePid = pid, ProcessStartedUtc = DateTime.UnixEpoch,
        ExecutablePath = @"C:\Sample\rclone.exe", StopDisposition = StopDisposition.Clean, RecoveryRequired = false,
        StopRequested = true, StoppedUtc = DateTime.UtcNow
    };

    private static void AmbiguousGenerations()
    {
        using (var f = new Unit5Fixture("u6c-unknown-generation"))
        {
            RuntimeState a = GenerationFixture(f, 1, f.State.PathName);
            using var self = Process.GetCurrentProcess();
            RuntimeState b = GenerationFixture(f, 2, Path.Combine(f.Root, "other-state", "state.json"), self.Id) with
            {
                ProcessStartedUtc = default, ExecutablePath = ProcessIdentity.ImagePath(self), RecoveryRequired = true,
                StopDisposition = StopDisposition.ProcessUnknown, StoppedUtc = null, StopRequested = false
            };
            f.State.Save(a); RecoveryJournal.Save(f.Store.PathName, b);
            var paths = new[] { f.State.PathName, RecoveryJournal.CachePath(f.Config.CacheDir), RecoveryJournal.ConfigPath(f.Store.PathName) };
            var bytes = paths.ToDictionary(p => p, File.ReadAllBytes);
            TestRunner.Assert(f.Controller.Status().ProcessUncertain);
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Stop(true));
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.UpdateConfig(c => c with { Retention = "3h" }));
            foreach (string path in paths) TestRunner.Assert(bytes[path].AsSpan().SequenceEqual(File.ReadAllBytes(path)));
            Console.WriteLine("R3 newer owner cannot be identified: process uncertainty retained; all mutation attempts refused; journal/state bytes unchanged.");
        }
        using (var f = new Unit5Fixture("u6c-generation-fork"))
        {
            RuntimeState a = GenerationFixture(f, 3, f.State.PathName) with { RecoveryRevisionUtc = DateTimeOffset.UtcNow.AddYears(10) };
            RuntimeState b = GenerationFixture(f, 3, f.State.PathName);
            f.State.Save(a); RecoveryJournal.Save(f.Store.PathName, b);
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Status());
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Stop(true));
            TestRunner.Equal(a.GenerationId, f.State.Load()!.GenerationId);
            TestRunner.Equal(b.GenerationId, PrivateJsonFile.Read<RuntimeState>(RecoveryJournal.CachePath(f.Config.CacheDir))!.GenerationId);
            Console.WriteLine("R3 same ordinal/different random IDs: no timestamp election; status/stop refused; both generations retained.");
        }
    }
}
