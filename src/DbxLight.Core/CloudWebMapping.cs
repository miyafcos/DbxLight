namespace DbxLight.Core;

internal sealed record CloudWebMapping(string Root)
{
    internal string Url(string relative) => CloudWebPaths.WebUrl(Join(Root, relative));

    internal static async Task<CloudWebMapping> ReadAsync(DbxConfig config, FolderShareMount mount,
        Func<DbxConfig, DropboxFolderClient> open, CancellationToken token)
    {
        if (Path.IsPathFullyQualified(config.Remote)) throw FolderOperationException.For("web_location_unknown");
        var before = await mount.ReadBoundConnectionAsync(config, token).ConfigureAwait(false);
        var identity = before.Identity;
        using var client = open(identity is null ? config : config with { RcloneConfigPath = identity.RcloneConfigPath });
        var account = await client.AccountAsync(token).ConfigureAwait(false);
        if (identity is not null && !identity.Matches(account))
            throw FolderOperationException.For("mount_unverified");
        // A custom app can be restricted to an App Folder. The account's home
        // metadata does not establish where that app's files appear in Dropbox Web.
        if (client.UsesCustomApp) throw FolderOperationException.For("web_location_unknown");

        string prefix = "";
        if (account.NamespaceKind == "explicit_namespace")
        {
            var share = await client.SharedMetadataAsync(account.NamespaceId, account.RootNamespaceId, token).ConfigureAwait(false);
            if (DropboxFolderClient.Text(share, "shared_folder_id") != account.NamespaceId)
                throw FolderOperationException.For("web_location_unknown");
            prefix = WebPrefix(DropboxFolderClient.Text(share, "path_display") ?? DropboxFolderClient.Text(share, "path_lower"));
            var rootEntry = await client.MetadataAsync("/" + prefix, account.RootNamespaceId, token).ConfigureAwait(false);
            if (DropboxFolderClient.Tag(rootEntry) != "folder" || DropboxFolderClient.OwnShareId(rootEntry) != account.NamespaceId)
                throw FolderOperationException.For("web_location_unknown");
        }
        else if (account.NamespaceId == account.HomeNamespaceId && account.HomeNamespaceId != account.RootNamespaceId)
        {
            prefix = WebPrefix(account.HomePath);
        }
        string root = Join(prefix, DropboxPathSelection.RclonePath(client.RemotePrefix));
        if (before != await mount.ReadBoundConnectionAsync(config, token).ConfigureAwait(false))
            throw FolderOperationException.For("mount_unverified");
        return new(root);
    }

    private static string Join(string root, string relative) =>
        root.Length == 0 ? relative : relative.Length == 0 ? root : root + "/" + relative;

    private static string WebPrefix(string? path)
    {
        if (path is not { Length: > 1 } || path[0] != '/' ||
            path[1..].Split('/').Any(part => part is "" or "." or ".." || part.Any(char.IsControl)))
            throw FolderOperationException.For("web_location_unknown");
        return path[1..];
    }

    internal string Relative(string cloudPath)
    {
        cloudPath = HiddenFolders.NormalizePath(cloudPath, allowRoot: true);
        string root = Root.Normalize();
        if (root.Length == 0) return cloudPath;
        if (cloudPath.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        if (cloudPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return cloudPath[(root.Length + 1)..];
        throw new ArgumentException("The URL is outside the configured remote subfolder.");
    }
}
