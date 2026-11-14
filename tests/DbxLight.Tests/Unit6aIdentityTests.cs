using System.ComponentModel;
using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void UntrustedIdentity()
    {
        using Process self = Process.GetCurrentProcess();
        DateTime started = self.StartTime.ToUniversalTime();
        string image = ProcessIdentity.ImagePath(self);
        foreach (RuntimeState invalid in new[]
        {
            new RuntimeState { RclonePid = self.Id, ProcessStartedUtc = started, ExecutablePath = Path.Combine(Environment.SystemDirectory, "ntdll.dll") },
            new RuntimeState { RclonePid = self.Id, ExecutablePath = image },
            new RuntimeState { RclonePid = self.Id, ProcessStartedUtc = started }
        })
        {
            TestRunner.Throws<ProcessIdentityUnknownException>(() => ProcessIdentity.Open(invalid));
            using var f = new Unit3cTests.LockoutMount("u6a-untrusted");
            RuntimeSnapshots.Save(f.State, invalid with { ConfigPath = f.Config.PathName, ActiveConfig = f.Config.Load(), RecoveryRequired = true });
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.ClearRecovery(true));
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
            Console.WriteLine("P5 invalid identity: clear and second start refused; saved start=" + invalid.ProcessStartedUtc.ToString("O"));
        }
        var denied = new ProcessIdentityQueries(_ => "another", _ => throw new Win32Exception(5),
            _ => throw new Win32Exception(5), _ => throw new Win32Exception(5));
        using Process? reused = ProcessIdentity.Open(self.Id, started, @"D:\sample\rclone.exe", denied);
        TestRunner.Assert(reused is null, "The unit3c name-based reused-PID guard regressed.");
    }
}
