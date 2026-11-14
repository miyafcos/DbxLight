using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DbxLight.Core;
namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void LegacyIdentityVersion()
    {
        using Process self = Process.GetCurrentProcess();
        using var f = new Unit3cTests.LockoutMount("u6a-legacy-identity");
        RuntimeSnapshots.Save(f.State, f.Clean with { RclonePid = self.Id, ProcessStartedUtc = self.StartTime.ToUniversalTime(),
            ExecutablePath = @"D:\Sample\old-rclone.exe", RecoveryRequired = true, StoppedUtc = null });
        var json = JsonNode.Parse(File.ReadAllText(f.State.PathName))!.AsObject(); json.Remove("identityVersion");
        File.WriteAllText(f.State.PathName, json.ToJsonString(JsonFiles.Options), new UTF8Encoding(false));
        TestRunner.Equal(0, f.State.Load()!.IdentityVersion);
        for (int i = 0; i < 2; i++)
        {
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.ClearRecovery(true));
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        }
        Console.WriteLine("P5 unversioned .exe + same PID/start: remains unknown across saves; clear and second start refused.");
    }

    private static void LegacyCacheOwner()
    {
        using Process self = Process.GetCurrentProcess();
        using var f = new Unit3cTests.LockoutMount("u6a-legacy-owner");
        RuntimeSnapshots.Save(f.State, f.Clean); Directory.CreateDirectory(f.Config.Load().CacheDir);
        JsonFiles.Write(Path.Combine(f.Config.Load().CacheDir, "dbxl-owner.json"),
            new { pid = self.Id, processStartedUtc = self.StartTime.ToUniversalTime(), executablePath = @"D:\Sample\old-rclone.exe", statePath = f.State.PathName });
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.ClearRecovery(true));
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
        Console.WriteLine("P5 unversioned cache owner + same PID/start: clear and second start refused.");
    }
}
