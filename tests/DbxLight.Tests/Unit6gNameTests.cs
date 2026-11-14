using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6gTests
{
    private static void RunNames(Action<string, string, Action> test)
    {
        test("J3", "broad keys cover compatibility and special case folds without proving identity", BroadKeys);
        test("J3", "per-target selection retains malformed-list and case-sensitive safeguards", TargetRules);
        test("J3", "casefold-related unknown parent spellings each receive their own decision", ParentSpellings);
        foreach (string mode in new[] { "unrelated", "collision", "exact", "missing" })
        foreach (bool parent in new[] { false, true })
            test("J3", "pending pair name selection: " + mode + "; parent=" + parent, () => TargetPending(mode, parent));
    }

    private static RecoveryFolder NameFolder(RemoteInfo info, string parent, params (string Path, bool Dir)[] names) =>
        RecoveryRemoteProbe.DecodeFolder(parent, info, JsonSerializer.SerializeToElement(new
        { list = names.Select(n => new { n.Path, IsDir = n.Dir }) }));

    private static void BroadKeys()
    {
        foreach (var pair in new[] { ("a", "\uff21"), ("ss", "\u00df"),
            ("i", "\u0131"), ("i", "\u0130"), ("s", "\u017f"), ("\u03c3", "\u03c2"),
            ("\u304c", "\u304b\u3099") })
            TestRunner.Equal(RecoveryRemoteProbe.BroadNameKey(pair.Item1), RecoveryRemoteProbe.BroadNameKey(pair.Item2));
        var info = new RemoteInfo("local", "", 1, [], true);
        foreach (var pair in new[] { ("ss", "\u00df"), ("i", "\u0131"), ("i", "\u0130"),
            ("s", "\u017f"), ("\u03c3", "\u03c2") })
            TestRunner.Assert(!RecoveryRemoteProbe.ResolveItem(NameFolder(info, "", (pair.Item2, false)), info, pair.Item1, out _));
    }

    private static void TargetRules()
    {
        var info = new RemoteInfo("local", "", 1, [], true);
        var unrelated = NameFolder(info, "", ("\uff21\u6848", true), ("caf\u00e9", true), ("\u304b\u3099", true), ("\u8cc7\u6599", true));
        TestRunner.Assert(unrelated.Comparable);
        TestRunner.Assert(RecoveryRemoteProbe.ResolveItem(unrelated, info, "\u8cc7\u6599", out var found) && found is not null);
        var ambiguous = NameFolder(info, "", ("a.txt", false), ("\uff21.txt", false));
        TestRunner.Assert(ambiguous.Comparable && !RecoveryRemoteProbe.ResolveItem(ambiguous, info, "a.txt", out _));
        TestRunner.Assert(RecoveryRemoteProbe.ResolveItem(ambiguous, info with { CaseInsensitive = false }, "a.txt", out found) && found is not null);
        TestRunner.Assert(RecoveryRemoteProbe.ResolveItem(NameFolder(info, "", ("REPORT.txt", false)), info, "report.txt", out found) && found is not null);
        TestRunner.Assert(!RecoveryRemoteProbe.ResolveItem(NameFolder(info, "", ("\u00e9", false), ("e\u0301", false)), info, "\u00e9", out _));
        TestRunner.Assert(!NameFolder(info, "", ("same", false), ("same", false)).Comparable);
        TestRunner.Assert(!NameFolder(info, "", ("other/file", false)).Comparable);
        TestRunner.Assert(!NameFolder(info, "", ("bad\nname", false)).Comparable);
        var blocked = RecoveryRemoteProbe.ResolveFolder("folder", info, _ => NameFolder(info, "", ("folder", false)));
        TestRunner.Assert(blocked.Blocked);
    }


    private static void ParentSpellings()
    {
        using var f = new Unit5Fixture("u6g-parent-spellings", writeBack: "5m");
        string upper = "\U00010400", lower = "\U00010428";
        TestRunner.Assert(StringComparer.OrdinalIgnoreCase.Equals(upper, lower));
        f.Start(); f.Write("one.txt", "healthy first parent"); f.Write("two.txt", "preserved second parent");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Count(e => e.Dirty == true && e.CompleteData) == 2, "Two pending parent pairs missing.");
        f.Controller.Stop(true);
        var state = f.State.Load()!;
        foreach (var entry in PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Where(e => e.Dirty == true))
        {
            string leaf = Path.GetFileName(entry.DataPath), parent = leaf == "one.txt" ? upper : lower;
            string data = Path.Combine(state.CacheDataRoot!, parent, leaf), meta = Path.Combine(state.CacheMetadataRoot!, parent, leaf);
            Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
            File.Move(entry.DataPath, data); File.Move(entry.MetadataPath, meta);
        }
        TestRunner.Equal(2, Directory.GetDirectories(state.CacheDataRoot!).Count(path => Path.GetFileName(path) == upper || Path.GetFileName(path) == lower));
        Directory.CreateDirectory(Path.Combine(f.RemoteRoot, upper));
        f.Controller.RecoveryInfoOverride = info => info with { CaseInsensitive = true };
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Start();
        var record = f.Controller.RescueList().Single();
        TestRunner.Equal(lower + "/two.txt", record.CloudPath);
        TestRunner.Equal("preserved second parent", File.ReadAllText(record.DestinationPath));
        TestRunner.Assert(record.MoveCompleted);
        Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.RemoteRoot, upper, "one.txt")) &&
            File.ReadAllText(Path.Combine(f.RemoteRoot, upper, "one.txt")) == "healthy first parent", "Exact unknown parent did not upload.");
        TestRunner.Assert(!File.Exists(Path.Combine(f.RemoteRoot, lower, "two.txt")));
        Console.WriteLine("J3 each parent spelling: exact parent uploaded; the related but nonexact parent preserved.");
    }

    private static void TargetPending(string mode, bool parent)
    {
        using var f = new Unit5Fixture("u6g-target-" + mode + "-" + parent, writeBack: "5m");
        string segment = mode is "exact" or "missing" ? "\u898b\u7a4d\u66f8\uff21" : mode == "collision" ? "a\u6848" : "\u8cc7\u6599";
        string name = parent ? segment + "/\u539f\u7a3f.txt" : segment + ".txt";
        bool existing = mode == "exact";
        if (existing) f.Seed(name, "original");
        f.Start();
        if (existing) _ = File.ReadAllText(Path.Combine(f.MountRoot, name.Replace('/', '\\')));
        if (parent) Directory.CreateDirectory(Path.Combine(f.MountRoot, segment));
        f.Write(name, "pending per-target body");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Pending pair missing.");
        f.Controller.Stop(true);
        if (mode == "unrelated")
        {
            string baseDir = f.RemoteRoot;
            foreach (string sibling in new[] { "\uff21\u6848", "caf\u00e9", "\u304b\u3099" })
                Directory.CreateDirectory(Path.Combine(baseDir, sibling));
            if (parent)
                foreach (string sibling in new[] { "\uff21\u6848", "caf\u00e9", "\u304b\u3099" })
                    f.Seed(segment + "/" + sibling + ".txt", "unrelated");
        }
        if (mode == "collision")
        {
            if (parent)
            {
                Directory.Move(Path.Combine(f.RemoteRoot, segment), Path.Combine(f.RemoteRoot, "\uff21\u6848"));
            }
            else f.Seed("\uff21\u6848.txt", "colleague");
        }
        if (mode == "missing" && parent)
            Directory.Move(Path.Combine(f.RemoteRoot, segment), Path.Combine(f.RemoteRoot, "different"));
        f.Controller.RecoveryInfoOverride = info => info with { CaseInsensitive = true };
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Start();
        if (mode is "unrelated" or "exact")
        {
            TestRunner.Equal(0, f.Controller.RescueList().Count);
            Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.RemoteRoot, name.Replace('/', '\\'))) &&
                File.ReadAllText(Path.Combine(f.RemoteRoot, name.Replace('/', '\\'))) == "pending per-target body", "Healthy pending data did not upload.");
        }
        else
        {
            var rescued = f.Controller.RescueList().Single();
            TestRunner.Assert(rescued.MoveCompleted && rescued.CompleteData);
            TestRunner.Equal("pending per-target body", File.ReadAllText(rescued.DestinationPath));
        }
    }
}
