using System.Text;
using DbxLight.Core;
namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void PreflightFailureKeepsRecovery()
    {
        using var f = new Unit5Fixture("u6a-preflight-failure", writeBack: "1s");
        f.Start(); f.Write("saved.txt", "preserve across preflight failure");
        Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.RemoteRoot, "saved.txt")) &&
            PendingCacheInspector.Observe(f.Config.CacheDir).Clean, "Initial file did not finish.");
        f.Controller.Stop();
        TestRunner.Assert(!f.State.Load()!.RecoveryRequired);
        string metadata = Path.Combine(f.State.Load()!.CacheMetadataRoot!, "saved.txt");
        File.WriteAllText(metadata, "{", new UTF8Encoding(false));
        f.Controller.StartupCheckpoint = (phase, _) =>
        {
            if (phase == "after-cache-preservation") throw new IOException("Injected failure before launch intent.");
        };
        TestRunner.Throws<IOException>(() => f.Controller.Start());
        TestRunner.Assert(PendingCacheInspector.InspectStopped(f.Config.CacheDir).Clean);
        var rescue = f.Controller.RescueList().Single();
        TestRunner.Equal("preserve across preflight failure", File.ReadAllText(rescue.DestinationPath));
        TestRunner.Assert(f.State.Load()!.RecoveryRequired && f.Controller.Status().RecoveryRequired);
        f.Controller.UpdateConfig(c => c with { CacheDir = Path.Combine(f.Root, "other-cache") });
        TestRunner.Equal("preserve across preflight failure", File.ReadAllText(rescue.DestinationPath));
        Console.WriteLine("P1 after rescue/before launch failure: clean cache, intact rescue, destination change allowed after owner/cache inspection.");
    }
}
