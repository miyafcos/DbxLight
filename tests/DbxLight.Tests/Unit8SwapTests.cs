using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit8Tests
{
    private static void Swap(string scenario)
    {
        string root = Root(), target = Install(root); var before = Snapshot(target);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var incoming = UpdatePackage.Stage(Package("0.1.1"), store);
        int launches = 0;
        var swap = new UpdateSwap(store) { RelaunchOverride = (_, args) => { launches++; TestRunner.Equal("--fixture", args.Single()); } };
        if (scenario is not ("success" or "locked"))
            swap.Checkpoint = point =>
            {
                if (point == (scenario == "rollback" ? "backed-up" : scenario) || scenario == "rollback" && point == "rollback")
                    throw new IOException("injected " + point);
            };
        using var locked = scenario == "locked" ? new FileStream(Path.Combine(target, "payload.txt"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var result = swap.Run(target, incoming.Staging, 0, true, ["--fixture"]); locked?.Dispose();
        TestRunner.Equal(scenario == "success", result.Success);
        if (scenario == "success")
        {
            TestRunner.Equal("0.1.1", UpdatePackage.ValidateDirectory(target, false).Version);
            TestRunner.Equal("0.1.0", UpdatePackage.ValidateDirectory(result.BackupPath!).Version);
            TestRunner.Equal("keep", File.ReadAllText(Path.Combine(target, "unmanaged.txt")));
            TestRunner.Equal("untouched", File.ReadAllText(Path.Combine(target, "unmanaged", "keep.txt"))); TestRunner.Equal(1, launches);
        }
        else if (scenario == "rollback")
        {
            TestRunner.Assert(!result.RolledBack && result.BackupPath is not null && Directory.Exists(result.BackupPath));
            TestRunner.Assert(result.Reason!.Contains("Rollback failed", StringComparison.Ordinal)); TestRunner.Equal(0, launches);
        }
        else
        {
            Same(before, target); TestRunner.Equal(scenario != "locked", result.RolledBack); TestRunner.Equal(1, launches);
        }
        TestRunner.Equal(result, JsonFiles.Read<UpdateResult>(store.ResultPath));
    }
    private static void WaitDeadline()
    {
        string root = Root(), target = Install(root); var before = Snapshot(target);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var incoming = UpdatePackage.Stage(Package("0.1.1"), store);
        string signal = @"Local\DbxLight.Unit8." + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, signal);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--unit8-child"); info.ArgumentList.Add(signal); using var child = Process.Start(info)!;
        try
        {
            TestRunner.Equal("ready", child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            var swap = new UpdateSwap(store) { WaitLimit = TimeSpan.FromMilliseconds(20) };
            var result = swap.Run(target, incoming.Staging, child.Id, false, []);
            TestRunner.Assert(!result.Success && result.Reason!.Contains("wait limit", StringComparison.Ordinal));
            TestRunner.Equal("wait", result.Stage); TestRunner.Assert(!child.HasExited); Same(before, target);
            TestRunner.Equal(TimeSpan.FromSeconds(120), new UpdateSwap(store).WaitLimit);
        }
        finally { release.Set(); TestRunner.Assert(child.WaitForExit(10000)); }
    }
    private static void Cleanup()
    {
        string root = Root(), target = Install(root); var store = new UpdateStore(Path.Combine(root, "state.json"));
        string[] stages = Enumerable.Range(0, 3).Select(_ => UpdatePackage.Stage(Package("0.1.1"), store).Staging).ToArray();
        foreach (string stage in stages) store.CompleteStage(stage);
        using var lease = new FileStream(stages[0] + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string unknown = Path.Combine(store.Root, "staging-human"); Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "keep"), "keep");
        string[] backups = Enumerable.Range(1, 4).Select(n => Path.Combine(target, "backup", "0.1.0-2026010" + n + "T0102030000000Z")).ToArray();
        foreach (string backup in backups)
        {
            Directory.CreateDirectory(backup);
            foreach (string path in Directory.EnumerateFiles(target).Where(p => Path.GetFileName(p) != "unmanaged.txt")) File.Copy(path, Path.Combine(backup, Path.GetFileName(path)));
        }
        string other = Path.Combine(target, "backup", "manual"); Directory.CreateDirectory(other); UpdateCleanup.Run(store, target);
        TestRunner.Assert(Directory.Exists(stages[0]) && !Directory.Exists(stages[1]) && !Directory.Exists(stages[2]) && Directory.Exists(unknown));
        TestRunner.Assert(!Directory.Exists(backups[0]) && !Directory.Exists(backups[1]) && Directory.Exists(backups[2]) && Directory.Exists(backups[3]) && Directory.Exists(other));
    }
    private static void Configuration()
    {
        string root = Root(), path = Path.Combine(root, "config.json");
        var config = new DbxConfig { Remote = "fixture:", CacheDir = Path.Combine(root, "cache"), RescueDir = Path.Combine(root, "rescue"), LogDir = Path.Combine(root, "logs") };
        var original = JsonSerializer.SerializeToNode(config, JsonFiles.Options)!.AsObject(); original.Remove("updateCheck");
        File.WriteAllText(path, original.ToJsonString(), new UTF8Encoding(false));
        var store = new ConfigStore(path); var loaded = store.Load(); TestRunner.Assert(loaded.UpdateCheck);
        store.Save(ConfigStore.Set(loaded, "updateCheck", "false"));
        var saved = JsonSerializer.SerializeToNode(store.Load(), JsonFiles.Options)!.AsObject();
        TestRunner.Assert(!saved["updateCheck"]!.GetValue<bool>()); saved.Remove("updateCheck"); TestRunner.Equal(original.ToJsonString(), saved.ToJsonString());
    }
}
