using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static class TrayTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
    public static void Run(TestRunner runner)
    {
        runner.Test("tray tree 01: unchecked folder is hidden", () =>
        {
            var s = new HiddenSelection([]); s.SetShown("a", false); Paths(s, "a"); Diff(s, "a", "");
        });
        runner.Test("tray tree 02: checked folder returns", () =>
        {
            var s = new HiddenSelection(["a"]); s.SetShown("a", true); Paths(s, ""); Diff(s, "", "a");
        });
        runner.Test("tray tree 03: hidden parent covers loaded and unloaded children", () =>
        {
            var s = new HiddenSelection([]); s.SetShown("a", false);
            TestRunner.Assert(!s.IsShown("a/child") && !s.IsShown("a/unloaded/deep")); Diff(s, "a", "");
        });
        runner.Test("tray tree 04: child hidden then parent hidden is one effective change", () =>
        {
            var s = new HiddenSelection(["a/child"]); s.SetShown("a", false); Paths(s, "a"); Diff(s, "a", "");
        });
        runner.Test("tray tree 05: child cannot return beneath a hidden parent", () =>
        {
            var s = new HiddenSelection(["a"]);
            TestRunner.Throws<ArgumentException>(() => s.SetShown("a/child", true)); Paths(s, "a"); Diff(s, "", "");
        });
        runner.Test("tray tree 06: checking parent returns its whole subtree", () =>
        {
            var s = new HiddenSelection(["a/one", "a/two"]); s.SetShown("a", true); Paths(s, ""); Diff(s, "", "a/one|a/two");
        });
        runner.Test("tray tree 07: toggling twice cancels the change", () =>
        {
            var s = new HiddenSelection([]); s.SetShown("a", false); s.SetShown("a", true); Diff(s, "", "");
        });
        runner.Test("tray tree 08: independent siblings give both diff sets", () =>
        {
            var s = new HiddenSelection(["a", "b"]); s.SetShown("a", true); s.SetShown("c", false);
            Paths(s, "b|c"); Diff(s, "c", "a");
        });
        runner.Test("tray tree 09: a path prefix without a separator is not a parent", () =>
        {
            var s = new HiddenSelection(["a"]); TestRunner.Assert(s.IsShown("ab") && !s.IsShown("a/b"));
        });
        runner.Test("tray tree 10: case and Unicode normalization", () =>
        {
            var s = new HiddenSelection(["cafe\u0301", "A"]); TestRunner.Assert(!s.IsShown("caf\u00e9/child"));
            s.SetShown("a", true); Diff(s, "", "A");
        });
        runner.Test("tray tree 11: accept resets diff and detects stale source", () =>
        {
            var s = new HiddenSelection(["a"]); TestRunner.Assert(s.Matches(["A"]) && !s.Matches(["b"]));
            s.SetShown("b", false); s.Accept(); Diff(s, "", "");
        });
        runner.Test("tray tree 12: selecting a parent after hiding it restores original hidden children too", () =>
        {
            var s = new HiddenSelection(["a/child"]); s.SetShown("a", false); s.SetShown("a", true); Diff(s, "", "a/child");
        });
        runner.Test("tray age: existing startup queue is marked and times from first observation", () =>
        {
            var t = new QueueAgeTracker(); t.Observe(100, [Item(1)], Epoch);
            QueueAge age = t.Oldest(Epoch.AddMinutes(7))!; TestRunner.Assert(age.PresentAtStartup);
            TestRunner.Equal(TimeSpan.FromMinutes(7), age.Elapsed);
            t.Observe(100, [Item(1)], Epoch.AddMinutes(8));
            TestRunner.Equal(TimeSpan.FromMinutes(9), t.Oldest(Epoch.AddMinutes(9))!.Elapsed);
        });
        runner.Test("tray age: post-start item is new and disappeared ids are removed", () =>
        {
            var t = new QueueAgeTracker(); t.Observe(100, [], Epoch); t.Observe(100, [Item(2)], Epoch.AddMinutes(1));
            TestRunner.Assert(!t.Oldest(Epoch.AddMinutes(4))!.PresentAtStartup);
            t.Observe(100, [], Epoch.AddMinutes(5)); TestRunner.Equal(0, t.TrackedCount); TestRunner.Assert(t.Oldest(Epoch) is null);
        });
        runner.Test("tray age: oldest item and pid-scoped reused ids", () =>
        {
            var t = new QueueAgeTracker(); t.Observe(100, [Item(1)], Epoch);
            t.Observe(100, [Item(1), Item(2)], Epoch.AddMinutes(1));
            TestRunner.Equal(TimeSpan.FromMinutes(3), t.Oldest(Epoch.AddMinutes(3))!.Elapsed);
            t.Observe(101, [Item(1)], Epoch.AddMinutes(4)); TestRunner.Equal(1, t.TrackedCount);
            TestRunner.Equal(TimeSpan.FromMinutes(1), t.Oldest(Epoch.AddMinutes(5))!.Elapsed);
        });
        runner.Test("tray age: backward wall clock does not produce negative age", () =>
        {
            var t = new QueueAgeTracker(); t.Observe(100, [Item(1)], Epoch);
            TestRunner.Equal(TimeSpan.Zero, t.Oldest(Epoch.AddSeconds(-1))!.Elapsed);
        });
        runner.Test("tray restart: a crash recovered between polls still produces a notice", () =>
        {
            var tracker = new RestartTracker();
            TestRunner.Assert(!tracker.Observe(0) && tracker.Observe(1) && !tracker.Observe(1));
            TestRunner.Assert(tracker.Observe(3) && !tracker.Observe(0) && tracker.Observe(1));
        });
        runner.Test("tray notice: same content is limited to once in ten minutes", () =>
        {
            var t = new NotificationThrottle(); TestRunner.Assert(t.Allow("upload failed", Epoch));
            TestRunner.Assert(!t.Allow("upload failed", Epoch.AddMinutes(9).AddSeconds(59)));
            TestRunner.Assert(t.Allow("upload failed", Epoch.AddMinutes(10)));
        });
        runner.Test("tray notice: different content can be shown", () =>
        {
            var t = new NotificationThrottle(); TestRunner.Assert(t.Allow("a", Epoch) && t.Allow("b", Epoch));
            TestRunner.Assert(!t.Allow("a", Epoch.AddSeconds(1)));
        });
        runner.Test("tray notice: backward clock does not repeat a notice", () =>
        {
            var t = new NotificationThrottle(); t.Allow("a", Epoch); TestRunner.Assert(!t.Allow("a", Epoch.AddMinutes(-1)));
        });
        runner.Test("tray registration: writes, readback and removal occur only in TestHive", Registration);
        runner.Test("tray registration: rejects every non-TestHive override before IO", () =>
        {
            foreach (string root in new[] { "", @"Software\Classes", @"Software\DbxLight\TestHiveOther", @"HKCU\Software\DbxLight\TestHive" })
                TestRunner.Throws<ArgumentException>(() => new RegistrationService(root));
        });
        runner.Test("tray shell: AppliesTo constrains prefix and excludes the hide root", () =>
        {
            TestRunner.Equal("System.ItemPathDisplay:~<\"T:\\\" AND NOT System.ItemPathDisplay:=\"T:\\\"", ShellPaths.AppliesTo("T:", true));
            TestRunner.Equal("System.ItemPathDisplay:~<\"T:\\\"", ShellPaths.AppliesTo("T:", false));
            TestRunner.Equal("System.ItemPathDisplay:~<\"D:\\mounted space\\\" AND NOT System.ItemPathDisplay:=\"D:\\mounted space\"", ShellPaths.AppliesTo(@"D:\mounted space", true));
        });
        runner.Test("tray shell: URL encodes Japanese, spaces, hash, percent, ampersand and plus", () =>
        {
            TestRunner.Equal("https://www.dropbox.com/home/%E6%97%A5%E6%9C%AC%E8%AA%9E/%E7%A9%BA%20%E7%99%BD%23%25%26%2B.txt",
                ShellPaths.WebUrl("T:\\日本語\\空 白#%&+.txt", "T:"));
        });
        runner.Test("tray shell: URL root and nested segments are correct", () =>
        {
            TestRunner.Equal("https://www.dropbox.com/home/", ShellPaths.WebUrl("T:\\", "T:"));
            TestRunner.Equal("https://www.dropbox.com/home/a/b%2Bc", ShellPaths.WebUrl("T:\\a\\b+c", "T:"));
        });
        runner.Test("tray shell: URL refuses outside and relative paths", () =>
        {
            TestRunner.Throws<ArgumentException>(() => ShellPaths.WebUrl("U:\\file", "T:"));
            TestRunner.Throws<ArgumentException>(() => ShellPaths.WebUrl("relative", "T:"));
        });
        runner.Test("tray pipe: second DbxLight process delivers --hide to first process", Pipe);
        runner.Test("tray arguments: config/state overrides and duplicate rejection", () =>
        {
            string root = NewRoot("options");
            AppOptions o = AppOptions.Parse(["--state", Path.Combine(root, "state.json"), "--config", Path.Combine(root, "config.json"), "--hide", "T:\\日本語 空白"]);
            TestRunner.Equal("T:\\日本語 空白", o.HidePath); TestRunner.Equal(Path.Combine(root, "state.json"), o.StatePath);
            TestRunner.Throws<ArgumentException>(() => AppOptions.Parse(["--config", "a", "--config", "b"]));
            TestRunner.Throws<ArgumentException>(() => AppOptions.Parse(["--config"]));
            TestRunner.Throws<ArgumentException>(() => AppOptions.Parse(["--hide", "a", "--web", "b"]));
        });
        runner.Test("tray stop: waiting and explicit force messages", () =>
        {
            TestRunner.Equal("アップロード待ちが 3 件あります。アップロードが終わるまで待ちます", TrayPresentation.StopWaiting(3));
            TestRunner.Assert(TrayPresentation.ForceExplanation.StartsWith("同じ設定で起動し直すと、停止中のキャッシュを走査します", StringComparison.Ordinal));
            TestRunner.Assert(TrayPresentation.ForceExplanation.Contains("問題のあるファイルは退避して残し", StringComparison.Ordinal));
            TestRunner.Assert(TrayPresentation.ForceExplanation.Contains("ドライブの元の場所へ自分で戻してください", StringComparison.Ordinal));
        });
        runner.Test("tray polling: busy five seconds and idle thirty seconds", () =>
        {
            TestRunner.Equal(TimeSpan.FromSeconds(5), TrayPresentation.PollInterval(new() { UploadsQueued = 1 }));
            TestRunner.Equal(TimeSpan.FromSeconds(5), TrayPresentation.PollInterval(new() { UploadsInProgress = 1 }));
            TestRunner.Equal(TimeSpan.FromSeconds(30), TrayPresentation.PollInterval(new() { UploadsQueued = 0, UploadsInProgress = 0 }));
            TestRunner.Equal(TimeSpan.FromSeconds(5), TrayPresentation.PollInterval(new(), true));
        });
        runner.Test("tray tooltip: read-only indicator is never omitted", () =>
        {
            var snapshot = new TraySnapshot(new() { Running = true, Ready = true, UploadsQueued = 3 }, [], [], true, null);
            var config = new DbxConfig { Remote = "local:" };
            TestRunner.Equal("アップロード待ち 3 件 (読み取り専用)", TrayPresentation.Tooltip(snapshot, config));
            TestRunner.Assert(!TrayPresentation.Tooltip(snapshot, config with { WriteAllowedRemotes = ["local:"] }).Contains("読み取り専用"));
            TestRunner.Assert(TrayPresentation.Tooltip(snapshot, config with { WriteAllowedRemotes = ["LOCAL:"] }).Contains("読み取り専用"));
            TestRunner.Assert(TrayPresentation.Tooltip(snapshot with { ActiveConfig = config }, config with { WriteAllowedRemotes = ["local:"] }).Contains("読み取り専用"));
            TestRunner.Assert(!TrayPresentation.Tooltip(snapshot with { ActiveConfig = config with { WriteAllowedRemotes = ["local:"] } }, config).Contains("読み取り専用"));
            TestRunner.Assert(TrayPresentation.Tooltip(snapshot with { Status = new(), ActiveConfig = config with { WriteAllowedRemotes = ["local:"] } }, config).Contains("読み取り専用"));
        });
        runner.Test("tray presentation: six distinct states follow severity", () =>
        {
            TraySnapshot s = new(new() { Running = true, Ready = true, UploadsQueued = 0, UploadsInProgress = 0, ErroredFiles = 0 }, [], [], true, null);
            TestRunner.Equal(TrayState.Running, TrayPresentation.State(s));
            TestRunner.Equal(TrayState.Uploading, TrayPresentation.State(s with { Status = s.Status with { UploadsQueued = 1 } }));
            TestRunner.Equal(TrayState.Attention, TrayPresentation.State(s with { Error = "unavailable" }));
            TestRunner.Equal(TrayState.Failed, TrayPresentation.State(s with { Status = s.Status with { ErroredFiles = 1 } }));
            TestRunner.Equal(TrayState.Preparing, TrayPresentation.State(s, true));
            var download = s with { Status = s.Status with { DownloadProgress = new("sample:", [new("key", "sample", 1, 2, 1, 1)]) } };
            TestRunner.Equal(TrayState.Downloading, TrayPresentation.State(download));
            TestRunner.Equal(TrayState.Attention, TrayPresentation.State(download with { Error = "unavailable" }));
            TestRunner.Equal(TrayState.Failed, TrayPresentation.State(download with { Status = download.Status with { ErroredFiles = 1 } }));
            TestRunner.Equal(6, Enum.GetValues<TrayState>().Select(TrayIconFactory.ColorFor).Distinct().Count());
        });
        runner.Test("tray log: latest twenty ERROR events span rotated logs without forwarding provider text", () =>
        {
            string root = Path.Combine(NewRoot("logs"), "private");
            PrivateStateFiles.EnsureParent(Path.Combine(root, "mount.log"));
            foreach (string name in new[] { "mount.log", "mount-2026-01-02T03-04-05.000.log" })
                File.WriteAllText(Path.Combine(root, name), string.Join('\n', Enumerable.Range(0, 12).Select(n => "ERROR path-" + n)) + "\n", new UTF8Encoding(false));
            IReadOnlyList<string> errors = ErrorLogTail.Read(root);
            TestRunner.Equal(20, errors.Count); TestRunner.Equal(ErrorLogTail.ProviderError, errors[0]); TestRunner.Equal(ErrorLogTail.ProviderError, errors[^1]);
        });
        runner.Test("tray encoding: child JSON and error streams explicitly use UTF-8", () =>
        {
            var info = RcloneArguments.ProcessInfo("rclone.exe", ["version"]);
            TestRunner.Equal(65001, info.StandardOutputEncoding!.CodePage); TestRunner.Equal(65001, info.StandardErrorEncoding!.CodePage);
        });
    }

    private static QueueItem Item(long id) => new(id, "file-" + id, 10, 0, false);
    private static void Paths(HiddenSelection s, string expected) => TestRunner.Equal(expected, string.Join('|', s.Paths));
    private static void Diff(HiddenSelection s, string hide, string show)
    {
        HiddenDiff d = s.Diff(); TestRunner.Equal(hide, string.Join('|', d.ToHide)); TestRunner.Equal(show, string.Join('|', d.ToShow));
    }
    private static string NewRoot(string name)
    {
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "tray-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static void Registration()
    {
        string root = RegistrationService.TestHive + "\\Run-" + Guid.NewGuid().ToString("N");
        var registration = new RegistrationService(root);
        string local = NewRoot("registry");
        var options = new AppOptions(Path.Combine(local, "config.json"), Path.Combine(local, "state.json"));
        string executable = @"C:\Program Files\DbxLight\DbxLight.exe";
        registration.SetAutoStart(true, executable, options);
        registration.InstallShell(new DbxConfig { MountPoint = "T:" }, executable, options);
        TestRunner.Assert(registration.IsAutoStart());
        string run = root + @"\Software\Microsoft\Windows\CurrentVersion\Run";
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(run)!)
        {
            TestRunner.Equal(RegistryValueKind.String, key.GetValueKind("DbxLight"));
            TestRunner.Assert(((string)key.GetValue("DbxLight")!).StartsWith("\"" + executable + "\" ", StringComparison.Ordinal));
            TestRunner.Assert(((string)key.GetValue("DbxLight")!).Contains("--registry-root", StringComparison.Ordinal));
        }
        string hide = root + @"\Software\Classes\Directory\shell\DbxLight.Hide";
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(hide)!)
        using (RegistryKey command = key.OpenSubKey("command")!)
        {
            TestRunner.Equal("このフォルダを表示しない", (string)key.GetValue("")!);
            TestRunner.Equal(ShellPaths.AppliesTo("T:", true), (string)key.GetValue("AppliesTo")!);
            TestRunner.Assert(((string)command.GetValue("")!).EndsWith(" --hide-shell \"%1|\"", StringComparison.Ordinal));
            TestRunner.Assert(((string)command.GetValue("")!).Contains("--registry-root", StringComparison.Ordinal));
        }
        foreach (string type in new[] { "Directory", "*" })
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(root + @"\Software\Classes\" + type + @"\shell\DbxLight.Web")!;
            using RegistryKey command = key.OpenSubKey("command")!;
            TestRunner.Equal("Web で開く", (string)key.GetValue("")!);
            TestRunner.Assert(((string)command.GetValue("")!).EndsWith(" --web-shell \"%1|\"", StringComparison.Ordinal));
        }
        registration.UninstallShell(); registration.SetAutoStart(false, executable, options);
        TestRunner.Assert(!registration.IsAutoStart());
        foreach (string path in new[] { hide, root + @"\Software\Classes\Directory\shell\DbxLight.Web", root + @"\Software\Classes\*\shell\DbxLight.Web" })
        {
            using RegistryKey? gone = Registry.CurrentUser.OpenSubKey(path); TestRunner.Assert(gone is null, "Verb was not removed.");
        }
        TestRunner.Assert(registration.AccessedPaths.Count > 0 && registration.AccessedPaths.All(p => p.StartsWith(root + "\\", StringComparison.Ordinal)));
        Console.WriteLine("Registry IO confined to: HKCU\\" + root + "; real Run/Classes IO=0");
    }
    private static void Pipe()
    {
        string root = NewRoot("pipe"), state = Path.Combine(root, "state.json"), config = Path.Combine(root, "config.json");
        string ready = Path.Combine(root, "ready.txt"), response = Path.Combine(root, "received.json"), exit = Path.Combine(root, "exit.txt");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "--pipe-host", state, ready, response, exit }) start.ArgumentList.Add(a);
        using Process host = Process.Start(start) ?? throw new IOException("Pipe host did not start.");
        try
        {
            Wait(() => File.Exists(ready), "Pipe host was not ready.");
            string executable = Path.Combine(UnitTests.RepositoryRoot(), "src", "DbxLight.Tray", "bin", "Release", "net8.0-windows", "DbxLight.exe");
            var forward = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            string[] args = ["--config", config, "--state", state, "--hide", "T:\\日本語 空白\\#%&+"];
            foreach (string a in args) forward.ArgumentList.Add(a);
            using Process second = Process.Start(forward) ?? throw new IOException("Second process did not start.");
            try { TestRunner.Assert(second.WaitForExit(15000), "Second process did not exit."); TestRunner.Equal(0, second.ExitCode); }
            finally { if (!second.HasExited) { second.Kill(); second.WaitForExit(5000); } }
            Wait(() => File.Exists(response), "The first process did not receive --hide.");
            string[] received = JsonFiles.Read<string[]>(response);
            TestRunner.Assert(args.SequenceEqual(received), "Pipe arguments changed in transit.");
            File.WriteAllText(exit, "exit", new UTF8Encoding(false));
            TestRunner.Assert(host.WaitForExit(10000)); TestRunner.Equal(0, host.ExitCode);
            Console.WriteLine($"Pipe host PID={host.Id}, forwarding DbxLight PID={second.Id}: both exited.");
        }
        finally
        {
            if (!host.HasExited) { host.Kill(); host.WaitForExit(5000); }
        }
    }
    private static void Wait(Func<bool> predicate, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10)) { if (predicate()) return; Thread.Sleep(25); }
        throw new TimeoutException(message);
    }
    public static int PipeHost(string[] args)
    {
        using var instance = new SingleInstance(args[1]);
        if (!instance.IsFirst) return 1;
        instance.Listen(words => JsonFiles.Write(args[3], words));
        File.WriteAllText(args[2], "ready", new UTF8Encoding(false));
        var timer = Stopwatch.StartNew();
        while (!File.Exists(args[4]) && timer.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(25);
        return File.Exists(args[4]) ? 0 : 1;
    }
}
