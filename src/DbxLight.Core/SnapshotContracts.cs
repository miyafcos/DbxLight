using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

internal sealed class SnapshotException(int exitCode, string code, string? localPath = null) : Exception(code)
{
    internal int ExitCode { get; } = exitCode;
    internal string Code { get; } = code;
    internal string? LocalPath { get; } = localPath;
}

internal sealed record SnapshotRequest(string Command, string? Folder = null, string? Entries = null,
    string? CursorOut = null, string? CursorFile = null, string? PathsFile = null, string? Output = null,
    string? Plan = null, string? Store = null, int Limit = 2000, double MaxRps = 4,
    int Parallel = 4, double DeadlineSeconds = 600, string? Failures = null);

internal sealed class SnapshotResult
{
    public string Schema { get; set; } = "dbxl.snapshot.v1";
    public bool Ok { get; set; }
    public string Code { get; set; } = "incomplete";
    public string Mode { get; set; } = "";
    public string Source => "dropbox-api";
    public string? RootPathLower { get; set; }
    public string? BindingId { get; set; }
    public bool Complete { get; set; }
    public long Entries { get; set; }
    public long Files { get; set; }
    public long Folders { get; set; }
    public long Deleted { get; set; }
    public int Pages { get; set; }
    public int Requests { get; set; }
    public int Throttled { get; set; }
    public int Retries { get; set; }
    public long Failed { get; set; }
    public string? FailuresFile { get; set; }
    public long Planned { get; set; }
    public long Fetched { get; set; }
    public long Reused { get; set; }
    public long Verified { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ObservedThroughUtc { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? EntriesFile { get; set; }
    public string? CursorFile { get; set; }
    public string? ResultsFile { get; set; }
    public List<string> PartialFiles { get; } = [];
    [JsonIgnore] internal int ExitCode { get; set; } = 3;
}

internal sealed record SnapshotCursor(string Schema, string BindingId, string Folder, string Cursor,
    DateTimeOffset CreatedUtc, string? RootPathLower = null, long? Entries = null, string? EntriesSha256 = null);
internal sealed record SnapshotEntry(string Schema, long Seq, string Type, string Name, string PathLower,
    string ParentPathLower, string? Id, string? PathDisplay, long? Size = null,
    DateTimeOffset? ClientModifiedUtc = null, DateTimeOffset? ServerModifiedUtc = null,
    string? Rev = null, string? ContentHash = null, bool? Downloadable = null);
internal sealed record SnapshotPlan(string Id, string Rev, string CloudPath, long Size, string ContentHash);
internal sealed record SnapshotFetchFailure(long PlanLine, string Id, string Rev, string Code, string? LocalPath = null);
internal sealed record SnapshotFetchRow(string Id, string Rev, string CloudPath, long Size, string ContentHash,
    string Sha256, string LocalPath, bool Reused);

internal static class SnapshotJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonFiles.Options) { WriteIndented = false };
    internal static string Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(field.GetString())) throw new SnapshotException(3, "invalid_response");
        return field.GetString()!;
    }
    internal static bool Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new SnapshotException(3, "invalid_response");
        return field.GetBoolean();
    }
    internal static long Size(JsonElement value)
    {
        if (!value.TryGetProperty("size", out var field) || !field.TryGetInt64(out long size) || size < 0)
            throw new SnapshotException(3, "invalid_response");
        return size;
    }
    internal static string Relative(string value, bool allowRoot = true, bool allowColon = false)
    {
        if (value.Length == 0 && allowRoot) return "";
        if (value.Length == 0 || value.StartsWith('/') || value.Contains('\\') || !allowColon && value.Contains(':') ||
            value.Any(char.IsControl) || value.Split('/').Any(p => p is "" or "." or ".."))
            throw new SnapshotException(2, "unsafe_cloud_path");
        return value;
    }
    internal static string ApiPath(string prefix, string relative) =>
        prefix.Length == 0 ? relative.Length == 0 ? "" : "/" + relative :
        relative.Length == 0 ? "/" + prefix : "/" + prefix + "/" + relative;

    internal static SnapshotEntry Entry(JsonElement value, long seq)
    {
        string type = Text(value, ".tag");
        if (type is not ("file" or "folder" or "deleted")) throw new SnapshotException(3, "unknown_entry");
        string name = Text(value, "name"), path = Text(value, "path_lower");
        if (name.Contains('/') || name.Any(char.IsControl) || name is "." or ".." ||
            !path.StartsWith('/') || path.EndsWith('/') || path.Split('/').Skip(1).Any(p => p is "" or "." or ".."))
            throw new SnapshotException(3, "invalid_response");
        string? display = value.TryGetProperty("path_display", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        string? id = type == "deleted" ? null : Text(value, "id");
        string parent = path[..path.LastIndexOf('/')];
        if (type != "file") return new("dbxl.snapshot.entry.v1", seq, type, name, path, parent, id, display);
        DateTimeOffset Stamp(string key)
        {
            if (!DateTimeOffset.TryParse(Text(value, key), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var stamp)) throw new SnapshotException(3, "invalid_response");
            return stamp.ToUniversalTime();
        }
        string rev = Text(value, "rev");
        bool downloadable = value.TryGetProperty("is_downloadable", out _) ? Boolean(value, "is_downloadable") : true;
        string? hash = value.TryGetProperty("content_hash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        if (hash is not null && !ValidHash(hash) || downloadable && hash is null)
            throw new SnapshotException(3, "invalid_response");
        return new("dbxl.snapshot.entry.v1", seq, type, name, path, parent, id, display, Size(value),
            Stamp("client_modified"), Stamp("server_modified"), rev, hash, downloadable);
    }

    internal static bool ValidHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string InputPath(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
                throw new SnapshotException(2, "invalid_input");
            return Relative(path.GetString()!, allowRoot: false, allowColon: true);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new SnapshotException(2, "invalid_input"); }
    }

    internal static SnapshotPlan Plan(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var value = doc.RootElement;
            string Field(string name)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) ||
                    field.ValueKind != JsonValueKind.String) throw new SnapshotException(2, "invalid_plan");
                return field.GetString()!;
            }
            string id = Field("id"), rev = Field("rev"), cloudPath = Field("cloudPath"), hash = Field("contentHash");
            if (!value.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out long length) || length < 0 || !id.StartsWith("id:", StringComparison.Ordinal) ||
                id.Length <= 3 || id.Any(char.IsControl) || !ValidHash(hash) ||
                !Regex.IsMatch(rev, "^[a-fA-F0-9]{1,128}$", RegexOptions.CultureInvariant))
                throw new SnapshotException(2, "invalid_plan");
            // An opaque result label only. Never use it to build a local or API path.
            if (cloudPath.Length == 0 || cloudPath.Any(c => c <= '\u001f' || c == '\u007f'))
                throw new SnapshotException(2, "unsafe_cloud_path");
            // Unknown plan fields are deliberately ignored; required fields remain strict.
            return new(id, rev, cloudPath, length, hash);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new SnapshotException(2, "invalid_plan"); }
    }

}

internal sealed class SnapshotBudget : IDisposable
{
    private readonly CancellationTokenSource deadline;
    private readonly CancellationToken external;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    internal CancellationToken Token => deadline.Token;
    internal double RemainingSeconds => Math.Max(0, Seconds - watch.Elapsed.TotalSeconds);
    internal double Seconds { get; }
    internal SnapshotBudget(double seconds, CancellationToken cancellation)
    {
        Seconds = seconds; external = cancellation;
        deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
    }
    internal void Check()
    {
        if (external.IsCancellationRequested) throw new SnapshotException(3, "cancelled");
        if (Token.IsCancellationRequested || RemainingSeconds <= 0) throw new SnapshotException(3, "timeout");
    }
    internal string CancellationCode => external.IsCancellationRequested ? "cancelled" : "timeout";
    public void Dispose() => deadline.Dispose();
}
