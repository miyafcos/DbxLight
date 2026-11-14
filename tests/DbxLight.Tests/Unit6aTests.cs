using System.Diagnostics;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    public static void Run(TestRunner runner, string? issue = null, bool includeMounts = true)
    {
        void Test(string id, string name, Action action, bool mount = false)
        {
            if ((issue is null || issue == id) && (includeMounts || !mount)) runner.Test(id + ": " + name, action);
        }
        Test("P8", "pending recovery refuses reconnect/logout even after state removal", PendingAccountGuards, true);
        foreach (string kind in new[] { "updated", "deleted", "created", "unchanged" })
            Test("P4", "pre-start conflict comparison: " + kind, () => ConflictCase(kind), true);
        Test("P4", "read-only mount preserves conflicting body without publishing it", ReadOnlyConflict, true);
        Test("P4", "unavailable comparison blocks start and watcher retries later", OfflineComparison, true);
        Test("P4", "fingerprint time preserves nanoseconds and UTC formatting", FingerprintTimes);
        Test("P4", "status window lists original, reason, destination and open button", RescueWindow);
        Test("P3", "fullwidth symbols and halfwidth cloud question are preserved before mount", SymbolRecovery, true);
        Test("P3", "hidden unreloadable name is rescued before starting rclone", StrandedRescue, true);
        Test("P2", "missing, empty or alternate state retains original destination and recovers", MissingState, true);
        Test("P2", "legacy unowned pending cache refuses first-use and empty-state bypass", LegacyUnknownCache);
        Test("P1", "failure after rescue but before launch retains the durable recovery pin", PreflightFailureKeepsRecovery, true);
        Test("P1", "missing, empty and truncated metadata are rescued before rclone starts", DamagedCache, true);
        Test("P1", "rescue setting requires an absolute location outside mount/cache", RescueConfiguration);
        Test("P6", "known CreateProcess failure records no child and permits explicit clear", KnownCreationFailure);
        Test("P6", "PID-zero launch intent is unknown and cannot clear or start on another drive/port", IncompleteLaunch);
        Test("P6", "parent crash before/after durable ownership cannot lose a running child", SuspendedOwnership, true);
        Test("P5", "unversioned runtime .exe record cannot infer death from name mismatch", LegacyIdentityVersion);
        Test("P5", "unversioned cache owner also prevents clear and duplicate start", LegacyCacheOwner);
        Test("P5", "untrusted executable or absent start time refuses clear and duplicate start", UntrustedIdentity);
        Test("P7", "observation handles allow write and replacement; partial JSON stays unknown", SharedObservation);
        Test("P7", "repeated live inspection does not reject rclone metadata writes and retains Dirty", LiveInspection, true);
    }

    private static void SharedObservation()
    {
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "u6a-share-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "vfs", "sample"), meta = Path.Combine(root, "vfsMeta", "sample");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(data, "pending", new UTF8Encoding(false));
        File.WriteAllText(meta, "{", new UTF8Encoding(false));
        using (var observer = PendingCacheInspector.OpenObservation(meta))
        using (var writer = new FileStream(meta, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.WriteByte((byte)' '); writer.Flush(true);
            File.Move(meta, meta + ".partial");
        }
        TestRunner.Assert(!PendingCacheInspector.Observe(root).Clean);
        File.Move(meta + ".partial", meta);
        TestRunner.Assert(!PendingCacheInspector.InspectStopped(root).Clean);
    }

    private static void LiveInspection()
    {
        using var f = new Unit5Fixture("u6a-observe", writeBack: "5m");
        f.Start();
        var observation = Task.Run(() =>
        {
            for (int i = 0; i < 600; i++) _ = PendingCacheInspector.Observe(f.Config.CacheDir);
        });
        for (int i = 0; i < 120; i++) f.Write("observed.txt", "pending version " + i);
        observation.GetAwaiter().GetResult();
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).PendingFiles == 1, "Dirty was lost during live observations.");
        f.Controller.Stop(true);
        TestRunner.Equal(1, PendingCacheInspector.InspectStopped(f.Config.CacheDir).PendingFiles);
        string logs = string.Join("\n", Directory.GetFiles(f.Config.LogDir, "*.log").Select(File.ReadAllText));
        TestRunner.Assert(!logs.Contains("sharing violation", StringComparison.OrdinalIgnoreCase) &&
            !logs.Contains("used by another process", StringComparison.OrdinalIgnoreCase), logs);
        Console.WriteLine("P7 saves=120 observations=600 Dirty=1 sharing violations=0");
    }
}
