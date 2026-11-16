using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6hTests
{
    private static void QueueAgeUnknown()
    {
        var unknown = new TraySnapshot(new() { Running = false, Ready = false, RecoveryRequired = false }, [], [], false, null);
        foreach (var sample in new[] { unknown, unknown with { Status = unknown.Status with { Running = true } },
            unknown with { QueueKnown = true, Queue = [new(1, "pending.txt", 1, 0, false)], Status = new() { UploadsQueued = 1, UploadsInProgress = 0 } },
            unknown with { QueueKnown = true, Status = new() { UploadsQueued = 0, UploadsInProgress = 0, ProcessUncertain = true } } })
        {
            string text = TrayPresentation.QueueAgeExplanation(sample, null);
            TestRunner.Assert(text.Contains("確認できません", StringComparison.Ordinal));
            TestRunner.Assert(!text.Contains("アップロード待ちはありません", StringComparison.Ordinal));
        }
        var empty = unknown with { QueueKnown = true, Status = new() { UploadsQueued = 0, UploadsInProgress = 0 } };
        TestRunner.Assert(TrayPresentation.QueueAgeExplanation(empty, null).Contains("アップロード待ちはありません", StringComparison.Ordinal));
        var pending = empty with { Queue = [new(1, "pending.txt", 1, 0, false)], Status = new() { UploadsQueued = 1, UploadsInProgress = 0 } };
        TestRunner.Assert(TrayPresentation.QueueAgeExplanation(pending, new(TimeSpan.FromMinutes(2), false)).Contains("2 分", StringComparison.Ordinal));
    }
    private static void PartialLogFailure(bool currentLocked)
    {
        using var f = new Unit5Fixture("u6h-partial-log-" + currentLocked);
        var info = MountLogStore.Prepare(f.Store.PathName, f.Config);
        string current = MountLogStore.FilePath(info);
        File.WriteAllText(current, "ERROR " + Unit4Tests.Marker + "\n");
        string previous = Path.Combine(info.Directory!, "mount-2026-01-02T03-04-05.000.log");
        File.WriteAllText(previous, "ERROR " + Unit4Tests.Marker + "\n");
        using var locked = new FileStream(currentLocked ? current : previous, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var tail = ErrorLogTail.ReadDetails(info.Directory);
        TestRunner.Assert(!tail.ReadSucceeded); TestRunner.Equal(currentLocked ? 0 : 1, tail.Errors.Count);
        var sample = new TraySnapshot(new() { LogCapture = info }, currentLocked ? [new(1, "sample.txt", 1, 2, false)] : [],
            tail.Errors, false, null, LogReadSucceeded: tail.ReadSucceeded);
        FormTests.Sta(() =>
        {
            using var form = new StatusForm(); form.UpdateSnapshot(sample, null, f.Config);
            string text = FormTests.Field<TextBox>(form, "errors").Text;
            TestRunner.Equal(1, text.Split("ログはありますが読めません", StringSplitOptions.None).Length - 1);
            TestRunner.Assert(text.Contains("失敗", StringComparison.Ordinal));
            TestRunner.Assert(text.Contains(currentLocked ? "sample.txt" : ErrorLogTail.ProviderError, StringComparison.Ordinal));
            TestRunner.Assert(!text.Contains(Unit4Tests.Marker, StringComparison.Ordinal));
            TestRunner.Assert(!text.Contains("失敗のログはありません", StringComparison.Ordinal));
        });
    }
}
