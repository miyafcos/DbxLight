using System.Diagnostics;
using DbxLight.Core;
using DbxLight.Tray;
using Microsoft.Win32;

namespace DbxLight.Tests;

internal static class Unit7Tests
{
    private const string GuidValue = "{C67DB66A-1A5E-4627-B519-0FA90E7D48FF}";
    private const string OtherGuid = "{BF360258-0AA8-44F0-BBDA-57F8380039ED}";
    private const string Native = @"Software\Classes\CLSID\" + GuidValue;
    private const string Wow = @"Software\Classes\Wow6432Node\CLSID\" + GuidValue;
    private const string Namespace = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + GuidValue;
    private const string Icons = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private static readonly string Executable = Path.Combine(TestPaths.WorkRoot, @"Sidebar app\DbxLight.exe");
    private sealed record Entry(string Path, string Name, RegistryValueKind Kind, object Value);
    private sealed class Fixture
    {
        internal string Hive { get; } = RegistrationService.TestHive + @"\Unit7-" + System.Guid.NewGuid().ToString("N");
        internal RegistrationService Service { get; }
        internal string Root { get; } = Path.Combine(TestPaths.WorkRoot, "run-unit7-" + System.Guid.NewGuid().ToString("N"));
        internal AppOptions Options => new(Path.Combine(Root, "config.json"), Path.Combine(Root, "state.json"), RegistryRoot: Hive);
        internal Fixture() { Service = new(Hive); Directory.CreateDirectory(Root); }
        internal void Accesses()
        {
            TestRunner.Assert(Service.AccessedPaths.Count > 0 && Service.AccessedPaths.All(path =>
                path.StartsWith(Hive + "\\", StringComparison.OrdinalIgnoreCase)), "Registry access escaped the unique TestHive root.");
            Console.WriteLine($"7 accesses={Service.AccessedPaths.Count}; all under HKCU\\{Hive}; files={Root}");
        }
    }
    internal static void Run(TestRunner runner, string? issue = null)
    {
        if (issue == "registration")
        {
            TrayTests.Run(runner);
            Unit3bTests.Run(runner, "B8"); Unit3bTests.Run(runner, "B9");
            return;
        }
        runner.Test("7 T1: all 22 navigation values match the specification including kinds", Values);
        runner.Test("7 T2: unregister deletes only own GUID trees and value, preserves parents and siblings", RemoveOnlyOwn);
        runner.Test("7 T3: repeated registration is identical and replaces old targets, icons and kinds", Replace);
        runner.Test("7 T4: folder mounts use the same canonical root as AppliesTo", Folder);
        runner.Test("7 T5: stale navigation warns even when shell verbs match, then re-registration clears it", Notice);
        runner.Test("7 T6: unregistered navigation stays silent and equivalent roots stay silent", Silent);
        runner.Test("7 T7: a stale 32-bit target and a missing target are also detected", PartialNotice);
        runner.Test("7 T8: settings display the navigation notice and registration command", SettingsNotice);
        runner.Test("7 T9: tray register and unregister entry points use only the explicit TestHive", EntryPoints);
    }
    private static List<Entry> Expected(string target, string? executable = null)
    {
        executable ??= Executable;
        var entries = new List<Entry>();
        foreach (string path in new[] { Native, Wow })
        {
            entries.AddRange(new[]
            {
                new Entry(path, "", RegistryValueKind.String, "DbxLight"),
                new Entry(path, "System.IsPinnedToNameSpaceTree", RegistryValueKind.DWord, 1),
                new Entry(path, "SortOrderIndex", RegistryValueKind.DWord, 0x42),
                new Entry(path + @"\DefaultIcon", "", RegistryValueKind.ExpandString, executable + ",0"),
                new Entry(path + @"\InProcServer32", "", RegistryValueKind.ExpandString, @"%SystemRoot%\system32\shell32.dll"),
                new Entry(path + @"\Instance", "CLSID", RegistryValueKind.String, "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}"),
                new Entry(path + @"\Instance\InitPropertyBag", "Attributes", RegistryValueKind.DWord, 0x11),
                new Entry(path + @"\Instance\InitPropertyBag", "TargetFolderPath", RegistryValueKind.ExpandString, target),
                new Entry(path + @"\ShellFolder", "FolderValueFlags", RegistryValueKind.DWord, 0x28),
                new Entry(path + @"\ShellFolder", "Attributes", RegistryValueKind.DWord, unchecked((int)0xF080004D))
            });
        }
        entries.Add(new(Namespace, "", RegistryValueKind.String, "DbxLight"));
        entries.Add(new(Icons, GuidValue, RegistryValueKind.DWord, 1));
        return entries;
    }
    private static void Verify(Fixture f, string target, string? executable = null)
    {
        executable ??= Executable;
        var entries = Expected(target, executable); TestRunner.Equal(22, entries.Count);
        TestRunner.Equal(GuidValue, RegistrationService.NavigationClsid);
        foreach (Entry item in entries)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(f.Hive + "\\" + item.Path);
            TestRunner.Assert(key is not null, "Missing key: " + item.Path);
            TestRunner.Equal(item.Kind, key!.GetValueKind(item.Name));
            TestRunner.Equal(item.Value, key.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
    }
    private static string Snapshot(Fixture f)
    {
        var rows = new List<string>();
        void Read(string path)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(f.Hive + "\\" + path);
            if (key is null) return;
            foreach (string name in key.GetValueNames())
                rows.Add(path + "|" + name + "|" + key.GetValueKind(name) + "|" +
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            foreach (string child in key.GetSubKeyNames()) Read(path + "\\" + child);
        }
        foreach (string path in new[] { Native, Wow, Namespace, Icons }) Read(path);
        return string.Join('\n', rows.Order(StringComparer.Ordinal));
    }
    private static void Values()
    {
        var f = new Fixture(); f.Service.InstallNavigation(new() { MountPoint = "U:" }, Executable);
        Verify(f, @"U:\"); TestRunner.Equal(22, Snapshot(f).Split('\n').Length); f.Accesses();
    }
    private static void Put(Fixture f, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(f.Hive + "\\" + path); key.SetValue(name, value, kind);
    }
    private static object? Get(Fixture f, string path, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(f.Hive + "\\" + path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
    private static void RemoveOnlyOwn()
    {
        var f = new Fixture();
        f.Service.InstallNavigation(new() { MountPoint = "U:" }, Executable);
        string[] parents = [@"Software\Classes\CLSID", @"Software\Classes\Wow6432Node\CLSID",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace"];
        foreach (string parent in parents)
        {
            Put(f, parent, "parent-sentinel", "keep-parent");
            Put(f, parent + "\\" + OtherGuid + @"\child", "sentinel", "keep-sibling");
        }
        Put(f, Icons, OtherGuid, 7, RegistryValueKind.DWord);
        f.Service.UninstallNavigation(); f.Service.UninstallNavigation();
        foreach (string path in new[] { Native, Wow, Namespace })
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(f.Hive + "\\" + path);
            TestRunner.Assert(key is null, "Own namespace key survived uninstall.");
        }
        TestRunner.Assert(Get(f, Icons, GuidValue) is null);
        TestRunner.Equal(7, Get(f, Icons, OtherGuid));
        foreach (string parent in parents)
        {
            TestRunner.Equal("keep-parent", Get(f, parent, "parent-sentinel"));
            TestRunner.Equal("keep-sibling", Get(f, parent + "\\" + OtherGuid + @"\child", "sentinel"));
        }
        f.Accesses();
    }
    private static void Replace()
    {
        var f = new Fixture(); var config = new DbxConfig { MountPoint = "U:" };
        foreach (Entry entry in Expected(@"V:\", Path.Combine(TestPaths.WorkRoot, @"old\DbxLight.exe")))
            Put(f, entry.Path, entry.Name, "old-value");
        foreach (string path in new[] { Native, Wow }) Put(f, path + @"\ShellFolder", "FolderValueFlags", 0x228, RegistryValueKind.DWord);
        f.Service.InstallNavigation(config, Executable); Verify(f, @"U:\");
        string before = Snapshot(f);
        f.Service.InstallNavigation(config, Executable);
        TestRunner.Equal(before, Snapshot(f)); Verify(f, @"U:\"); f.Accesses();
    }
    private static void Folder()
    {
        var f = new Fixture();
        var config = new DbxConfig { MountPoint = Path.Combine(TestPaths.WorkRoot, @"Sidebar folder\child\..\"), NetworkMode = false };
        f.Service.InstallNavigation(config, Executable); Verify(f, Path.Combine(TestPaths.WorkRoot, @"Sidebar folder"));
        TestRunner.Assert(ShellPaths.AppliesTo(config.MountPoint, false).Contains(Path.Combine(TestPaths.WorkRoot, @"Sidebar folder\"), StringComparison.Ordinal));
        f.Accesses();
    }
    private static void Notice()
    {
        var f = new Fixture(); var current = new DbxConfig { MountPoint = "V:" };
        f.Service.InstallNavigation(new() { MountPoint = "U:" }, Executable);
        f.Service.InstallShell(current, Executable, f.Options);
        string text = f.Service.ShellRegistrationNotice(current, Executable, f.Options);
        TestRunner.Assert(text.Contains(@"U:\", StringComparison.Ordinal) && text.Contains(@"V:\", StringComparison.Ordinal) &&
            text.Contains("登録し直す必要があります", StringComparison.Ordinal) && text.Contains("--register", StringComparison.Ordinal));
        f.Service.InstallNavigation(current, Executable);
        TestRunner.Equal("", f.Service.ShellRegistrationNotice(current, Executable, f.Options)); f.Accesses();
    }
    private static void Silent()
    {
        var f = new Fixture(); var config = new DbxConfig { MountPoint = "U:" };
        TestRunner.Equal("", f.Service.ShellRegistrationNotice(config, Executable, f.Options));
        f.Service.InstallNavigation(config, Executable);
        TestRunner.Equal("", f.Service.ShellRegistrationNotice(config with { MountPoint = @"u:\" }, Executable, f.Options)); f.Accesses();
    }
    private static void PartialNotice()
    {
        var f = new Fixture(); var config = new DbxConfig { MountPoint = "U:" };
        f.Service.InstallNavigation(config, Executable);
        Put(f, Wow + @"\Instance\InitPropertyBag", "TargetFolderPath", @"V:\", RegistryValueKind.ExpandString);
        TestRunner.Assert(f.Service.ShellRegistrationNotice(config, Executable, f.Options).Contains(@"V:\", StringComparison.Ordinal));
        f.Service.UninstallNavigation();
        Put(f, Native, "", "DbxLight");
        TestRunner.Assert(f.Service.ShellRegistrationNotice(config, Executable, f.Options).Contains("確認できません", StringComparison.Ordinal)); f.Accesses();
    }
    private static void SettingsNotice()
    {
        var f = new Fixture(); f.Service.InstallNavigation(new() { MountPoint = "U:" }, Executable);
        FormTests.Sta(() =>
        {
            using var form = new SettingsForm(new() { MountPoint = "V:" }, f.Service, Executable, f.Options, _ => Task.CompletedTask);
            string text = FormTests.Field<TextBox>(form, "shellNotice").Text;
            TestRunner.Assert(text.Contains("ナビゲーション", StringComparison.Ordinal) && text.Contains("--register", StringComparison.Ordinal));
        });
        f.Accesses();
    }
    private static void EntryPoints()
    {
        var f = new Fixture();
        var config = new DbxConfig { MountPoint = "U:" };
        new ConfigStore(f.Options.ConfigPath).Save(config);
        string executable = Path.ChangeExtension(typeof(DbxLight.Tray.Program).Assembly.Location, ".exe");
        void Run(string command)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string word in new[] { "--config", f.Options.ConfigPath, "--state", f.Options.StatePath,
                "--registry-root", f.Hive, command }) start.ArgumentList.Add(word);
            using Process child = Process.Start(start) ?? throw new IOException("Test tray did not start.");
            try
            {
                if (!child.WaitForExit(15000)) throw new TimeoutException("Test tray registration timed out.");
                TestRunner.Equal(0, child.ExitCode);
            }
            finally { if (!child.HasExited) { child.Kill(); TestRunner.Assert(child.WaitForExit(5000)); } }
        }
        Run("--register"); Verify(f, @"U:\", executable); TestRunner.Assert(f.Service.IsAutoStart());
        Run("--unregister"); TestRunner.Assert(!f.Service.IsAutoStart());
        foreach (string path in new[] { Native, Wow, Namespace })
        { using var key = Registry.CurrentUser.OpenSubKey(f.Hive + "\\" + path); TestRunner.Assert(key is null); }
        TestRunner.Assert(Get(f, Icons, GuidValue) is null); f.Accesses();
    }
}
