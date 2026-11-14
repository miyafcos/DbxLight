using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class Unit3cQueueTextTests
{
    private const string Caveat = "\u8868\u793a\u3057\u305f\u6642\u9593\u3088\u308a\u524d\u304b\u3089\u5f85\u3063\u3066\u3044\u305f\u53ef\u80fd\u6027\u304c\u3042\u308a\u307e\u3059\u3002";
    public static void Run(TestRunner runner)
    {
        runner.Test("C4: confirmed empty queue omits the age caveat in the builder and window", Empty);
        runner.Test("C4: pending queue retains the age caveat", Pending);
        runner.Test("C4: unknown queue retains the age caveat", Unknown);
    }

    private static TraySnapshot Snapshot(long? queued, bool known, IReadOnlyList<QueueItem>? queue = null) =>
        new(new() { Running = true, Ready = true, UploadsQueued = queued, UploadsInProgress = queued is null ? null : 0 }, queue ?? [], [], known, null);

    private static string Check(TraySnapshot snapshot, QueueAge? age)
    {
        string text = TrayPresentation.QueueAgeExplanation(snapshot, age);
        FormTests.Sta(() =>
        {
            using var form = new StatusForm(); form.UpdateSnapshot(snapshot, age, new DbxConfig());
            TestRunner.Equal(text, FormTests.Field<Label>(form, "oldest").Text);
        });
        Console.WriteLine("C4 age text: " + text.Replace('\n', ' '));
        return text;
    }

    private static void Empty()
    {
        string text = Check(Snapshot(0, true), null);
        TestRunner.Assert(!text.Contains(Caveat, StringComparison.Ordinal), "An empty queue still has an earlier-wait caveat.");
        TestRunner.Assert(text.Contains("\u4e0a\u3052\u5f85\u3061\u306f\u3042\u308a\u307e\u305b\u3093", StringComparison.Ordinal));
    }

    private static void Pending()
    {
        string text = Check(Snapshot(1, true, [new(1, "pending", 7, 0, false)]), new(TimeSpan.FromMinutes(2), false));
        TestRunner.Assert(text.Contains(Caveat, StringComparison.Ordinal) && text.Contains("2", StringComparison.Ordinal));
    }

    private static void Unknown()
    {
        string text = Check(Snapshot(null, false), null);
        TestRunner.Assert(text.Contains(Caveat, StringComparison.Ordinal) && text.Contains("\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093", StringComparison.Ordinal));
    }
}
