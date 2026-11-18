using System.Security.Cryptography;

namespace DbxLight.Core;

// A fresh account read is evidence for this generation only when the credential
// source before child launch and after VFS readiness is exactly the same.
internal sealed class FolderMountCredentialCapture(SnapshotTokenBroker broker, byte[] nonce, SnapshotCredentialSource source,
    string? initialBinding, string? cursor)
{
    internal string? CursorFor(string? binding) => binding is not null && binding == initialBinding ? cursor : null;

    internal static FolderMountCredentialCapture? Begin(DbxConfig config, CancellationToken token,
        Func<ISnapshotTokenBroker, DropboxFolderClient>? open = null)
    {
        try
        {
            if (Path.IsPathFullyQualified(config.Remote)) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            var broker = new SnapshotTokenBroker(config);
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            var source = broker.CopySourceAsync(nonce, deadline.Token).GetAwaiter().GetResult();
            if (source is null) return null;
            string? binding = null, cursor = null;
            try
            {
                using var client = open?.Invoke(broker) ?? new DropboxFolderClient(broker, allowRenewal: false);
                var account = client.AccountAsync(deadline.Token).GetAwaiter().GetResult();
                cursor = client.LatestFolderCursorAsync(account.NamespaceId, deadline.Token).GetAwaiter().GetResult();
                binding = FolderShareMount.BindAccount(account, source.Path);
            }
            catch (Exception e) when (e is FolderOperationException or OperationCanceledException) { }
            return new(broker, nonce, source, binding, cursor);
        }
        catch (Exception) { return null; }
    }

    internal string? Confirm(string? activeFile, CancellationToken token,
        Func<ISnapshotTokenBroker, DropboxFolderClient>? open = null)
    {
        if (!string.Equals(source.Path, activeFile, StringComparison.OrdinalIgnoreCase)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            if (source != broker.CopySourceAsync(nonce, deadline.Token).GetAwaiter().GetResult()) return null;
            using var client = open?.Invoke(broker) ?? new DropboxFolderClient(broker, allowRenewal: false);
            var account = client.AccountAsync(deadline.Token).GetAwaiter().GetResult();
            return source == broker.CopySourceAsync(nonce, deadline.Token).GetAwaiter().GetResult()
                ? FolderShareMount.BindAccount(account, source.Path) : null;
        }
        catch (Exception e) when (e is FolderOperationException or OperationCanceledException) { return null; }
    }
}
