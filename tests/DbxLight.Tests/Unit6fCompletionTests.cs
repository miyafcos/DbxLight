using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    private static void RunCompletion(Action<string, string, Action> test)
    {
        test("G4", "both moves complete before parent interruption; restart repairs the completion marker", () => CompletionMarker(false));
        test("G4", "completion-marker save failure reconciles without another body move", () => CompletionMarker(true));
        test("G4", "missing rescue body is reported specifically without moving an original", MissingRescueBody);
    }
    private static void CompletionMarker(bool saveFailure)
    {
        using var f = new Unit5Fixture("u6f-complete", writeBack: "5m");
        f.Start(); f.Write("saved.txt", "complete marker body");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Dirty pair missing.");
        f.Controller.Stop(true);
        var entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        File.WriteAllText(entry.MetadataPath, "{", new UTF8Encoding(false));
        if (saveFailure) RescueStore.CompletionSaveCheckpoint = () => throw new IOException("Injected completion write failure.");
        else RescueStore.MoveCheckpoint = (stage, _) => { if (stage == "after-metadata-move") throw new IOException("Injected parent interruption."); };
        try { TestRunner.Throws<IOException>(() => f.Controller.Start()); }
        finally { RescueStore.CompletionSaveCheckpoint = null; RescueStore.MoveCheckpoint = null; }
        var planned = f.Controller.RescueList().Single();
        TestRunner.Assert(!planned.MoveCompleted && !File.Exists(entry.DataPath) && !File.Exists(entry.MetadataPath));
        TestRunner.Assert(File.Exists(planned.DestinationPath) && File.Exists(planned.MetadataDestinationPath));
        var stamp = Stamp(planned.DestinationPath);
        f.Start();
        var done = f.Controller.RescueList().Single();
        TestRunner.Assert(done.Id == planned.Id && done.MoveCompleted);
        TestRunner.Equal(stamp, Stamp(done.DestinationPath));
        TestRunner.Equal("complete marker body", File.ReadAllText(done.DestinationPath));
        f.Controller.Stop(); f.Controller.AcknowledgeRescue(done.Id);
        f.Controller.UpdateConfig(c => c with { RescueDir = Path.Combine(f.Root, "next-rescue") });
    }
    private static void MissingRescueBody()
    {
        using var f = new Unit5Fixture("u6f-missing-body");
        string data = Path.Combine(f.Config.CacheDir, "vfs", "sample", "saved.txt");
        string meta = Path.Combine(f.Config.CacheDir, "vfsMeta", "sample", "saved.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(data, "preserved body"); File.WriteAllText(meta, "{}");
        var entry = new CacheEntry("sample/saved.txt", data, meta, true, true, false, null, null, null, null, false, "sample");
        var record = RescueStore.Rescue(f.Config, entry, "saved.txt", "sample");
        PrivateJsonFile.Write(record.RecordPath, record with { MoveCompleted = false });
        File.Move(record.DestinationPath, record.DestinationPath + ".retained");
        var unknown = f.Controller.RescueList().Single();
        TestRunner.Assert(unknown.RecordStatus == "unknown" && unknown.RecordProblem!.Contains("ファイル本体", StringComparison.Ordinal) &&
            unknown.RecordProblem.Contains("元にも退避先にもありません", StringComparison.Ordinal));
        TestRunner.Assert(File.Exists(record.DestinationPath + ".retained"));
    }
}
