using System.ComponentModel;
using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static class Unit3cIdentityReviewTests
{
    public static void Run(TestRunner runner, bool includeMounts)
    {
        runner.Test("C1 review: missing legacy supervisor image never proves death by an assumed name", LegacyImage);
        runner.Test("C1 review: malformed directory components cannot prove death by name", MalformedDirectory);
        if (includeMounts) runner.Test("C1 review: a clean stopped supervisor record is retired under the control gate", RetiredSupervisor);
    }

    private static ProcessIdentityQueries Denied => new(_ => "renamed-host",
        _ => throw new Win32Exception(5, "injected access denied"),
        _ => throw new InvalidOperationException("Start must not be queried"),
        _ => throw new InvalidOperationException("Image must not be queried"));

    private static void LegacyImage() => TestRunner.Throws<ProcessIdentityUnknownException>(() =>
        ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, "", Denied, legacySupervisor: true));

    private static void MalformedDirectory()
    {
        foreach (string path in new[] { @"C:\bad*\rclone.exe", @"C:\bad?\rclone.exe", @"C:\bad:part\rclone.exe", @"\\bad*server\share\rclone.exe", @"C:\bad\D:\rclone.exe", @"C:\bad\C:\rclone.exe", @"\\C:\share\rclone.exe" })
            TestRunner.Throws<ProcessIdentityUnknownException>(() => ProcessIdentity.Open(Environment.ProcessId, DateTime.UnixEpoch, path, Denied));
    }

    private static void RetiredSupervisor()
    {
        using var f = new Unit3cTests.LockoutMount("retired-supervisor");
        using var self = Process.GetCurrentProcess();
        RuntimeSnapshots.Save(f.State, f.Clean with { SupervisorPid = self.Id, SupervisorStartedUtc = self.StartTime.ToUniversalTime(),
            SupervisorExecutablePath = null });
        f.Start();
        TestRunner.Assert(f.Controller.Status().Ready);
        TestRunner.Assert(f.State.Load()!.SupervisorPid is null, "A stopped supervisor retained ownership of the replacement runtime.");
        TestRunner.Assert(!self.HasExited);
    }
}
