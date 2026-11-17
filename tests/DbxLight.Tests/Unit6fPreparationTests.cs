using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    private static void RunPreparation(Action<string, string, Action> test)
    {
        test("G8", "the global comparison time bound preserves remaining folders without querying them", () => ComparisonBudget(false));
        test("G8", "the folder count bound preserves remaining folders", () => ComparisonBudget(true));
        test("G8", "status and UI return progress near two seconds; stop cancels at a pair boundary and restart continues", () => PreparationControl(false));
        test("G8", "resident run accepts preparation cancellation and exits without launching a child", () => PreparationControl(true));
        test("G8", "many acknowledged records are skipped; newest fifty, --max and --all retain every original", BoundedRescueHistory);
        test("G8", "omitted rescueDir defaults to a private sibling of the cache on the same volume", DefaultRescue);
        test("G8", "an explicitly configured other-volume rescue location is retained and moves a complete pair", ExplicitRescue);
    }
    private static void PendingBatch(Unit5Fixture f, params string[] names)
    {
        foreach (string name in names) f.Seed(name, "original " + name);
        f.Start();
        foreach (string name in names)
        {
            _ = File.ReadAllText(Path.Combine(f.MountRoot, name.Replace('/', '\\')));
            f.Write(name, "saved " + name);
        }
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == names.Length, "Pending batch missing.");
        f.Controller.Stop(true);
        f.Store.Save(f.Store.Load() with { TestOverrides = new() { WriteBack = "1s" } });
    }
    private static void ComparisonBudget(bool folderLimit)
    {
        using var f = new Unit5Fixture("u6f-budget-" + folderLimit, writeBack: "5m");
        PendingBatch(f, "a/one.txt", "z/two.txt");
        TestTimeline.Mark(f.Root, "budget-start", new { folderLimit });
        f.Controller.RecoveryInfoOverride = info => { TestTimeline.Mark(f.Root, "budget-info"); return info; };
        using var peer = new ControlledComparison(f);
        TimeSpan logicalElapsed = TimeSpan.Zero;
        f.Controller.PreparationElapsedTestOverride = () => logicalElapsed;
        int listed = 0;
        if (folderLimit) f.Controller.PreparationFolderTestLimit = 2; // Root plus a; z would be the third distinct folder.
        f.Controller.RecoveryComparisonCheckpoint = (parent, _) =>
        {
            listed++; TestTimeline.Mark(f.Root, "budget-group", new { parent, listed });
            TestRunner.Equal("a", parent);
            if (!folderLimit) { logicalElapsed = StartupPreparation.MaximumComparisonTime; TestTimeline.Mark(f.Root, "budget-delay-end", new { logicalElapsed }); }
        };
        f.Start();
        TestTimeline.Mark(f.Root, "budget-end", new { listed, rescued = f.Controller.RescueList().Count });
        TestRunner.Equal(1, listed);
        var rescued = f.Controller.RescueList().Single();
        TestRunner.Equal("z/two.txt", rescued.CloudPath);
        TestRunner.Assert(rescued.Reason.Contains("数が多い・遅い", StringComparison.Ordinal));
        TestRunner.Equal("saved z/two.txt", File.ReadAllText(rescued.DestinationPath));
        TestRunner.Equal("original z/two.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "z", "two.txt")));
        TestRunner.Equal("saved a/one.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "a", "one.txt")));
        TestRunner.Equal(TimeSpan.FromSeconds(120), StartupPreparation.MaximumComparisonTime);
        TestRunner.Equal(200, StartupPreparation.MaximumComparisonFolders);
    }
    private static void PreparationControl(bool run)
    {
        foreach (string reader in new[] { "core", "cli", "snapshot" }) PreparationControl(run, reader);
    }

    private static void PreparationControl(bool run, string reader)
    {
        using var f = new Unit5Fixture("u6f-preparation", writeBack: "5m");
        PendingBatch(f, "one.txt", "two.txt");
        foreach (var entry in PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Where(e => e.Dirty == true))
            File.WriteAllText(entry.MetadataPath, "{", new UTF8Encoding(false));
        using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int guardExpired = 0;
        using var guard = new System.Threading.Timer(_ => { Interlocked.Exchange(ref guardExpired, 1); release.Set(); }, null, 30000, Timeout.Infinite);
        int children = 0;
        f.Controller.StartupCheckpoint = (stage, _) => { if (stage == "before-record") Interlocked.Increment(ref children); };
        RescueStore.MoveCheckpoint = (stage, _) =>
        {
            if (stage == "after-data-move" && !reached.IsSet) { TestTimeline.Mark(f.Root, "move-held"); reached.Set(); release.Wait(); bool released = true; TestTimeline.Mark(f.Root, "move-release", new { released }); if (!released) throw new TimeoutException("Test release missing."); }
        };
        Task<Exception?> operation = Task.Run(() => { try { if (run) TestRunner.Equal(0, f.Controller.Run(CancellationToken.None)); else f.Controller.Start(); return null; } catch (Exception e) { return e; } });
        try
        {
            TestRunner.Assert(reached.Wait(15000), "Move boundary missing.");
            var timer = Stopwatch.StartNew();
            if (reader == "core")
            {
                var status = f.Controller.Status();
                TestRunner.Assert(status.Preparation is { Stage: "退避" });
            }
            else if (reader == "cli")
            {
                var reply = f.Cli("status", "--json"); using var json = JsonDocument.Parse(reply.Output);
                TestRunner.Equal("preparing", json.RootElement.GetProperty("code").GetString());
                TestRunner.Assert(!json.RootElement.GetProperty("rescueCountsKnown").GetBoolean());
            }
            else
            {
                var snapshot = SnapshotReader.Read(f.Controller);
                TestRunner.Assert(snapshot.Status.Preparation is not null && snapshot.Rescued!.Count == 0);
                TestRunner.Assert(TrayPresentation.QueueAgeExplanation(snapshot, null).Contains("確認できません", StringComparison.Ordinal));
            }
            double statusSeconds = timer.Elapsed.TotalSeconds;
            TestTimeline.Mark(f.Root, "status-return", new { reader, statusSeconds, released = release.IsSet });
            TestRunner.Assert(statusSeconds is >= 1.8 and < 10 && !release.IsSet && !operation.IsCompleted && Volatile.Read(ref guardExpired) == 0, "Status must return while the pair move is still blocked.");
            timer.Restart(); var stopped = f.Controller.Stop(); double stopSeconds = timer.Elapsed.TotalSeconds;
            TestRunner.Assert(stopped.PreparationCancellationRequested && stopSeconds < 10 && !release.IsSet && !operation.IsCompleted && Volatile.Read(ref guardExpired) == 0);
            Console.WriteLine($"G8 reader={reader}; status={statusSeconds:F3}s; stop={stopSeconds:F3}s; release follows status and accepted cancellation; watchdog=30s.");
        }
        finally { release.Set(); RescueStore.MoveCheckpoint = null; }
        Exception? error = operation.GetAwaiter().GetResult();
        if (run) TestRunner.Assert(error is null);
        else TestRunner.Assert(error is PendingUploadsException && error.Data["PreparationCancelled"] is true);
        TestRunner.Equal(0, children);
        TestRunner.Assert(!f.Controller.Status().Running);
        var completed = f.Controller.RescueList().Where(r => r.MoveCompleted).ToArray(); TestRunner.Equal(1, completed.Length);
        TestRunner.Equal(1, PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Count(e => !e.MetadataValid));
        f.Start();
        TestRunner.Equal(2, f.Controller.RescueList().Count);
        TestRunner.Assert(f.Controller.RescueList().All(r => r.MoveCompleted));
    }
    private static RescueRecord SyntheticRescue(Unit5Fixture f, string name)
    {
        string data = Path.Combine(f.Config.CacheDir, "vfs", "sample", name), meta = Path.Combine(f.Config.CacheDir, "vfsMeta", "sample", name);
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(data, "preserved " + name); File.WriteAllText(meta, "{}");
        return RescueStore.Rescue(f.Config, new CacheEntry("sample/" + name, data, meta, true, true, false, null, null, null, null, false, "sample"), name, "sample");
    }
    private static void BoundedRescueHistory()
    {
        using var f = new Unit5Fixture("u6f-history");
        var acknowledged = new List<RescueRecord>();
        for (int i = 0; i < 200; i++)
        {
            var record = SyntheticRescue(f, "ack-" + i + ".txt");
            RescueStore.Save(record with { Acknowledged = true }); acknowledged.Add(record);
        }
        for (int i = 0; i < 60; i++) SyntheticRescue(f, "pending-" + i + ".txt");
        int reads = 0; RescueStore.RecordReadCheckpoint = _ => reads++;
        try
        {
            var timer = Stopwatch.StartNew(); var status = f.Controller.Status();
            TestRunner.Equal(60, status.RescueNotice.Unacknowledged);
            TestRunner.Equal(60, reads);
            Console.WriteLine($"G8 acknowledged=200; pending=60; recordReads={reads}; status={timer.Elapsed.TotalSeconds:F3}s");
        }
        finally { RescueStore.RecordReadCheckpoint = null; }
        var page = f.Controller.RescueList(); TestRunner.Equal(50, page.Count);
        TestRunner.Assert(page[0].RescuedUtc >= page[^1].RescuedUtc);
        foreach (var request in new[] { (Args: new[] { "rescue", "list", "--json" }, Count: 50), (Args: new[] { "rescue", "list", "--max", "7", "--json" }, Count: 7), (Args: new[] { "rescue", "list", "--all", "--json" }, Count: 60) })
        {
            var reply = f.Cli(request.Args); TestRunner.Equal(0, reply.Exit); using var json = JsonDocument.Parse(reply.Output);
            TestRunner.Equal(request.Count, json.RootElement.GetProperty("result").GetProperty("items").GetArrayLength());
        }
        foreach (var record in acknowledged) TestRunner.Assert(File.Exists(record.RecordPath) && File.Exists(record.DestinationPath));
        File.WriteAllText(acknowledged[0].RecordPath, "{", new UTF8Encoding(false));
        TestRunner.Equal(1, f.Controller.Status().RescueNotice.UnknownRecords);
    }
    private static void DefaultRescue()
    {
        using var f = new Unit5Fixture("u6f-default-rescue");
        string? previous = Environment.GetEnvironmentVariable("DBXL_RESCUE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("DBXL_RESCUE_DIR", null);
            var json = JsonSerializer.SerializeToNode(f.Config, JsonFiles.Options)!.AsObject(); json.Remove("rescueDir");
            var config = json.Deserialize<DbxConfig>(JsonFiles.Options)!;
            TestRunner.Equal(Path.Combine(f.Root, "cache-rescue"), config.RescueDir);
            TestRunner.Equal(Path.GetPathRoot(config.CacheDir), Path.GetPathRoot(config.RescueDir));
            TestRunner.Assert(!MountPaths.IsSameOrBelow(config.RescueDir, config.CacheDir));
            f.Store.Save(config); f.Start(); f.Controller.Stop();
        }
        finally { Environment.SetEnvironmentVariable("DBXL_RESCUE_DIR", previous); }
    }
    private static void ExplicitRescue()
    {
        using var f = new Unit5Fixture("u6f-explicit-rescue", writeBack: "5m");
        NoticePending(f);
        string target = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "run-u6f-rescue-" + Guid.NewGuid().ToString("N"));
        f.Store.Save(f.Store.Load() with { RescueDir = target });
        f.Start(); var record = f.Controller.RescueList().Single();
        TestRunner.Assert(record.MoveCompleted && MountPaths.IsSameOrBelow(record.DestinationPath, target));
        TestRunner.Equal("preserved notice body", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal(target, f.Store.Load().RescueDir);
    }
    private static void LegacyMutationAuthority()
    {
        using var f = new LoginFixture();
        string legacy = CredentialSwitch.SharedLegacyPath(f.Controller.Config.PathName);
        string pending = legacy + ".pending-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, new string('k', 19)); File.WriteAllText(pending, new string('p', 23));
        var before = Stamp(legacy); var pendingBefore = Stamp(pending);
        f.Seed("sample-existing:", legacy);
        PrivateJsonFile.Write(CredentialSwitch.RegistryPath(f.Controller.Config.PathName),
            new KeyRegistry { ConfigPath = f.Controller.Config.PathName, Keys = [legacy], RegisteredPaths = [pending] });
        _ = f.Controller.Config.Load();
        TestRunner.Equal(before, Stamp(legacy)); TestRunner.Equal(pendingBefore, Stamp(pending));
        TestRunner.Assert(CredentialSwitch.UnregisteredFiles(f.Controller.Config.PathName).Contains(pending));
        var tx = new KeyTransaction { ConfigPath = f.Controller.Config.PathName, KeyPath = legacy,
            WorkingPath = legacy + ".work-" + Guid.NewGuid().ToString("N"), BackupPath = pending, Before = f.Controller.Config.Load() };
        TestRunner.Throws<PendingUploadsException>(() => CredentialSwitch.RecoverHeld(f.Controller.Config.PathName, tx));
        TestRunner.Equal(before, Stamp(legacy)); TestRunner.Equal(pendingBefore, Stamp(pending));
    }

    private static void ModernLegacyPending(bool changed)
    {
        using var f = new Unit5Fixture("u6f-modern-legacy-" + changed, writeBack: "5m");
        string legacy = CredentialSwitch.SharedLegacyPath(f.Store.PathName);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!); File.WriteAllText(legacy, "");
        var stamp = Stamp(legacy);
        f.Store.Save(f.Config with { RcloneConfigPath = legacy });
        PendingBatch(f, "shared.txt");
        if (changed) f.Seed("shared.txt", "colleague cloud changed");
        f.Start();
        TestRunner.Equal(stamp, Stamp(legacy));
        if (changed)
        {
            TestRunner.Equal("colleague cloud changed", File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")));
            TestRunner.Equal("saved shared.txt", File.ReadAllText(f.Controller.RescueList().Single().DestinationPath));
        }
        else
        {
            TestRunner.Equal(0, f.Controller.RescueList().Count);
            TestRunner.Equal("saved shared.txt", File.ReadAllText(Path.Combine(f.RemoteRoot, "shared.txt")));
        }
    }
}
