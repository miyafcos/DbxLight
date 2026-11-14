namespace DbxLight.Core;

public sealed record AgentResolution(bool Ok, string LocalPath, string RemotePath, string? WebUrl, DbxStatus Status, bool RequiresExtendedPath = false);

public sealed partial class AgentMetadata
{
    public AgentResolution Resolve(string path)
    {
        if (path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !(uri.Host.Equals("www.dropbox.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("dropbox.com", StringComparison.OrdinalIgnoreCase)) ||
                !(uri.AbsolutePath == "/home" || uri.AbsolutePath.StartsWith("/home/", StringComparison.Ordinal)))
                throw new ArgumentException("Use an HTTPS Dropbox /home/ URL without a query or fragment.");
            string escaped = uri.AbsolutePath.Length <= 6 ? "" : uri.AbsolutePath[6..];
            string[] parts = escaped.Split('/').Select(Uri.UnescapeDataString).ToArray();
            if (parts.Any(p => p.IndexOfAny(['/', '\\', ':']) >= 0)) throw new ArgumentException("URL contains an encoded path separator or drive prefix.");
            var mapping = webMapping.Value ?? throw new AgentCommandException(3, "web_mapping_unknown",
                "A Dropbox mapping cannot be verified for this remote; use a cloud-relative path.", Status);
            path = mapping.Relative(string.Join('/', parts));
        }
        string relative = Relative(path); EnsureVisible(relative);
        string local = Path.Combine(MountPaths.CanonicalMount(Config.MountPoint), relative.Replace('/', '\\'));
        bool extended = relative.Split('/').Any(part => part.EndsWith('.') || part.EndsWith(' '));
        if (extended) local = MountMetadata.Extended(local);
        return new(true, local, relative, webMapping.Value?.Url(relative), Status, extended);
    }
}
