using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private static void RunPerConfigKeys(Action<string, string, Action> test)
    {
        test("E5", "same-parent configurations keep B key and write grant unchanged through A login and logout", PerConfigKeys);
        foreach (string stage in new[] { "switch-conf", "switch-installed" })
            test("E5", "same-parent B key stays unchanged through A crash at " + stage, () => PerConfigCrash(stage));
    }

    // Key material is deliberately never read or hashed. Compare metadata and
    // the nonsecret application configuration, with distinct synthetic key lengths.
    private static (long Length, DateTime Created, DateTime Written, string Acl) KeyStamp(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc,
            file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    private static (ConfigStore Store, string Key) SeedB(LoginFixture f)
    {
        string configPath = Path.Combine(f.Root, "config-B.json");
        string key = CredentialSwitch.KeyPath(configPath);
        var config = f.Controller.Config.Load() with { RcloneConfigPath = key, Remote = "dbxlight:", WriteAllowedRemotes = ["dbxlight:"],
            CacheDir = Path.Combine(f.Root, "cache-B"), RescueDir = Path.Combine(f.Root, "rescue-B"), LogDir = Path.Combine(f.Root, "logs-B") };
        var store = new ConfigStore(configPath); store.Save(config);
        PrivateLoginFile.Empty(key); File.WriteAllText(key, new string('b', 29), new UTF8Encoding(false));
        return (store, key);
    }

    private static void SeedA(LoginFixture f)
    {
        f.Seed("dbxlight:", f.Conf);
        f.Controller.Config.Save(f.Controller.Config.Load() with { WriteAllowedRemotes = ["dbxlight:"] });
        PrivateLoginFile.Empty(f.Conf); File.WriteAllText(f.Conf, new string('a', 19), new UTF8Encoding(false));
    }

    private static SetupSession ReadyLogin(LoginFixture f, string? requestedPath = null)
    {
        var session = new SetupSession(f.Controller, requestedPath);
        session.CheckRequirements(); TestRunner.Assert(session.Next());
        TestRunner.Assert(session.AuthorizeAsync(true).GetAwaiter().GetResult().Succeeded);
        session.SelectScopeAsync(false).GetAwaiter().GetResult(); TestRunner.Assert(session.Next());
        session.SetOptions(f.Controller.Config.Load().MountPoint, "1h"); TestRunner.Assert(session.Next());
        return session;
    }

    private static void AssertB(ConfigStore store, string key, byte[] config, (long Length, DateTime Created, DateTime Written, string Acl) stamp)
    {
        TestRunner.Assert(config.AsSpan().SequenceEqual(File.ReadAllBytes(store.PathName)));
        TestRunner.Equal(stamp, KeyStamp(key));
        DbxConfig b = store.Load();
        TestRunner.Equal(key, b.RcloneConfigPath);
        TestRunner.Assert(b.WriteAllowedRemotes.SequenceEqual(new[] { "dbxlight:" }));
    }

    private static void PerConfigKeys()
    {
        using var f = new LoginFixture(); SeedA(f);
        var b = SeedB(f); var stamp = KeyStamp(b.Key); byte[] config = File.ReadAllBytes(b.Store.PathName);
        TestRunner.Assert(!RescuePaths.Same(f.Conf, b.Key) && Path.GetDirectoryName(f.Conf) != Path.GetDirectoryName(b.Key));
        using (var session = ReadyLogin(f, b.Key)) // Even a supplied login path cannot share B's key.
        {
            TestRunner.Assert(session.BrowsingConfig.RcloneConfigPath!.StartsWith(f.Conf + ".work-", StringComparison.Ordinal));
            DbxConfig a = session.Commit();
            TestRunner.Equal(f.Conf, a.RcloneConfigPath); TestRunner.Equal(0, a.WriteAllowedRemotes.Length);
        }
        AssertB(b.Store, b.Key, config, stamp);
        new AccountSettings(f.Controller, b.Key).LogoutAsync().GetAwaiter().GetResult();
        AssertB(b.Store, b.Key, config, stamp);
        TestRunner.Equal(0L, new FileInfo(f.Conf).Length);
        Console.WriteLine("E5 A/B same parent: distinct config-path-derived key folders; A login/logout leave B metadata, settings and write grant unchanged.");
    }

    private static void PerConfigCrash(string stage)
    {
        using var f = new LoginFixture(); SeedA(f);
        var b = SeedB(f); var stamp = KeyStamp(b.Key); byte[] config = File.ReadAllBytes(b.Store.PathName);
        using var parent = Process.Start(RcloneArguments.ProcessInfo(Environment.ProcessPath!,
            ["--unit6d-key-child", f.Controller.Config.PathName, f.Controller.State.PathName, stage]))!;
        Task<string> output = parent.StandardOutput.ReadToEndAsync(), error = parent.StandardError.ReadToEndAsync();
        try
        {
            Unit5Fixture.Wait(() => File.Exists(Path.Combine(f.Root, "key-reached.json")), "A key switch checkpoint missing.");
            parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000));
            DbxConfig a = f.Controller.Config.Load();
            TestRunner.Assert(new FileInfo(f.Conf).Length == 19 || a.WriteAllowedRemotes.Length == 0);
            AssertB(b.Store, b.Key, config, stamp);
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); TestRunner.Assert(parent.WaitForExit(10000)); }
            _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
        }
    }

}
