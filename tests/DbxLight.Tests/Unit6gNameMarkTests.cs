using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6gTests
{
    private static void RunNameMarks(Action<string, string, Action> test)
    {
        test("J3b", "a dakuten sibling parent does not block a healthy pending child", () => MarkSibling(true, false, false));
        test("J3b", "a dakuten sibling leaf is unrelated to the pending target", () => MarkSibling(false, false, false));
        test("J3b", "an NFD sibling of a known-rule NFC target remains ambiguous", () => MarkSibling(false, true, false));
        test("J3b", "cafe and an accented cafe sibling remain unrelated", () => MarkSibling(false, false, true));
    }

    private static void MarkSibling(bool parent, bool decomposed, bool accented)
    {
        using var f = new Unit5Fixture("u6g-name-marks-" + parent + "-" + decomposed + "-" + accented, writeBack: "5m");
        string target = accented ? "cafe" : decomposed ? "\u304c\u884c" : "\u304b\u884c";
        string sibling = accented ? "caf\u00e9" : decomposed ? "\u304b\u3099\u884c" : "\u304c\u884c";
        if (decomposed)
        {
            TestRunner.Assert(RecoveryRemoteProbe.NameRuleKnown(new("local", "", 1, [], true), target));
            TestRunner.Equal(RecoveryRemoteProbe.BroadNameKey(target), RecoveryRemoteProbe.BroadNameKey(sibling));
        }
        else TestRunner.Assert(RecoveryRemoteProbe.BroadNameKey(target) != RecoveryRemoteProbe.BroadNameKey(sibling));
        string name = parent ? target + "/body.txt" : target + ".txt";
        f.Start();
        if (parent) Directory.CreateDirectory(Path.Combine(f.MountRoot, target));
        f.Write(name, "pending mark distinction");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Pending mark pair missing.");
        f.Controller.Stop(true);
        if (parent) Directory.CreateDirectory(Path.Combine(f.RemoteRoot, sibling));
        else f.Seed(sibling + ".txt", "unrelated sibling");
        f.Controller.RecoveryInfoOverride = info => info with { CaseInsensitive = true };
        f.Store.Save(f.Config with { TestOverrides = new() { WriteBack = "1s" } });
        f.Start();
        if (decomposed)
        {
            var record = f.Controller.RescueList().Single();
            TestRunner.Assert(record.MoveCompleted && record.CompleteData);
            TestRunner.Equal("pending mark distinction", File.ReadAllText(record.DestinationPath));
        }
        else
        {
            TestRunner.Equal(0, f.Controller.RescueList().Count);
            Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.RemoteRoot, name.Replace('/', '\\'))) &&
                File.ReadAllText(Path.Combine(f.RemoteRoot, name.Replace('/', '\\'))) == "pending mark distinction", "Distinct sibling prevented normal upload.");
        }
        if (!parent) TestRunner.Equal("unrelated sibling", File.ReadAllText(Path.Combine(f.RemoteRoot, sibling + ".txt")));
    }
}
