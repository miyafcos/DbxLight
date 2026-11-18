namespace DbxLight.Core;

internal sealed record ShareFolderInfo(DbxConfig Config, string Relative, string LocalPath, FolderShareView View,
    FolderMountIdentity? Mount, string? CopyPath, string? PathError, bool CheckCopyPath = false);

internal sealed class ShareFolderSession(string path, FolderShareMount mount,
    Func<DbxConfig, DropboxFolderClient> open, Func<bool>? canSend = null)
{
    internal async Task<ShareFolderInfo> LoadAsync(bool requireMount, bool includeCopyPath, CancellationToken token,
        bool verifySelection = true)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var config = DropboxFolderClient.ReadConfiguration(mount.ConfigPath);
            string relative = DropboxCopyPaths.Relative(config, path);
            string localPath = Path.Combine(MountPaths.CanonicalMount(config.MountPoint), relative.Replace('/', '\\'));
            var identity = await Task.Run(() => mount.Observe(config), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (requireMount && identity?.DropboxBinding is null) throw FolderOperationException.For("mount_unverified");
            if (identity is not null && (requireMount || includeCopyPath))
                await FolderShareMount.RequireVisibleAsync(config, relative, deadline.Token).ConfigureAwait(false);
            using var client = OpenFor(config, identity);
            var account = await client.AccountAsync(deadline.Token).ConfigureAwait(false);
            bool verified = identity?.Matches(account) == true;
            if (requireMount && !verified) throw FolderOperationException.For("mount_unverified");
            var view = await FolderSharing.ShowAsync(client, relative, deadline.Token).ConfigureAwait(false);
            string? copyPath = null, pathError = null;
            if (includeCopyPath)
            {
                try
                {
                    string prefix = await DropboxCopyPaths.PrefixAsync(config, client, account, deadline.Token).ConfigureAwait(false);
                    copyPath = DropboxCopyPaths.Join(prefix, view.Target.Path[1..]);
                }
                catch (FolderOperationException e) when (e.Code != "cancelled")
                {
                    pathError = e.Code == "copy_path_prefix_unknown" ? e.Message : e.Message + " " + DropboxCopyPaths.PrefixHelp;
                }
            }
            if (verified && (requireMount || includeCopyPath))
            {
                try
                {
                    if (verifySelection)
                        await FolderSelectionHistory.VerifyAsync(client, view.Target, identity, deadline.Token).ConfigureAwait(false);
                    await FolderSharing.VerifyOriginalPathAsync(client, view.Target, deadline.Token).ConfigureAwait(false);
                    await mount.CheckAsync(config, identity!, relative, includeCopyPath, deadline.Token).ConfigureAwait(false);
                }
                catch (FolderOperationException e) when (!requireMount && e.Code != "cancelled")
                {
                    // The sharing status can still be displayed. Disable the invite control.
                    copyPath = null;
                    pathError = e.Message;
                }
            }
            return new(config, relative, localPath, view, verified ? identity : null, copyPath, pathError, includeCopyPath);
        }
        catch (OperationCanceledException)
        { throw FolderOperationException.For(token.IsCancellationRequested ? "cancelled" : "timeout"); }
        catch (FolderOperationException e) when (e.Code == "cancelled" && !token.IsCancellationRequested)
        { throw FolderOperationException.For("timeout"); }
    }

    internal async Task<(FolderInviteResult Result, FolderOperationException? Error)> InviteAsync(
        ShareFolderInfo info, FolderInvitePlan plan, CancellationToken token)
    {
        using var client = OpenFor(info.Config, info.Mount);
        return await FolderSharing.InviteAsync(client, info.Relative, plan, info.Mount,
            VerifyMount, VerifyLatest, token).ConfigureAwait(false);

        IDisposable? VerifyLatest(DropboxFolderAccount account)
        {
            if (canSend?.Invoke() == false || info.Mount?.Matches(account) != true)
                throw FolderOperationException.For("mount_unverified");
            return mount.CheckStillCurrent(info.Config, info.Mount, info.CheckCopyPath);
        }

        async Task VerifyMount(DropboxFolderAccount account, CancellationToken cancellation)
        {
            if (canSend?.Invoke() == false || info.Mount?.Matches(account) != true)
                throw FolderOperationException.For("mount_unverified");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try { await mount.CheckAsync(info.Config, info.Mount, info.Relative, info.CheckCopyPath, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            { throw FolderOperationException.For(cancellation.IsCancellationRequested ? "cancelled" : "timeout"); }
        }
    }

    private DropboxFolderClient OpenFor(DbxConfig config, FolderMountIdentity? identity) =>
        open(identity is null ? config : config with { RcloneConfigPath = identity.RcloneConfigPath });
}
