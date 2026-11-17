using System.Reflection;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

public readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    public static string Information => typeof(UpdateVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static string Current => Information.Split('+')[0];
    public static string? Commit => Information.Contains('+') ? Information[(Information.IndexOf('+') + 1)..] : null;
    public static bool TryParse(string? value, out UpdateVersion version)
    {
        version = default;
        if (value is null || !Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)) return false;
        string[] parts = value.Split('.');
        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor) || !int.TryParse(parts[2], out int patch)) return false;
        version = new(major, minor, patch); return true;
    }
    public static bool TryTag(string? tag, out UpdateVersion version)
    {
        version = default;
        return tag is not null && tag.StartsWith('v') && TryParse(tag[1..], out version);
    }
    public int CompareTo(UpdateVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
