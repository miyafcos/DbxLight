using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6hTests
{
    private static void RunNames(Action<string, string, Action> test)
    {
        test("N5", "one sibling index serves every pending target in the same folder", NameIndexOnce);
        foreach (string boundary in new[] { "listed", "key", "target" })
            test("N5", "deadline at a deterministic name boundary preserves remaining pairs: " + boundary, () => NameDeadline(boundary));
        foreach (string boundary in new[] { "key", "target" })
            test("N5", "stop inside the same folder prevents child launch: " + boundary, () => NameStop(boundary));
    }

    private static void PrepareNameBatch(Unit5Fixture f, int count = 3)
    {
        f.Start();
        Directory.CreateDirectory(Path.Combine(f.MountRoot, "same"));
        for (int i = 0; i < count; i++) f.Write($"same/pending-{i:D2}.txt", "pending body " + i);
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == count,
            "Same-parent pending pairs missing.");
        f.Controller.Stop(true);
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Controller.RecoveryInfoOverride = info => info with { CaseInsensitive = true };
    }

    private static void NameIndexOnce()
    {
        using var f = new Unit5Fixture("u6h-name-index", writeBack: "5m");
        PrepareNameBatch(f, 12);
        for (int i = 0; i < 80; i++) f.Seed($"same/sibling-{i:D2}.txt", "untouched sibling");
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        var targets = new List<string>();
        f.Controller.RecoveryNameCheckpoint = (stage, path) =>
        {
            if (!path.StartsWith("same/", StringComparison.Ordinal)) return;
            if (stage == "key") keys[path] = keys.GetValueOrDefault(path) + 1;
            if (stage == "target") targets.Add(path);
        };
        f.Start();
        TestRunner.Equal(80, keys.Count);
        TestRunner.Assert(keys.Values.All(count => count == 1));
        TestRunner.Equal(12, targets.Count);
        TestRunner.Equal(12, targets.Distinct(StringComparer.Ordinal).Count());
        TestRunner.Equal(0, f.Controller.RescueList().Count);
        Unit5Fixture.Wait(() => Enumerable.Range(0, 12).All(i =>
            File.Exists(Path.Combine(f.RemoteRoot, "same", $"pending-{i:D2}.txt")) &&
            File.ReadAllText(Path.Combine(f.RemoteRoot, "same", $"pending-{i:D2}.txt")) == "pending body " + i),
            "Ordinary pending files did not upload.", 30);
        Console.WriteLine("N5 same-parent targets=12; sibling keys=80; generations per sibling=1; ordinary uploads=12.");
    }

    private static void NameDeadline(string boundary)
    {
        using var f = new Unit5Fixture("u6h-name-deadline-" + boundary, writeBack: "5m");
        PrepareNameBatch(f);
        f.Seed("same/sibling-a.txt"); f.Seed("same/sibling-b.txt");
        TimeSpan elapsed = TimeSpan.Zero;
        int targets = 0, keys = 0;
        f.Controller.PreparationElapsedTestOverride = () => elapsed;
        f.Controller.RecoveryNameCheckpoint = (stage, path) =>
        {
            bool inside = path.StartsWith("same/", StringComparison.Ordinal);
            if (stage == "target" && inside) targets++;
            if (stage == "key" && inside) keys++;
            if (stage == boundary && (boundary == "listed" ? path == "same" : inside) &&
                (boundary != "target" || targets == 2))
                elapsed = TimeSpan.FromSeconds(121);
        };
        f.Start();
        var records = f.Controller.RescueList();
        TestRunner.Equal(3, records.Count);
        foreach (var record in records)
        {
            TestRunner.Assert(record.MoveCompleted && record.CompleteData &&
                record.Reason.Contains("\u6570\u304c\u591a\u3044\u30fb\u9045\u3044", StringComparison.Ordinal));
            TestRunner.Assert(record.CloudPath is not null);
            int number = int.Parse(Path.GetFileNameWithoutExtension(record.CloudPath!).Split('-')[1]);
            TestRunner.Equal("pending body " + number, File.ReadAllText(record.DestinationPath));
            TestRunner.Assert(!File.Exists(Path.Combine(f.RemoteRoot, record.CloudPath!.Replace('/', '\\'))));
        }
        TestRunner.Equal(boundary == "listed" ? 0 : boundary == "key" ? 1 : 2, targets);
        TestRunner.Equal(boundary == "listed" ? 0 : boundary == "key" ? 1 : 2, keys);
        Console.WriteLine($"N5 deadline boundary={boundary}; elapsed=121s injected; targets={targets}; keys={keys}; preserved=3; pending uploads=0.");
    }

    private static void NameStop(string boundary)
    {
        using var f = new Unit5Fixture("u6h-name-stop-" + boundary, writeBack: "5m");
        PrepareNameBatch(f);
        f.Seed("same/sibling-a.txt"); f.Seed("same/sibling-b.txt");
        var previous = f.State.Load()!;
        int targets = 0, children = 0;
        bool accepted = false;
        f.Controller.StartupCheckpoint = (stage, _) => { if (stage == "before-record") children++; };
        f.Controller.RecoveryNameCheckpoint = (stage, path) =>
        {
            if (!path.StartsWith("same/", StringComparison.Ordinal)) return;
            if (stage == "target") targets++;
            if (stage == boundary && (boundary != "target" || targets == 2))
                accepted = f.Controller.Stop().PreparationCancellationRequested;
        };
        try { f.Controller.Start(); throw new InvalidOperationException("Cancelled comparison launched."); }
        catch (PendingUploadsException e) { TestRunner.Assert(e.Data["PreparationCancelled"] is true); }
        TestRunner.Assert(accepted);
        TestRunner.Equal(0, children);
        TestRunner.Equal(previous.RclonePid, f.State.Load()!.RclonePid);
        using var child = ProcessIdentity.Open(previous);
        TestRunner.Assert(child is null);
        TestRunner.Equal(3, PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData));
        TestRunner.Equal(0, f.Controller.RescueList().Count);
        Console.WriteLine($"N5 stop boundary={boundary}; targets={targets}; stop accepted; new children=0; pending pairs intact=3.");
    }
}
