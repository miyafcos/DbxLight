using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6eTests
{
    private const string OutputMarker = Unit4Tests.Marker;
    private static void RunOutputPrivacy(Action<string, string, Action> test)
    {
        foreach (string scenario in new[] { "poison", "poison-failure", "poison-json" })
            test("E7", "stopped ls/find/stat discard stderr and return only selected stdout fields: " + scenario, () => MetadataOutputPrivacy(scenario));
        test("E7", "comparison and folder parser exceptions never attach provider text", ParserOutputPrivacy);
        test("E7", "detached mount output and legacy log presentation never forward provider text", MountOutputPrivacy);
    }

    private static void NoMarker(string value) => TestRunner.Assert(!value.Contains(OutputMarker, StringComparison.Ordinal), "Provider text crossed the output boundary.");

    private static void MetadataOutputPrivacy(string scenario)
    {
        using var f = new LoginFixture(scenario); f.Seed("sample-existing:", f.Conf);
        string[][] commands = [["ls", "", "--json"], ["find", "sample", "--json"], ["stat", "sample.txt", "--json"], ["ls", ""]];
        foreach (string[] command in commands)
        {
            var captured = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(
                new[] { "--config", f.Controller.Config.PathName, "--state", f.Controller.State.PathName }.Concat(command).ToArray()));
            NoMarker(captured.Output); NoMarker(captured.Error);
            if (scenario == "poison") TestRunner.Equal(0, captured.Exit);
            else TestRunner.Assert(captured.Exit != 0);
            if (command.Contains("--json")) { using var json = JsonDocument.Parse(captured.Output); }
        }
        foreach (string path in Directory.Exists(f.Controller.Config.Load().LogDir) ? Directory.GetFiles(f.Controller.Config.Load().LogDir, "*.log") : [])
            NoMarker(File.ReadAllText(path));
        IReadOnlyList<string> remotes = new RcloneLogin(f.Executable, f.Conf).ListRemotesAsync(false).GetAwaiter().GetResult();
        foreach (string remote in remotes) NoMarker(remote);
    }

    private static void ParserOutputPrivacy()
    {
        using var f = new LoginFixture("poison-json"); f.Seed("sample-existing:", f.Conf);
        foreach (Action action in new Action[] { () => new RecoveryRemoteProbe(f.Controller.Config.Load()).Info(),
            () => FolderBrowser.List(f.Controller.Config.Load()),
            () => RcloneOutput.Json("{" + (char)34 + OutputMarker + (char)34 + ":", "test") })
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is IOException or InvalidDataException) { failed = true; NoMarker(e.Message); NoMarker(e.ToString()); }
            TestRunner.Assert(failed);
        }
    }

    private static void MountOutputPrivacy()
    {
        using var f = new LoginFixture("mount-poison"); f.Seed("sample-existing:", f.Conf);
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            f.Controller.Config.Save(f.Controller.Config.Load() with { MountPoint = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable),
                RcPort = ((IPEndPoint)listener.LocalEndpoint).Port });
        }
        RuntimeState? owned = null;
        try
        {
            var captured = Unit3cTests.Capture(() => { f.Controller.Start(); return 0; });
            NoMarker(captured.Output); NoMarker(captured.Error);
            owned = f.Controller.State.Load();
            NoMarker(JsonSerializer.Serialize(f.Controller.Status(), JsonFiles.Options));
            foreach (string path in Directory.GetFiles(f.Controller.Config.Load().LogDir, "*.log")) NoMarker(File.ReadAllText(path));
            TestRunner.Assert(f.Calls().Where(c => c.Args[0] == "mount").All(c => c.Args.Contains("--log-file") && c.Args.Contains("INFO")));
            string log = Path.Combine(f.Controller.Config.Load().LogDir, "mount-old-provider.log");
            File.WriteAllText(log, "ERROR " + OutputMarker + "\n", new UTF8Encoding(false)); // Legacy input is retained unchanged.
            TraySnapshot snapshot = SnapshotReader.Read(f.Controller);
            foreach (string error in snapshot.Errors) NoMarker(error);
            FormTests.Sta(() =>
            {
                using var form = new StatusForm(); form.UpdateSnapshot(snapshot, null, f.Controller.Config.Load());
                IEnumerable<Control> Texts(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Texts(c)));
                foreach (Control control in Texts(form)) NoMarker(control.Text);
            });
            f.Controller.Stop();
        }
        finally { if (owned is not null) { using var child = ProcessIdentity.Open(owned); if (child is not null) f.Controller.Stop(true); } }
        Console.WriteLine("E7 stdout/stderr/JSON/exceptions/application logs/form text contain no synthetic key marker; selected metadata remains available.");
    }
}
