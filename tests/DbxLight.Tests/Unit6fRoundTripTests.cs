using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    private static void RunRoundTrips(Action<string, string, Action> test)
    {
        test("G3", "Windows OS suffix encoding round-trips every component and preserves measured ordinary symbols", RoundTripNames);
        foreach (string suffix in new[] { "\uff0e", "\u201b\uff0e", "\u2420", "\u201b\u2420", "\uff3c" })
        foreach (bool parent in new[] { false, true })
        {
            string captured = suffix; bool inParent = parent;
            test("G3", "unreloadable suffix is preserved: " + string.Join("-", suffix.Select(c => ((int)c).ToString("X4"))) + "; parent=" + parent,
                () => SuffixRescue(captured, inParent));
        }
    }
    private static void RoundTripNames()
    {
        foreach (string cloud in new[] { "ordinary.txt", "dot\uff0e.txt", "space\u2420.txt", "quoted\u201b.txt",
            "wide\uff1f.txt", "dir\uff1a/child\uff1f.txt", "dir\u201b/file.txt", "question?.txt" })
        {
            string encoded = RecoveryCacheNames.EncodeForDisk(cloud);
            if (RecoveryCacheNames.PreservationReason(encoded) is not null)
            { TestRunner.Assert(!RecoveryCacheNames.TryDecode(encoded, out _)); continue; }
            TestRunner.Assert(RecoveryCacheNames.TryDecode(encoded, out string decoded), "Cannot decode measured name: " + cloud);
            TestRunner.Equal(cloud, decoded);
            TestRunner.Equal(encoded, RecoveryCacheNames.EncodeForDisk(decoded));
        }
        foreach (string suffix in new[] { "\uff0e", "\u2420" })
        foreach (string name in new[] { "tail" + suffix, "parent" + suffix + "/file.txt" })
            TestRunner.Assert(!RecoveryCacheNames.TryDecode(name, out _));
        TestRunner.Equal("tail\uff0e", RecoveryCacheNames.EncodeForDisk("tail."));
        TestRunner.Equal("tail\u201b\uff0e", RecoveryCacheNames.EncodeForDisk("tail\uff0e"));
        TestRunner.Equal("tail\u2420", RecoveryCacheNames.EncodeForDisk("tail "));
        TestRunner.Equal("tail\u201b\u2420", RecoveryCacheNames.EncodeForDisk("tail\u2420"));
    }
    private static void SuffixRescue(string suffix, bool parent)
    {
        using var f = new Unit5Fixture("u6f-suffix", writeBack: "5m");
        f.Start(); f.Write("template.txt", "complete suffix body");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Pending template missing.");
        f.Controller.Stop(true);
        var state = f.State.Load()!;
        var entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        string relative = parent ? "parent" + suffix + "/file.txt" : "tail" + suffix;
        string data = Path.Combine(state.CacheDataRoot!, relative.Replace('/', '\\'));
        string meta = Path.Combine(state.CacheMetadataRoot!, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.Move(entry.DataPath, data); File.Move(entry.MetadataPath, meta);
        f.Start();
        var record = f.Controller.RescueList().Single();
        TestRunner.Assert(record.MoveCompleted);
        TestRunner.Equal("complete suffix body", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal(0, Directory.GetFiles(f.RemoteRoot, "*", SearchOption.AllDirectories).Length);
        TestRunner.Assert(PendingCacheInspector.Observe(f.Config.CacheDir).Clean);
    }
}
