using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6dTests
{
    private static void SeedLegacy(LoginFixture f)
    {
        string legacy = CredentialSwitch.LegacyPath(f.Controller.Config.PathName);
        using var stream = new FileInfo(legacy).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.Read,
            4096, FileOptions.WriteThrough, PrivateStateFiles.FileSecurity());
        stream.Write(Encoding.ASCII.GetBytes(new string('o', 19))); stream.Flush(true);
    }
    private static SetupSession FinishIsolated(LoginFixture f)
    {
        var session = new SetupSession(f.Controller, CredentialSwitch.KeyPath(f.Controller.Config.PathName));
        session.CheckRequirements(); TestRunner.Assert(session.Next());
        TestRunner.Assert(session.AuthorizeAsync(true).GetAwaiter().GetResult().Succeeded);
        session.SelectScopeAsync(false).GetAwaiter().GetResult(); TestRunner.Assert(session.Next());
        session.SetOptions(f.Controller.Config.Load().MountPoint, "1h"); TestRunner.Assert(session.Next());
        return session;
    }
    private static void InheritedLogin(bool legacy)
    {
        using var f = new LoginFixture("mount");
        string old = CredentialSwitch.LegacyPath(f.Controller.Config.PathName), key = CredentialSwitch.KeyPath(f.Controller.Config.PathName);
        f.Seed(legacy ? "dbxlight:" : "", legacy ? old : null);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        f.Controller.Config.Save(f.Controller.Config.Load() with
        {
            MountPoint = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable), RcPort = port,
            WriteAllowedRemotes = legacy ? ["dbxlight:"] : []
        });
        if (legacy) SeedLegacy(f);
        string acl = new DirectoryInfo(f.Root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        RuntimeState? mounted = null;
        try
        {
            using var session = FinishIsolated(f);
            DbxConfig saved = session.Commit();
            TestRunner.Equal(key, saved.RcloneConfigPath);
            TestRunner.Equal(0, saved.WriteAllowedRemotes.Length);
            TestRunner.Equal(legacy, File.Exists(old));
            TestRunner.Assert(new DirectoryInfo(Path.GetDirectoryName(key)!).GetAccessControl().AreAccessRulesProtected);
            TestRunner.Equal(acl, new DirectoryInfo(f.Root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            f.Controller.Start(); mounted = f.Controller.State.Load();
            TestRunner.Assert(f.Controller.Status() is { Running: true, Ready: true, RecoveryRequired: false, ReadOnly: true });
            TestRunner.Assert(f.Calls().Any(c => c.Args[0] == "mount" && c.Args.Contains("--read-only") && c.Args.Contains(key)));
            f.Controller.Stop();
            TestRunner.Assert(!f.Controller.Status().Running);
        }
        finally
        {
            if (mounted is not null)
            {
                using Process? child = ProcessIdentity.Open(mounted);
                if (child is not null) f.Controller.Stop(true);
            }
        }
        Console.WriteLine("S6 inherited root, legacy=" + legacy + ": private key folder, fake authorization/finish/start/stop PASS; parent ACL unchanged.");
    }
    private static void UnsafeCredentialDirectory()
    {
        using var f = new LoginFixture();
        f.Seed();
        string key = CredentialSwitch.KeyPath(f.Controller.Config.PathName);
        Directory.CreateDirectory(Path.GetDirectoryName(key)!); // Existing inherited ACL must never be accepted.
        using var session = new SetupSession(f.Controller, key);
        session.CheckRequirements(); session.Next();
        TestRunner.Throws<UnauthorizedAccessException>(() => session.AuthorizeAsync(true).GetAwaiter().GetResult());
        TestRunner.Assert(!File.Exists(key) && f.Calls().Length == 0);
    }
}
