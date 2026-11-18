using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

internal sealed record DropboxPathSelection(string Root, IReadOnlyList<string[]> Components)
{
    // The Dropbox encoder and rclone's Standard encoder differ for a literal full-width
    // backslash, control symbols and trailing spaces. Preserve only reversible spellings.
    private const char Quote = '\u201b';
    private const string WinFspCharacters = "\u0022*:<>?|\\";
    private const int MaxCandidateReads = 64;

    internal bool NeedsLookup => Components.Any(component => component.Length > 1);

    internal static DropboxPathSelection FromMount(string remotePrefix, string relative)
    {
        var components = new List<string[]>();
        int reads = 0;
        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            char[] standard = part.ToCharArray();
            var escaped = new List<int>();
            for (int i = 0; i < part.Length; i++)
            {
                char c = part[i];
                if (c is not (>= '\uf000' and <= '\uf0ff')) continue;
                char plain = (char)(c - '\uf000');
                if (!WinFspCharacters.Contains(plain) || escaped.Count == 4) throw Ambiguous();
                escaped.Add(i);
                standard[i] = plain;
            }
            int count = 1 << escaped.Count;
            if (count > 1 && (reads += count) > MaxCandidateReads) throw Ambiguous();
            var spellings = new string[count];
            // First try the decoded Windows spelling, then mixed and entirely literal spellings.
            for (int mask = 0; mask < count; mask++)
            {
                char[] spelling = (char[])standard.Clone();
                for (int i = 0; i < escaped.Count; i++)
                    if ((mask & (1 << i)) != 0) spelling[escaped[i]] = part[escaped[i]];
                spellings[mask] = RcloneName(new string(spelling));
            }
            components.Add(spellings);
        }
        return new(RclonePath(remotePrefix), components);
    }

    internal string LocalOnlyPath() => NeedsLookup ? throw Ambiguous() :
        Append(Root, string.Join('/', Components.Select(component => component[0])));

    internal static string RclonePath(string path)
    {
        if (path.Length == 0) return "";
        return string.Join('/', path.Split('/').Select(RcloneName));
    }

    private static string RcloneName(string name)
    {
        if (name is "" or "." or ".." || name.EndsWith(' ') || name.EndsWith('.') || name.EndsWith('\u2420'))
            throw Ambiguous();
        var result = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsControl(c) || c is >= '\u2401' and <= '\u241f' || c is '\u2400' or '\u2421')
                throw Ambiguous();
            if (c == Quote)
            {
                if (++i == name.Length) throw Ambiguous();
                c = name[i];
                if (c is >= '\u2401' and <= '\u241f') { result.Append(c); continue; }
                if (c is not (Quote or '\uff0f' or '\u2400' or '\u2421')) throw Ambiguous();
                result.Append(Quote).Append(c);
                continue;
            }
            if (c == '\uff3c') result.Append(Quote);
            result.Append(c == '\\' ? '\uff3c' : c);
        }
        return result.ToString();
    }

    private static string Append(string path, string name) => path.Length == 0 ? name : name.Length == 0 ? path : path + "/" + name;
    private static FolderOperationException Ambiguous() => FolderOperationException.For("ambiguous_name");

    internal async Task<(string Path, JsonElement Metadata)> ResolveAsync(DropboxFolderClient client, string ns, CancellationToken token)
    {
        string path = Root;
        JsonElement metadata = default;
        for (int i = 0; i < Components.Count; i++)
        {
            string[] spellings = Components[i];
            metadata = default;
            if (spellings.Length == 1)
            {
                path = Append(path, spellings[0]);
                continue;
            }
            string? match = null;
            foreach (string spelling in spellings)
            {
                string candidate = Append(path, spelling);
                JsonElement item;
                try { item = await client.MetadataAsync("/" + candidate, ns, token).ConfigureAwait(false); }
                catch (FolderOperationException e) when (e.Code == "not_found") { continue; }
                if (match is not null || DropboxFolderClient.Tag(item) is not ("folder" or "file")) throw Ambiguous();
                match = candidate;
                metadata = item;
            }
            if (match is null || i < Components.Count - 1 && DropboxFolderClient.Tag(metadata) != "folder")
                throw Ambiguous();
            path = match;
        }
        return (path, metadata);
    }
}
