using System.Text;
namespace DbxLight.Core;

/// <summary>Recovery-only decoding of the measured Windows cache and WinFsp spellings.</summary>
internal static class RecoveryCacheNames
{
    private const char Quote = '\u201b';
    private const string Wide = "\uff1a\uff1f\uff0a\uff5c\uff1c\uff1e\uff3c\uff02";
    private const string Ascii = ":?*|<>\\\"";
    internal static bool TryDecode(string encoded, out string relative)
    {
        relative = "";
        if (PreservationReason(encoded) is not null) return false;
        var decodedParts = new List<string>();
        foreach (string component in encoded.Split('/'))
        {
            if (!DecodeComponent(component, out string decodedPart)) return false;
            decodedParts.Add(decodedPart);
        }
        string decoded = string.Join('/', decodedParts);
        if (decoded.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' '))) return false;
        if (EncodeForDisk(decoded) != encoded) return false;
        relative = decoded; return true;
    }
    internal static string? PreservationReason(string encoded) => encoded.Contains(Quote)
        ? "名前に全角の記号（：？＊｜＜＞＼＂）か「‛」を含むファイルは、自動では再アップロードせず、退避して残します。" : null;

    private static bool DecodeComponent(string encoded, out string decoded)
    {
        decoded = "";
        if (encoded.EndsWith('\uff0e') || encoded.EndsWith('\u2420')) return false;
        var result = new StringBuilder();
        for (int i = 0; i < encoded.Length; i++)
        {
            char c = encoded[i];
            int n = Wide.IndexOf(c);
            if (n >= 0) c = Ascii[n];
            else if (char.IsControl(c) || c is >= '\ue000' and <= '\uf8ff' || c is >= '\u2400' and <= '\u2421' && c != '\u2420') return false;
            result.Append(c);
        }
        decoded = result.ToString(); return true;
    }
    internal static string EncodeForDisk(string relative) => string.Join('/', relative.Split('/').Select(EncodeComponent));
    private static string EncodeComponent(string component)
    {
        string suffix = "";
        if (component.Length > 0)
        {
            char last = component[^1];
            if (last is '.' or ' ' or '\uff0e' or '\u2420')
            {
                component = component[..^1];
                suffix = last switch { '.' => "\uff0e", ' ' => "\u2420", _ => Quote.ToString() + last };
            }
        }
        var result = new StringBuilder();
        foreach (char c in component)
        {
            int n = Ascii.IndexOf(c);
            if (n >= 0) result.Append(Wide[n]);
            else { if (c == Quote || Wide.Contains(c)) result.Append(Quote); result.Append(c); }
        }
        return result.Append(suffix).ToString();
    }
}
