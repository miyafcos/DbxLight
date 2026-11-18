using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

internal sealed record DropboxFolderAccount(string AccountId, string NamespaceId, string HomeNamespaceId,
    string RootNamespaceId, bool IsTeam, string? TeamName, string? HomePath, string? DisplayName = null)
{
    internal string NamespaceKind => NamespaceId == HomeNamespaceId ? (IsTeam ? "team_home" : "personal") :
        IsTeam && NamespaceId == RootNamespaceId ? "team_space" : "explicit_namespace";
    internal bool TeamSpace => NamespaceKind == "team_space";
    internal string ConnectionLabel => DisplayName + (NamespaceKind switch
    {
        "team_space" => "（チームの空間）",
        "team_home" => "（チームのホーム）",
        "personal" => "（個人の空間）",
        _ => "（指定した名前空間）"
    });

    internal bool SameBinding(DropboxFolderAccount other) =>
        AccountId == other.AccountId && NamespaceId == other.NamespaceId &&
        HomeNamespaceId == other.HomeNamespaceId && RootNamespaceId == other.RootNamespaceId && IsTeam == other.IsTeam;
}

internal sealed class DropboxFolderClient(ISnapshotTokenBroker broker, HttpMessageHandler? handler = null,
    bool allowRenewal = true) : IDisposable
{
    private const string PathRootHeader = "Dropbox-API-Path-Root";
    private readonly HttpClient http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private SnapshotToken? credential;
    private DropboxFolderAccount? account;
    private SnapshotToken? renewalAttempt;
    internal string RemotePrefix => broker.Remote.Prefix;
    // The mount and rclone helpers both strip inherited RCLONE_* options.
    internal bool UsesCustomApp => broker.Remote.UsesCustomApp;

    internal Task<SnapshotCredentialSource?> CopySourceAsync(byte[] nonce, CancellationToken token) =>
        broker.CopySourceAsync(nonce, token);

    private enum Route { Account, Metadata, ListRoot, ListContinue, LatestCursor, SharedMetadata, Members, MembersContinue, Share, ShareJob, AddMembers }

    internal static DropboxFolderClient Open(DbxConfig config) => Open(config, allowRenewal: true);
    internal static DropboxFolderClient OpenWithoutRenewal(DbxConfig config) => Open(config, allowRenewal: false);

    private static DropboxFolderClient Open(DbxConfig config, bool allowRenewal)
    {
        try { return new(new SnapshotTokenBroker(config), allowRenewal: allowRenewal); }
        catch (SnapshotException e) { throw FolderOperationException.For(e.Code == "unsafe_cloud_path" ? "unsupported_remote" : e.Code); }
    }

    internal static DbxConfig ReadConfiguration(string? path)
    {
        try { return DropboxSnapshotReader.ReadConfiguration(path); }
        catch (SnapshotException e) { throw FolderOperationException.For(e.Code); }
    }

    internal async Task<DropboxFolderAccount> AccountAsync(CancellationToken token, bool fresh = false)
    {
        if (account is not null && !fresh) return account;
        SnapshotRemote initial = broker.Remote;
        credential = await ReadCredentialAsync(token).ConfigureAwait(false);
        if (account is not null && initial != broker.Remote)
            throw FolderOperationException.For("sharing_changed");
        var current = await ReadAccountAsync(token).ConfigureAwait(false);
        if (account is not null && !account.SameBinding(current))
            throw FolderOperationException.For("sharing_changed");
        return account = current;
    }

    private async Task<DropboxFolderAccount> ReadAccountAsync(CancellationToken token) =>
        ParseAccount(await RequestAsync(Route.Account, null, null, token).ConfigureAwait(false));

    private DropboxFolderAccount ParseAccount(JsonElement user)
    {
        var root = Object(user, "root_info");
        string kind = Required(root, ".tag");
        if (kind is not ("user" or "team")) throw FolderOperationException.For("unsupported_namespace");
        string home = Required(root, "home_namespace_id"), teamRoot = Required(root, "root_namespace_id");
        string selected = broker.Remote.NamespaceId ?? (broker.Remote.TeamRoot ? teamRoot : home);
        return new(Required(user, "account_id"), selected, home, teamRoot,
            kind == "team" || Object(user, "team").ValueKind == JsonValueKind.Object,
            Text(Object(user, "team"), "name"), Text(root, "home_path"),
            Text(Object(user, "name"), "display_name") ?? Required(user, "email"));
    }

    private async Task<SnapshotToken> ReadCredentialAsync(CancellationToken token)
    {
        try { return await broker.ReadAsync(token).ConfigureAwait(false); }
        catch (SnapshotException e) { throw FolderOperationException.For(e.Code); }
        catch (OperationCanceledException) { throw FolderOperationException.For("cancelled"); }
    }

    private async Task RenewAsync(CancellationToken token, bool rejectedByServer = false)
    {
        var expired = credential ?? throw FolderOperationException.For("auth_unavailable");
        SnapshotRemote remote = broker.Remote;
        var updated = await ReadCredentialAsync(token).ConfigureAwait(false);
        if (remote != broker.Remote) throw FolderOperationException.For("sharing_changed");
        if (NeedsRenewal(updated))
        {
            if (!allowRenewal || expired.SameValue(renewalAttempt)) throw FolderOperationException.For("auth_refresh_unavailable");
            renewalAttempt = expired;
            try
            {
                Task renew = rejectedByServer ? broker.RenewRejectedAsync(expired, token) : broker.RenewAsync(token);
                await renew.ConfigureAwait(false);
            }
            catch (SnapshotException) { throw FolderOperationException.For("auth_refresh_unavailable"); }
            catch (OperationCanceledException) { throw FolderOperationException.For("cancelled"); }
            updated = await ReadCredentialAsync(token).ConfigureAwait(false);
            if (remote != broker.Remote) throw FolderOperationException.For("sharing_changed");
            if (NeedsRenewal(updated)) throw FolderOperationException.For("auth_refresh_unavailable");
        }
        credential = updated;
        if (account is { } before)
        {
            var user = await SendAsync(Route.Account, null, null, token).ConfigureAwait(false);
            if (!before.SameBinding(ParseAccount(user))) throw FolderOperationException.For("sharing_changed");
        }

        bool NeedsRenewal(SnapshotToken value) => expired.SameValue(value) ||
            rejectedByServer && expired.AccessToken == value.AccessToken ||
            value.ExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(10);
    }

    internal Task<JsonElement> MetadataAsync(string path, string ns, CancellationToken token) =>
        RequestAsync(Route.Metadata, new { path, include_deleted = false }, ns, token);

    internal Task<JsonElement> ListRootAsync(string ns, CancellationToken token) =>
        RequestAsync(Route.ListRoot, new { path = "", recursive = false, limit = 2000 }, ns, token);

    internal Task<JsonElement> ContinueListAsync(string cursor, string ns, CancellationToken token) =>
        RequestAsync(Route.ListContinue, new { cursor }, ns, token);

    internal async Task<string> LatestFolderCursorAsync(string ns, CancellationToken token) =>
        Required(await RequestAsync(Route.LatestCursor, new { path = "", recursive = true, include_deleted = true }, ns, token)
            .ConfigureAwait(false), "cursor");

    internal Task<JsonElement> SharedMetadataAsync(string id, string ns, CancellationToken token) =>
        RequestAsync(Route.SharedMetadata, new { shared_folder_id = id }, ns, token);

    internal Task<JsonElement> MembersAsync(string id, string? path, string ns, CancellationToken token)
    {
        object args = path is null ? new { shared_folder_id = id, limit = 1000 } :
            new { shared_folder_id = id, path, limit = 1000 };
        return RequestAsync(Route.Members, args, ns, token);
    }

    internal Task<JsonElement> ContinueMembersAsync(string cursor, string ns, CancellationToken token) =>
        RequestAsync(Route.MembersContinue, new { cursor }, ns, token);

    internal async Task<string> ShareAsync(string folderId, string ns, Func<IDisposable?> verify, CancellationToken token)
    {
        // ShareFolder can create a new folder when addressed by path. Use the verified ID.
        if (!folderId.StartsWith("id:", StringComparison.Ordinal) || folderId.Length <= 3)
            throw FolderOperationException.For("invalid_response");

        var response = await WriteAsync(Route.Share, new { path = folderId }, ns, verify, token).ConfigureAwait(false);
        if (Tag(response) == "async_job_id")
        {
            // The launch succeeded. Until Dropbox reports a result, creation is uncertain.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                string id = Required(response, "async_job_id");
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
                    response = await RequestAsync(Route.ShareJob, new { async_job_id = id }, ns, deadline.Token).ConfigureAwait(false);
                } while (Tag(response) == "in_progress");
            }
            catch (OperationCanceledException)
            {
                throw FolderOperationException.For(token.IsCancellationRequested ? "cancelled" : "timeout", outcomeUnknown: true);
            }
            catch (FolderOperationException e)
            {
                string code = e.Code == "cancelled" && !token.IsCancellationRequested ? "timeout" : e.Code;
                throw FolderOperationException.For(code, outcomeUnknown: true);
            }
            if (Tag(response) == "failed")
                throw FolderOperationException.For(Code(Object(response, "failed"), HttpStatusCode.Conflict));
        }
        if (Tag(response) != "complete" || Text(response, "shared_folder_id") is not { } shareId)
            throw FolderOperationException.For("invalid_response", outcomeUnknown: true);
        return shareId;
    }

    internal async Task AddMembersAsync(string id, FolderInviteRequest request, string ns, Func<IDisposable?> verify, CancellationToken token)
    {
        var body = new Dictionary<string, object>
        {
            ["shared_folder_id"] = id,
            ["members"] = request.To.Select(email => new { member = new Dictionary<string, string> { [".tag"] = "email", ["email"] = email },
                access_level = new Dictionary<string, string> { [".tag"] = request.Access } }).ToArray(),
            ["quiet"] = false
        };
        if (request.Message is { } message) body["custom_message"] = message;
        await WriteAsync(Route.AddMembers, body, ns, verify, token).ConfigureAwait(false);
    }

    private Task<JsonElement> WriteAsync(Route route, object body, string ns, Func<IDisposable?> verify, CancellationToken token) =>
        Task.Run(() =>
        {
            // Hold the existing mount control mutex through the POST, then release it
            // before any async share job polling. The mutex belongs to this thread.
            if (!ControlGate.TryExecute(() =>
            {
                using var process = verify();
                return RequestAsync(route, body, ns, token).GetAwaiter().GetResult();
            }, MountController.ReadControlWait, out JsonElement result))
                throw FolderOperationException.For("sharing_changed");
            return result;
        });

    private async Task<JsonElement> RequestAsync(Route route, object? body, string? ns, CancellationToken token)
    {
        bool write = route is Route.Share or Route.AddMembers;
        if (credential is { } current && current.ExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(10))
        {
            if (write) throw FolderOperationException.For("auth_refresh_unavailable");
            await RenewAsync(token).ConfigureAwait(false);
        }
        try { return await SendAsync(route, body, ns, token).ConfigureAwait(false); }
        catch (FolderOperationException e) when (e.Code == "expired_access_token" && !write)
        {
            await RenewAsync(token, rejectedByServer: true).ConfigureAwait(false);
            return await SendAsync(route, body, ns, token).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> SendAsync(Route route, object? body, string? ns, CancellationToken token)
    {
        string endpoint = route switch
        {
            Route.Account => SnapshotEndpoints.Account,
            Route.Metadata => SnapshotEndpoints.Metadata,
            Route.ListRoot => SnapshotEndpoints.List,
            Route.ListContinue => SnapshotEndpoints.Continue,
            Route.LatestCursor => "files/list_folder/get_latest_cursor",
            Route.SharedMetadata => "sharing/get_folder_metadata",
            Route.Members => "sharing/list_folder_members",
            Route.MembersContinue => "sharing/list_folder_members/continue",
            Route.Share => "sharing/share_folder",
            Route.ShareJob => "sharing/check_share_job_status",
            Route.AddMembers => "sharing/add_folder_member",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        if (token.IsCancellationRequested) throw FolderOperationException.For("cancelled");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        bool write = route is Route.Share or Route.AddMembers;
        using var request = new HttpRequestMessage(HttpMethod.Post, SnapshotEndpoints.ApiHost + "/2/" + endpoint);
        var auth = credential ?? throw FolderOperationException.For("auth_unavailable");
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken); }
        catch (FormatException) { throw FolderOperationException.For("auth_unavailable"); }
        if (ns is not null) request.Headers.Add(PathRootHeader, JsonSerializer.Serialize(new Dictionary<string, string>
            { [".tag"] = "namespace_id", ["namespace_id"] = ns }, SnapshotJson.Options));
        request.Content = new StringContent(JsonSerializer.Serialize(body, SnapshotJson.Options), Encoding.UTF8, "application/json");

        try
        {
            // A lost write response can mean Dropbox performed the operation. Never replay it.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.RequestMessage = null;
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500) throw FolderOperationException.For("network_error", write);
                if ((int)response.StatusCode == 429) throw FolderOperationException.For("rate_limited");
                JsonElement error;
                try { error = Object(await ReadJsonAsync(response, 64 * 1024, timeout.Token).ConfigureAwait(false), "error"); }
                catch (JsonException) { error = default; }
                throw FolderOperationException.For(Code(error, response.StatusCode));
            }
            if (route == Route.AddMembers) return default;
            return await ReadJsonAsync(response, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw FolderOperationException.For(token.IsCancellationRequested ? "cancelled" : "timeout", write);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw FolderOperationException.For("network_error", write);
        }
        catch (JsonException) { throw FolderOperationException.For("invalid_response", write); }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream();
        byte[] buffer = new byte[16384];
        int length;
        while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (data.Length + length > limit) throw new JsonException();
            data.Write(buffer, 0, length);
        }
        using var doc = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length));
        return doc.RootElement.Clone();
    }

    private static string Code(JsonElement error, HttpStatusCode status)
    {
        for (int i = 0; i < 4 && Tag(error) is { } tag; i++, error = Object(error, tag))
        {
            string? known = tag switch
            {
                "missing_scope" or "inside_shared_folder" or "contains_shared_folder" or "invalid_email" or "email_unverified" or "not_found" => tag,
                "is_file" or "not_folder" => "not_folder",
                "already_member" or "already_invited" => "already_member",
                "already_shared" or "invalid_root" => "sharing_changed",
                "cant_share_outside_team" or "team_policy_disallows_member_policy" or "disallowed_shared_link_policy" or
                    "contains_team_folder" or "team_folder" or "group_not_on_team" => "team_policy",
                "no_permission" or "access_denied" or "banned_member" or "invalid_account_type" => "permission_denied",
                "invalid_access_token" or "user_suspended" => "auth_unavailable",
                "expired_access_token" => "expired_access_token",
                "rate_limit" => "rate_limited",
                _ => null
            };
            if (known is not null) return known;
        }
        return status switch { HttpStatusCode.Unauthorized => "auth_unavailable", HttpStatusCode.Forbidden => "permission_denied", _ => "remote_error" };
    }

    internal static string? Text(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()) ? value.GetString() : null;
    internal static string Required(JsonElement obj, string key) => Text(obj, key) ?? throw FolderOperationException.For("invalid_response");
    internal static JsonElement Object(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Object ? value : default;
    internal static string? Tag(JsonElement obj) => Text(obj, ".tag");
    internal static string? OwnShareId(JsonElement metadata) =>
        Text(Object(metadata, "sharing_info"), "shared_folder_id") ?? Text(metadata, "shared_folder_id");

    public void Dispose() { http.Dispose(); credential = null; }
}
