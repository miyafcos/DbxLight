using System.Diagnostics;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6cTests
{
    internal static void Run(TestRunner runner, string? issue = null, bool includeMounts = true)
    {
        void Test(string id, string name, Action action, bool mount = false)
        {
            if ((issue is null || issue == id) && (includeMounts || !mount)) runner.Test(id + ": " + name, action);
        }
        Test("R1", "an open same-size writer immediately after mount is never stopped or rescued", LiveWriter, true);
        Test("R2", "read-only original-name recovery retains complete bodies without uploading", OriginalReadOnly, true);
        Test("R5", "fingerprints compare only available components and unavailable evidence stays unknown", FingerprintComponents);
        Test("R5", "a saved fingerprint missing only its hash is not a conflict", MissingFingerprintHash, true);
        Test("R3", "stale state A sees live B and cannot stop or change its generation", StaleStateGeneration, true);
        Test("R3", "unknown newer owners and unordered generation forks refuse without overwriting records", AmbiguousGenerations);
        Test("R7", "unrecorded intents retain live or incomplete parent identity and release a stopped parent", ParentIntentRecovery, true);
        Test("R7", "a recorded suspended child is identified, terminated and replaced after parent death", SuspendedLaunchRecovery, true);
        Test("R7", "a running child is adopted from a suspended-stage marker by exact identity", RunningLaunchMarker, true);
    }

    private static void LiveWriter()
    {
        using var f = new Unit5Fixture("u6c-live-writer", writeBack: "1s");
        f.Seed("writing.txt", "original");
        FileStream? writer = null;
        int mountedPid = 0;
        f.Controller.StartupCheckpoint = (stage, state) =>
        {
            if (stage != "mount-ready") return;
            mountedPid = state.RclonePid;
            writer = new FileStream(Path.Combine(f.MountRoot, "writing.txt"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            byte[] bytes = Encoding.UTF8.GetBytes("new-data");
            var timer = Stopwatch.StartNew();
            do
            {
                writer.Position = 0; writer.Write(bytes); writer.Flush(); Thread.Sleep(100);
            } while (timer.Elapsed < TimeSpan.FromSeconds(4));
        };
        try
        {
            f.Start(awaitRecovery: false);
            TestRunner.Assert(writer is not null);
            var status = f.Controller.Status();
            TestRunner.Assert(status.Running && status.Ready);
            TestRunner.Equal(mountedPid, f.State.Load()!.RclonePid);
            TestRunner.Equal(0, f.Controller.RescueList().Count);
            TestRunner.Assert(PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true));
            Console.WriteLine($"R1 open writer: pid={mountedPid} still running; rescue=0; Dirty retained after >4s.");
        }
        finally { writer?.Dispose(); f.Controller.StartupCheckpoint = null; }
        Unit5Fixture.Wait(() => File.ReadAllText(Path.Combine(f.RemoteRoot, "writing.txt")) == "new-data",
            "Closed writer was not uploaded normally.");
        TestRunner.Equal(0, f.Controller.RescueList().Count);
        Console.WriteLine("R1 close: cloud=new-data; ordinary upload completed; no mount restart.");
    }
}
