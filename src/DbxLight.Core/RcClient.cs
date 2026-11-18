using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

public sealed class RcClient : IDisposable
{
    private readonly HttpClient client;
    public RcClient(int port, RcCredentials credentials, TimeSpan? timeout = null)
    {
        client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(3)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.User + ":" + credentials.Password)));
    }
    public RcClient(RuntimeState state) : this(state.RcPort, new RcCredentials(state.RcUser, state.RcPassword)) { }

    public JsonElement Call(string command, object? body = null)
    {
        if (command.StartsWith('/') || command.Contains("..") || command.Contains(':')) throw new ArgumentException("Invalid rc command.");
        using var content = new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = client.PostAsync(command, content).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"rc {command}: HTTP {(int)response.StatusCode}.");
        return RcloneOutput.Json(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), "rc");
    }
    public void Dispose() => client.Dispose();
}

public sealed record DbxStatus
{
    public bool Running { get; init; }
    public PreparationProgress? Preparation { get; init; }
    public bool RescueCountsKnown { get; init; } = true;
    public MountLogInfo LogCapture { get; init; } = new();
    public IReadOnlyList<string> UnregisteredCredentialFiles { get; init; } = [];
    public int RescueFiles { get; init; }
    public RescueNotice RescueNotice { get; init; } = new();
    public bool? ReadOnly { get; init; }
    public string ReadOnlySource { get; init; } = "unknown";
    public bool Ready { get; init; }
    public int? RclonePid { get; init; }
    public long? PrivateMemoryBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? UploadsQueued { get; init; }
    public long? UploadsInProgress { get; init; }
    public long? ErroredFiles { get; init; }
    public long? CacheFiles { get; init; }
    public long? CacheBytes { get; init; }
    public double? OldestQueuedAgeSeconds { get; init; }
    public string OldestQueuedAgeReason { get; init; } = "No running mount.";
    public bool QueueSupported { get; init; }
    public ProcessUncertainty? UncertainProcess { get; init; }
    public bool ProcessUncertain { get; init; }
    public bool RecoveryRequired { get; init; }
    public string? StopDisposition { get; init; }
    public string? RecoveryLocation { get; init; }
    public DownloadActivity? DownloadActivity { get; init; }
    internal DownloadProgress? DownloadProgress { get; init; }
    public string? StatusError { get; init; }
}

public static class StatusReader
{
    public static DbxStatus Read(RuntimeState? state, Func<RuntimeState, System.Diagnostics.Process?>? processQuery = null)
    {
        if (state is null) return new();
        System.Diagnostics.Process? inspected;
        try { inspected = (processQuery ?? ProcessIdentity.Open)(state); }
        catch (Exception e) when (e is ProcessIdentityUnknownException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            ProcessUncertainty identity = ProcessUncertainty.From(e, state.RclonePid);
            return new DbxStatus { ProcessUncertain = true, UncertainProcess = identity, StatusError = identity.Explain() };
        }
        using var process = inspected;
        if (process is null) return new();
        DbxStatus status;
        try
        {
            process.Refresh();
            status = new DbxStatus
            {
                Running = true, RclonePid = process.Id,
                PrivateMemoryBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
                OldestQueuedAgeReason = "rc statistics are unavailable."
            };
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ProcessUncertainty identity = ProcessUncertainty.From(e, state.RclonePid);
            return new DbxStatus { ProcessUncertain = true, UncertainProcess = identity, StatusError = identity.Explain() };
        }
        try
        {
            using var rc = new RcClient(state);
            JsonElement stats = rc.Call("vfs/stats");
            JsonElement disk = stats.GetProperty("diskCache");
            status = status with
            {
                Ready = Directory.Exists(MountPaths.CanonicalMount(state.ActiveConfig.MountPoint)),
                UploadsQueued = Number(disk, "uploadsQueued"), UploadsInProgress = Number(disk, "uploadsInProgress"),
                ErroredFiles = Number(disk, "erroredFiles"), CacheFiles = Number(disk, "files"), CacheBytes = Number(disk, "bytesUsed"),
                OldestQueuedAgeReason = "No pending uploads."
            };
            try
            {
                JsonElement queue = rc.Call("vfs/queue");
                status = status with { QueueSupported = queue.TryGetProperty("queue", out var items) && items.ValueKind == JsonValueKind.Array };
                if (status.UploadsQueued + status.UploadsInProgress > 0)
                    status = status with { OldestQueuedAgeReason = "vfs/queue exposes expiry and retry delay, but no enqueue timestamp; elapsed age cannot be measured reliably." };
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                status = status with { OldestQueuedAgeReason = "vfs/queue unavailable: " + e.Message };
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        {
            status = status with { StatusError = e.Message };
        }
        return status;
    }

    public static long Number(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.TryGetInt64(out long number)
        ? number : throw new InvalidDataException("Missing rc statistic: " + key);
}
