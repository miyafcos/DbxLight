using System.Security.Cryptography;
using System.Text;

namespace DbxLight.Core;

public sealed record UpdateAsset(string Name, string Url, long Size, string? Digest);
public sealed record UpdateRelease(string Version, UpdateAsset Zip, UpdateAsset Checksum);
public sealed record UpdateCheckState
{
    public DateTimeOffset? CheckedUtc { get; init; }
    public string? Source { get; init; }
    public string? ETag { get; init; }
    public string? LatestVersion { get; init; }
    public UpdateRelease? Release { get; init; }
    public string? NotifiedVersion { get; init; }
    public string[] NotifiedVersions { get; init; } = [];
}
public sealed record UpdateResult(bool Success, string Version, string? Reason, bool RolledBack,
    string? BackupPath, DateTimeOffset FinishedUtc, bool Read = false, string? Stage = null);
internal sealed record UpdateStageRecord(string Name, bool Completed);

public sealed class UpdateStore
{
    public string Root { get; }
    public string ResultPath => Path.Combine(Root, "last-result.json");
    public UpdateStore(string? statePath = null) =>
        Root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath ?? Environment.GetEnvironmentVariable("DBXL_STATE") ?? DbxConfig.DefaultStatePath))!, "update");
    private T Locked<T>(Func<T> action)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Local\DbxLight.Update." + hash);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Update state is busy.");
        try { return action(); } finally { mutex.ReleaseMutex(); }
    }
    public UpdateCheckState LoadCheck() => Locked(ReadCheck);
    private UpdateCheckState ReadCheck() => File.Exists(Path.Combine(Root, "check.json"))
        ? JsonFiles.Read<UpdateCheckState>(Path.Combine(Root, "check.json")) : new();
    public void ChangeCheck(Func<UpdateCheckState, UpdateCheckState> change) => Locked(() =>
    { JsonFiles.Write(Path.Combine(Root, "check.json"), change(ReadCheck())); return 0; });
    public bool IsDue(DateTimeOffset now) => LoadCheck().CheckedUtc is not { } last || now - last >= TimeSpan.FromHours(24);
    public bool WasNotified(string version) { var state = LoadCheck(); return state.NotifiedVersion == version || state.NotifiedVersions.Contains(version, StringComparer.Ordinal); }
    public bool MarkNotified(string version) => Locked(() =>
    {
        var state = ReadCheck();
        if (state.NotifiedVersion == version || state.NotifiedVersions.Contains(version, StringComparer.Ordinal)) return false;
        string[] history = state.NotifiedVersions.Concat(state.NotifiedVersion is { } previous ? new[] { previous } : []).Append(version).Distinct(StringComparer.Ordinal).ToArray();
        JsonFiles.Write(Path.Combine(Root, "check.json"), state with { NotifiedVersion = version, NotifiedVersions = history }); return true;
    });
    public UpdateResult? ConsumeResult() => Locked(() =>
    {
        if (!File.Exists(ResultPath)) return null;
        var result = JsonFiles.Read<UpdateResult>(ResultPath);
        if (result.Read) return null;
        JsonFiles.Write(ResultPath, result with { Read = true }); return result;
    });
    internal void WriteResult(UpdateResult result) => Locked(() => { JsonFiles.Write(ResultPath, result); return 0; });
    internal void RecordStage(string stage, bool completed) =>
        JsonFiles.Write(stage + ".json", new UpdateStageRecord(Path.GetFileName(stage), completed));
    public void CompleteStage(string stage)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(stage)) != Path.GetFullPath(Root) || !UpdateCleanup.IsStage(Path.GetFileName(stage))) return;
        RecordStage(stage, true);
    }
}
