using System.Diagnostics;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6dTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        Test("S7", "small work area retains all status content and buttons at 100/125/150 percent", SmallStatusWindow);
        foreach (string stage in new[] { "authorized", "switch-marker", "switch-conf", "switch-before-settings", "switch-settings", "switch-installed" })
            Test("S3", "key and settings recover together after parent death: " + stage, () => KeyCrash(stage));
        Test("S3", "an unknown switch marker refuses mount and allows a new read-only login", () => KeyCrash("switch-conf", corrupt: true));
        Test("S3", "authorization leaves the production key and old settings untouched until commit", StagedAuthorization);
        Test("S3", "unregistered legacy pending credentials remain visible through load and logout", LegacyKeyCleanup);
        foreach (bool legacy in new[] { false, true })
            Test("S6", "inherited application parent supports private login, commit and fake start; legacy=" + legacy, () => InheritedLogin(legacy));
        Test("S6", "an unsafe existing credential subdirectory still refuses login", UnsafeCredentialDirectory);
        foreach (string stage in new[] { "created", "before-job" })
            Test("S4", "helper never executes after parent death before job membership: " + stage, () => HelperCrash(stage));
        foreach (string kind in new[] { "pending", "one-sided", "clean" })
            Test("S5", "connection changes inspect legacy cache without state: " + kind, () => LegacyConnectionCache(kind));
        Test("S5", "unknown owners and unresolved rescue records refuse connection changes without state", ConnectionStorageMarkers);
        Test("S5", "first configuration accepts missing and empty isolated caches", FirstConnectionStorage);
        Test("S2", "shared rescue owners isolate clear, pending checks and reupload", SharedRescueOwners);
        Test("S2", "legacy ownership requires matching cache pairs and refuses ambiguous owners", LegacyRescueOwners);
        foreach (string kind in new[] { "outside", "other-record", "junction", "missing", "traversal", "record-path", "cache-pair", "bad-json", "null-fields", "null-entry", "forged-clear" })
            Test("S1",  "untrusted rescue paths refuse record mutations but allow clean-cache lifecycle: " + kind, () => InvalidRescue(kind));
        Test("S1", "valid plans resume and only existing validated rescue folders can open", ValidRescue);
    }
    private static RescueRecord MakeRescue(DbxConfig config, string name = "saved.txt")
    {
        string body = Path.Combine(config.CacheDir, "vfs", "sample", name);
        string meta = Path.Combine(config.CacheDir, "vfsMeta", "sample", name);
        Directory.CreateDirectory(Path.GetDirectoryName(body)!);
        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(body, "preserved sample", new UTF8Encoding(false));
        File.WriteAllText(meta, "sample metadata", new UTF8Encoding(false));
        return RescueStore.Rescue(config, new CacheEntry("sample/" + name, body, meta, true, true,
            false, null, null, null, null, false, "sample"), name, "sample");
    }
    private static void InvalidRescue(string kind)
    {
        using var f = new Unit5Fixture("u6d-S1-" + kind);
        RescueRecord good = MakeRescue(f.Config);
        string outside = Path.Combine(f.Root, "outside.txt");
        File.WriteAllText(outside, "outside unchanged", new UTF8Encoding(false));
        RescueRecord bad = kind switch
        {
            "outside" => good with { DestinationPath = outside },
            "other-record" => good with { DestinationPath = MakeRescue(f.Config, "other.txt").DestinationPath },
            "missing" => good with { DestinationPath = Path.Combine(f.Config.RescueDir, "20000101-000000-0000000-" + good.Id[..6], "saved.txt") },
            "traversal" => good with { DestinationPath = Path.Combine(f.Config.RescueDir, "..", "outside.txt") },
            "record-path" => good with { RecordPath = outside },
            "cache-pair" => good with { OriginalMetadataPath = outside, MoveCompleted = false },
            "null-fields" => good with { CacheRelativePath = null!, DestinationPath = null! },
            "null-entry" => good with { HadData = false, HadMetadata = false },
            "forged-clear" => good with { DestinationPath = outside, Acknowledged = true },
            _ => good
        };
        if (kind == "junction")
        {
            string folder = Path.GetDirectoryName(good.DestinationPath)!;
            string retained = folder + "-retained";
            Directory.Move(folder, retained);
            using var helper = Process.Start(RcloneArguments.ProcessInfo("cmd.exe", ["/d", "/c", "mklink", "/J", folder, retained]))!;
            TestRunner.Assert(helper.WaitForExit(10000) && helper.ExitCode == 0);
        }
        PrivateJsonFile.Write(good.RecordPath, bad);
        if (kind == "bad-json") File.WriteAllText(good.RecordPath, "{", new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(good.RecordPath);
        var item = RescueStore.List(f.Config).Single(r => r.RecordPath == good.RecordPath);
        TestRunner.Equal("unknown", item.RecordStatus);
        FormTests.Sta(() =>
        {
            using var form = new DbxLight.Tray.StatusForm();
            form.UpdateSnapshot(new DbxLight.Tray.TraySnapshot(new DbxStatus { RescueFiles = 1 }, [], [], true, null, Rescued: [item]), null, f.Config);
        });
        TestRunner.Assert(RescueStore.OpenFolder(item, f.Config) is null);
        f.State.Save(new RuntimeState { ConfigPath = f.Store.PathName, ActiveConfig = f.Config,
            StopDisposition = StopDisposition.Forced, RecoveryRequired = true, RescueRecords = [good.RecordPath] });
        f.Controller.ClearRecovery(true);
        f.Start();
        TestRunner.Assert(!f.Controller.Status().RecoveryRequired);
        f.Controller.Stop();
        TestRunner.Throws<PendingUploadsException>(() => RescueStore.Save(item with { Acknowledged = true }));
        TestRunner.Throws<PendingUploadsException>(() => RescueStore.FinishMove(item));
        TestRunner.Equal("outside unchanged", File.ReadAllText(outside));
        TestRunner.Assert(before.AsSpan().SequenceEqual(File.ReadAllBytes(good.RecordPath)));
    }
    private static void ValidRescue()
    {
        using var f = new Unit5Fixture("u6d-S1-valid");
        RescueRecord record = MakeRescue(f.Config);
        var reread = RescueStore.List(f.Config).Single();
        TestRunner.Equal("verified", reread.RecordStatus);
        TestRunner.Equal(Path.GetDirectoryName(record.DestinationPath), RescueStore.OpenFolder(reread, f.Config));
        TestRunner.Assert(RescueStore.OpenFolder(reread, f.Config with { RescueDir = Path.Combine(f.Root, "elsewhere") }) is null);
        RescueStore.Save(reread with { NotificationShown = true });
        TestRunner.Assert(RescueStore.List(f.Config).Single().NotificationShown);
        File.Move(record.DestinationPath, record.OriginalPath);
        File.Move(record.MetadataDestinationPath, record.OriginalMetadataPath);
        PrivateJsonFile.Write(record.RecordPath, record with { MoveCompleted = false });
        var plan = RescueStore.List(f.Config).Single();
        TestRunner.Equal("verified", plan.RecordStatus);
        File.Move(record.OriginalPath, record.DestinationPath);
        var finished = RescueStore.FinishMove(plan);
        TestRunner.Assert(finished.MoveCompleted && File.Exists(finished.MetadataDestinationPath));
        TestRunner.Equal("preserved sample", File.ReadAllText(finished.DestinationPath));
    }
}
