using System.Security.Cryptography;
using System.Text.Json;
using static DbxLight.Core.DropboxFolderClient;

namespace DbxLight.Core;

internal static class FolderSelectionHistory
{
    private sealed record Checkpoint(string MountKey, string Version, string Cursor, string FolderId,
        Dictionary<string, string> FolderIds, bool Replaced = false);

    internal static async Task VerifyAsync(DropboxFolderClient client, FolderShareTarget target, FolderMountIdentity? mount,
        CancellationToken token)
    {
        // Both sides come from Dropbox in the same namespace. Windows casing and
        // normalization cannot reproduce Dropbox's path_lower rules.
        string path = target.PathLower ?? "";
        if (mount?.DropboxCursor is not { Length: > 0 } start || !IsPath(path) ||
            path.Count(c => c == '/') != target.Path.Count(c => c == '/'))
            throw Unverified();

        string mountKey = Hash(mount);
        string fileKey = Hash(new { State = mount.StatePath.ToUpperInvariant(), Path = path });
        string file = Path.Combine(Path.GetDirectoryName(mount.StatePath)!, "share-selection", fileKey + ".json");
        Checkpoint? saved = Read(file);
        Checkpoint history = saved?.MountKey == mountKey ? saved :
            new(mountKey, "", start, target.FolderId, new(StringComparer.Ordinal));
        if (history.Replaced) throw Unverified();
        var folderIds = new Dictionary<string, string>(history.FolderIds, StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal) { history.Cursor };
        string ns = target.Account.NamespaceId;
        bool dirty = history != saved;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                JsonElement changes;
                try { changes = await client.ContinueListAsync(history.Cursor, ns, token).ConfigureAwait(false); }
                catch (FolderOperationException e) when (e.Code is "remote_error" or "not_found" or "invalid_response")
                { throw Unverified(); }
                string? next = Text(changes, "cursor");
                if (next is null || !changes.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                    !changes.TryGetProperty("has_more", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    more.GetBoolean() && !cursors.Add(next))
                    throw Unverified();

                foreach (var entry in entries.EnumerateArray())
                {
                    string? changed = Text(entry, "path_lower");
                    string? tag = Tag(entry);
                    if (!IsPath(changed) || tag is not ("file" or "folder" or "deleted"))
                        throw Unverified();
                    if (path != changed && !(path.StartsWith(changed!, StringComparison.Ordinal) && path[changed!.Length] == '/'))
                        continue;

                    string? id = tag == "folder" ? Text(entry, "id") : null;
                    if (id is null || target.HasAlternativeSpelling ||
                        changed == path && (id != history.FolderId || id != target.FolderId) ||
                        folderIds.TryGetValue(changed!, out string? prior) && prior != id)
                    {
                        history = history with { Replaced = true };
                        dirty = true;
                        throw Unverified();
                    }
                    dirty |= folderIds.TryAdd(changed!, id);
                }
                dirty |= history.Cursor != next;
                history = history with { Cursor = next };
                if (!more.GetBoolean()) break;
            }

            if (history.FolderId != target.FolderId) throw Unverified();

            foreach (var (parent, id) in folderIds)
            {
                if (parent != path && await ReadFolderIdAsync(client, parent, ns, token).ConfigureAwait(false) != id)
                {
                    history = history with { Replaced = true };
                    dirty = true;
                    throw Unverified();
                }
            }

            // An observed creation must also be under the current, unchanged parent.
            // Keep the parent's ID so future pages cannot treat its replacement as new.
            int parentEnd = path.LastIndexOf('/');
            if (parentEnd > 0 && folderIds.ContainsKey(path) && !folderIds.ContainsKey(path[..parentEnd]))
            {
                string parent = path[..parentEnd];
                folderIds.Add(parent, await ReadFolderIdAsync(client, parent, ns, token).ConfigureAwait(false));
                dirty = true;
            }
        }
        finally
        {
            // A retry can resume even after a timeout, but never advance without
            // saving the evidence that came with the cursor.
            Publish(file, fileKey, saved, dirty ? history with { FolderIds = folderIds } : null);
        }
    }

    private static async Task<string> ReadFolderIdAsync(DropboxFolderClient client, string path, string ns, CancellationToken token)
    {
        JsonElement metadata;
        try { metadata = await client.MetadataAsync(path, ns, token).ConfigureAwait(false); }
        catch (FolderOperationException e) when (e.Code is "not_found" or "invalid_response") { throw Unverified(); }
        return Tag(metadata) == "folder" && Text(metadata, "path_lower") == path && Text(metadata, "id") is { } id
            ? id : throw Unverified();
    }

    private static bool IsPath(string? path) => path is { Length: > 1 } && path[0] == '/' &&
        !path.Any(char.IsControl) && !path.AsSpan(1).Contains("//", StringComparison.Ordinal) && path[^1] != '/' &&
        !path.Split('/').Skip(1).Any(component => component is "." or "..");

    private static Checkpoint? Read(string file)
    {
        try
        {
            RescuePaths.Ancestors(file);
            var history = PrivateJsonFile.Read<Checkpoint>(file);
            if (history is not null && (string.IsNullOrEmpty(history.Version) || string.IsNullOrEmpty(history.Cursor) ||
                string.IsNullOrEmpty(history.FolderId) || history.FolderIds is null))
                throw Unverified();
            return history;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { throw Unverified(); }
    }

    private static void Publish(string path, string fileKey, Checkpoint? saved, Checkpoint? updated)
    {
        using var gate = new Mutex(false, @"Local\DbxLight.ShareSelection." + fileKey);
        bool held;
        try { held = gate.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw Unverified();
        try
        {
            // A concurrent process may have recorded more evidence. Retry from its
            // checkpoint instead of replacing it with the result of an older read.
            if (Read(path)?.Version != saved?.Version) throw FolderOperationException.For("sharing_changed");
            if (updated is not null)
                PrivateJsonFile.Write(path, updated with { Version = Guid.NewGuid().ToString("N") });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Unverified(); }
        finally { gate.ReleaseMutex(); }
    }

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static FolderOperationException Unverified() => FolderOperationException.For("selection_unverified");
}
