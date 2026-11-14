using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit4Tests
{
    private static void LogoutDedicated()
    {
        using var f = new LoginFixture(); f.Seed("dbxlight:/", f.Conf);
        PrivateLoginFile.Empty(f.Conf);
        File.WriteAllText(f.Conf, "[dbxlight]\ntoken = " + Marker, new UTF8Encoding(false));
        DbxConfig saved = new AccountSettings(f.Controller, f.Conf).LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Equal("", saved.Remote); TestRunner.Equal(0, saved.WriteAllowedRemotes.Length);
        TestRunner.Assert(saved.RcloneConfigPath is null); TestRunner.Equal(0L, new FileInfo(f.Conf).Length);
        string[] call = f.Calls().Single().Args;
        TestRunner.Assert(call[4].StartsWith(f.Conf + ".work-", StringComparison.Ordinal));
        TestRunner.Assert(call.SequenceEqual(new[] { "config", "delete", "dbxlight", "--config", call[4] }));
    }

    private static void LogoutExisting()
    {
        using var f = new LoginFixture(); f.Seed("sample-existing:");
        new AccountSettings(f.Controller, f.Conf).LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Equal("", f.Controller.Config.Load().Remote);
        TestRunner.Equal(0, f.Calls().Length);
        TestRunner.Assert(!File.Exists(f.Conf));
    }

    private static void LogoutQueueRefusals()
    {
        foreach (bool unknown in new[] { false, true })
        {
            using var f = new Unit3aTests.RcFixture();
            Directory.CreateDirectory(f.Config.Load().MountPoint);
            Directory.CreateDirectory(f.Config.Load().CacheDir);
            RuntimeSnapshots.Save(f.State, f.State.Load()! with { RecoveryRequired = false, StopDisposition = StopDisposition.Running });
            int pid = f.State.Load()!.RclonePid;
            f.Respond = path => path == "/vfs/stats"
                ? unknown ? (503, "{}") : (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}")
                : path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, $"{{\"pid\":{pid}}}");
            string before = File.ReadAllText(f.Config.PathName);
            TestRunner.Throws<PendingUploadsException>(() => new AccountSettings(f.Controller, Path.Combine(f.Root, "private", "rclone.conf")).LogoutAsync().GetAwaiter().GetResult());
            TestRunner.Equal(before, File.ReadAllText(f.Config.PathName));
            using var alive = ProcessIdentity.Open(f.State.Load()!); TestRunner.Assert(alive is not null);
        }
    }

    private static void LogoutRecovery()
    {
        using var f = new LoginFixture(); f.Seed("dbxlight:", f.Conf);
        f.MarkPendingCache();
        RuntimeSnapshots.Save(f.Controller.State, new RuntimeState { RclonePid = int.MaxValue, ExecutablePath = f.Executable,
            ProcessStartedUtc = DateTime.UnixEpoch, ConfigPath = f.Controller.Config.PathName, ActiveConfig = f.Controller.Config.Load(),
            RecoveryRequired = true, StopDisposition = StopDisposition.Forced });
        try
        {
            new AccountSettings(f.Controller, f.Conf).LogoutAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected recovery refusal.");
        }
        catch (PendingUploadsException e) { TestRunner.Assert(e.Message.Contains("回復がまだです")); }
        TestRunner.Equal(0, f.Calls().Length); TestRunner.Assert(!File.Exists(f.Conf));
    }

    private static void LogoutFailureRollback()
    {
        using var f = new LoginFixture("delete-failure"); f.Seed("dbxlight:", f.Conf);
        PrivateLoginFile.Empty(f.Conf);
        string original = "[dbxlight]\ntoken = " + Marker;
        File.WriteAllText(f.Conf, original, new UTF8Encoding(false));
        var account = new AccountSettings(f.Controller, f.Conf);
        try { account.LogoutAsync().GetAwaiter().GetResult(); throw new InvalidOperationException("Expected deletion failure."); }
        catch (IOException e) { TestRunner.Assert(!e.ToString().Contains(Marker)); }
        TestRunner.Equal(original, File.ReadAllText(f.Conf));
        TestRunner.Equal("dbxlight:", f.Controller.Config.Load().Remote);
        f.Scenario("success"); account.LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Equal("", f.Controller.Config.Load().Remote);
    }

    private static void LogoutStopsFirst()
    {
        using var f = new Unit3aTests.RcFixture();
        DbxConfig config = f.Config.Load();
        Directory.CreateDirectory(config.MountPoint); Directory.CreateDirectory(config.CacheDir);
        RuntimeSnapshots.Save(f.State, f.State.Load()! with { RecoveryRequired = false, StopDisposition = StopDisposition.Running });
        bool stopped = false;
        int pid = f.State.Load()!.RclonePid;
        f.Respond = path =>
        {
            if (path == "/core/quit")
            {
                stopped = true;
                File.WriteAllText(Path.Combine(f.Root, "child-stop.txt"), "exit", new UTF8Encoding(false));
                return (200, "{}");
            }
            return path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}")
                : path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, $"{{\"pid\":{pid}}}");
        };
        new AccountSettings(f.Controller, Path.Combine(f.Root, "private", "rclone.conf")).LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Assert(stopped && !f.Controller.Status().Running);
        TestRunner.Equal("", f.Config.Load().Remote);
        TestRunner.Assert(!f.State.Load()!.RecoveryRequired);
    }
}
