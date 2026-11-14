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
            throw new ArgumentException("\u5272\u308a\u5f53\u3066\u5148\u306e\u4e2d\u306e\u5b8c\u5168\u4fee\u98fe\u306e\u7d76\u5bfe\u30d1\u30b9\u3092\u6307\u5b9a\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
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
    private string Command(string executable, AppOptions options) =>
        string.Join(' ', new[] { executable, "--config", options.ConfigPath, "--state", options.StatePath }.Select(ShellPaths.QuoteArgument)) +
        (root is null ? "" : " --registry-root " + ShellPaths.QuoteArgument(root));
    public string ShellRegistrationNotice(DbxConfig config, string executable, AppOptions options)
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in new[] { (Type: "Directory", Verb: "Hide"), (Type: "Directory", Verb: "Web"), (Type: "*", Verb: "Web") })
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(VerbKey(item.Type, item.Verb));
            if (key is null) continue;
            const string prefix = "System.ItemPathDisplay:~<\"";
            string expression = key.GetValue("AppliesTo") as string ?? "";
            int end = expression.StartsWith(prefix, StringComparison.Ordinal) ? expression.IndexOf('"', prefix.Length) : -1;
            registered.Add(expression.StartsWith(prefix, StringComparison.Ordinal) && end >= 0 ? expression[prefix.Length..end] : "(\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093)");
        }
        string current = MountPaths.CanonicalMount(config.MountPoint);
        if (registered.Count == 0 || registered.All(old => old.TrimEnd('\\').Equals(current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return "";
        return "\u53f3\u30af\u30ea\u30c3\u30af\u306e\u767b\u9332\u5148: " + string.Join(", ", registered) + "\u3001\u73fe\u5728: " + current + "\u3002\u767b\u9332\u3057\u76f4\u3059\u5fc5\u8981\u304c\u3042\u308a\u307e\u3059\u3002\n" +
            "\u5272\u308a\u5f53\u3066\u5148\u3092\u78ba\u8a8d\u3057\u3066\u304b\u3089\u6b21\u306e\u30b3\u30de\u30f3\u30c9\u3067\u767b\u9332\u3057\u76f4\u3057\u3001\u3053\u306e\u753b\u9762\u3092\u958b\u304d\u76f4\u3057\u3066\u304f\u3060\u3055\u3044\u3002\n" + Command(executable, options) + " --register";
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
