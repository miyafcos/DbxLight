using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private static void RunRegisteredCleanup(Action<string, string, Action> test)
    {
        test("E6", "unregistered exact-pattern files survive load login logout and mount with unchanged metadata", UnregisteredTemporaryFiles);
        test("E6", "working and backup locations are registered before file creation and only those paths can scrub", RegisteredBeforeCreation);
        test("E6", "version-one discovered cleanup paths are retained as unregistered files", LegacyDiscoveredPaths);
    }

    private static void CreateUnregistered(string path, int length)
    {
        // This fixture stands for a user-owned copy, never an application transaction.
        string parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) PrivateStateFiles.EnsureParent(path);
        using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.Read,
            4096, FileOptions.WriteThrough, PrivateStateFiles.FileSecurity());
        file.Write(Encoding.ASCII.GetBytes(new string('u', length))); file.Flush(true);
    }

    private static void UnregisteredTemporaryFiles()
    {
        using var f = new LoginFixture("mount"); SeedA(f);
        string[] keys = [f.Conf, CredentialSwitch.LegacyPath(f.Controller.Config.PathName), CredentialSwitch.SharedLegacyPath(f.Controller.Config.PathName)];
        var files = keys.SelectMany(k => new[] { k + ".work-" + Guid.NewGuid().ToString("N"), k + ".pending-" + Guid.NewGuid().ToString("N") }).ToArray();
        foreach (string path in files) CreateUnregistered(path, 37);
        var stamps = files.ToDictionary(p => p, KeyStamp);
        void Unchanged()
        {
            foreach (string path in files) TestRunner.Equal(stamps[path], KeyStamp(path));
            var listed = f.Controller.Status().UnregisteredCredentialFiles;
            TestRunner.Assert(files.All(listed.Contains));
        }
        _ = f.Controller.Config.Load(); Unchanged();
        using (var session = ReadyLogin(f)) session.Commit();
        Unchanged();
        new AccountSettings(f.Controller).LogoutAsync().GetAwaiter().GetResult();
        Unchanged();
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            f.Controller.Config.Save(f.Controller.Config.Load() with { Remote = "dbxlight:", RcloneConfigPath = f.Conf,
                MountPoint = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable),
                RcPort = ((IPEndPoint)listener.LocalEndpoint).Port });
        }
        RuntimeState? owned = null;
        try { f.Controller.Start(); owned = f.Controller.State.Load(); Unchanged(); f.Controller.Stop(); }
        finally { if (owned is not null) { using var child = ProcessIdentity.Open(owned); if (child is not null) f.Controller.Stop(true); } }
        Unchanged();
        Console.WriteLine("E6 six unregistered exact-pattern files: load/login/logout/start leave lengths, times and ACL unchanged; status lists only their paths.");
    }

    private static void RegisteredBeforeCreation()
    {
        using var f = new LoginFixture(); SeedA(f);
        bool registeredBefore = false, observedCreation = false;
        CredentialSwitch.Checkpoint = stage =>
        {
            if (stage != "temporary-registered") return;
            var registry = PrivateJsonFile.Read<KeyRegistry>(CredentialSwitch.RegistryPath(f.Controller.Config.PathName))!;
            TestRunner.Equal(2, registry.RegisteredPaths.Length);
            TestRunner.Assert(registry.RegisteredPaths.All(p => !File.Exists(p)));
            registeredBefore = true;
        };
        try
        {
            using var change = new PrivateLoginFile(f.Controller.Config, f.Conf, path =>
            {
                TestRunner.Assert(registeredBefore);
                TestRunner.Assert(PrivateJsonFile.Read<KeyRegistry>(CredentialSwitch.RegistryPath(f.Controller.Config.PathName))!.RegisteredPaths.Contains(path));
                observedCreation = true;
            });
        }
        finally { CredentialSwitch.Checkpoint = null; }
        TestRunner.Assert(registeredBefore && observedCreation);
        TestRunner.Equal(0, PrivateJsonFile.Read<KeyRegistry>(CredentialSwitch.RegistryPath(f.Controller.Config.PathName))!.RegisteredPaths.Length);
        string unknown = f.Conf + ".work-" + Guid.NewGuid().ToString("N");
        CreateUnregistered(unknown, 23);
        TestRunner.Throws<PendingUploadsException>(() => CredentialSwitch.Scrub(f.Controller.Config.PathName, unknown));
        TestRunner.Equal(23L, new FileInfo(unknown).Length);
    }

    private static void LegacyDiscoveredPaths()
    {
        using var f = new LoginFixture(); SeedA(f);
        string unknown = f.Conf + ".pending-" + Guid.NewGuid().ToString("N");
        CreateUnregistered(unknown, 41);
        PrivateJsonFile.Write(CredentialSwitch.RegistryPath(f.Controller.Config.PathName), new
            { version = 1, configPath = f.Controller.Config.PathName, keys = new[] { f.Conf }, cleanupPaths = new[] { unknown } });
        _ = f.Controller.Config.Load();
        TestRunner.Equal(41L, new FileInfo(unknown).Length);
        TestRunner.Assert(f.Controller.Status().UnregisteredCredentialFiles.Contains(unknown));
        TestRunner.Assert(f.Controller.Config.Load().WriteAllowedRemotes.SequenceEqual(new[] { "dbxlight:" }));
    }
}
