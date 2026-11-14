using System.Text.Json.Nodes;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6dTests
{
    private static void SharedRescueOwners()
    {
        using var a = new Unit5Fixture("u6d-S2-A");
        using var b = new Unit5Fixture("u6d-S2-B");
        DbxConfig ca = a.Config, cb = b.Config with { RescueDir = ca.RescueDir };
        b.Store.Save(cb);
        var ra = MakeRescue(ca, "A.txt");
        var rb = MakeRescue(cb, "B.txt") with { CompleteData = true, ModifiedUtc = DateTimeOffset.UtcNow };
        RescueStore.Save(rb);
        byte[] bBefore = File.ReadAllBytes(rb.RecordPath);
        a.State.Save(new RuntimeState { ConfigPath = a.Store.PathName, ActiveConfig = ca,
            StopDisposition = StopDisposition.Forced, RecoveryRequired = true, RescueRecords = [ra.RecordPath, rb.RecordPath] });
        a.Controller.ClearRecovery(true);
        TestRunner.Assert(RescueStore.List(ca).Single(r => r.Id == ra.Id).Acknowledged);
        TestRunner.Assert(bBefore.AsSpan().SequenceEqual(File.ReadAllBytes(rb.RecordPath)), "A changed B's record.");
        var forB = RescueStore.List(cb).Single(r => r.Id == rb.Id);
        TestRunner.Assert(!forB.Acknowledged && forB.MoveCompleted);
        TestRunner.Assert(RescueStore.HasUnacknowledged(cb));
        var summary = a.Controller.RescueList().Single(r => r.RecordStatus == "foreign");
        TestRunner.Equal(1, summary.ForeignRecordCount);
        TestRunner.Equal("", summary.DestinationPath);
        TestRunner.Equal("", summary.RecordPath);
        string foreignJson = System.Text.Json.JsonSerializer.Serialize(summary, JsonFiles.Options);
        TestRunner.Assert(!foreignJson.Contains(a.Root, StringComparison.OrdinalIgnoreCase) &&
            !foreignJson.Contains(b.Root, StringComparison.OrdinalIgnoreCase) && summary.Origin is null);
        a.Controller.PrepareConnectionChange(ca);
        a.Start(); // B's pending item must not block A or become acknowledged by A.
        a.Controller.Stop();
        b.Start();
        TestRunner.Assert(!File.Exists(Path.Combine(b.RemoteRoot, "B.txt")));
        TestRunner.Equal("preserved sample", File.ReadAllText(rb.DestinationPath));
        Console.WriteLine("S2 shared rescue: A clear/start leaves B pending; B keeps its body outside cache without uploading.");
    }
    private static void LegacyRescueOwners()
    {
        using var a = new Unit5Fixture("u6d-S2-legacy");
        var r = MakeRescue(a.Config);
        PrivateJsonFile.Write(r.RecordPath, r with { OwnerId = null });
        var inferred = RescueStore.List(a.Config).Single();
        TestRunner.Equal("verified", inferred.RecordStatus);
        TestRunner.Assert(RescueStore.List(a.Config with { Remote = "" }).All(r => r.IsForeign));
        TestRunner.Equal(RescueStore.Owner(a.Config), inferred.OwnerId);
        TestRunner.Assert(RescueStore.HasUnacknowledged(a.Config));
        PrivateJsonFile.Write(r.RecordPath, r with { OwnerId = null, OriginalPath = Path.Combine(a.Root, "other-cache", "saved.txt") });
        TestRunner.Equal("unknown", RescueStore.List(a.Config).Single().RecordStatus);
        PrivateJsonFile.Write(r.RecordPath, r with { OwnerId = new string('0', 64) });
        TestRunner.Equal("unknown", RescueStore.List(a.Config).Single().RecordStatus);
    }
}
