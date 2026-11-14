using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class Unit3cRecoveryTests
{
    public static void Run(TestRunner runner)
    {
        runner.Test("C3: every stop disposition is explained in Japanese", StopReasons);
        runner.Test("C3: current cache results include Japanese counts, location and failure reasons", CacheResults);
        runner.Test("C3: old English inspection records display in Japanese without changing recovery", LegacyInspection);
        runner.Test("C3: pre-3b states and machine JSON keep their existing names, values and defaults", MachineCompatibility);
        runner.Test("C3: CLI refusal, status window and settings use the same Japanese recovery explanation", SharedExplanation);
    }

    private static void Japanese(string text)
    {
        foreach (string token in new[] { "(Forced)", "(UnexpectedExit)", "(StartupFailed)", "(StopUncertain)", "(Unknown)",
            "Dirty metadata", "Verified metadata", "Unknown metadata", "Process identity", "Cache inspection", "remote:",
            "rcloneConfigPath:", "cacheDir:", "original absolute", "Original absolute", "Access is denied", "unfamiliar old record" })
            TestRunner.Assert(!text.Contains(token, StringComparison.Ordinal), "Internal English is visible: " + text);
        TestRunner.Assert(text.Contains('\u3002'), "No Japanese sentence: " + text);
    }

    private static void StopReasons()
    {
        using var f = new Unit3cTests.LockoutMount("stop-reasons");
        foreach (var item in new (StopDisposition Kind, string Words)[]
        {
            (StopDisposition.Unknown, "\u524d\u306e\u505c\u6b62\u72b6\u614b\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093"),
            (StopDisposition.Running, "\u4e0a\u3052\u7d42\u308f\u3063\u305f\u3053\u3068\u3092\u307e\u3060\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093"),
            (StopDisposition.Clean, "\u505c\u6b62\u5f8c\u306e\u78ba\u8a8d"),
            (StopDisposition.Forced, "\u5f37\u5236\u3067\u6b62\u3081\u307e\u3057\u305f"),
            (StopDisposition.UnexpectedExit, "\u4e88\u671f\u305b\u305a\u7d42\u4e86\u3057\u307e\u3057\u305f"),
            (StopDisposition.StartupFailed, "\u8d77\u52d5\u306b\u5931\u6557\u3057\u307e\u3057\u305f"),
            (StopDisposition.ProcessUnknown, "\u30d7\u30ed\u30bb\u30b9\u306e\u72b6\u614b\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093"),
            (StopDisposition.StopUncertain, "\u6b62\u3081\u305f\u3042\u3068\u306e\u78ba\u8a8d"),
            (StopDisposition.ClearedByUser, "\u56de\u5fa9\u306e\u56fa\u5b9a\u3092\u89e3\u9664\u3057\u307e\u3057\u305f")
        })
        {
            string text = RecoveryDestination.Explain(f.Clean with { RecoveryRequired = true, StopDisposition = item.Kind });
            Japanese(text);
            TestRunner.Assert(text.Contains(item.Words, StringComparison.Ordinal), item.Kind + ": " + text);
            Console.WriteLine($"C3 stop {item.Kind}: {text.Split('\n')[0]}");
        }
        string dirty = RecoveryDestination.Explain(f.Clean with { RecoveryRequired = true, StopDisposition = StopDisposition.StopUncertain,
            CacheInspection = $"Dirty metadata: 1 pending files in {f.Clean.ActiveConfig.CacheDir}." });
        TestRunner.Assert(dirty.Contains("\u307e\u3060\u4e0a\u304c\u3063\u3066\u3044\u306a\u3044\u30d5\u30a1\u30a4\u30eb\u304c\u898b\u3064\u304b\u308a\u307e\u3057\u305f", StringComparison.Ordinal));
    }

    private static void CacheResults()
    {
        using var f = new Unit3cTests.LockoutMount("cache-results");
        string cache = f.Clean.ActiveConfig.CacheDir; Directory.CreateDirectory(cache);
        CacheInspection empty = PendingCacheInspector.Inspect(cache);
        TestRunner.Assert(empty.Clean); Japanese(empty.Detail);
        TestRunner.Assert(empty.Detail.Contains("0") && empty.Detail.Contains(cache, StringComparison.Ordinal));
        string data = Path.Combine(cache, "vfs", "file"), meta = Path.Combine(cache, "vfsMeta", "file");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(data, "pending", new UTF8Encoding(false));
        JsonFiles.Write(meta, new Dictionary<string, object?> { ["ModTime"] = "2026-01-01T00:00:00Z", ["ATime"] = "2026-01-01T00:00:00Z",
            ["Size"] = 7, ["Rs"] = null, ["Fingerprint"] = "", ["Dirty"] = true });
        CacheInspection dirty = PendingCacheInspector.Inspect(cache);
        TestRunner.Assert(!dirty.Clean && dirty.PendingFiles == 1, dirty.Detail); Japanese(dirty.Detail);
        TestRunner.Assert(dirty.Detail.Contains("1") && dirty.Detail.Contains(cache, StringComparison.Ordinal));
        Console.WriteLine("C3 cache pending: " + dirty.Detail.Replace('\n', ' '));
        JsonFiles.Write(meta, new { unexpected = true });
        CacheInspection schema = PendingCacheInspector.Inspect(cache);
        TestRunner.Assert(!schema.Clean); Japanese(schema.Detail);
        TestRunner.Assert(schema.Detail.Contains("\u9805\u76ee", StringComparison.Ordinal) && schema.Detail.Contains(cache, StringComparison.Ordinal));
        File.WriteAllText(meta, "{broken", new UTF8Encoding(false));
        CacheInspection broken = PendingCacheInspector.Inspect(cache);
        TestRunner.Assert(!broken.Clean); Japanese(broken.Detail);
        TestRunner.Assert(broken.Detail.Contains("\u5f62\u5f0f", StringComparison.Ordinal) && broken.Detail.Contains(cache, StringComparison.Ordinal));
        Console.WriteLine("C3 cache unknown: " + broken.Detail.Replace('\n', ' '));
    }

    private static void LegacyInspection()
    {
        using var f = new Unit3cTests.LockoutMount("legacy-inspection");
        string cache = f.Clean.ActiveConfig.CacheDir;
        foreach (string detail in new[] { $"Dirty metadata: 1 pending files in {cache}.", "Verified metadata: 0 clean entries.",
            "Unknown metadata fields: file", "Unknown metadata time: file", "Unknown metadata values: file", "Unknown metadata ranges: file",
            "Cache data and metadata do not match.", "Cache inspection is unknown: Access is denied.",
            "Original absolute recovery destination is unknown; legacy relative paths were not inspected.", "unfamiliar old record" })
        {
            RuntimeState saved = f.Clean with { RecoveryRequired = true, StopDisposition = StopDisposition.Forced, CacheInspection = detail };
            RuntimeSnapshots.Save(f.State, saved); RuntimeState loaded = f.State.Load()!;
            Japanese(RecoveryDestination.Explain(loaded));
            TestRunner.Equal(detail, loaded.CacheInspection);
            TestRunner.Assert(loaded.RecoveryRequired && loaded.StopDisposition == StopDisposition.Forced);
            TestRunner.Assert(RecoveryDestination.Explain(loaded).Contains(cache, StringComparison.Ordinal));
        }
    }

    private static void MachineCompatibility()
    {
        using var f = new Unit3cTests.LockoutMount("machine-compat");
        foreach (StopDisposition kind in Enum.GetValues<StopDisposition>())
        {
            RuntimeSnapshots.Save(f.State, f.Clean with { StopDisposition = kind, RecoveryRequired = true });
            RuntimeState loaded = f.State.Load()!;
            TestRunner.Equal(kind, loaded.StopDisposition); TestRunner.Assert(loaded.RecoveryRequired);
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(new DbxStatus { StopDisposition = kind.ToString(), RecoveryRequired = true, ProcessUncertain = true }, JsonFiles.Options));
            TestRunner.Equal(kind.ToString(), doc.RootElement.GetProperty("stopDisposition").GetString());
            TestRunner.Assert(doc.RootElement.GetProperty("recoveryRequired").GetBoolean() && doc.RootElement.GetProperty("processUncertain").GetBoolean());
        }
        JsonObject old = JsonNode.Parse(File.ReadAllText(f.State.PathName))!.AsObject();
        foreach (string key in new[] { "stopDisposition", "recoveryRequired", "cacheInspection", "uncertainProcess", "supervisorExecutablePath" }) old.Remove(key);
        File.WriteAllText(f.State.PathName, old.ToJsonString(JsonFiles.Options), new UTF8Encoding(false));
        RuntimeState legacy = f.State.Load()!;
        TestRunner.Equal(StopDisposition.Unknown, legacy.StopDisposition); TestRunner.Assert(legacy.RecoveryRequired);
        Japanese(RecoveryDestination.Explain(legacy));
    }

    private static void SharedExplanation()
    {
        using var f = new Unit3cTests.LockoutMount("shared-explanation");
        RuntimeState pending = f.Clean with { RecoveryRequired = true, StopDisposition = StopDisposition.Forced,
            CacheInspection = $"Dirty metadata: 1 pending files in {f.Clean.ActiveConfig.CacheDir}." };
        RuntimeSnapshots.Save(f.State, pending);
        f.Config.Save(pending.ActiveConfig with { CacheDir = Path.Combine(f.Root, "other-cache") });
        var cli = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(["--config", f.Config.PathName, "--state", f.State.PathName, "start"]));
        TestRunner.Equal(3, cli.Exit); Japanese(cli.Error);
        string explanation = RecoveryDestination.Explain(pending);
        TestRunner.Assert(cli.Error.Contains(explanation, StringComparison.Ordinal));
        TraySnapshot snapshot = SnapshotReader.Read(f.Controller);
        FormTests.Sta(() =>
        {
            using var status = new StatusForm(); status.UpdateSnapshot(snapshot, null, pending.ActiveConfig);
            TestRunner.Equal(explanation.ReplaceLineEndings(Environment.NewLine), FormTests.Field<TextBox>(status, "recovery").Text);
            var options = new AppOptions(f.Config.PathName, f.State.PathName);
            using var settings = new SettingsForm(pending.ActiveConfig, new RegistrationService(RegistrationService.TestHive + "\\C3-" + Guid.NewGuid().ToString("N")),
                Environment.ProcessPath!, options, _ => Task.FromException(new PendingUploadsException(explanation)));
            ((Task)typeof(SettingsForm).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null)!).GetAwaiter().GetResult();
            string text = FormTests.Field<TextBox>(settings, "result").Text;
            Japanese(text); TestRunner.Assert(text.Contains(explanation.ReplaceLineEndings(Environment.NewLine), StringComparison.Ordinal));
        });
        Console.WriteLine("C3 shared explanation: " + explanation.Replace('\n', ' '));
    }
}
