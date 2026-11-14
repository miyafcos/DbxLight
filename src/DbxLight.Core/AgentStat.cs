namespace DbxLight.Core;

public sealed record AgentStatResult(bool Ok, AgentEntry Entry, string UploadState, bool? Queued, bool? Uploading,
    bool? Failed, string? WebUrl, DbxStatus Status, int SkippedUnsafe = 0);

public static class CloudWebPaths
{
    public static string WebUrl(string relative) => "https://www.dropbox.com/home/" +
        string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
}

public sealed partial class AgentMetadata
{
    public AgentStatResult Stat(string path) => StableRead(() => StatCore(path)) with { Status = Status };

    private AgentStatResult StatCore(string path)
    {
        string relative = Relative(path); EnsureVisible(relative);
        unsafeFolders.Clear();
        AgentEntry entry = MetadataStat(relative) ?? throw NotFound();
        UploadQueueEntry? pending = rc is null ? null : ReadUploadQueue().Find(q => PathKey(q.Path) == PathKey(relative));
        bool? queued = rc is null ? null : pending is { Uploading: false };
        bool? uploading = rc is null ? null : pending?.Uploading ?? false;
        bool? failed = rc is null ? null : pending is { Tries: > 1 } or { Tries: > 0, Uploading: false };
        string state = rc is null ? "unknown" : pending is null ? "idle" :
            pending.Uploading ? "uploading" : failed == true ? "failed" : "queued";
        return new(true, entry, state, queued, uploading, failed, webMapping.Value?.Url(relative), Status, SkippedUnsafe);
    }
}
