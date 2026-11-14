using System.Diagnostics;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void SessionDecisions()
    {
        var readOnly = new DbxConfig { Remote = "sample:" };
        var clean = new DbxStatus { Running = true, Ready = true, ReadOnly = true, UploadsQueued = 0,
            UploadsInProgress = 0, ErroredFiles = 0, QueueSupported = true };
        TestRunner.Assert(!SessionEndPolicy.Decide(readOnly, clean, false, false, true).Block);
        foreach (DbxStatus pending in new[] { clean with { UploadsQueued = 1 }, clean with { UploadsInProgress = 1 },
            clean with { ProcessUncertain = true }, clean with { RecoveryRequired = true },
            clean with { UploadsQueued = null }, clean with { StatusError = "unknown" }, clean with { ErroredFiles = 1 } })
            TestRunner.Assert(SessionEndPolicy.Decide(readOnly, pending, false, false, true).Block);
        TestRunner.Assert(SessionEndPolicy.Decide(readOnly, clean, false, true, true).Block);
        TestRunner.Assert(SessionEndPolicy.Decide(readOnly, clean, false, false, false).Block);
        TestRunner.Assert(SessionEndPolicy.Decide(readOnly, clean with { ReadOnly = false }, false, false, true).Block);
        TestRunner.Assert(!SessionEndPolicy.Decide(readOnly, new(), true, false).Block);
        Console.WriteLine("Q5 read-only allows only verified empty queue/cache; pending, uploading, recovery, process/RC/cache uncertainty and external-generation uncertainty all require guarded stop.");
    }

    private static void SessionGenerations()
    {
        using var f = new Unit5Fixture("u6b-session", writable: false); f.Start();
        // These generation assertions require uncontended control. The deliberately busy case below stays nonblocking.
        TraySnapshot snapshot = ControlGate.Execute(() =>
        {
            TraySnapshot current = SnapshotReader.Read(f.Controller);
            TestRunner.Assert(current.PendingCacheClean);
            TestRunner.Assert(SnapshotReader.IsCurrentForSession(f.Controller, current, f.Config));
            f.Store.Save(f.Config with { WriteAllowedRemotes = [f.Config.Remote] });
            TestRunner.Assert(!SnapshotReader.IsCurrentForSession(f.Controller, current, f.Config));
            f.Store.Save(f.Config);
            RuntimeState original = f.State.Load()!;
            RuntimeSnapshots.Save(f.State, original with { ActiveConfig = original.ActiveConfig with { Retention = "3h" } });
            TestRunner.Assert(!SnapshotReader.IsCurrentForSession(f.Controller, current, f.Config));
            RuntimeSnapshots.Save(f.State, original);
            return current;
        });
        TestControlGate.WhileHeld(() =>
        {
            var timer = Stopwatch.StartNew();
            TestRunner.Assert(!SnapshotReader.IsCurrentForSession(f.Controller, snapshot, f.Config));
            TestRunner.Assert(timer.Elapsed < TimeSpan.FromMilliseconds(250));
            return 0;
        });
        string stranded = Path.Combine(f.Config.CacheDir, "vfs", "unidentified", "body");
        Directory.CreateDirectory(Path.GetDirectoryName(stranded)!); File.WriteAllText(stranded, "preserve pending body");
        TraySnapshot unknown = SnapshotReader.Read(f.Controller);
        TestRunner.Assert(!unknown.PendingCacheClean);
        TestRunner.Assert(SessionEndPolicy.Decide(f.Config, unknown.Status, false, false, unknown.PendingCacheClean).Block);
        Console.WriteLine("Q5 external permission/ActiveConfig changes and busy mutex make snapshot unknown; zero queue plus body-only cache cannot allow read-only shutdown.");
    }
}
