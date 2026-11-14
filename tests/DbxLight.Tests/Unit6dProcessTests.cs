using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6dTests
{
    internal static int ProcessChild(string stage, string root, string executable)
    {
        JobProcess.Checkpoint = (at, child) =>
        {
            if (at != stage) return;
            JsonFiles.Write(Path.Combine(root, "checkpoint.json"), new RuntimeState { RclonePid = child.Id,
                ProcessStartedUtc = child.StartTime.ToUniversalTime(), ExecutablePath = executable });
            Thread.Sleep(Timeout.Infinite);
        };
        _ = new RcloneLogin(executable, Path.Combine(root, "private", "rclone.conf")).ListRemotesAsync(false).GetAwaiter().GetResult();
        return 2;
    }
    private static void HelperCrash(string stage)
    {
        using var f = new LoginFixture();
        string ownExe = Path.ChangeExtension(typeof(Unit6dTests).Assembly.Location, ".exe");
        using Process parent = Process.Start(RcloneArguments.ProcessInfo(ownExe,
            ["--unit6d-process-child", stage, f.Root, f.Executable]))!;
        RuntimeState? childState = null;
        try
        {
            string checkpoint = Path.Combine(f.Root, "checkpoint.json");
            Unit5Fixture.Wait(() => File.Exists(checkpoint), "Child creation checkpoint missing.");
            childState = JsonFiles.Read<RuntimeState>(checkpoint);
            parent.Kill();
            TestRunner.Assert(parent.WaitForExit(10000));
            using Process? child = ProcessIdentity.Open(childState);
            if (child is not null)
            {
                TestRunner.Assert(MountController.AllThreadsSuspended(child), "A helper executed before joining its job.");
                Thread.Sleep(150);
                TestRunner.Assert(MountController.AllThreadsSuspended(child));
                child.Kill(); TestRunner.Assert(child.WaitForExit(10000));
            }
            TestRunner.Equal(0, f.Calls().Length);
            Console.WriteLine("S4 " + stage + ": parent killed; no helper output or execution; exact suspended child stopped.");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(10000); }
            if (childState is not null)
            {
                using Process? child = ProcessIdentity.Open(childState);
                if (child is not null) { child.Kill(); TestRunner.Assert(child.WaitForExit(10000)); }
            }
        }
    }
}
