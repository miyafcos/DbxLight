using System.Text;
using Microsoft.Win32;
using DbxLight.Core;

namespace DbxLight.Tray;

public static class ShellPaths
{
    public static string AppliesTo(string mount, bool excludeRoot)
    {
        string root = MountPaths.CanonicalMount(mount);
        if (root.Contains('"')) throw new ArgumentException("The mount path contains a quote.");
        string prefix = root.TrimEnd('\\') + "\\";
        return "System.ItemPathDisplay:~<\"" + prefix + "\"" +
            (excludeRoot ? " AND NOT System.ItemPathDisplay:=\"" + root + "\"" : "");
    }
    public static string WebUrl(string path, string mount)
    {
        string relative = ToRelativeAbsolute(path, mount, allowRoot: true);
        return CloudWebPaths.WebUrl(relative);
    }
    public static string ToRelativeAbsolute(string path, string mount, bool allowRoot = false)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("\u30de\u30a6\u30f3\u30c8\u5148\u306e\u4e2d\u306e\u5b8c\u5168\u4fee\u98fe\u306e\u7d76\u5bfe\u30d1\u30b9\u3092\u6307\u5b9a\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
        return MountPaths.ToRelative(path, mount, allowRoot);
    }
    public static string DecodeShellPath(string encoded)
    {
        // The non-path terminator keeps a trailing backslash away from the closing quote.
        if (encoded.Length <= 1 || !encoded.EndsWith('|') || encoded[..^1].Contains('|'))
            throw new ArgumentException("Invalid terminated shell path.");
        return encoded[..^1];
    }
    public static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

public sealed class RegistrationService
{
    public const string TestHive = @"Software\DbxLight\TestHive";
    public const string NavigationClsid = "{C67DB66A-1A5E-4627-B519-0FA90E7D48FF}";
    private static readonly string[] NavigationClassPaths =
    [
        @"Software\Classes\CLSID\" + NavigationClsid,
        @"Software\Classes\Wow6432Node\CLSID\" + NavigationClsid
    ];
    private const string NavigationNamespacePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + NavigationClsid;
    private const string HideDesktopIconsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private readonly string? root;
    public List<string> AccessedPaths { get; } = [];
    public RegistrationService(string? root = null)
    {
        if (root is not null)
        {
            root = root.Replace('/', '\\').TrimEnd('\\');
            if (!root.Equals(TestHive, StringComparison.OrdinalIgnoreCase) &&
                !root.StartsWith(TestHive + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("試験用の登録先は Software/DbxLight/TestHive の下にしてください。");
        }
        this.root = root;
    }
    private string Resolve(string real)
    {
        string path = root is null ? real : root + "\\" + real;
        AccessedPaths.Add(path);
        return path;
    }
    private string StartupKey => Resolve(@"Software\Microsoft\Windows\CurrentVersion\Run");
    private string VerbKey(string type, string verb) => Resolve(@"Software\Classes\" + type + @"\shell\DbxLight." + verb);
    public static string[] StartupArguments(AppOptions options, string? registryRoot = null)
    {
        var arguments = new List<string> { "--config", Path.GetFullPath(options.ConfigPath), "--state", Path.GetFullPath(options.StatePath) };
        string? registrationRoot = registryRoot ?? options.RegistryRoot;
        if (registrationRoot is not null) { arguments.Add("--registry-root"); arguments.Add(registrationRoot); }
        return arguments.ToArray();
    }
    private string Command(string executable, AppOptions options) =>
        string.Join(' ', new[] { executable }.Concat(StartupArguments(options, root)).Select(ShellPaths.QuoteArgument));
    public string ShellRegistrationNotice(DbxConfig config, string? executable, AppOptions options)
    {
        if (executable is null) return "";
        bool staleExecutable = false;
        bool DifferentExecutable(string? value, bool icon = false)
        {
            if (value is null) return true;
            string path;
            if (icon)
            {
                int comma = value.LastIndexOf(',');
                if (comma < 0) return true;
                path = value[..comma].Trim().Trim('"');
            }
            else if (value.StartsWith('"'))
            {
                int end = value.IndexOf('"', 1);
                if (end < 0) return true;
                path = value[1..end];
            }
            else path = value.Split(' ', 2)[0];
            try { return !Path.GetFullPath(path).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        }
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in new[] { (Type: "Directory", Verb: "Hide"), (Type: "Directory", Verb: "Web"), (Type: "*", Verb: "Web") })
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(VerbKey(item.Type, item.Verb));
            if (key is null) continue;
            using RegistryKey? command = key.OpenSubKey("command");
            staleExecutable |= DifferentExecutable(command?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
            const string prefix = "System.ItemPathDisplay:~<\"";
            string expression = key.GetValue("AppliesTo", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            int end = expression.StartsWith(prefix, StringComparison.Ordinal) ? expression.IndexOf('"', prefix.Length) : -1;
            registered.Add(expression.StartsWith(prefix, StringComparison.Ordinal) && end >= 0 ? expression[prefix.Length..end] : "(確認できません)");
        }
        var navigation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in NavigationClassPaths)
        {
            using RegistryKey? entry = Registry.CurrentUser.OpenSubKey(Resolve(path));
            if (entry is null) continue;
            using RegistryKey? bag = Registry.CurrentUser.OpenSubKey(Resolve(path + @"\Instance\InitPropertyBag"));
            using RegistryKey? icon = Registry.CurrentUser.OpenSubKey(Resolve(path + @"\DefaultIcon"));
            staleExecutable |= DifferentExecutable(icon?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, icon: true);
            navigation.Add(bag?.GetValue("TargetFolderPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "(確認できません)");
        }
        using (RegistryKey? startup = Registry.CurrentUser.OpenSubKey(StartupKey))
        {
            object? run = startup?.GetValue("DbxLight", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (run is not null) staleExecutable |= DifferentExecutable(run as string);
        }
        string current = MountPaths.CanonicalMount(config.MountPoint);
        bool Changed(IEnumerable<string> paths) => paths.Any(old => !old.TrimEnd('\\').Equals(current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        var locations = new List<string>();
        if (Changed(registered)) locations.Add("右クリックの登録先: " + string.Join(", ", registered));
        if (Changed(navigation)) locations.Add("ナビゲーション ウィンドウの登録先: " + string.Join(", ", navigation));
        if (staleExecutable) locations.Add("実行ファイルの登録先が現在の exe と違います");
        if (locations.Count == 0) return "";
        return string.Join("、", locations) + "、現在: " + current + "。登録し直す必要があります。\n" +
            "マウント先を確認してから次のコマンドで登録し直し、この画面を開き直してください。自動起動も入ります。\n" + Command(executable, options) + " --register";
    }
    public bool IsAutoStart()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKey);
        return key?.GetValue("DbxLight") is string;
    }
    public void SetAutoStart(bool enabled, string executable, AppOptions options)
    {
        string path = StartupKey;
        if (enabled)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("DbxLight", Command(executable, options), RegistryValueKind.String);
        }
        else
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            key?.DeleteValue("DbxLight", throwOnMissingValue: false);
        }
    }
    public void InstallNavigation(DbxConfig config, string executable)
    {
        string target = MountPaths.CanonicalMount(config.MountPoint);
        string icon = Path.GetFullPath(executable) + ",0";
        foreach (string path in NavigationClassPaths)
        {
            WriteNavigationValue(path, "", "DbxLight", RegistryValueKind.String);
            WriteNavigationValue(path, "System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            WriteNavigationValue(path, "SortOrderIndex", 0x42, RegistryValueKind.DWord);
            WriteNavigationValue(path + @"\DefaultIcon", "", icon, LiteralPathKind(icon));
            WriteNavigationValue(path + @"\InProcServer32", "", @"%SystemRoot%\system32\shell32.dll", RegistryValueKind.ExpandString);
            WriteNavigationValue(path + @"\Instance", "CLSID", "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}", RegistryValueKind.String);
            WriteNavigationValue(path + @"\Instance\InitPropertyBag", "Attributes", 0x11, RegistryValueKind.DWord);
            WriteNavigationValue(path + @"\Instance\InitPropertyBag", "TargetFolderPath", target, LiteralPathKind(target));
            WriteNavigationValue(path + @"\ShellFolder", "FolderValueFlags", 0x28, RegistryValueKind.DWord);
            WriteNavigationValue(path + @"\ShellFolder", "Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
        }
        WriteNavigationValue(HideDesktopIconsPath, NavigationClsid, 1, RegistryValueKind.DWord);
        WriteNavigationValue(NavigationNamespacePath, "", "DbxLight", RegistryValueKind.String);
        NotifyNavigationChanged();
    }
    private static RegistryValueKind LiteralPathKind(string path) =>
        path.Contains('%') ? RegistryValueKind.String : RegistryValueKind.ExpandString;
    private void WriteNavigationValue(string path, string name, object value, RegistryValueKind kind)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(Resolve(path));
        key.SetValue(name, value, kind);
    }
    public void UninstallNavigation()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Resolve(NavigationNamespacePath), throwOnMissingSubKey: false);
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(Resolve(HideDesktopIconsPath), writable: true))
            key?.DeleteValue(NavigationClsid, throwOnMissingValue: false);
        foreach (string path in NavigationClassPaths)
            Registry.CurrentUser.DeleteSubKeyTree(Resolve(path), throwOnMissingSubKey: false);
        NotifyNavigationChanged();
    }
    private void NotifyNavigationChanged()
    {
        if (root is null) SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public void InstallShell(DbxConfig config, string executable, AppOptions options)
    {
        string command = Command(executable, options);
        WriteVerb("Directory", "Hide", "このフォルダを表示しない", ShellPaths.AppliesTo(config.MountPoint, true), command + " --hide-shell \"%1|\"");
        WriteVerb("Directory", "Web", "Web で開く", ShellPaths.AppliesTo(config.MountPoint, false), command + " --web-shell \"%1|\"");
        WriteVerb("*", "Web", "Web で開く", ShellPaths.AppliesTo(config.MountPoint, false), command + " --web-shell \"%1|\"");
    }
    private void WriteVerb(string type, string verb, string title, string appliesTo, string command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(VerbKey(type, verb));
        key.SetValue("", title, RegistryValueKind.String);
        key.SetValue("AppliesTo", appliesTo, RegistryValueKind.String);
        using RegistryKey child = key.CreateSubKey("command");
        child.SetValue("", command, RegistryValueKind.String);
    }
    public void UninstallShell()
    {
        foreach (var item in new[] { (Type: "Directory", Verb: "Hide"), (Type: "Directory", Verb: "Web"), (Type: "*", Verb: "Web") })
        {
            string path = VerbKey(item.Type, item.Verb);
            // Remove only the known command child and our own verb. Leave all other keys intact.
            Registry.CurrentUser.DeleteSubKey(path + @"\command", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }
}
