using Microsoft.Win32;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6s7rTests
{
    private const string NavNative = @"Software\Classes\CLSID\" + RegistrationService.NavigationClsid;
    private const string NavWow = @"Software\Classes\Wow6432Node\CLSID\" + RegistrationService.NavigationClsid;
    private const string NavNamespace = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + RegistrationService.NavigationClsid;
    private const string NavIcons = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string HideCommand = @"Software\Classes\Directory\shell\DbxLight.Hide\command";
    private static void RegistrationReviews(TestRunner runner, string? issue)
    {
        if (issue is null or "all" or "R5")
        foreach (string kind in new[] { "same", "icon", "run", "shell", "none" })
        {
            string scenario = kind;
            runner.Test("6s7r R5: executable registration notice: " + kind, () => ExecutableNotice(scenario));
        }
        if (issue is null or "all" or "R6")
        {
            runner.Test("6s7r R6: one unregister failure does not skip startup or navigation", UnregisterContinues);
            runner.Test("6s7r R6: registration failure emits one stderr line", RegisterFailure);
        }
        if (issue is null or "all" or "R7") runner.Test("6s7r R7: navigation visibility is registered last and removed first", NavigationOrder);
        if (issue is null or "all" or "R8") runner.Test("6s7r R8: literal percent paths round-trip without a registration warning", LiteralPercentPaths);
    }
    private sealed class RegistrationFixture
    {
        internal readonly string Hive = RegistrationService.TestHive + @"\Unit6s7r-" + Guid.NewGuid().ToString("N");
        internal readonly string Root = Path.Combine(TestPaths.WorkRoot, "run-unit6s7r-reg-" + Guid.NewGuid().ToString("N"));
        internal RegistrationService Service { get; }
        internal string Exe => Path.Combine(Root, "app", "DbxLight.exe");
        internal AppOptions Options => new(Path.Combine(Root, "config.json"), Path.Combine(Root, "state.json"), RegistryRoot: Hive);
        internal RegistrationFixture() { Service = new(Hive); Directory.CreateDirectory(Root); Console.WriteLine("Registration artifacts=" + Root + "; hive=" + Hive); }
        internal RegistryKey Put(string path) => Registry.CurrentUser.CreateSubKey(Hive + "\\" + path);
        internal RegistryKey? Get(string path) => Registry.CurrentUser.OpenSubKey(Hive + "\\" + path);
        internal void Safe() => TestRunner.Assert(Service.AccessedPaths.All(p => p.StartsWith(Hive + "\\", StringComparison.OrdinalIgnoreCase)));
    }
    private static void ExecutableNotice(string kind)
    {
        var f = new RegistrationFixture(); var config = new DbxConfig { MountPoint = "U:" };
        if (kind != "none")
        {
            f.Service.InstallNavigation(config, f.Exe); f.Service.InstallShell(config, f.Exe, f.Options); f.Service.SetAutoStart(true, f.Exe, f.Options);
        }
        if (kind == "icon") { using var key = f.Put(NavNative + @"\DefaultIcon"); key.SetValue("", Path.Combine(f.Root, "old.exe") + ",0"); }
        if (kind == "run") f.Service.SetAutoStart(true, Path.Combine(f.Root, "old.exe"), f.Options);
        if (kind == "shell") { using var key = f.Put(HideCommand); key.SetValue("", "\"" + Path.Combine(f.Root, "old.exe") + "\" --hide-shell "); }
        string notice = f.Service.ShellRegistrationNotice(config, f.Exe, f.Options);
        if (kind is "same" or "none") TestRunner.Equal("", notice);
        else
        {
            TestRunner.Assert(notice.Length > 0 && notice.Contains("自動起動も入ります"));
            TestRunner.Equal(2, notice.Split("--register", StringSplitOptions.None).Length);
        }
        f.Safe();
    }
    private static void UnregisterContinues()
    {
        var f = new RegistrationFixture(); var config = new DbxConfig { MountPoint = "U:" };
        f.Service.InstallShell(config, f.Exe, f.Options); f.Service.SetAutoStart(true, f.Exe, f.Options); f.Service.InstallNavigation(config, f.Exe);
        // An unexpected child makes the first nonrecursive shell deletion fail.
        using (var key = f.Put(HideCommand + @"\retained")) key.SetValue("proof", "preserved");
        var result = Unit3cTests.Capture(() => DbxLight.Tray.Program.ApplyRegistration(f.Options with { Registration = "--unregister" }, f.Service));
        TestRunner.Equal(1, result.Exit); TestRunner.Equal("", result.Output);
        TestRunner.Equal(1, result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        TestRunner.Assert(result.Error.Contains("右クリック"));
        using (var run = f.Get(RunKey)) TestRunner.Assert(run?.GetValue("DbxLight") is null);
        foreach (string path in new[] { NavNative, NavWow, NavNamespace }) { using var key = f.Get(path); TestRunner.Assert(key is null); }
        using (var kept = f.Get(HideCommand + @"\retained")) TestRunner.Assert(kept is not null);
        f.Safe();
    }
    private static void RegisterFailure()
    {
        var f = new RegistrationFixture(); File.WriteAllText(f.Options.ConfigPath, "{");
        var result = Unit3cTests.Capture(() => DbxLight.Tray.Program.ApplyRegistration(f.Options with { Registration = "--register" }, f.Service));
        TestRunner.Equal(1, result.Exit); TestRunner.Equal("", result.Output);
        TestRunner.Equal(1, result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); TestRunner.Assert(result.Error.Contains("登録"));
        TestRunner.Equal(0, f.Service.AccessedPaths.Count);
    }
    private static void NavigationOrder()
    {
        var f = new RegistrationFixture(); f.Service.InstallNavigation(new() { MountPoint = "U:" }, f.Exe);
        TestRunner.Equal(f.Hive + "\\" + NavIcons, f.Service.AccessedPaths[^2]);
        TestRunner.Equal(f.Hive + "\\" + NavNamespace, f.Service.AccessedPaths[^1]);
        f.Service.AccessedPaths.Clear(); f.Service.UninstallNavigation();
        TestRunner.Assert(f.Service.AccessedPaths.SequenceEqual(new[] { NavNamespace, NavIcons, NavNative, NavWow }.Select(p => f.Hive + "\\" + p)));
        f.Safe();
    }
    private static void LiteralPercentPaths()
    {
        var f = new RegistrationFixture();
        var config = new DbxConfig { MountPoint = Path.Combine(f.Root, "%TEMP%", "mount"), NetworkMode = false };
        string exe = Path.Combine(f.Root, "%TEMP%", "DbxLight.exe");
        f.Service.InstallNavigation(config, exe); f.Service.InstallShell(config, exe, f.Options); f.Service.SetAutoStart(true, exe, f.Options);
        foreach (string path in new[] { NavNative, NavWow })
        {
            using var bag = f.Get(path + @"\Instance\InitPropertyBag"); using var icon = f.Get(path + @"\DefaultIcon");
            TestRunner.Equal(RegistryValueKind.String, bag!.GetValueKind("TargetFolderPath"));
            TestRunner.Equal(config.MountPoint, bag.GetValue("TargetFolderPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            TestRunner.Equal(RegistryValueKind.String, icon!.GetValueKind("")); TestRunner.Equal(exe + ",0", icon.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        TestRunner.Equal("", f.Service.ShellRegistrationNotice(config, exe, f.Options)); f.Safe();
    }
}
