using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void MissingState()
    {
        using var f = new Unit5Fixture("u6a-missing-state", writeBack: "5m");
        f.Start(); f.Write("pending.txt", "recover after missing state");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).PendingFiles == 1, "No Dirty.");
        f.Controller.Stop(true);
        File.Move(f.State.PathName, Path.Combine(f.Root, "saved-state.json"));
        foreach (DbxConfig changed in new[]
        {
            f.Config with { Remote = Path.Combine(f.Root, "other-remote") },
            f.Config with { CacheDir = Path.Combine(f.Root, "other-cache") }
        })
        {
            f.Store.Save(changed);
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        }
        f.Store.Save(f.Config);
        // A syntactically valid empty state cannot remove the cache/config-side recovery pin.
        File.WriteAllText(f.State.PathName, "{}", new UTF8Encoding(false));
        var alternate = new MountController(f.Store, new StateStore(Path.Combine(f.Root, "other-state", "state.json")));
        alternate.UpdateConfig(c => c with { TestOverrides = new() { WriteBack = "1s" } });
        alternate.Start();
        try
        {
            Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.RemoteRoot, "pending.txt")) &&
                !alternate.Status().RecoveryRequired, "Same-origin recovery did not finish.", 20);
            TestRunner.Equal("recover after missing state", File.ReadAllText(Path.Combine(f.RemoteRoot, "pending.txt")));
            TestRunner.Assert(f.Controller.Status() is { Running: true, RecoveryRequired: false }, "Original state path did not recover the live generation.");
            Console.WriteLine("P2 state missing/empty/alternate: changed remote/cache refused; original destination uploaded intact.");
        }
        finally { alternate.Stop(true); }
    }

    private static void LegacyUnknownCache()
    {
        using var f = new Unit5Fixture("u6a-legacy-cache");
        string body = Path.Combine(f.Config.CacheDir, "vfs", "legacy", "sample.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(body)!); File.WriteAllText(body, "unowned pending", new UTF8Encoding(false));
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        TestRunner.Equal("unowned pending", File.ReadAllText(body));
        RuntimeSnapshots.Save(f.State, new RuntimeState { ConfigPath = f.Store.PathName, ActiveConfig = f.Config, RecoveryRequired = false });
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        Console.WriteLine("P2 unowned legacy cache: refused; body untouched; synthetic empty state cannot bypass.");
    }
}
