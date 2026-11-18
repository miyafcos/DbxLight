using System.Runtime.InteropServices;
using System.Text;

namespace DbxLight.Core;

public static class HiddenFolders
{
    public static string NormalizePath(string path, bool allowRoot = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        string result = path.Replace('\\', '/').Trim('/').Normalize(NormalizationForm.FormC);
        if (result.Length == 0)
        {
            if (allowRoot) return "";
            throw new ArgumentException("The remote root cannot be hidden.");
        }
        if (result.Contains(':') || result.Any(char.IsControl) || result.Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("Expected a relative folder path without traversal or empty segments.");
        return result;
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string> paths)
    {
        var candidates = paths.Select(p => NormalizePath(p)).OrderBy(p => p, StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.Ordinal).ToArray();
        var result = new List<string>();
        foreach (string path in candidates)
            if (!result.Any(parent => IsHidden(path, parent))) result.Add(path);
        return result.OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    private static bool IsHidden(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    public static bool IsHidden(string path, IEnumerable<string> hidden) => hidden.Any(parent => IsHidden(path, parent));

    public static string Escape(string path)
    {
        var builder = new StringBuilder();
        foreach (char c in path)
        {
            if ("*?[]{}\\".Contains(c)) builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    public static string Render(IEnumerable<string> hidden, IEnumerable<string>? exclude = null)
    {
        var lines = Normalize(hidden).Select(p => "- /" + Escape(p) + "/**").ToList();
        foreach (string pattern in exclude ?? [])
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Any(char.IsControl)) throw new ArgumentException("Invalid exclude pattern.");
            lines.Add("- " + pattern);
        }
        return lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
    }
}

public static class MountPaths
{
    internal const string WinFspCharacters = "\u0022*:<>?|\\";
    public static bool IsDriveRoot(string path) => path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    public static string CanonicalMount(string mount)
    {
        if (mount.Length == 2 && char.IsAsciiLetter(mount[0]) && mount[1] == ':') mount += "\\";
        if (!Path.IsPathFullyQualified(mount)) throw new ArgumentException("mountPoint must be an absolute Windows path or a drive letter.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount));
    }

    public static bool IsSameOrBelow(string path, string root)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalized.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string ToRelative(string input, string mount, bool allowRoot = false)
    {
        if (input.StartsWith("\\\\", StringComparison.Ordinal) || (input.Length >= 2 && input[1] == ':'))
        {
            string local = input.Replace('/', '\\');
            if (local.Length == 2 || !Path.IsPathFullyQualified(local)) throw new ArgumentException("A local path must be absolute.");
            string root = CanonicalMount(mount);
            string full = Path.GetFullPath(local);
            if (!IsSameOrBelow(full, root)) throw new ArgumentException("Local path is outside mountPoint.");
            input = Path.GetRelativePath(root, full);
            if (input == ".") input = "";
        }
        return HiddenFolders.NormalizePath(input, allowRoot);
    }

    public static bool IsAvailable(string mount)
    {
        string full = CanonicalMount(mount);
        if (IsDriveRoot(full))
        {
            var buffer = new StringBuilder(32768);
            return QueryDosDevice(full[..2], buffer, buffer.Capacity) == 0 && !Directory.Exists(full);
        }
        return !File.Exists(full) && !Directory.Exists(full) && Directory.Exists(Path.GetDirectoryName(full));
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int maxChars);
}
