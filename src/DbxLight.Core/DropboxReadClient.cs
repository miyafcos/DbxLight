using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

// The fixed allowlist for snapshot operations. No configurable URLs or redirects.
internal static class SnapshotEndpoints
{
    internal const string ApiHost = "https://api.dropboxapi.com";
    internal const string ContentHost = "https://content.dropboxapi.com";
    internal const string Account = "users/get_current_account";
    internal const string List = "files/list_folder";
    internal const string Continue = "files/list_folder/continue";
    internal const string Metadata = "files/get_metadata";
    internal const string Download = "files/download";
}

internal sealed class DropboxReadClient : IDisposable
{
    private readonly ISnapshotTokenBroker broker;
    private readonly HttpClient http;
    private readonly SnapshotBudget budget;
    private readonly SnapshotRateGate rate;
    private readonly TimeSpan httpTimeout;
    private readonly SemaphoreSlim auth = new(1);
    // All credential access is under auth. Never publish a half-renewed credential.
    private SnapshotToken? credential;
    private SnapshotException? authenticationFailure;
    private readonly HashSet<string> attemptedTokens = [];
    private int requests, throttled, consecutive429, retries;
    private string? pathRoot;
    internal string BindingId { get; private set; } = "";
    internal string Prefix => broker.Remote.Prefix;
    internal int Requests => Volatile.Read(ref requests);
    internal int Throttled => Volatile.Read(ref throttled);
    internal int Retries => Volatile.Read(ref retries);

    internal DropboxReadClient(ISnapshotTokenBroker broker, SnapshotBudget budget, double maxRps,
        HttpMessageHandler? handler = null, TimeSpan? httpTimeout = null)
    {
        this.broker = broker; this.budget = budget;
        this.httpTimeout = httpTimeout ?? TimeSpan.FromSeconds(60);
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        rate = new(maxRps, budget);
    }

    internal async Task InitializeAsync()
    {
        credential = await broker.ReadAsync(budget.Token);
        using var doc = await JsonAsync(SnapshotEndpoints.Account, null, maximum: 1024 * 1024, account: true);
        (string ns, string binding) = Identity(doc.RootElement);
        pathRoot = JsonSerializer.Serialize(new Dictionary<string, string> { [".tag"] = "namespace_id", ["namespace_id"] = ns });
        BindingId = binding;
    }

    private (string Namespace, string Binding) Identity(JsonElement root)
    {
        string account = SnapshotJson.Text(root, "account_id");
        JsonElement info = root.GetProperty("root_info");
        string kind = SnapshotJson.Text(info, ".tag");
        if (kind is not ("team" or "user")) throw new SnapshotException(3, "unsupported_namespace");
        string ns = broker.Remote.NamespaceId ?? SnapshotJson.Text(info, broker.Remote.TeamRoot ? "root_namespace_id" : "home_namespace_id");
        byte[] binding = JsonSerializer.SerializeToUtf8Bytes(new[] { broker.Remote.Name, broker.Remote.Prefix, account, ns });
        return (ns, Convert.ToHexString(SHA256.HashData(binding)).ToLowerInvariant());
    }

    private async Task<SnapshotToken> CredentialAsync(SnapshotToken? rejected = null)
    {
        await auth.WaitAsync(budget.Token);
        try
        {
            if (authenticationFailure is not null) throw authenticationFailure;
            SnapshotToken current = credential ?? throw new SnapshotException(3, "auth_unavailable");
            // A concurrent request has already renewed the rejected token: share its result.
            if (rejected is not null && !current.SameValue(rejected)) return current;
            if (rejected is null && current.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(10)) return current;
            byte[] key = Encoding.UTF8.GetBytes(current.AccessToken + "\n" + current.ExpiresUtc.UtcTicks);
            string fingerprint = Convert.ToHexString(SHA256.HashData(key));
            CryptographicOperations.ZeroMemory(key);
            if (!attemptedTokens.Add(fingerprint)) throw new SnapshotException(3, "auth_unavailable");
            SnapshotRemote original = broker.Remote;
            await broker.RenewAsync(budget.Token);
            SnapshotToken updated = await broker.ReadAsync(budget.Token);
            if (original != broker.Remote) throw new SnapshotException(3, "binding_changed");
            if (current.SameValue(updated) || updated.ExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(10))
                throw new SnapshotException(3, "auth_unavailable");
            if (pathRoot is not null)
            {
                // Verify before publishing, while other callers wait. The explicit token
                // bypasses this gate, and a 401 here fails rather than recursively renewing.
                using var doc = await JsonAsync(SnapshotEndpoints.Account, null, maximum: 1024 * 1024,
                    account: true, validationToken: updated);
                if (Identity(doc.RootElement).Binding != BindingId) throw new SnapshotException(3, "binding_changed");
            }
            credential = updated;
            return updated;
        }
        catch (SnapshotException e) { authenticationFailure = e; throw; }
        finally { auth.Release(); }
    }

    private sealed class RetryState { internal int Count; }

    private async Task RetryAsync(RetryState state, string code, CancellationToken token)
    {
        budget.Check(); token.ThrowIfCancellationRequested();
        if (state.Count >= 3) throw new SnapshotException(4, code);
        int seconds = 1 << state.Count;
        if (seconds >= budget.RemainingSeconds) throw new SnapshotException(3, "timeout");
        await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        budget.Check();
        state.Count++; Interlocked.Increment(ref retries);
    }

    private async Task<HttpResponseMessage> SendAsync(string route, object? body, RetryState retry,
        CancellationToken token, bool account = false, bool download = false, SnapshotToken? validationToken = null)
    {
        if (route is not (SnapshotEndpoints.Account or SnapshotEndpoints.List or SnapshotEndpoints.Continue or
            SnapshotEndpoints.Metadata or SnapshotEndpoints.Download)) throw new SnapshotException(1, "internal_error");
        while (true)
        {
            budget.Check(); token.ThrowIfCancellationRequested();
            await rate.WaitAsync();
            SnapshotToken selected = validationToken ?? await CredentialAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, (download ? SnapshotEndpoints.ContentHost : SnapshotEndpoints.ApiHost) + "/2/" + route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", selected.AccessToken);
            if (!account) request.Headers.Add("Dropbox-API-Path-Root", pathRoot);
            if (download) request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(body, SnapshotJson.Options));
            else request.Content = new StringContent(JsonSerializer.Serialize(body, SnapshotJson.Options), Encoding.UTF8, "application/json");
            Interlocked.Increment(ref requests);
            HttpResponseMessage response;
            try
            {
                // Bound connection, sending and response headers separately from body reads.
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(token);
                headers.CancelAfter(httpTimeout);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            }
            catch (Exception e) when (SnapshotNetworkFailure.IsTransient(e, token))
            { await RetryAsync(retry, "network_error", token); continue; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException) { throw new SnapshotException(4, "network_error"); }
            response.RequestMessage = null;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                if (validationToken is not null) throw new SnapshotException(3, "auth_unavailable");
                _ = await CredentialAsync(selected);
                continue;
            }
            if ((int)response.StatusCode == 429)
            {
                Interlocked.Increment(ref throttled);
                int consecutive = Interlocked.Increment(ref consecutive429);
                TimeSpan? delay = response.Headers.RetryAfter?.Delta;
                if (delay is null && response.Headers.RetryAfter?.Date is { } date) delay = date - DateTimeOffset.UtcNow;
                response.Dispose();
                if (consecutive >= 3 || delay is null || !double.IsFinite(delay.Value.TotalSeconds))
                    throw new SnapshotException(3, "rate_limited");
                rate.Cooldown(TimeSpan.FromSeconds(Math.Max(0, delay.Value.TotalSeconds)));
                continue;
            }
            Interlocked.Exchange(ref consecutive429, 0);
            if ((int)response.StatusCode >= 500)
            {
                response.Dispose(); await RetryAsync(retry, "remote_error", token); continue;
            }
            if (response.IsSuccessStatusCode) return response;
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose(); throw new SnapshotException(4, "redirect_refused");
            }
            string code;
            try
            {
                using (response)
                using (var error = await ReadJsonAsync(response, 65536, token)) code = ErrorCode(error.RootElement, route);
            }
            catch (SnapshotNetworkFailure) { await RetryAsync(retry, "network_error", token); continue; }
            catch (OperationCanceledException) { throw; }
            catch { throw new SnapshotException(4, "remote_error"); }
            throw new SnapshotException(code is "cursor_reset" ? 3 : 4, code);
        }
    }

    private static string ErrorCode(JsonElement root, string route)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return "remote_error";
        string tag = error.TryGetProperty(".tag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        if (route == SnapshotEndpoints.Continue && tag == "reset") return "cursor_reset";
        if (tag == "path" && error.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Object &&
            path.TryGetProperty(".tag", out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString() switch { "not_found" => "not_found", "not_file" => "not_file",
                "restricted_content" => "restricted_content", "no_permission" => "permission_denied", _ => "remote_error" };
        return tag switch { "unsupported_file" => "not_downloadable", "access_denied" => "permission_denied", _ => "remote_error" };
    }

    internal async Task<Stream> BodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
            read.CancelAfter(httpTimeout);
            return new SnapshotResponseStream(await response.Content.ReadAsStreamAsync(read.Token), httpTimeout);
        }
        catch (Exception e) when (SnapshotNetworkFailure.IsTransient(e, token)) { throw new SnapshotNetworkFailure(); }
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        try
        {
            using var data = new MemoryStream();
            using Stream stream = await BodyAsync(response, token);
            byte[] buffer = new byte[65536]; int n;
            while ((n = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (data.Length + n > maximum) throw new SnapshotException(3, "invalid_response");
                data.Write(buffer, 0, n);
            }
            return JsonDocument.Parse(data.GetBuffer().AsMemory(0, checked((int)data.Length)));
        }
        catch (OperationCanceledException) { throw; }
        catch (SnapshotException) { throw; }
        catch (SnapshotNetworkFailure) { throw; }
        catch { throw new SnapshotException(3, "invalid_response"); }
    }

    private async Task<JsonDocument> JsonAsync(string route, object? args, int maximum = 32 * 1024 * 1024,
        bool account = false, SnapshotToken? validationToken = null)
    {
        var retry = new RetryState();
        while (true)
        {
            try
            {
                using var response = await SendAsync(route, args, retry, budget.Token, account: account, validationToken: validationToken);
                return await ReadJsonAsync(response, maximum, budget.Token);
            }
            catch (SnapshotNetworkFailure) { await RetryAsync(retry, "network_error", budget.Token); }
        }
    }

    internal Task<JsonDocument> ListAsync(string folder, int limit, string? cursor)
    {
        object args = cursor is null ? new { path = SnapshotJson.ApiPath(Prefix, folder), recursive = true,
            include_deleted = true, include_non_downloadable_files = true, limit } : new { cursor };
        return JsonAsync(cursor is null ? SnapshotEndpoints.List : SnapshotEndpoints.Continue, args);
    }

    internal async Task<JsonDocument?> MetadataAsync(string path)
    {
        try { return await JsonAsync(SnapshotEndpoints.Metadata, new { path = SnapshotJson.ApiPath(Prefix, path), include_deleted = false }, 1024 * 1024); }
        catch (SnapshotException e) when (e.Code == "not_found") { return null; }
    }

    internal async Task<T> DownloadAsync<T>(string rev, Func<HttpResponseMessage, CancellationToken, Task<T>> consume, CancellationToken token)
    {
        var retry = new RetryState();
        while (true)
        {
            try
            {
                using var response = await SendAsync(SnapshotEndpoints.Download, new { path = "rev:" + rev }, retry, token, download: true);
                return await consume(response, token);
            }
            catch (SnapshotNetworkFailure) { await RetryAsync(retry, "network_error", token); }
        }
    }

    public void Dispose() { credential = null; attemptedTokens.Clear(); http.Dispose(); auth.Dispose(); rate.Dispose(); }
}

// Only failures reading an HTTP body are retried. Local output I/O remains io_error.
internal sealed class SnapshotNetworkFailure() : Exception("network_error")
{
    internal static bool IsTransient(Exception e, CancellationToken token) =>
        e is IOException or HttpRequestException || e is OperationCanceledException && !token.IsCancellationRequested;
}

internal sealed class SnapshotResponseStream(Stream source, TimeSpan readTimeout) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            // A fresh timer for each read: sustained large downloads have no total body limit.
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            read.CancelAfter(readTimeout);
            return await source.ReadAsync(buffer, read.Token);
        }
        catch (Exception e) when (SnapshotNetworkFailure.IsTransient(e, cancellationToken)) { throw new SnapshotNetworkFailure(); }
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}

internal sealed class SnapshotRateGate(double maxRps, SnapshotBudget budget) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double next;
    internal void Cooldown(TimeSpan retry)
    {
        if (retry.TotalSeconds >= budget.RemainingSeconds) throw new SnapshotException(3, "rate_limited");
        lock (clock) next = Math.Max(next, clock.Elapsed.TotalSeconds + retry.TotalSeconds);
    }
    internal async Task WaitAsync()
    {
        await gate.WaitAsync(budget.Token);
        try
        {
            while (true)
            {
                budget.Check();
                double delay;
                lock (clock)
                {
                    delay = next - clock.Elapsed.TotalSeconds;
                    if (delay <= 0) { next = clock.Elapsed.TotalSeconds + 1 / maxRps; return; }
                }
                if (delay >= budget.RemainingSeconds) throw new SnapshotException(3, "timeout");
                await Task.Delay(TimeSpan.FromSeconds(delay), budget.Token);
            }
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}
