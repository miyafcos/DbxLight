using System.Security.Cryptography;
using DbxLight.Core;
using DbxLight.Tray;
namespace DbxLight.Tests;

internal static partial class Unit6aTests
{
    private static void PendingAccountGuards()
    {
        using var f = new Unit5Fixture("u6a-account-guards", writeBack: "5m");
        SavePending(f, true);
        string configuration = File.ReadAllText(f.Store.PathName);
        byte[] connection = File.ReadAllBytes(f.Config.RcloneConfigPath!);
        File.Move(f.State.PathName, Path.Combine(f.Root, "state-removed.saved"));
        string loginConfig = Path.Combine(f.Root, "private-login", "rclone.conf");
        using var session = new SetupSession(f.Controller, loginConfig);
        session.CheckRequirements(); TestRunner.Assert(session.Next());
        TestRunner.Throws<PendingUploadsException>(() => session.AuthorizeAsync(true).GetAwaiter().GetResult());
        TestRunner.Throws<PendingUploadsException>(() => new AccountSettings(f.Controller, loginConfig).LogoutAsync().GetAwaiter().GetResult());
        TestRunner.Equal(configuration, File.ReadAllText(f.Store.PathName));
        TestRunner.Assert(connection.AsSpan().SequenceEqual(File.ReadAllBytes(f.Config.RcloneConfigPath!)));
        TestRunner.Assert(!File.Exists(loginConfig));
        TestRunner.Assert(RecoveryDestination.Explain(f.State.Load()!).Contains("\u5225\u306e\u30a2\u30ab\u30a6\u30f3\u30c8", StringComparison.Ordinal));
        Console.WriteLine("P8 pending + missing state: reconnect and logout refused before authorization; both configuration files unchanged.");
    }
}
