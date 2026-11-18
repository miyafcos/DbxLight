using System.Security.Cryptography;
using System.Text.Json;

namespace DbxLight.Core;

internal static class DropboxCopyPaths
{
    internal const string PrefixHelp = "Dropbox のパスの頭を自動で確認できませんでした。設定の「コピーするパスの頭」に入力してください。";

    internal static string? NormalizePrefix(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.All(c => c == ' ')) return null;
        string prefix = value.Trim(' ').TrimEnd('\\');
        if (!prefix.StartsWith('\\') ||
            prefix[1..].Split('\\').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("コピーするパスの頭は「\\サンプル社 Dropbox\\メンバー」の形で入力するか、空にしてください。");
        return prefix;
    }

    internal static string Relative(DbxConfig config, string path)
    {
        try
        {
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 &&
                char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\') path = path[4..];
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                !Path.IsPathFullyQualified(path))
                throw new ArgumentException();
            if (path.Split('\\').Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
                throw FolderOperationException.For("ambiguous_name");
            return MountPaths.ToRelative(path, config.MountPoint, allowRoot: true);
        }
        catch (ArgumentException)
        {
            throw new FolderOperationException("outside_mount", "マウント先の中にある項目の絶対パスを指定してください。", 2);
        }
    }

    private sealed record CachedPrefix(byte[] Nonce, string? Connection = null, SnapshotCredentialSource? Source = null,
        string? Binding = null, string? LegacyBinding = null, string? MountBinding = null, string? Automatic = null);

    internal static async Task<string[]> CopyAsync(DbxConfig config, IEnumerable<string> paths,
        Func<DbxConfig, DropboxFolderClient> open, CancellationToken token = default, FolderShareMount? mount = null)
    {
        string remotePrefix = RemotePrefix(config);
        var relative = paths.Select(path => Relative(config, path)).ToArray();
        var selections = relative.Select(path => DropboxPathSelection.FromMount(remotePrefix, path)).ToArray();
        if (selections.Length == 0) throw new ArgumentException("コピーする項目を選んでください。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var connectionObservation = mount is null ? default :
                await mount.ReadBoundConnectionAsync(config, deadline.Token).ConfigureAwait(false);
            var identity = connectionObservation.Identity;

            string? cacheFile = mount is null ? null : CacheFile(mount.ConfigPath);
            string connection = ConnectionKey(config);
            var saved = cacheFile is null ? null : ReadCache(cacheFile);
            var cached = saved?.Connection == connection ? saved : null;
            var effective = config with
            {
                RcloneConfigPath = identity?.RcloneConfigPath ?? config.RcloneConfigPath ?? cached?.Source?.Path
            };
            using var client = open(effective);
            byte[] nonce = saved?.Nonce ?? RandomNumberGenerator.GetBytes(32);
            var source = cacheFile is null ? null : await client.CopySourceAsync(nonce, deadline.Token).ConfigureAwait(false);
            if (source is null || !SameSource(source, cached?.Source) ||
                identity?.DropboxBinding is { } mounted && mounted != cached?.MountBinding)
                cached = null;

            string? prefix = cached is null ? null : ManualPrefix(config, cached.Binding, cached.LegacyBinding) ?? cached.Automatic;
            var copied = new string[selections.Length];
            CachedPrefix? updatedCache = null;
            if (prefix is null || selections.Any(item => item.NeedsLookup))
            {
                var account = await client.AccountAsync(deadline.Token).ConfigureAwait(false);
                if (identity?.DropboxBinding is not null && !identity.Matches(account))
                    throw FolderOperationException.For("mount_unverified");
                string binding = Binding(config, account);
                string legacy = LegacyBinding(config, account);
                string? manual = ManualPrefix(config, binding, legacy);
                string? automatic = cached?.Binding == binding ? cached.Automatic : null;
                prefix = manual ?? automatic ?? await AutomaticPrefixAsync(client, account, deadline.Token).ConfigureAwait(false);
                for (int i = 0; i < selections.Length; i++)
                {
                    if (identity is not null && selections[i].NeedsLookup)
                        await FolderShareMount.RequireVisibleAsync(config, relative[i], deadline.Token, folderOnly: false).ConfigureAwait(false);
                    var resolved = await selections[i].ResolveAsync(client, account.NamespaceId, deadline.Token).ConfigureAwait(false);
                    copied[i] = Join(prefix, resolved.Path);
                }
                if (source is not null && SameSource(source, await client.CopySourceAsync(nonce, deadline.Token).ConfigureAwait(false)))
                    updatedCache = new(nonce, connection, source, binding, legacy,
                        FolderShareMount.BindAccount(account, source.Path), manual is null ? prefix : automatic);
            }
            else
            {
                if (!SameSource(source!, await client.CopySourceAsync(nonce, deadline.Token).ConfigureAwait(false)))
                    throw FolderOperationException.For("copy_path_prefix_unknown");
                for (int i = 0; i < selections.Length; i++)
                    copied[i] = Join(prefix, selections[i].LocalOnlyPath());
            }

            if (mount is not null &&
                (!SameConnection(config, DropboxFolderClient.ReadConfiguration(mount.ConfigPath)) ||
                 connectionObservation != await mount.ReadBoundConnectionAsync(config, deadline.Token).ConfigureAwait(false)))
                throw FolderOperationException.For("sharing_changed");
            if (updatedCache is not null) SaveCache(mount!.ConfigPath, cacheFile!, saved?.Nonce, updatedCache);
            return copied;
        }
        catch (OperationCanceledException)
        {
            throw FolderOperationException.For(token.IsCancellationRequested ? "cancelled" : "timeout");
        }
        catch (FolderOperationException e) when (e.Code == "cancelled" && !token.IsCancellationRequested)
        {
            throw FolderOperationException.For("timeout");
        }
    }

    private static bool SameSource(SnapshotCredentialSource source, SnapshotCredentialSource? other) =>
        source.Key == other?.Key && string.Equals(source.Path, other.Path, StringComparison.OrdinalIgnoreCase);

    internal static string CacheFile(string configPath) =>
        Path.Combine(DbxConfig.LocalRoot, "copy-path", Hash(Path.GetFullPath(configPath).ToUpperInvariant()) + ".json");

    private static CachedPrefix? ReadCache(string path)
    {
        try
        {
            RescuePaths.Ancestors(path);
            var cache = PrivateJsonFile.Read<CachedPrefix>(path);
            return cache?.Nonce is { Length: 32 } ? cache : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private static void SaveCache(string configPath, string path, byte[]? previousNonce, CachedPrefix cache) =>
        UpdateCache(path, () =>
        {
            var current = ReadCache(path);
            bool sameGeneration = previousNonce is null ? current is null :
                current is not null && current.Nonce.AsSpan().SequenceEqual(previousNonce);
            if (sameGeneration && cache.Connection == ConnectionKey(DropboxFolderClient.ReadConfiguration(configPath)))
                PrivateJsonFile.Write(path, cache);
        });

    internal static void InvalidateCache(string configPath)
    {
        string path = CacheFile(configPath);
        UpdateCache(path, () => ClearCache(path));
    }

    internal static DbxConfig CompleteLogout(string configPath, Func<DbxConfig> commit)
    {
        string path = CacheFile(configPath);
        DbxConfig? saved = null;
        bool clearing = true;
        try
        {
            WithCacheLock(path, () =>
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { ClearCache(path); break; }
                    catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 && attempt < 10)
                    { Thread.Sleep(50); }
                }
                clearing = false;
                saved = commit();
            });
        }
        catch (Exception e) when (clearing && e is not OutOfMemoryException)
        {
            throw new InvalidOperationException("コピー用の記録を消去できませんでした。コピー操作を閉じてから、ログアウトし直してください。");
        }
        return saved!;
    }

    private static void ClearCache(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        PrivateJsonFile.Write(path, new CachedPrefix(RandomNumberGenerator.GetBytes(32)));
    }

    private static void UpdateCache(string path, Action action)
    {
        try { WithCacheLock(path, action); }
        catch (Exception e) when (e is not OutOfMemoryException) { } // Copy can work without optional cached state.
    }

    private static void WithCacheLock(string path, Action action)
    {
        RescuePaths.Ancestors(path);
        using var gate = new Mutex(false, @"Local\DbxLight.CopyPath." + Path.GetFileNameWithoutExtension(path));
        bool held;
        try { held = gate.WaitOne(TimeSpan.FromSeconds(1)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new ControlBusyException();
        try { action(); }
        finally { gate.ReleaseMutex(); }
    }

    internal static string RemotePrefix(DbxConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Remote)) throw FolderOperationException.For("not_configured");
        int colon = config.Remote.IndexOf(':');
        if (colon <= 0 || Path.IsPathFullyQualified(config.Remote)) throw FolderOperationException.For("unsupported_remote");
        return config.Remote[(colon + 1)..].Trim('/');
    }

    internal static DbxConfig ClearBinding(DbxConfig config) => config with { CopyPathPrefix = null, CopyPathBinding = null };

    internal static bool SameConnection(DbxConfig left, DbxConfig right) => ConnectionKey(left) == ConnectionKey(right);

    private static string? WindowsPath(string? path) =>
        path is null ? null : (Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path).ToUpperInvariant();

    private static string ConnectionKey(DbxConfig config) =>
        Hash(new { config.Remote, RclonePath = WindowsPath(config.RclonePath), RcloneConfigPath = WindowsPath(config.RcloneConfigPath) });

    internal static string Binding(DbxConfig config, DropboxFolderAccount account) =>
        "v2:" + Hash(new { Connection = ConnectionKey(config), account.AccountId, account.NamespaceId });

    private static string LegacyBinding(DbxConfig config, DropboxFolderAccount account) =>
        Hash(new { config.Remote, config.RclonePath, config.RcloneConfigPath, account.AccountId, account.NamespaceId });

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static string? ManualPrefix(DbxConfig config, string? binding, string? legacyBinding = null) =>
        config.CopyPathBinding is { } saved && (saved == binding || saved == legacyBinding) ? config.CopyPathPrefix : null;

    internal static async Task<DbxConfig> BindSettingsAsync(DbxConfig expected, DbxConfig next,
        Func<DbxConfig, DropboxFolderClient> open, CancellationToken token, bool rebind = false, FolderShareMount? mount = null)
    {
        if (next.CopyPathPrefix is null) return ClearBinding(next);
        bool sameConnection = SameConnection(expected, next);
        bool entered = rebind || next.CopyPathPrefix != expected.CopyPathPrefix || next.CopyPathBinding != expected.CopyPathBinding;
        if (!entered && !sameConnection) return ClearBinding(next);
        if (!entered && expected.CopyPathBinding is { } saved && (saved.StartsWith("v2:", StringComparison.Ordinal) ||
            expected.RcloneConfigPath == next.RcloneConfigPath && expected.RclonePath == next.RclonePath))
            return next;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var identity = sameConnection ? mount?.Observe(expected) : null;
        using var client = open(identity is null ? next : next with { RcloneConfigPath = identity.RcloneConfigPath });
        var account = await client.AccountAsync(deadline.Token).ConfigureAwait(false);
        if (identity?.DropboxBinding is not null && !identity.Matches(account))
            throw FolderOperationException.For("mount_unverified");
        if (!entered && ManualPrefix(expected, Binding(expected, account), LegacyBinding(expected, account)) is null)
            return ClearBinding(next);
        return next with { CopyPathBinding = Binding(next, account) };
    }

    internal static Task<string> PrefixAsync(DbxConfig config, DropboxFolderClient client, DropboxFolderAccount account,
        CancellationToken token) =>
        ManualPrefix(config, Binding(config, account), LegacyBinding(config, account)) is { } manual ? Task.FromResult(manual) :
            AutomaticPrefixAsync(client, account, token);

    private static async Task<string> AutomaticPrefixAsync(DropboxFolderClient client, DropboxFolderAccount account, CancellationToken token)
    {
        if (client.UsesCustomApp) throw FolderOperationException.For("copy_path_prefix_unknown");
        if (!account.IsTeam && account.NamespaceId == account.HomeNamespaceId && account.RootNamespaceId == account.HomeNamespaceId)
            return @"\Dropbox";
        if (account.TeamName is not { } team || account.RootNamespaceId == account.HomeNamespaceId)
            throw FolderOperationException.For("copy_path_prefix_unknown");

        string? prefix;
        try { prefix = NormalizePrefix(@"\" + team + " Dropbox"); }
        catch (ArgumentException) { throw FolderOperationException.For("copy_path_prefix_unknown"); }
        if (prefix is null || team.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw FolderOperationException.For("copy_path_prefix_unknown");
        if (account.NamespaceId == account.RootNamespaceId) return prefix;
        if (account.NamespaceId != account.HomeNamespaceId)
            throw FolderOperationException.For("copy_path_prefix_unknown");

        // home_path identifies the home namespace under the team root. Read that folder in
        // the root namespace for its actual casing; real home entries may have no sharing ID.
        if (account.HomePath is { } home && home.StartsWith('/') && home[1..].Length > 0 &&
            !home[1..].Contains('/'))
        {
            try
            {
                var metadata = await client.MetadataAsync(home, account.RootNamespaceId, token).ConfigureAwait(false);
                if (DropboxFolderClient.Tag(metadata) == "folder" && DropboxFolderClient.Text(metadata, "name") is { } name &&
                    name.Equals(home[1..], StringComparison.OrdinalIgnoreCase))
                    return AppendHome(name);
            }
            catch (FolderOperationException e) when (e.Code == "not_found") { }
        }
        // The home path can be absent. Only an explicit namespace match can replace it.
        var listing = await client.ListRootAsync(account.RootNamespaceId, token).ConfigureAwait(false);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (listing.ValueKind != JsonValueKind.Object || !listing.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                throw FolderOperationException.For("invalid_response");
            foreach (var entry in entries.EnumerateArray())
                if (DropboxFolderClient.Tag(entry) == "folder" &&
                    DropboxFolderClient.OwnShareId(entry) == account.HomeNamespaceId &&
                    DropboxFolderClient.Text(entry, "name") is { } name)
                    return AppendHome(name);
            if (!listing.TryGetProperty("has_more", out var more) || more.ValueKind == JsonValueKind.False)
                throw FolderOperationException.For("copy_path_prefix_unknown");
            if (more.ValueKind != JsonValueKind.True || !cursors.Add(DropboxFolderClient.Required(listing, "cursor")))
                throw FolderOperationException.For("invalid_response");
            listing = await client.ContinueListAsync(DropboxFolderClient.Required(listing, "cursor"),
                account.RootNamespaceId, token).ConfigureAwait(false);
        }

        string AppendHome(string name)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw FolderOperationException.For("copy_path_prefix_unknown");
            try { return NormalizePrefix(prefix + "\\" + name)!; }
            catch (ArgumentException) { throw FolderOperationException.For("copy_path_prefix_unknown"); }
        }
    }

    internal static string Join(string prefix, string relative) =>
        relative.Length == 0 ? prefix : prefix + "\\" + relative.Replace('/', '\\');
}
