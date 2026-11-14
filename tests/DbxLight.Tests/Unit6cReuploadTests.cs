using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6cTests
{
    private static void OriginalReadOnly()
    {
        using var f = new Unit5Fixture("u6c-original-readonly", writeBack: "5m");
        f.Start();
        f.Write("saved\uff1f.txt", "retained original");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData),
            "Complete Dirty missing.");
        f.Controller.Stop(true);
        f.Store.Save(f.Config with { WriteAllowedRemotes = [], TestOverrides = new() { WriteBack = "1s" } });
        f.Start(awaitRecovery: false);
        RescueRecord record = f.Controller.RescueList().Single();
        TestRunner.Assert(record.MoveCompleted);
        TestRunner.Equal("retained original", File.ReadAllText(record.DestinationPath));
        TestRunner.Assert(!File.Exists(Path.Combine(f.RemoteRoot, "saved\uff1f.txt")));
        TestRunner.Assert(!f.Controller.Status().RecoveryRequired);
        Console.WriteLine("R2 read-only: original retained, no upload, recovery resolved.");
    }
}
