using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6cTests
{
    private static JsonElement RemoteItem(long size = 3, string? hash = "900150983cd24fb0d6963f7d28e17f72",
        string time = "2026-01-01T00:00:00Z") => JsonSerializer.SerializeToElement(new
    {
        IsDir = false, Size = size, ModTime = time,
        Hashes = hash is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["md5"] = hash }
    });

    private static void FingerprintComponents()
    {
        var info = new RemoteInfo("local", "sample", 1, ["md5"]);
        string noHash = "3,2026-01-01 00:00:00 +0000 UTC";
        TestRunner.Equal(RecoveryMatch.Same, RecoveryVerification.CompareFingerprint(noHash, info, RemoteItem()));
        TestRunner.Equal(RecoveryMatch.Same, RecoveryVerification.CompareFingerprint(noHash + ",", info, RemoteItem()));
        TestRunner.Equal(RecoveryMatch.Same, RecoveryVerification.CompareFingerprint(noHash + ",900150983cd24fb0d6963f7d28e17f72", info, RemoteItem(hash: null)));
        TestRunner.Equal(RecoveryMatch.Different, RecoveryVerification.CompareFingerprint(noHash, info, RemoteItem(size: 4)));
        TestRunner.Equal(RecoveryMatch.Unknown, RecoveryVerification.CompareFingerprint("-1", info, RemoteItem()));
        TestRunner.Equal(RecoveryMatch.Unknown, RecoveryVerification.CompareFingerprint(null, info, RemoteItem()));
        Console.WriteLine("R5 components: missing original hash is Same; no comparable evidence is Unknown.");
    }

    private static void MissingFingerprintHash()
    {
        using var f = new Unit5Fixture("u6c-missing-fingerprint-hash", writeBack: "5m");
        f.Seed("shared.txt", "cloud original"); f.Start();
        _ = File.ReadAllText(Path.Combine(f.MountRoot, "shared.txt"));
        f.Write("shared.txt", "local saved version");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData && !string.IsNullOrEmpty(e.Fingerprint)),
            "Saved fingerprint missing.");
        f.Controller.Stop(true);
        CacheEntry entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        string original = File.ReadAllText(entry.MetadataPath);
        string shortened = string.Join(',', entry.Fingerprint!.Split(',').Take(2));
        File.WriteAllText(entry.MetadataPath, original.Replace(entry.Fingerprint, shortened, StringComparison.Ordinal), new UTF8Encoding(false));
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Start();
        TestRunner.Equal(0, f.Controller.RescueList().Count);
        TestRunner.Equal("local saved version", File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")));
        Console.WriteLine("R5 missing saved hash: unchanged size+time matched; conflict count=0; normal original upload succeeded.");
    }

}
