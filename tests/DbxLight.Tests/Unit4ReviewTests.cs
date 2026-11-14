using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit4Tests
{
    private static void PrivacyAcrossSurfaces()
    {
        using var f = new LoginFixture("failure"); f.Seed();
        using var captured = new StringWriter();
        using var listener = new TextWriterTraceListener(captured);
        TextWriter previousOut = Console.Out, previousError = Console.Error;
        Trace.Listeners.Add(listener); Console.SetOut(captured); Console.SetError(captured);
        try
        {
            using var session = f.AtLogin();
            LoginResult failed = session.AuthorizeAsync(false).GetAwaiter().GetResult();
            TestRunner.Assert(!failed.ToString().Contains(Marker) && !session.Message.Contains(Marker));
            f.Scenario("listing-failure");
            TestRunner.Assert(session.AuthorizeAsync(false).GetAwaiter().GetResult().Succeeded);
            session.SelectScopeAsync(false).GetAwaiter().GetResult();
            TestRunner.Assert(!session.Message.Contains(Marker));
            FormTests.Sta(() =>
            {
                using var setup = new SetupForm(session);
                TestRunner.Assert(!string.Join("\n", Controls(setup).Select(c => c.Text)).Contains(Marker));
                var options = new AppOptions(f.Controller.Config.PathName, f.Controller.State.PathName,
                    RegistryRoot: RegistrationService.TestHive + "\\Privacy-" + Guid.NewGuid().ToString("N"));
                using var settings = new SettingsForm(session.Draft, new RegistrationService(options.RegistryRoot),
                    f.Executable, options, _ => Task.CompletedTask);
                TestRunner.Assert(!string.Join("\n", Controls(settings).Select(c => c.Text)).Contains(Marker));
            });
            Trace.Flush();
            TestRunner.Assert(!captured.ToString().Contains(Marker), "Authentication output reached console or trace.");
            string logs = f.Controller.Config.Load().LogDir;
            if (Directory.Exists(logs))
                foreach (string file in Directory.GetFiles(logs, "*", SearchOption.AllDirectories))
                    TestRunner.Assert(!File.ReadAllText(file).Contains(Marker), "Authentication output reached a log.");
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); Trace.Listeners.Remove(listener); }
    }

    private static void ExistingFirstPrivateParent()
    {
        using var f = new LoginFixture();
        string parent = Path.Combine(f.Root, "account");
        Directory.CreateDirectory(parent);
        string beforeAcl = new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var controller = new MountController(new ConfigStore(Path.Combine(parent, "config.json"), initialConfig: f.Controller.Config.Load()),
            new StateStore(Path.Combine(f.Root, "private-runtime", "state.json")));
        using var session = new SetupSession(controller, new RcloneLogin(f.Executable, Path.Combine(parent, "rclone.conf")));
        session.CheckRequirements(); session.Next();
        session.UseExistingRemoteAsync("sample-existing:").GetAwaiter().GetResult();
        session.SelectScopeAsync(false).GetAwaiter().GetResult(); session.Next(); session.SetOptions("T:", "1h"); session.Next(); session.Commit();
        TestRunner.Equal(beforeAcl, new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        DirectorySecurity acl = new DirectoryInfo(Path.GetDirectoryName(CredentialSwitch.KeyPath(controller.Config.PathName))!).GetAccessControl();
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        TestRunner.Assert(acl.AreAccessRulesProtected && sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))));
        TestRunner.Assert(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).All(r => sid.Equals(r.IdentityReference)));
        TestRunner.Assert(!File.Exists(Path.Combine(parent, "rclone.conf")));
    }

    private static void WriteOffVerifiedZero()
    {
        using var f = new Unit3aTests.RcFixture();
        DbxConfig config = f.Config.Load(); config = config with { WriteAllowedRemotes = [config.Remote] }; f.Config.Save(config);
        Directory.CreateDirectory(config.MountPoint); Directory.CreateDirectory(config.CacheDir);
        int pid = f.State.Load()!.RclonePid;
        RuntimeSnapshots.Save(f.State, f.State.Load()! with { ActiveConfig = config, RecoveryRequired = false, StopDisposition = StopDisposition.Running });
        bool stopped = false;
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
        DbxConfig saved = new WriteAccessSettings(f.Controller).SetEnabled(config, false, false);
        TestRunner.Assert(stopped && saved.WriteAllowedRemotes.Length == 0);
        TestRunner.Assert(!f.State.Load()!.RecoveryRequired && !f.Controller.Status().Running);
    }
}
