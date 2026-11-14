namespace DbxLight.Core;

public sealed partial class MountController
{
    public IReadOnlyList<RescueRecord> RescueList(int? maximum = 50) => ControlGate.Execute(() =>
    {
        RuntimeState? state = LoadRuntime();
        if (maximum is < 1 or > 10000) throw new ArgumentException("maximum must be 1..10000 or null.");
        var records = RescueStore.Summarize(state is null ? RescueStore.Pending(Config.Load()) : RescueStore.Pending(state));
        return maximum is { } limit ? records.Take(limit).ToArray() : records;
    });

    public void AcknowledgeRescue(string id) => ControlGate.Execute(() =>
    {
        RescueRecord record = RescueList(null).SingleOrDefault(r => r.Id == id && r.RecordStatus == "verified")
            ?? throw new InvalidOperationException("確認できる取り出し記録を指定してください。");
        if (!record.MoveCompleted) throw new InvalidOperationException("取り出し途中です。先に起動前の走査を完了してください。");
        RescueStore.Save(record with { Acknowledged = true });
        return 0;
    });

    public void MarkRescueNotified(IEnumerable<string> ids) => ControlGate.Execute(() =>
    {
        var keys = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var record in RescueList(null).Where(r => keys.Contains(r.Id) && !r.NotificationShown && r.RecordStatus == "verified"))
            RescueStore.Save(record with { NotificationShown = true });
        return 0;
    });
}
