using System.Diagnostics;
using System.Security.AccessControl;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6dTests
{
    internal static int KeyChild(string config, string state, string stage)
    {
        void Reach(string at)
        {
            if (at != stage) return;
            JsonFiles.Write(Path.Combine(Path.GetDirectoryName(config)!, "key-reached.json"), new { stage = at });
            Thread.Sleep(Timeout.Infinite);
        }
        CredentialSwitch.Checkpoint = Reach;
        var controller = new MountController(new ConfigStore(config), new StateStore(state));
        using var session = new SetupSession(controller, CredentialSwitch.KeyPath(config));
        session.CheckRequirements(); TestRunner.Assert(session.Next());
        TestRunner.Assert(session.AuthorizeAsync(true).GetAwaiter().GetResult().Succeeded);
        Reach("authorized");
        session.SelectScopeAsync(false).GetAwaiter().GetResult(); TestRunner.Assert(session.Next());
        session.SetOptions(controller.Config.Load().MountPoint, "1h"); TestRunner.Assert(session.Next());
        session.Commit();
        return 2;
    }
    private static void SeedCurrentKey(LoginFixture f)
    {
        string key = CredentialSwitch.KeyPath(f.Controller.Config.PathName);
        f.Seed("dbxlight:", key);
        f.Controller.Config.Save(f.Controller.Config.Load() with { WriteAllowedRemotes = ["dbxlight:"] });
        PrivateLoginFile.Empty(key);
        File.WriteAllText(key, new string('o', 19), new UTF8Encoding(false));
    }
    private static void KeyCrash(string stage, bool corrupt = false)
    {
        using var f = new LoginFixture(); SeedCurrentKey(f);
        string ownExe = Path.ChangeExtension(typeof(Unit6dTests).Assembly.Location, ".exe");
        using Process parent = Process.Start(RcloneArguments.ProcessInfo(ownExe,
            ["--unit6d-key-child", f.Controller.Config.PathName, f.Controller.State.PathName, stage]))!;
        try
        {
            Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.Root, "key-reached.json")), "Key switch checkpoint missing.");
            parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000));
            if (corrupt)
            {
                string txPath = CredentialSwitch.TransactionPath(f.Controller.Config.PathName);
                var tx = PrivateJsonFile.Read<KeyTransaction>(txPath)!;
                PrivateJsonFile.Write(txPath, tx with { Stage = "Unrecognized" });
            }
            DbxConfig recovered = f.Controller.Config.Load();
            string key = CredentialSwitch.KeyPath(f.Controller.Config.PathName);
            long length = new FileInfo(key).Length;
            TestRunner.Assert(length == 19 || recovered.WriteAllowedRemotes.Length == 0, "New key was paired with an old write grant.");
            TestRunner.Equal(0, Directory.GetFiles(Path.GetDirectoryName(key)!, "rclone.conf.work-*").Length);
            TestRunner.Equal(0, Directory.GetFiles(Path.GetDirectoryName(key)!, "rclone.conf.pending-*").Length);
            if (corrupt)
            {
                TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
                TestRunner.Equal(0, recovered.WriteAllowedRemotes.Length);
                using var retry = FinishIsolated(f);
                retry.Commit();
                TestRunner.Assert(!File.Exists(CredentialSwitch.TransactionPath(f.Controller.Config.PathName)) &&
                    !File.Exists(CredentialSwitch.ReloginPath(f.Controller.Config.PathName)));
            }
            else if (stage == "switch-installed")
            { TestRunner.Assert(length != 19); TestRunner.Equal(0, recovered.WriteAllowedRemotes.Length); }
            else { TestRunner.Equal(19L, length); TestRunner.Assert(recovered.WriteAllowedRemotes.SequenceEqual(new[] { "dbxlight:" })); }
            TestRunner.Assert(!File.Exists(CredentialSwitch.TransactionPath(f.Controller.Config.PathName)));
            Console.WriteLine("S3 crash " + stage + (corrupt ? " (unknown marker)" : "") + ": safe pair recovered; registered work/backup files scrubbed.");
        }
        finally { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(10000); } }
    }
    private static void StagedAuthorization()
    {
        using var f = new LoginFixture(); SeedCurrentKey(f);
        string key = CredentialSwitch.KeyPath(f.Controller.Config.PathName);
        using (var session = FinishIsolated(f))
        {
            TestRunner.Equal(19L, new FileInfo(key).Length);
            TestRunner.Assert(f.Controller.Config.Load().WriteAllowedRemotes.SequenceEqual(new[] { "dbxlight:" }));
            TestRunner.Assert(session.BrowsingConfig.RcloneConfigPath!.StartsWith(key + ".work-", StringComparison.Ordinal));
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
            TestRunner.Assert(f.Calls().Where(c => c.Args[0] == "lsjson").All(c => c.Args.Contains(session.BrowsingConfig.RcloneConfigPath!)));
        }
        TestRunner.Equal(19L, new FileInfo(key).Length);
        TestRunner.Equal(0, Directory.GetFiles(Path.GetDirectoryName(key)!, "rclone.conf.work-*").Length);
    }
    private static void LegacyKeyCleanup()
    {
        using var f = new LoginFixture(); SeedCurrentKey(f);
        string orphan = CredentialSwitch.LegacyPath(f.Controller.Config.PathName) + ".pending-" + Guid.NewGuid().ToString("N");
        using (var file = new FileInfo(orphan).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.Read, 4096,
            FileOptions.WriteThrough, PrivateStateFiles.FileSecurity()))
        { file.Write(Encoding.ASCII.GetBytes(new string('s', 29))); file.Flush(true); }
        string retained = orphan + "-user-file";
        File.WriteAllText(retained, "retain ordinary file", new UTF8Encoding(false));
        TestRunner.Assert(f.Controller.Config.Load().WriteAllowedRemotes.SequenceEqual(new[] { "dbxlight:" }));
        TestRunner.Assert(File.Exists(orphan) && File.Exists(retained));
        TestRunner.Equal(29L, new FileInfo(orphan).Length);
        TestRunner.Assert(f.Controller.Status().UnregisteredCredentialFiles.Contains(orphan));
        using (var session = FinishIsolated(f)) session.Commit();
        string key = CredentialSwitch.KeyPath(f.Controller.Config.PathName);
        string pending = key + ".pending-" + Guid.NewGuid().ToString("N");
        PrivateLoginFile.Empty(pending); File.WriteAllText(pending, new string('s', 31), new UTF8Encoding(false));
        new AccountSettings(f.Controller).LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Assert(File.Exists(pending) && File.Exists(orphan) && !File.Exists(CredentialSwitch.ReloginPath(f.Controller.Config.PathName)));
        TestRunner.Equal(31L, new FileInfo(pending).Length);
        TestRunner.Equal(29L, new FileInfo(orphan).Length);
        TestRunner.Equal("", f.Controller.Config.Load().Remote);
        TestRunner.Equal(0L, new FileInfo(key).Length);
        TestRunner.Equal("retain ordinary file", File.ReadAllText(retained));
    }
}
