using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    private static void RunNames(Action<string, string, Action> test)
    {
        test("G1", "case-insensitive new name collides with pre-existing upper-case cloud name", () => CaseNames(false, false));
        test("G1", "an existing object's case-only spelling change keeps healthy pending data", () => CaseNames(true, false));
        test("G1", "parent folder spelling is resolved with the backend's name rule", () => CaseNames(true, true));
        test("G1", "ambiguous equivalent parents and leaves, malformed listings and missing rules are unknown", NameRules);
        foreach (string kind in new[] { "ambiguous", "malformed", "unknown-rule" })
            test("G1", "unknown name identity preserves a complete pending pair without upload: " + kind, () => NameSafetyFallback(kind));
    }
    private static RecoveryFolder Folder(RemoteInfo info, string parent, params (string Path, bool Dir)[] entries) =>
        RecoveryRemoteProbe.DecodeFolder(parent, info, JsonSerializer.SerializeToElement(new { list = entries.Select(e => new { Path = e.Path, IsDir = e.Dir }) }));

    private static void NameRules()
    {
        var insensitive = new RemoteInfo("dropbox", "", 1, [], true);
        TestRunner.Assert(!RecoveryRemoteProbe.NameRuleKnown(insensitive, "stra\u00dfe.txt"));
        TestRunner.Assert(!RecoveryRemoteProbe.NameRuleKnown(insensitive, "e\u0301.txt"));
        TestRunner.Assert(RecoveryRemoteProbe.NameRuleKnown(insensitive, "\u898b\u672c\uff1f.txt"));
        TestRunner.Assert(RecoveryRemoteProbe.NameRuleKnown(insensitive with { CaseInsensitive = false }, "stra\u00dfe.txt"));
        var sensitive = insensitive with { CaseInsensitive = false };
        var duplicate = Folder(insensitive, "", ("report.txt", false), ("REPORT.txt", false));
        TestRunner.Assert(!duplicate.Comparable);
        TestRunner.Assert(Folder(sensitive, "", ("report.txt", false), ("REPORT.txt", false)).Comparable);
        var ordinary = Folder(sensitive, "", ("REPORT.txt", false));
        TestRunner.Assert(!ordinary.Items.ContainsKey("report.txt"));
        TestRunner.Assert(!Folder(insensitive with { CaseInsensitive = null }, "", ("file.txt", false)).Comparable);
        foreach (string json in new[] { "{}", "{\"list\":{}}", "{\"list\":[null]}", "{\"list\":[{\"Path\":\"file.txt\"}]}", "{\"list\":[{\"Path\":\"other/file.txt\",\"IsDir\":false}]}" })
            TestRunner.Assert(!RecoveryRemoteProbe.DecodeFolder("", insensitive, JsonDocument.Parse(json).RootElement).Comparable);
        var parents = Folder(insensitive, "", ("Docs", true));
        var resolved = RecoveryRemoteProbe.ResolveFolder("docs", insensitive, p => p == "" ? parents : Folder(insensitive, p, ("Docs/REPORT.txt", false)));
        TestRunner.Assert(resolved.Comparable && RecoveryRemoteProbe.ResolveItem(resolved, insensitive, "docs/report.txt", out var resolvedItem) && resolvedItem is not null);
        var ambiguous = RecoveryRemoteProbe.ResolveFolder("docs", insensitive, _ => Folder(insensitive, "", ("Docs", true), ("DOCS", true)));
        TestRunner.Assert(!ambiguous.Comparable);
        var changed = Folder(insensitive, "", ("REPORT.txt", false));
        TestRunner.Assert(RecoveryRemoteProbe.ResolveItem(changed, insensitive, "report.txt", out var changedItem));
        TestRunner.Equal(RecoveryMatch.Different, RecoveryVerification.CompareFingerprint("", insensitive, changedItem));
        using var fs = JsonDocument.Parse("{\"Name\":\"dropbox\",\"Root\":\"\",\"Hashes\":[],\"Features\":{\"CaseInsensitive\":true}}");
        TestRunner.Equal(true, RecoveryRemoteProbe.DecodeInfo(fs.RootElement).CaseInsensitive);
    }

    private static void NameSafetyFallback(string kind)
    {
        using var f = new Unit5Fixture("u6f-name-uncertain", writeBack: "5m");
        f.Start(); f.Write("report.txt", "complete uncertain body");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Dirty pair missing.");
        f.Controller.Stop(true);
        f.Controller.RecoveryInfoOverride = info => info with { CaseInsensitive = kind == "unknown-rule" ? null : true };
        if (kind != "unknown-rule")
            f.Controller.RecoveryFolderTestOverride = (_, info) => kind == "ambiguous"
                ? Folder(info, "", ("report.txt", false), ("REPORT.txt", false))
                : RecoveryRemoteProbe.DecodeFolder("", info, JsonSerializer.SerializeToElement(new { invalid = true }));
        f.Start();
        var record = f.Controller.RescueList().Single();
        TestRunner.Assert(record.MoveCompleted && record.CompleteData);
        TestRunner.Equal("complete uncertain body", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal(0, Directory.GetFiles(f.RemoteRoot).Length);
    }

    private static void CaseNames(bool existing, bool parentCase)
    {
        using var f = new Unit5Fixture("u6f-case-" + existing + "-" + parentCase, writeBack: "5m");
        string name = parentCase ? "docs/report.txt" : "report.txt";
        if (existing) f.Seed(name, "original cloud");
        f.Start();
        if (existing) _ = File.ReadAllText(Path.Combine(f.MountRoot, name.Replace('/', '\\')));
        if (parentCase) Directory.CreateDirectory(Path.Combine(f.MountRoot, "docs"));
        f.Write(name, "complete pending version");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Dirty pair missing.");
        f.Controller.Stop(true);
        if (parentCase)
        {
            Directory.Move(Path.Combine(f.RemoteRoot, "docs"), Path.Combine(f.RemoteRoot, "temporary"));
            Directory.Move(Path.Combine(f.RemoteRoot, "temporary"), Path.Combine(f.RemoteRoot, "DOCS"));
        }
        else if (existing)
        {
            File.Move(Path.Combine(f.RemoteRoot, "report.txt"), Path.Combine(f.RemoteRoot, "temporary"));
            File.Move(Path.Combine(f.RemoteRoot, "temporary"), Path.Combine(f.RemoteRoot, "REPORT.txt"));
        }
        else f.Seed("REPORT.txt", "pre-existing colleague cloud");
        f.Controller.RecoveryInfoOverride = info => info with { CaseInsensitive = true };
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Start();
        if (existing)
        {
            TestRunner.Equal(0, f.Controller.RescueList().Count);
            Unit5Fixture.Wait(() => File.ReadAllText(Path.Combine(f.RemoteRoot, name.Replace('/', '\\'))) == "complete pending version", "Healthy case-only update did not upload.");
        }
        else
        {
            TestRunner.Equal("pre-existing colleague cloud", File.ReadAllText(Path.Combine(f.RemoteRoot, "REPORT.txt")));
            var rescue = f.Controller.RescueList().Single();
            TestRunner.Equal("complete pending version", File.ReadAllText(rescue.DestinationPath));
            TestRunner.Assert(rescue.Conflict && rescue.MoveCompleted);
        }
    }
}
