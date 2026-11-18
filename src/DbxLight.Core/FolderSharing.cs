using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using static DbxLight.Core.DropboxFolderClient;

namespace DbxLight.Core;

internal sealed record FolderMember(string Kind, string Name, string? Email, string Access);
internal sealed record FolderShareStatus(bool Shared, bool Inherited, string? Access, IReadOnlyList<FolderMember> Members);
internal sealed record FolderShareTarget(DropboxFolderAccount Account, string Path, string FolderId, string? ShareId, string? ParentShareId, string? PathLower, bool HasAlternativeSpelling);
internal sealed record FolderShareView(FolderShareTarget Target, FolderShareStatus Status);

internal sealed record FolderInviteRequest(string[] To, string Access, string? Message)
{
    internal static FolderInviteRequest Create(IEnumerable<string> recipients, string access, string? message)
    {
        if (access is not ("viewer" or "editor")) throw new ArgumentException("権限は viewer または editor を指定してください。");
        string[] emails = recipients.Select(email => email.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (emails.Length == 0 || emails.Any(email => email.Any(char.IsControl) ||
            !MailAddress.TryCreate(email, out var parsed) || !parsed.Address.Equals(email, StringComparison.Ordinal) ||
            !parsed.Host.Contains('.') || parsed.Host.StartsWith('.') || parsed.Host.EndsWith('.')))
            throw FolderOperationException.For("invalid_email");
        return new(emails, access, string.IsNullOrWhiteSpace(message) ? null : message);
    }
}

internal sealed record FolderInvitePlan(FolderShareTarget Target, string Path, FolderInviteRequest Request)
{
    internal bool WouldShareFolder => Target.ShareId is null;
    internal bool Inherited => WouldShareFolder && Target.ParentShareId is not null;

    internal string Confirmation(FolderMountIdentity mount) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 3, mount, Target.Account.AccountId, Target.Account.NamespaceId, Target.Account.RootNamespaceId,
            Target.Account.HomeNamespaceId, Target.Path, Target.PathLower, Target.FolderId, Target.ShareId, Target.ParentShareId,
            Request.To, Request.Access, Request.Message
        }))).ToLowerInvariant();
}

internal sealed record FolderInviteResult(string Path, string Access, bool SharedFolderCreated, string SharedFolderState,
    IReadOnlyList<string> Invited, string InvitationState)
{
    internal string Progress => InvitationState switch
    {
        "sent" => SharedFolderCreated ? "共有フォルダにして、招待を送りました。" : "招待を送りました。",
        "unknown" => SharedFolderCreated
            ? "共有フォルダになりました。招待が送られたかは確認できていません。"
            : "招待が送られたかは確認できていません。",
        _ when SharedFolderCreated => "共有フォルダになりました。招待は送っていません。",
        _ when SharedFolderState == "unknown" => "共有フォルダになったかは確認できていません。招待は送っていません。",
        _ => "招待は送っていません。"
    };
}

internal static class FolderSharing
{
    internal const string InheritedNotice = "親のフォルダはすでに共有されています。この下のフォルダだけを共有します。";
    internal static async Task<FolderShareView> ShowAsync(DropboxFolderClient client, string relative, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var target = await TargetAsync(client, relative, deadline.Token).ConfigureAwait(false);
            bool directlyShared = target.ShareId is not null;
            bool inherited = !directlyShared && target.ParentShareId is not null;
            string? source = target.ShareId ?? target.ParentShareId;
            if (source is null) return new(target, new(false, false, null, []));

            string ns = target.Account.NamespaceId;
            var metadata = await client.SharedMetadataAsync(source, ns, deadline.Token).ConfigureAwait(false);
            if (Text(metadata, "shared_folder_id") != source) throw FolderOperationException.For("invalid_response");
            var members = await MembersAsync(client, source, inherited ? target.Path : null, ns, deadline.Token).ConfigureAwait(false);
            return new(target, new(directlyShared, inherited, Tag(Object(metadata, "access_type")), members));
        }
        catch (FolderOperationException e) when (e.Code == "cancelled" && !token.IsCancellationRequested)
        { throw FolderOperationException.For("timeout"); }
    }

    internal static FolderInvitePlan Plan(FolderShareView view, string path, FolderInviteRequest request)
    {
        if (view.Status.Shared && view.Status.Members.Any(member => member.Email is not null &&
            request.To.Contains(member.Email, StringComparer.OrdinalIgnoreCase)))
            throw FolderOperationException.For("already_member");
        return new(view.Target, path, request);
    }

    internal static async Task VerifyOriginalPathAsync(DropboxFolderClient client, FolderShareTarget target, CancellationToken token)
    {
        var latest = await ReadExactAsync(client, target.Account, target.Path, target.HasAlternativeSpelling, token).ConfigureAwait(false);
        if (!SameTarget(latest, target) || latest.ShareId != target.ShareId || latest.ParentShareId != target.ParentShareId)
            throw FolderOperationException.For("sharing_changed");
    }

    internal static async Task<(FolderInviteResult Result, FolderOperationException? Error)> InviteAsync(
        DropboxFolderClient client, string relative, FolderInvitePlan plan, FolderMountIdentity? mount,
        Func<DropboxFolderAccount, CancellationToken, Task> verifyMount,
        Func<DropboxFolderAccount, IDisposable?> verifyLatest, CancellationToken token)
    {
        string shared = plan.WouldShareFolder ? "not_created" : "existing";
        string invitation = "not_sent";
        string[] invited = [];
        FolderOperationException? error = null;
        string stage = "recheck";
        try
        {
            var current = await ShowAsync(client, relative, token).ConfigureAwait(false);
            if (!SameTarget(current.Target, plan.Target) || current.Target.ShareId != plan.Target.ShareId ||
                current.Target.ParentShareId != plan.Target.ParentShareId)
                throw FolderOperationException.For("sharing_changed");
            Plan(current, plan.Path, plan.Request);
            string ns = current.Target.Account.NamespaceId;
            string? shareId = current.Target.ShareId;
            var latest = await RecheckAsync().ConfigureAwait(false);
            if (shareId is null)
            {
                stage = "share";
                shareId = await client.ShareAsync(latest.FolderId, ns, () => verifyLatest(latest.Account), token).ConfigureAwait(false);
                shared = "created";
                stage = "recheck";
                latest = await RecheckAsync().ConfigureAwait(false);
            }

            stage = "invite";
            await client.AddMembersAsync(shareId, plan.Request, ns, () => verifyLatest(latest.Account), token).ConfigureAwait(false);

            async Task<FolderShareTarget> RecheckAsync()
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                FolderShareTarget target;
                try
                {
                    await FolderSelectionHistory.VerifyAsync(client, plan.Target, mount, deadline.Token).ConfigureAwait(false);
                    await verifyMount(plan.Target.Account, deadline.Token).ConfigureAwait(false);
                    target = await TargetAsync(client, relative, deadline.Token, freshAccount: true).ConfigureAwait(false);
                }
                catch (FolderOperationException e) when (e.Code is "not_found" or "not_folder" or "ambiguous_name")
                { throw FolderOperationException.For("sharing_changed"); }
                catch (FolderOperationException e) when (e.Code == "cancelled" && !token.IsCancellationRequested)
                { throw FolderOperationException.For("timeout"); }
                if (!SameTarget(target, plan.Target) || target.ShareId != shareId ||
                    shareId is not null && shareId == plan.Target.ParentShareId ||
                    target.ParentShareId != plan.Target.ParentShareId)
                    throw FolderOperationException.For("sharing_changed");
                return target;
            }
            invited = plan.Request.To;
            invitation = "sent";
        }
        catch (FolderOperationException e)
        {
            error = e;
            if (e.OutcomeUnknown && stage == "share") shared = "unknown";
            if (e.OutcomeUnknown && stage == "invite") invitation = "unknown";
        }
        return (new(plan.Path, plan.Request.Access, shared == "created", shared, invited, invitation), error);
    }

    private static bool SameTarget(FolderShareTarget left, FolderShareTarget right) =>
        left.Account.SameBinding(right.Account) && left.Path == right.Path && left.PathLower == right.PathLower && left.FolderId == right.FolderId;

    private static async Task<FolderShareTarget> TargetAsync(DropboxFolderClient client, string relative, CancellationToken token,
        bool freshAccount = false)
    {
        var selection = DropboxPathSelection.FromMount(client.RemotePrefix, relative);
        if (selection.Root.Length == 0 && selection.Components.Count == 0) throw FolderOperationException.For("namespace_root");
        var account = await client.AccountAsync(token, freshAccount).ConfigureAwait(false);
        var (cloudPath, metadata) = await selection.ResolveAsync(client, account.NamespaceId, token).ConfigureAwait(false);
        string path = "/" + cloudPath;
        // A later, rejected spelling may have taken time after reading the chosen candidate.
        if (freshAccount || metadata.ValueKind == JsonValueKind.Undefined)
            return await ReadExactAsync(client, account, path, selection.NeedsLookup, token).ConfigureAwait(false);
        return ParseTarget(account, path, metadata, selection.NeedsLookup);
    }

    private static async Task<FolderShareTarget> ReadExactAsync(DropboxFolderClient client, DropboxFolderAccount account,
        string path, bool hasAlternativeSpelling, CancellationToken token) =>
        ParseTarget(account, path, await client.MetadataAsync(path, account.NamespaceId, token).ConfigureAwait(false), hasAlternativeSpelling);

    private static FolderShareTarget ParseTarget(DropboxFolderAccount account, string path, JsonElement metadata, bool hasAlternativeSpelling)
    {
        if (Required(metadata, ".tag") != "folder") throw FolderOperationException.For("not_folder");
        string? share = OwnShareId(metadata);
        string? parent = Text(Object(metadata, "sharing_info"), "parent_shared_folder_id") ?? Text(metadata, "parent_shared_folder_id");
        if (share is not null && share == parent) throw FolderOperationException.For("invalid_response");
        return new(account, path, Required(metadata, "id"), share, parent, Text(metadata, "path_lower"), hasAlternativeSpelling);
    }

    private static async Task<IReadOnlyList<FolderMember>> MembersAsync(DropboxFolderClient client, string id, string? path, string ns, CancellationToken token)
    {
        var page = await client.MembersAsync(id, path, ns, token).ConfigureAwait(false);
        var result = new List<FolderMember>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            foreach (var (field, kind) in new[] { ("users", "user"), ("groups", "group"), ("invitees", "invitee") })
            {
                if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty(field, out var entries) || entries.ValueKind != JsonValueKind.Array)
                    throw FolderOperationException.For("invalid_response");
                foreach (var entry in entries.EnumerateArray())
                {
                    var item = Object(entry, kind);
                    string? email = Text(item, "email") ?? Text(Object(entry, "user"), "email");
                    string name = Text(item, "display_name") ?? Text(item, "group_name") ?? email ?? "";
                    result.Add(new(kind, name, email, Tag(Object(entry, "access_type")) ?? "unknown"));
                }
            }
            string? cursor = Text(page, "cursor");
            if (cursor is null) return result;
            if (!cursors.Add(cursor)) throw FolderOperationException.For("invalid_response");
            page = await client.ContinueMembersAsync(cursor, ns, token).ConfigureAwait(false);
        }
    }

    internal static string AccessLabel(string? access) => access switch
    {
        "viewer" => "閲覧のみ", "editor" => "編集できる", "owner" => "所有者",
        "viewer_no_comment" => "閲覧のみ (コメント不可)",
        "traverse" => "配下の一部にアクセス可", "no_access" => "この場所へのアクセスなし", _ => "不明"
    };
}
