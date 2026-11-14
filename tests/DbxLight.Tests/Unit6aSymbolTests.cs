using DbxLight.Core;
namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void SymbolRecovery()
    {
        using var f = new Unit5Fixture("u6a-symbols", writeBack: "5m");
        f.Seed("cloud\uff1f.txt", "cloud before"); f.Start();
        var expected = new Dictionary<string, string>
        {
            ["\u3053\u308c\u3067\u3088\u3044\uff1f.txt"] = "fullwidth question",
            ["\u8b70\u4e8b\u9332\uff1a\u7b2c1\u56de.txt"] = "fullwidth colon",
            ["cloud?.txt"] = "halfwidth cloud question"
        };
        foreach (var pair in expected) f.Write(pair.Key.Replace('?', '\uf03f'), pair.Value);
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == 3, "Three complete Dirty entries not saved.");
        f.Controller.Stop(true);
        var stopped = PendingCacheInspector.InspectStopped(f.Config.CacheDir);
        TestRunner.Equal(3, stopped.PendingFiles);
        var originals = stopped.Entries.Where(e => e.Dirty == true).ToDictionary(e => File.ReadAllText(e.DataPath), e => e.DataPath, StringComparer.Ordinal);
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Start(awaitRecovery: false);
        var records = RescueStore.List(f.State.Load()!);
        TestRunner.Equal(3, records.Count);
        foreach (var pair in expected)
            TestRunner.Equal(pair.Value, File.ReadAllText(records.Single(r => r.OriginalPath == originals[pair.Value]).DestinationPath));
        Unit5Fixture.Wait(() => !f.Controller.Status().RecoveryRequired, "Original-name recovery not confirmed.", 30);
        records = f.Controller.RescueList();
        foreach (var pair in expected)
        {
            string cloudFile = Path.Combine(f.RemoteRoot, RecoveryCacheNames.EncodeForDisk(pair.Key));
            TestRunner.Equal(pair.Key == "cloud?.txt" ? "cloud before" : null, File.Exists(cloudFile) ? File.ReadAllText(cloudFile) : null);
            RescueRecord record = records.Single(r => r.OriginalPath == originals[pair.Value]);
            if (RecoveryCacheNames.PreservationReason(record.OriginalPath) is { } quotedReason)
            { TestRunner.Assert(record.CloudPath is null); TestRunner.Equal(quotedReason, record.Reason); }
            else TestRunner.Equal(pair.Key, record.CloudPath);
            TestRunner.Assert(!record.Conflict && record.MoveCompleted);
            TestRunner.Assert(File.Exists(record.DestinationPath));
            Console.WriteLine($"E1 preserved path={pair.Key}; body matches; cloud unchanged; moveCompleted={record.MoveCompleted}");
        }
        TestRunner.Equal(1, Directory.GetFiles(f.RemoteRoot).Length);
        Console.WriteLine("P3 stopped preflight: all three bodies preserved, cloud unchanged, no automatic reupload.");
    }

    private static void StrandedRescue()
    {
        using var f = new Unit5Fixture("u6a-hidden-stranded", writeBack: "5m");
        string name = "hidden\uff1f.txt";
        f.Start(); f.Write(name, "preserve a hidden stranded file");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Dirty not saved.");
        f.Controller.Stop(true);
        f.Store.Save(f.Config with { HiddenPaths = [name], TestOverrides = new() { WriteBack = "1s" } });
        f.Start(awaitRecovery: false);
        var rescued = RescueStore.List(f.State.Load()!); TestRunner.Equal(1, rescued.Count);
        TestRunner.Equal("preserve a hidden stranded file", File.ReadAllText(rescued[0].DestinationPath));
        TestRunner.Assert(rescued[0].MoveCompleted && !f.Controller.Status().RecoveryRequired);
        Console.WriteLine("P3 hidden name: rescued before launch; body intact; recovery resolved.");
    }
}
