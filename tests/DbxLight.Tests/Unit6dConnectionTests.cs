using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6dTests
{
    private static void LegacyConnectionCache(string kind)
    {
        foreach (string operation in new[] { "login", "logout", "write-on", "write-off", "remote" })
        {
            using var f = new LoginFixture();
            f.Seed("dbxlight:", f.Conf);
            DbxConfig config = f.Controller.Config.Load() with { WriteAllowedRemotes = operation == "write-off" ? ["dbxlight:"] : [] };
            f.Controller.Config.Save(config);
            string body = Path.Combine(config.CacheDir, "vfs", "sample", "saved.txt");
            string meta = Path.Combine(config.CacheDir, "vfsMeta", "sample", "saved.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(body)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
            File.WriteAllText(body, "x", new UTF8Encoding(false));
            if (kind != "one-sided")
                File.WriteAllText(meta, "{\"ModTime\":\"2026-01-01T00:00:00Z\",\"ATime\":\"2026-01-01T00:00:00Z\",\"Size\":1,\"Rs\":[{\"Pos\":0,\"Size\":1}],\"Fingerprint\":\"\",\"Dirty\":" +
                    (kind == "pending" ? "true" : "false") + "}", new UTF8Encoding(false));
            TestRunner.Assert(!File.Exists(f.Controller.State.PathName) && !File.Exists(RecoveryJournal.CachePath(config.CacheDir)) &&
                !File.Exists(RecoveryJournal.ConfigPath(f.Controller.Config.PathName)));
            byte[] before = File.ReadAllBytes(f.Controller.Config.PathName);
            void Change()
            {
                switch (operation)
                {
                    case "login":
                        using (var session = f.AtLogin()) TestRunner.Assert(session.AuthorizeAsync(false).GetAwaiter().GetResult().Succeeded);
                        break;
                    case "logout": new AccountSettings(f.Controller, f.Conf).LogoutAsync().GetAwaiter().GetResult(); break;
                    case "write-on": new WriteAccessSettings(f.Controller).SetEnabled(config, true, true); break;
                    case "write-off": new WriteAccessSettings(f.Controller).SetEnabled(config, false, false); break;
                    default: f.Controller.UpdateConfig(c => c with { Remote = "changed:" }); break;
                }
            }
            if (kind == "clean") Change();
            else
            {
                TestRunner.Throws<PendingUploadsException>(Change);
                TestRunner.Assert(before.AsSpan().SequenceEqual(File.ReadAllBytes(f.Controller.Config.PathName)));
                TestRunner.Equal(0, f.Calls().Length);
                TestRunner.Assert(!File.Exists(f.Conf));
                TestRunner.Equal("x", File.ReadAllText(body));
            }
        }
        Console.WriteLine("S5 legacy " + kind + ": login/logout/write-on/write-off/remote " + (kind == "clean" ? "allowed" : "refused") + " without state or journals.");
    }
    private static void ConnectionStorageMarkers()
    {
        using var f = new Unit5Fixture("u6d-S5-markers");
        Directory.CreateDirectory(f.Config.CacheDir);
        string owner = Path.Combine(f.Config.CacheDir, "dbxl-owner.json");
        JsonFiles.Write(owner, new CacheOwner(0, default, "", f.State.PathName));
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.PrepareConnectionChange(f.Config));
        JsonFiles.Write(owner, new CacheOwner(0, default, "", f.State.PathName, LaunchPending: true, IdentityVersion: 1));
        TestRunner.Throws<PendingUploadsException>(() => f.Controller.PrepareConnectionChange(f.Config));
        JsonFiles.Write(owner, new CacheOwner(0, default, "", f.State.PathName, IdentityVersion: 1));
        f.Controller.PrepareConnectionChange(f.Config);
        MakeRescue(f.Config);
        f.Controller.PrepareConnectionChange(f.Config);
    }
    private static void FirstConnectionStorage()
    {
        using var f = new LoginFixture();
        TestRunner.Assert(!Directory.Exists(f.Controller.Config.Load().CacheDir));
        f.Controller.PrepareConnectionChange(f.Controller.Config.Load());
        Directory.CreateDirectory(f.Controller.Config.Load().CacheDir);
        f.Controller.PrepareConnectionChange(f.Controller.Config.Load());
        TestRunner.Assert(!File.Exists(f.Controller.Config.PathName));
    }
}
