using System.Text;

namespace DbxLight.Core;

/// <summary>Measured rclone 1.74.3 Windows cache spellings; unknown encodings stay unknown.</summary>
internal static class CacheNames
{
    private const char Quote = '\u201b';
    private const string Quoted = "\uff1a\uff1f\uff0a\uff5c\uff1c\uff1e\uff3c\uff02";

    internal static bool TryEncode(string relative, out string encoded)
    {
        encoded = "";
        var result = new StringBuilder();
        foreach (string part in relative.Split('/'))
        {
            // Trailing-character encodings and WinFsp private-use substitutions were
            // not measured by the saved cache-name probe; do not guess an absent file.
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.EndsWith('\uff0e') || part.EndsWith('\u2420') || part.IndexOfAny([':', '\\', '"', '<', '>', '*', '?', '|']) >= 0)
                return false;
            for (int i = 0; i < part.Length; i++)
            {
                char c = part[i];
                if (char.IsControl(c) || c is >= '\ue000' and <= '\uf8ff' ||
                    c is >= '\u2400' and <= '\u241f' && c != '\u2401' || c == '\u2421') return false;
                // Standard-encoding quote sequences have context-sensitive meanings.
                if (c == Quote && i + 1 < part.Length &&
                    (part[i + 1] == Quote || Quoted.Contains(part[i + 1]) || part[i + 1] is '\uff0f' or '\uff0e' or '\u2420' ||
                        part[i + 1] is >= '\u2400' and <= '\u2421')) return false;
                if (c == Quote || Quoted.Contains(c)) result.Append(Quote);
                result.Append(c);
            }
            result.Append('/');
        }
        encoded = result.ToString(0, result.Length - 1);
        return true;
    }

    internal static bool TryDecode(string encoded, out string relative)
    {
        relative = "";
        var result = new StringBuilder();
        for (int i = 0; i < encoded.Length; i++)
        {
            char c = encoded[i];
            if (c == Quote)
            {
                if (++i >= encoded.Length || encoded[i] != Quote && !Quoted.Contains(encoded[i])) return false;
                c = encoded[i];
            }
            else if (Quoted.Contains(c)) return false;
            result.Append(c);
        }
        string decoded = result.ToString();
        if (!TryEncode(decoded, out string check) || check != encoded) return false;
        relative = decoded;
        return true;
    }

    internal static string? PathIn(string root, string relative) => TryEncode(relative, out string encoded)
        ? MountMetadata.Extended(Path.Combine(root, encoded.Replace('/', '\\'))) : null;
}
