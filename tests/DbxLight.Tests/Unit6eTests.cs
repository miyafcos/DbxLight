using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        Test("E1", "a body move interrupted before metadata resumes without losing either file", InterruptedPreservation);
        Test("E1", "invalid interrupted records preserve the remaining cache side under a fresh plan", InvalidInterruptedPreservation);
        Test("E1", "preserved records allow write toggles and connection changes while rescueDir waits for acknowledgment", PreservedLifecycle);
        Test("E1", "a user copying a preserved body back through the mount uses normal write-back", ManualRestore);
        Test("E1", "legacy upload fields are ignored rather than reactivating cloud writes", LegacyUploadFields);
        Test("E1", "preserved records do not prevent staged login and logout", PreservedLoginLogout);
        Test("E1", "no comparable fingerprint evidence preserves the complete body without uploading", NoComparisonEvidence);
        Test("E1", "incomplete cached ranges preserve the available body and metadata before launch", PartialBody);
        RunFreshness(Test);
        Test("E3", "real dropbox hash names and aliases compare equal, different and unavailable evidence", DropboxHashNames);
        RunLaunchRecovery(Test);
        RunPerConfigKeys(Test);
        RunRegisteredCleanup(Test);
        RunOutputPrivacy(Test);
        Test("E8", "large work areas grow the status window to content while retaining a fixed reachable footer", LargeStatusContent);
        Test("E8", "write confirmation and compact agent guide state preserve-only recovery and the actual freshness limit", CurrentRecoveryText);
    }

    private static void Pending(Unit5Fixture f, string name = "saved.txt", string body = "complete saved body")
    {
        f.Start(); f.Write(name, body);
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Complete cache pair missing.");
        f.Controller.Stop(true);
    }

    private static void InterruptedPreservation()
    {
        using var f = new Unit5Fixture("u6e-move-interrupt", writeBack: "5m");
        Pending(f);
        CacheEntry entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        File.WriteAllText(entry.MetadataPath, "{", new UTF8Encoding(false));
        RescueStore.MoveCheckpoint = (stage, _) => throw new IOException("Injected interruption after body move.");
        try { TestRunner.Throws<IOException>(() => f.Controller.Start()); }
        finally { RescueStore.MoveCheckpoint = null; }
        var planned = RescueStore.List(f.Config).Single();
        TestRunner.Assert(!planned.MoveCompleted && File.Exists(planned.DestinationPath) && File.Exists(entry.MetadataPath));
        f.Start();
        var done = RescueStore.List(f.Config).Single();
        TestRunner.Equal(planned.Id, done.Id);
        TestRunner.Assert(done.MoveCompleted && File.Exists(done.MetadataDestinationPath));
        TestRunner.Equal("complete saved body", File.ReadAllText(done.DestinationPath));
        TestRunner.Equal(0, Directory.GetFiles(f.RemoteRoot).Length);
    }

    private static void InvalidInterruptedPreservation()
    {
        using var f = new Unit5Fixture("u6e-move-invalid", writeBack: "5m");
        Pending(f);
        CacheEntry entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        File.WriteAllText(entry.MetadataPath, "{", new UTF8Encoding(false));
        RescueStore.MoveCheckpoint = (_, _) => throw new IOException("Injected interruption.");
        try { TestRunner.Throws<IOException>(() => f.Controller.Start()); }
        finally { RescueStore.MoveCheckpoint = null; }
        var plan = RescueStore.List(f.Config).Single();
        File.WriteAllText(plan.RecordPath, "{", new UTF8Encoding(false));
        f.Start();
        var list = f.Controller.RescueList();
        TestRunner.Equal(2, list.Count);
        TestRunner.Assert(list.Any(r => r.RecordStatus == "unknown" && r.RecordProblem is not null));
        TestRunner.Assert(list.Any(r => r.RecordStatus == "verified" && r.MoveCompleted && r.Id != plan.Id));
        TestRunner.Equal("complete saved body", File.ReadAllText(plan.DestinationPath));
        TestRunner.Assert(PendingCacheInspector.Observe(f.Config.CacheDir).Clean);
    }

    private static void PreservedLifecycle()
    {
        using var f = new Unit5Fixture("u6e-lifecycle", writeBack: "5m");
        Pending(f);
        CacheEntry entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        File.WriteAllText(entry.MetadataPath, "{", new UTF8Encoding(false));
        f.Start(); f.Controller.Stop();
        RescueRecord record = f.Controller.RescueList().Single();
        var access = new WriteAccessSettings(f.Controller);
        var config = access.SetEnabled(f.Store.Load(), false, false);
        config = access.SetEnabled(config, true, true);
        f.Controller.PrepareConnectionChange(config);
        byte[] saved = File.ReadAllBytes(f.Store.PathName);
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.UpdateConfig(c => c with { RescueDir = Path.Combine(f.Root, "new-rescue") }));
        TestRunner.Assert(saved.AsSpan().SequenceEqual(File.ReadAllBytes(f.Store.PathName)));
        f.Controller.UpdateConfig(c => c with { Remote = c.Remote + Path.DirectorySeparatorChar });
        f.Controller.UpdateConfig(c => c with { Remote = config.Remote });
        var cli = f.Cli("rescue", "list", "--json");
        TestRunner.Equal(0, cli.Exit);
        TestRunner.Assert(cli.Output.Contains("nextAction", StringComparison.Ordinal));
        f.Controller.AcknowledgeRescue(record.Id);
        TestRunner.Equal("complete saved body", File.ReadAllText(record.DestinationPath));
        f.Controller.UpdateConfig(c => c with { RescueDir = Path.Combine(f.Root, "new-rescue") });
        f.Start();
        TestRunner.Assert(f.Controller.RescueList().Any(r => r.RecordStatus == "unknown"));
        TestRunner.Assert(!f.Controller.Status().RecoveryRequired);
    }

    private static void ManualRestore()
    {
        using var f = new Unit5Fixture("u6e-manual-restore", writeBack: "5m");
        f.Seed("shared.txt", "original cloud");
        f.Start();
        _ = File.ReadAllText(Path.Combine(f.MountRoot, "shared.txt"));
        f.Write("shared.txt", "user saved complete version");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Dirty missing.");
        f.Controller.Stop(true); f.Seed("shared.txt", "colleague updated version");
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Start();
        var preserved = f.Controller.RescueList().Single();
        TestRunner.Equal("colleague updated version", File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")));
        TestRunner.Equal(1, Directory.GetFiles(f.RemoteRoot).Length);
        File.Copy(preserved.DestinationPath, Path.Combine(f.MountRoot, "shared.txt"), overwrite: true); // Explicit user action in the fixture.
        Unit5Fixture.Wait(() => File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")) == "user saved complete version", "Ordinary manual restore did not upload.");
        TestRunner.Equal("user saved complete version", File.ReadAllText(preserved.DestinationPath));
    }

    private static void NoComparisonEvidence()
    {
        using var f = new Unit5Fixture("u6e-no-evidence", writeBack: "5m");
        f.Seed("saved.txt", "original");
        f.Start(); _ = File.ReadAllText(Path.Combine(f.MountRoot, "saved.txt"));
        f.Write("saved.txt", "retained complete body");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData && !string.IsNullOrEmpty(e.Fingerprint)), "Fingerprint missing.");
        f.Controller.Stop(true);
        var entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        string raw = File.ReadAllText(entry.MetadataPath);
        File.WriteAllText(entry.MetadataPath, raw.Replace(entry.Fingerprint!, "-1", StringComparison.Ordinal), new UTF8Encoding(false));
        f.Start();
        var record = f.Controller.RescueList().Single();
        TestRunner.Assert(!record.Conflict && record.MoveCompleted);
        TestRunner.Equal("retained complete body", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal("original", File.ReadAllText(Path.Combine(f.RemoteRoot, "saved.txt")));
        TestRunner.Equal(1, Directory.GetFiles(f.RemoteRoot).Length);
    }

    private static void PartialBody()
    {
        using var f = new Unit5Fixture("u6e-partial", writeBack: "5m");
        Pending(f);
        var entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        string raw = File.ReadAllText(entry.MetadataPath);
        string changed = System.Text.RegularExpressions.Regex.Replace(raw, "\\\"Rs\\\"\\s*:\\s*\\[[^\\]]*\\]", "\\\"Rs\\\":[]");
        TestRunner.Assert(changed != raw);
        File.WriteAllText(entry.MetadataPath, changed, new UTF8Encoding(false));
        f.Start();
        var record = f.Controller.RescueList().Single();
        TestRunner.Assert(!record.CompleteData && record.MoveCompleted);
        TestRunner.Equal("complete saved body", File.ReadAllText(record.DestinationPath));
        TestRunner.Assert(File.Exists(record.MetadataDestinationPath));
        TestRunner.Equal(0, Directory.GetFiles(f.RemoteRoot).Length);
    }

    private static void PreservedLoginLogout()
    {
        using var f = new LoginFixture();
        f.Seed("dbxlight:", f.Conf);
        var c = f.Controller.Config.Load();
        string body = Path.Combine(c.CacheDir, "vfs", "sample", "saved.txt");
        string meta = Path.Combine(c.CacheDir, "vfsMeta", "sample", "saved.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(body)!);
        File.WriteAllText(body, "retained after logout", new UTF8Encoding(false));
        var record = RescueStore.Rescue(c, new("sample/saved.txt", body, meta, true, false, false, null, null, null, null, false, "sample"),
            "saved.txt", "sample");
        using (var session = f.AtLogin())
            TestRunner.Assert(session.AuthorizeAsync(false).GetAwaiter().GetResult().Succeeded);
        new AccountSettings(f.Controller, f.Conf).LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Equal("", f.Controller.Config.Load().Remote);
        TestRunner.Equal("retained after logout", File.ReadAllText(record.DestinationPath));
    }

    private static void LegacyUploadFields()
    {
        RuntimeState? state = JsonSerializer.Deserialize<RuntimeState>("{\"recoveryReuploads\":[{\"contentHash\":\"ignored\"}],\"recoveryComparisons\":[]}", JsonFiles.Options);
        RescueRecord? record = JsonSerializer.Deserialize<RescueRecord>("{\"uploaded\":true,\"uploadStage\":\"Copied\",\"copyStarted\":true,\"reuploadOriginal\":true}", JsonFiles.Options);
        TestRunner.Assert(state is not null && record is not null);
        string json = JsonSerializer.Serialize(state, JsonFiles.Options) + JsonSerializer.Serialize(record, JsonFiles.Options);
        TestRunner.Assert(!json.Contains("recoveryReuploads") && !json.Contains("uploadStage") && !json.Contains("copyStarted"));
    }
}
