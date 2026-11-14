using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6fTests
{
    internal static void Run(TestRunner runner, string? issue = null)
    {
        void Test(string id, string name, Action action)
        { if (issue is null || issue == id) runner.Test(id + ": " + name, action); }
        foreach (string kind in new[] { "exited-running", "exited-stopped", "reused", "live" })
            Test("G2", "ed98be1 state and owner pair upgrades only after confirmed exit: " + kind, () => LegacyOwnerPair(kind));
        Test("G2", "same-parent A and B keep both legacy key placements unchanged through start and relogin", LegacySharedKeys);
        Test("G2", "old registry and transaction entries cannot modify legacy external key files", LegacyMutationAuthority);
        RunNames(Test);
        RunRoundTrips(Test);
        RunCompletion(Test);
        RunNotices(Test);
        RunLogs(Test);
        RunPreparation(Test);
        foreach (bool changed in new[] { false, true }) Test("G2", "modern pending cache using the legacy conf path is not blocked by migration: changed=" + changed, () => ModernLegacyPending(changed));
        Test("G7", "write warning has four complete paragraphs at 100, 125 and 150 percent", WriteWarning);
    }

    private static void LegacyOwnerPair(string kind)
    {
        using var f = new Unit5Fixture("u6f-upgrade-" + kind, writable: false);
        using var self = Process.GetCurrentProcess();
        int pid = self.Id; DateTime started = self.StartTime.ToUniversalTime(); string image = Environment.ProcessPath!;
        if (kind.StartsWith("exited", StringComparison.Ordinal))
        {
            using var child = Process.Start(RcloneArguments.ProcessInfo(Environment.ProcessPath!, ["--unit6f-exit"]))!;
            pid = child.Id; started = child.StartTime.ToUniversalTime(); image = ProcessIdentity.ImagePath(child);
            TestRunner.Assert(child.WaitForExit(10000));
        }
        else if (kind == "reused") started = started.AddMinutes(-1);
        string fixtures = Path.Combine(UnitTests.RepositoryRoot(), "_spec", "fixtures");
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "ed98be1_state_redacted.json")))!.AsObject();
        var owner = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "ed98be1_owner_redacted.json")))!.AsObject();
        state["rclonePid"] = pid; state["processStartedUtc"] = started; state["executablePath"] = image;
        state["configPath"] = f.Store.PathName; state["activeConfig"] = JsonSerializer.SerializeToNode(f.Config, JsonFiles.Options);
        state["supervisorPid"] = null; state["supervisorStartedUtc"] = null; state["supervisorExecutablePath"] = null;
        if (kind == "exited-stopped")
        {
            // ed98be1 Stop writes these fields; its cache owner remains the old PID/start/image.
            state["stopRequested"] = true; state["stoppedUtc"] = DateTime.UtcNow;
            state["stopDisposition"] = "Clean"; state["cacheInspection"] = "clean"; state["recoveryRequired"] = false;
        }
        owner["pid"] = pid; owner["processStartedUtc"] = started; owner["executablePath"] = image; owner["statePath"] = f.State.PathName;
        f.State.EnsurePrivateDirectory(); PrivateJsonFile.Write(f.State.PathName, state);
        Directory.CreateDirectory(f.Config.CacheDir);
        string ownerPath = Path.Combine(f.Config.CacheDir, "dbxl-owner.json"); JsonFiles.Write(ownerPath, owner);
        if (kind == "live")
        {
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.Start());
            TestRunner.Throws<PendingUploadsException>(() => f.Controller.PrepareConnectionChange(f.Store.Load()));
            TestRunner.Throws<PendingUploadsException>(() => new WriteAccessSettings(f.Controller).SetEnabled(f.Store.Load(), true, true));
            TestRunner.Throws<PendingUploadsException>(() => new AccountSettings(f.Controller).LogoutAsync().GetAwaiter().GetResult());
            TestRunner.Equal(0, JsonFiles.Read<CacheOwner>(ownerPath).IdentityVersion);
            return;
        }
        f.Controller.PrepareConnectionChange(f.Store.Load());
        TestRunner.Equal(1, JsonFiles.Read<CacheOwner>(ownerPath).IdentityVersion);
        f.Start(); TestRunner.Assert(f.Controller.Status() is { Running: true, Ready: true, ReadOnly: true });
        f.Controller.Stop();
        new WriteAccessSettings(f.Controller).SetEnabled(f.Store.Load(), true, true);
        TestRunner.Assert(f.Store.Load().WriteAllowedRemotes.Contains(f.Config.Remote));
    }

    private static (long Length, DateTime Created, DateTime Written) Stamp(string path) =>
        (new FileInfo(path).Length, File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));

    private static void LegacySharedKeys()
    {
        using var f = new LoginFixture("mount");
        f.Seed("dbxlight:", null);
        string[] legacy = [CredentialSwitch.LegacyPath(f.Controller.Config.PathName), CredentialSwitch.SharedLegacyPath(f.Controller.Config.PathName)];
        foreach (string path in legacy) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, new string('s', 19)); }
        var stamps = legacy.Select(Stamp).ToArray();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var a = f.Controller.Config.Load() with { Remote = "dbxlight:", RcloneConfigPath = legacy[1],
            MountPoint = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable), RcPort = port, WriteAllowedRemotes = [] };
        f.Controller.Config.Save(a);
        var bStore = new ConfigStore(Path.Combine(f.Root, "config-B.json"));
        bStore.Save(a with { CacheDir = Path.Combine(f.Root, "cache-B"), RescueDir = Path.Combine(f.Root, "rescue-B") });
        var b = new MountController(bStore, new StateStore(Path.Combine(f.Root, "runtime-B", "state.json")));
        foreach (var controller in new[] { f.Controller, b })
        {
            try
            {
                controller.Start(); TestRunner.Assert(controller.Status().Ready); controller.Stop();
                using var session = new SetupSession(controller);
                session.CheckRequirements(); TestRunner.Assert(session.Next());
                TestRunner.Assert(session.AuthorizeAsync(true).GetAwaiter().GetResult().Succeeded);
                session.SelectScopeAsync(false).GetAwaiter().GetResult(); TestRunner.Assert(session.Next());
                session.SetOptions(a.MountPoint, "1h"); TestRunner.Assert(session.Next());
                var saved = session.Commit();
                TestRunner.Equal(CredentialSwitch.KeyPath(controller.Config.PathName), saved.RcloneConfigPath);
                TestRunner.Equal(0, saved.WriteAllowedRemotes.Length);
                TestRunner.Assert(CredentialSwitch.UnregisteredFiles(controller.Config.PathName).Contains(legacy[1]));
                for (int i = 0; i < legacy.Length; i++) TestRunner.Equal(stamps[i], Stamp(legacy[i]));
            }
            finally { controller.Stop(true); }
        }
    }
}
