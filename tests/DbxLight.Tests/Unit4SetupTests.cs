using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit4Tests
{
    private static void ScopeChoices()
    {
        using var f = new LoginFixture(); f.Seed();
        using var session = f.AtLogin();
        TestRunner.Assert(session.AuthorizeAsync(false).GetAwaiter().GetResult().Succeeded);
        session.SelectScopeAsync(false).GetAwaiter().GetResult();
        TestRunner.Assert(session.ScopeVerified && !session.TeamRoot); TestRunner.Equal("dbxlight:", session.Draft.Remote);
        TestRunner.Equal(2, session.Folders.Count);
        session.SelectScopeAsync(true).GetAwaiter().GetResult();
        TestRunner.Assert(session.ScopeVerified && session.TeamRoot); TestRunner.Equal("dbxlight:/", session.Draft.Remote);
        TestRunner.Assert(f.Calls().Any(c => c.Args.Contains("dbxlight:/")));
        TestRunner.Assert(f.Calls().Where(c => c.Args[0] == "lsjson").All(c => c.Args.Contains(session.BrowsingConfig.RcloneConfigPath!)));
    }

    private static void TeamFallback()
    {
        using var f = new LoginFixture("team-failure"); f.Seed();
        using var session = f.AtLogin();
        session.AuthorizeAsync(false).GetAwaiter().GetResult();
        session.SelectScopeAsync(true).GetAwaiter().GetResult();
        TestRunner.Assert(session.ScopeVerified && !session.TeamRoot);
        TestRunner.Equal("dbxlight:", session.Draft.Remote);
        TestRunner.Assert(session.Message.Contains("戻しました") && !session.Message.Contains(Marker));
        f.Scenario("listing-failure");
        session.SelectScopeAsync(false).GetAwaiter().GetResult();
        TestRunner.Assert(!session.ScopeVerified && !session.Next());
        TestRunner.Assert(!session.Message.Contains(Marker));
        try { FolderBrowser.List(session.Draft); throw new InvalidOperationException("Expected listing failure."); }
        catch (IOException e) { TestRunner.Assert(!e.ToString().Contains(Marker)); }
    }

    private static void NavigationRollback()
    {
        using var f = new LoginFixture(); f.Seed();
        string before = File.ReadAllText(f.Controller.Config.PathName);
        using (var session = f.AtLogin())
        {
            session.AuthorizeAsync(false).GetAwaiter().GetResult();
            TestRunner.Assert(!session.Next());
            session.SelectScopeAsync(false).GetAwaiter().GetResult();
            TestRunner.Assert(session.Next()); TestRunner.Equal(SetupStep.Options, session.Step);
            session.SetOptions("U:", "3h"); session.SetHidden(["02 共有"]);
            TestRunner.Assert(session.Next()); TestRunner.Equal(SetupStep.Finish, session.Step);
            session.Back(); TestRunner.Equal(SetupStep.Options, session.Step);
            TestRunner.Equal("U:", session.Draft.MountPoint);
            session.Back(); TestRunner.Equal(SetupStep.Scope, session.Step);
            session.Back(); TestRunner.Equal(SetupStep.Login, session.Step);
            TestRunner.Equal(before, File.ReadAllText(f.Controller.Config.PathName));
        }
        TestRunner.Equal(before, File.ReadAllText(f.Controller.Config.PathName));
        TestRunner.Assert(!File.Exists(f.Conf));
    }

    private static SetupSession AtFinish(LoginFixture f)
    {
        var session = f.AtLogin(); session.AuthorizeAsync(false).GetAwaiter().GetResult();
        session.SelectScopeAsync(false).GetAwaiter().GetResult(); TestRunner.Assert(session.Next());
        session.SetOptions("T:", "1d"); session.SetHidden(["02 共有"]); TestRunner.Assert(session.Next());
        return session;
    }

    private static void CommitReadOnly()
    {
        using var f = new LoginFixture(); f.Seed();
        using var session = AtFinish(f);
        DbxConfig saved = session.Commit();
        TestRunner.Assert(session.Committed);
        TestRunner.Equal("dbxlight:", saved.Remote); TestRunner.Equal(f.Conf, saved.RcloneConfigPath);
        TestRunner.Equal("1d", saved.Retention); TestRunner.Equal("02 共有", saved.HiddenPaths.Single());
        TestRunner.Equal(0, saved.WriteAllowedRemotes.Length);
        TestRunner.Assert(RcloneArguments.Mount(saved, Path.Combine(f.Root, "hidden.filter")).Contains("--read-only"));
        TestRunner.Assert(File.ReadAllText(f.Conf).Contains(Marker));
        TestRunner.Assert(!File.ReadAllText(f.Controller.Config.PathName).Contains(Marker));
    }

    private static void ExistingSelection()
    {
        using var f = new LoginFixture(); f.Seed();
        using var session = f.AtLogin();
        TestRunner.Throws<ArgumentException>(() => session.UseExistingRemoteAsync("not-listed:").GetAwaiter().GetResult());
        session.UseExistingRemoteAsync("sample-existing:").GetAwaiter().GetResult();
        session.SelectScopeAsync(false).GetAwaiter().GetResult();
        TestRunner.Equal("sample-existing:", session.Draft.Remote);
        TestRunner.Assert(session.Draft.RcloneConfigPath is null);
        TestRunner.Assert(!File.Exists(f.Conf));
        TestRunner.Assert(f.Calls().All(c => !c.Args.Contains("--config")));
        session.Next(); session.SetOptions("T:", "1h"); session.Next(); session.Commit();
        TestRunner.Assert(f.Controller.Config.Load().RcloneConfigPath is null);
    }

    private static void OverwriteConfirmation()
    {
        using var f = new LoginFixture(); f.Seed();
        PrivateLoginFile.Empty(f.Conf);
        const string original = "[dbxlight]\ntype = local\n";
        File.WriteAllText(f.Conf, original, new UTF8Encoding(false));
        using (var session = f.AtLogin())
        {
            TestRunner.Assert(session.NeedsOverwriteAsync().GetAwaiter().GetResult());
            TestRunner.Assert(!session.AuthorizeAsync(false).GetAwaiter().GetResult().Succeeded);
            TestRunner.Equal(original, File.ReadAllText(f.Conf));
            TestRunner.Assert(!f.Calls().Any(c => c.Args.Contains("create")));
            TestRunner.Assert(session.AuthorizeAsync(true).GetAwaiter().GetResult().Succeeded);
        }
        TestRunner.Equal(original, File.ReadAllText(f.Conf));
    }

    private static void SetupRecovery()
    {
        using var f = new LoginFixture(); f.Seed("previous:");
        DbxConfig original = f.Controller.Config.Load();
        f.MarkPendingCache();
        var recovery = new RuntimeState { RclonePid = int.MaxValue, ProcessStartedUtc = DateTime.UnixEpoch, ExecutablePath = f.Executable,
            ConfigPath = f.Controller.Config.PathName, ActiveConfig = original, StopDisposition = StopDisposition.Forced, RecoveryRequired = true };
        RuntimeSnapshots.Save(f.Controller.State, recovery);
        using var session = f.AtLogin();
        foreach (Action action in new Action[] {
            () => session.AuthorizeAsync(true).GetAwaiter().GetResult(),
            () => session.UseExistingRemoteAsync("sample-existing:").GetAwaiter().GetResult()
        })
        {
            try { action(); throw new InvalidOperationException("Expected recovery refusal."); }
            catch (PendingUploadsException e) { TestRunner.Assert(e.Message.Contains("回復がまだです")); }
        }
        TestRunner.Assert(!f.Calls().Any(c => c.Args.Contains("create")));
        TestRunner.Equal("previous:", f.Controller.Config.Load().Remote);
    }

    private static void FinalGuardAndLease()
    {
        using var f = new LoginFixture(); f.Seed("previous:", f.Conf);
        using var session = AtFinish(f);
        TestRunner.Throws<InvalidOperationException>(() => f.Controller.Start());
        TestRunner.Assert(!f.Calls().Any(c => c.Args.Contains("mount")));
        f.MarkPendingCache();
        RuntimeSnapshots.Save(f.Controller.State, new RuntimeState { RclonePid = int.MaxValue, ProcessStartedUtc = DateTime.UnixEpoch,
            ExecutablePath = f.Executable, ConfigPath = f.Controller.Config.PathName, ActiveConfig = session.Original,
            RecoveryRequired = true, StopDisposition = StopDisposition.Forced });
        TestRunner.Throws<PendingUploadsException>(() => session.Commit());
        TestRunner.Equal("previous:", f.Controller.Config.Load().Remote);
    }
}
