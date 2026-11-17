using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6s7rTests
{
    internal static void RunReview2(TestRunner runner, string? issue = null)
    {
        RunReview2Wait(runner, issue);
        if (issue is null or "Q4")
        {
            foreach (bool find in new[] { false, true })
            foreach (int offset in new[] { 0, 5 })
            {
                bool search = find; int start = offset;
                runner.Test($"6s7rb Q4: stalled timed page suggests a larger budget or narrower scope; find={find}; offset={offset}",
                    () => Review2Continuation(search, start, progressed: false));
            }
            foreach (bool find in new[] { false, true })
            {
                bool search = find;
                runner.Test("6s7rb Q4: progressed timed page retains the original hint; find=" + find,
                    () => Review2Continuation(search, 0, progressed: true));
            }
            runner.Test("6s7rb Q4: progressed search with no matches retains the original hint",
                () => Review2Continuation(true, 0, progressed: true, query: "absent"));
        }
        if (issue is null or "Q5")
            runner.Test("6s7rb Q5: runtime reads keep the two-second control wait", () =>
                TestRunner.Equal(TimeSpan.FromSeconds(2), MountController.ReadControlWait));
        if (issue is null or "Q9")
        {
            string repository = UnitTests.RepositoryRoot(), temporary = Path.Combine(repository, "_test-work", "temp-root");
            string configured = Path.Combine(repository, "_test-work", "configured-root");
            runner.Test("6s7rb Q9: configured root takes precedence", () =>
            {
                TestRunner.Equal(configured, TestWorkPaths.ResolveWorkRoot(configured, true, repository, temporary, _ => (DriveType.Fixed, "NTFS")));
                TestRunner.Equal(configured, TestWorkPaths.ResolveWorkRoot(configured, false, null, temporary, _ => (DriveType.Fixed, "NTFS")));
            });
            runner.Test("6s7rb Q9: drive D keeps the existing root", () =>
                TestRunner.Equal(@"D:\dbxl-test", TestWorkPaths.ResolveWorkRoot(null, true, null, temporary, _ => (DriveType.Fixed, "NTFS"))));
            runner.Test("6s7rb Q9: repository without drive D uses _test-work", () =>
                TestRunner.Equal(Path.Combine(repository, "_test-work"), TestWorkPaths.ResolveWorkRoot(" ", false, repository, temporary, _ => (DriveType.Fixed, "NTFS"))));
            runner.Test("6s7rb Q9: published app without drive D uses the temporary root", () =>
                TestRunner.Equal(Path.Combine(temporary, "dbxl-test"), TestWorkPaths.ResolveWorkRoot(null, false, null, temporary, _ => (DriveType.Fixed, "NTFS"))));
        }
        if (issue is null or "Q11")
            runner.Test("6s7rb Q11: null executable skips the registration advisory and settings stay open", Review2NullExecutable);
    }

    private static void Review2Continuation(bool find, int offset, bool progressed, string query = "file")
    {
        using var f = new Unit5Fixture("6s7rb-page");
        using var clock = new TestTraversalClock();
        int checks = 0;
        clock.StopCheckpoint = () => { if (progressed && ++checks == 2) clock.Advance(1.1); };
        using var agent = new AgentMetadata(f.Controller)
        {
            ChildrenReader = _ =>
            {
                if (!progressed) clock.Advance(1.1);
                return Enumerable.Range(0, 10).Select(i => new MetadataItem($"file{i:D2}.txt", false, 7, null)).ToArray();
            }
        };
        AgentContinuation continuation;
        int count;
        if (find)
        {
            AgentFindResult result = agent.Find(query, offset: offset, timeLimitSeconds: 1);
            TestRunner.Assert(result.TimeLimited && result.Truncated); continuation = result.Continuation!; count = result.Entries.Count;
        }
        else
        {
            AgentListing result = agent.List("", new(Depth: 2, Offset: offset, TimeLimitSeconds: 1));
            TestRunner.Assert(result.TimeLimited && result.Truncated); continuation = result.Continuation!; count = result.Entries.Count;
        }
        int expectedOffset = progressed ? 1 : offset;
        TestRunner.Equal(expectedOffset, continuation.Offset);
        TestRunner.Equal(progressed && query == "file" ? 1 : 0, count);
        string expected = "Time limit reached; there is more to search, even when entries is empty. " +
            (progressed ? "Repeat the same command and options with --offset " :
                "Increase --time-limit or narrow the scope with --in, then continue with --offset ") +
            expectedOffset + "; listing must remain stable.";
        TestRunner.Equal(expected, continuation.Hint);
        Console.WriteLine($"Q4 find={find}; entries={count}; offset={offset}; next={continuation.Offset}; hint={continuation.Hint}");
    }

    private static void Review2NullExecutable()
    {
        var f = new RegistrationFixture();
        var registered = new DbxConfig { MountPoint = "U:" };
        f.Service.InstallNavigation(registered, f.Exe);
        f.Service.InstallShell(registered, f.Exe, f.Options);
        f.Service.SetAutoStart(true, f.Exe, f.Options);
        var current = registered with { MountPoint = "V:" };
        int readsBefore = f.Service.AccessedPaths.Count;
        TestRunner.Equal("", f.Service.ShellRegistrationNotice(current, null, f.Options));
        TestRunner.Equal(readsBefore, f.Service.AccessedPaths.Count);
        FormTests.Sta(() =>
        {
            using var form = new SettingsForm(current, f.Service, null, f.Options, _ => Task.CompletedTask);
            var notice = FormTests.Field<TextBox>(form, "shellNotice");
            TestRunner.Equal("", notice.Text);
            TestRunner.Assert(!notice.Visible && !form.IsDisposed);
        });
        f.Safe();
        Console.WriteLine("Q11 null executable: advisory skipped; settings constructed without exception; TestHive only.");
    }
}
