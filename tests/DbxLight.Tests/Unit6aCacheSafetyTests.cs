using System.Text;
using System.Security.AccessControl;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void DamagedCache()
    {
        foreach (string damage in new[] { "missing", "empty", "truncated" })
        {
            using var f = new Unit5Fixture("u6a-" + damage, writeBack: "5m");
            if (damage == "missing") f.Store.Save(f.Config with { RescueDir = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "u6a-cross-volume-" + Guid.NewGuid().ToString("N")) });
            f.Start();
            string content = "preserve pending " + damage;
            f.Write("power-cut.txt", content);
            Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).PendingFiles == 1, "No pending cache.");
            RuntimeState before = f.State.Load()!;
            f.Controller.Stop(true);
            string data = Path.Combine(before.CacheDataRoot!, "power-cut.txt"), meta = Path.Combine(before.CacheMetadataRoot!, "power-cut.txt");
            TestRunner.Equal(content, File.ReadAllText(data));
            File.Copy(meta, Path.Combine(f.Root, "original.metadata"));
            if (damage == "missing") File.Move(meta, Path.Combine(f.Root, "removed.metadata"));
            else File.WriteAllText(meta, damage == "empty" ? "" : "{\"ModTime\":", new UTF8Encoding(false));
            f.Start(awaitRecovery: false);
            var records = RescueStore.List(f.State.Load()!);
            TestRunner.Equal(1, records.Count);
            TestRunner.Equal(content, File.ReadAllText(records[0].DestinationPath));
            TestRunner.Assert(records[0].MoveCompleted && !records[0].Conflict);
            TestRunner.Assert(!File.Exists(Path.Combine(f.RemoteRoot, "power-cut.txt")));
            for (int i = 0; i < 3; i++) TestRunner.Assert(!f.Controller.Status().RecoveryRequired, "Preserved items must not pin recovery.");
            TestRunner.Assert(new FileInfo(records[0].DestinationPath).GetAccessControl().AreAccessRulesProtected);
            Console.WriteLine($"P1 {damage}: body rescued intact; automatic upload=false; recovery remains=false; record={records[0].RecordPath}");
        }
    }

    private static void RescueConfiguration()
    {
        TestRunner.Throws<ArgumentException>(() => new DbxConfig { RescueDir = "relative" }.Validate());
        TestRunner.Throws<ArgumentException>(() => new DbxConfig { RescueDir = @"T:\rescue" }.Validate());
        using var f = new Unit5Fixture("u6a-rescue-setting");
        string destination = Path.Combine(f.Root, "custom-rescue");
        TestRunner.Equal(destination, ConfigStore.Set(f.Config, "rescueDir", destination).RescueDir);
    }
}
