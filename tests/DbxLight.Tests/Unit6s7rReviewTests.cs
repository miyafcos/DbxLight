using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6s7rTests
{
    private static void Review(TestRunner runner, string? issue)
    {
        RegistrationReviews(runner, issue);
        if (issue is null or "all" or "R1")
        {
            runner.Test("6s7r R1: normal exit wait stays at 25000 milliseconds", () => TestRunner.Equal(25000, MountController.NormalStopWaitMilliseconds));
            runner.Test("6s7r R1: forced exit wait stays at 10000 milliseconds", () => TestRunner.Equal(10000, MountController.ForcedStopWaitMilliseconds));
            runner.Test("6s7r R1: bounded waits sum stays below the 45000 millisecond gate wait", () =>
                TestRunner.Assert(3000 + 3000 + MountController.NormalStopWaitMilliseconds + 10000 < 45000));
        }
        if (issue is null or "all" or "R2")
        {
            foreach (var phase in new[] { ResidentPhase.Stopping, ResidentPhase.Exiting, ResidentPhase.Watching })
            {
                var current = phase;
                runner.Test("6s7r R2: wired refresh busy notifications in " + phase, () => CheckRefreshNotice(current, new ControlBusyException(), current == ResidentPhase.Watching ? 1 : 0));
            }
            runner.Test("6s7r R2: wired refresh other failure while stopping", () => CheckRefreshNotice(ResidentPhase.Stopping, new IOException("fixture"), 1));
        }
        if (issue is null or "all" or "R9") runner.Test("6s7r R9: test work root follows environment, drive D and repository fallback", () =>
        {
            string repository = UnitTests.RepositoryRoot(); string configured = Path.Combine(repository, "_test-work", "custom");
            TestRunner.Equal(configured, TestPaths.ResolveWorkRoot(configured, true, repository, _ => (DriveType.Fixed, "NTFS")));
            TestRunner.Equal(configured, TestPaths.ResolveWorkRoot(configured, false, repository, _ => (DriveType.Fixed, "NTFS")));
            TestRunner.Equal(@"D:\dbxl-test", TestPaths.ResolveWorkRoot(null, true, repository, _ => (DriveType.Fixed, "NTFS")));
            TestRunner.Equal(Path.Combine(repository, "_test-work"), TestPaths.ResolveWorkRoot(null, false, repository, _ => (DriveType.Fixed, "NTFS")));
            Console.WriteLine("TestPaths.WorkRoot=" + TestPaths.WorkRoot);
        });
    }
    private static void CheckRefreshNotice(ResidentPhase phase, Exception error, int expected)
    {
        int count = 0;
        ResidentContext.ReportRefreshFailure(phase, error, (title, detail, icon) =>
        {
            count++; TestRunner.Equal("状態を確認できません", title);
            TestRunner.Equal(ToolTipIcon.Warning, icon); TestRunner.Assert(detail.Length > 0);
        });
        TestRunner.Equal(expected, count);
    }
}
