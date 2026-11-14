using System.Diagnostics;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit4Tests
{
    private static void WriteConfirmationRequired()
    {
        using var f = new LoginFixture(); f.Seed("sample-existing:");
        string before = File.ReadAllText(f.Controller.Config.PathName);
        TestRunner.Throws<InvalidOperationException>(() => new WriteAccessSettings(f.Controller).SetEnabled(f.Controller.Config.Load(), true, false));
        TestRunner.Equal(before, File.ReadAllText(f.Controller.Config.PathName));
        TestRunner.Equal(0, f.Calls().Length);
    }

    private static void WriteExactAndGuide()
    {
        using var f = new LoginFixture(); f.Seed("sample-existing:");
        var access = new WriteAccessSettings(f.Controller);
        DbxConfig enabled = access.SetEnabled(f.Controller.Config.Load(), true, true);
        TestRunner.Assert(enabled.WriteAllowedRemotes.SequenceEqual(new[] { "sample-existing:" }));
        TestRunner.Assert(!RcloneArguments.Mount(enabled, Path.Combine(f.Root, "hidden.filter")).Contains("--read-only"));
        AssertGuide(f, false);
        DbxConfig disabled = access.SetEnabled(enabled, false, false);
        TestRunner.Equal(0, disabled.WriteAllowedRemotes.Length);
        TestRunner.Assert(RcloneArguments.Mount(disabled, Path.Combine(f.Root, "hidden.filter")).Contains("--read-only"));
        AssertGuide(f, true);
    }

    private static void AssertGuide(LoginFixture f, bool readOnly)
    {
        string exe = Path.Combine(UnitTests.RepositoryRoot(), "src", "DbxLight.Cli", "bin", "Release", "net8.0-windows", "dbxl.exe");
        using var process = Process.Start(RcloneArguments.ProcessInfo(exe,
            ["--config", f.Controller.Config.PathName, "--state", f.Controller.State.PathName, "agent-guide", "--json"]))!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        TestRunner.Assert(process.WaitForExit(30000)); TestRunner.Equal(0, process.ExitCode);
        string text = output.GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(text);
        TestRunner.Equal(readOnly, doc.RootElement.GetProperty("readOnly").GetBoolean());
        TestRunner.Assert(doc.RootElement.GetProperty("markdown").GetString()!.Contains(readOnly ? "読み取り専用" : "書き込み可"));
        TestRunner.Assert(!text.Contains(Marker) && !errors.GetAwaiter().GetResult().Contains(Marker));
        Console.WriteLine("dbxl agent-guide --json after switch: readOnly=" + readOnly + "; exit=0");
    }

    private static void WriteOffRefusals()
    {
        foreach (bool unknown in new[] { false, true })
        {
            using var f = new Unit3aTests.RcFixture();
            DbxConfig config = f.Config.Load(); config = config with { WriteAllowedRemotes = [config.Remote] }; f.Config.Save(config);
            Directory.CreateDirectory(config.MountPoint); Directory.CreateDirectory(config.CacheDir);
            int pid = f.State.Load()!.RclonePid;
            RuntimeSnapshots.Save(f.State, f.State.Load()! with { ActiveConfig = config, RecoveryRequired = false, StopDisposition = StopDisposition.Running });
            f.Respond = path => path == "/vfs/stats"
                ? unknown ? (503, "{}") : (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":1,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}")
                : path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, $"{{\"pid\":{pid}}}");
            TestRunner.Throws<PendingUploadsException>(() => new WriteAccessSettings(f.Controller).SetEnabled(config, false, false));
            TestRunner.Assert(f.Config.Load().WriteAllowedRemotes.Contains(config.Remote));
            using var alive = ProcessIdentity.Open(f.State.Load()!); TestRunner.Assert(alive is not null);
        }
    }

    private static void WriteRecoveryRefusal()
    {
        using var f = new LoginFixture(); f.Seed("sample-existing:");
        DbxConfig config = f.Controller.Config.Load();
        f.MarkPendingCache();
        RuntimeSnapshots.Save(f.Controller.State, new RuntimeState { RclonePid = int.MaxValue, ProcessStartedUtc = DateTime.UnixEpoch,
            ExecutablePath = f.Executable, ConfigPath = f.Controller.Config.PathName, ActiveConfig = config,
            RecoveryRequired = true, StopDisposition = StopDisposition.Forced });
        foreach (bool enabled in new[] { true, false })
            TestRunner.Throws<PendingUploadsException>(() => new WriteAccessSettings(f.Controller).SetEnabled(config, enabled, true));
        TestRunner.Assert(!f.Controller.Config.Load().WriteAllowedRemotes.Contains(config.Remote));
    }

    private static void WriteStaleConfirmation()
    {
        using var f = new LoginFixture(); f.Seed("sample-existing:");
        DbxConfig confirmed = f.Controller.Config.Load();
        f.Controller.Config.Save(confirmed with { Remote = "changed:" });
        TestRunner.Throws<InvalidOperationException>(() => new WriteAccessSettings(f.Controller).SetEnabled(confirmed, true, true));
        TestRunner.Assert(!f.Controller.Config.Load().WriteAllowedRemotes.Contains("changed:"));
    }

    private static void WriteConfirmationUi() => FormTests.Sta(() =>
    {
        using var form = new WriteConfirmationForm("dbxlight:");
        string text = string.Join("\n", Controls(form).Select(c => c.Text));
        foreach (string expected in new[] { "少し遅れて", "取り出して残し", "30 秒以内", "競合コピーを作りません", "先に保存した人の変更が失われる", "自分で戻してください", "公式の Dropbox アプリ", "同時" }) TestRunner.Assert(text.Contains(expected));
        TestRunner.Equal(DialogResult.None, form.DialogResult);
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-20000, -20000);
        form.Show();
        Controls(form).OfType<Button>().Single(b => b.Text.StartsWith("確認して", StringComparison.Ordinal)).PerformClick();
        TestRunner.Equal(DialogResult.OK, form.DialogResult);
    });

    private static IEnumerable<Control> Controls(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Controls(child)) yield return nested; }
    }
}
