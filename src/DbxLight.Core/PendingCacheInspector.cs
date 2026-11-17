using System.Globalization;
using System.Text.Json;

namespace DbxLight.Core;

public sealed record CacheEntry(string Relative, string DataPath, string MetadataPath, bool HasData, bool HasMetadata,
    bool MetadataValid, bool? Dirty, long? Size, DateTimeOffset? ModTime, string? Fingerprint, bool CompleteData, string? Problem)
{
    // Observation facts for wait --all only; recovery still treats a body-only pair as unknown.
    internal long? ObservedBytes { get; init; }
    internal DateTimeOffset? ObservedModifiedUtc { get; init; }
}

public sealed record CacheInspection(bool Clean, int PendingFiles, string Detail)
{
    public IReadOnlyList<CacheEntry> Entries { get; init; } = [];
    public bool CompleteScan { get; init; } = true;
}

/// <summary>Conservative reader of the verified rclone 1.74.3 vfscache.Info format.</summary>
public static class PendingCacheInspector
{
    public static CacheInspection Inspect(string cacheDir) => Inspect(cacheDir, live: true);
    public static CacheInspection Observe(string cacheDir) => Inspect(cacheDir, live: true);
    public static CacheInspection InspectStopped(string cacheDir, Action? checkpoint = null) => Inspect(cacheDir, live: false, checkpoint);

    private static CacheInspection Inspect(string cacheDir, bool live, Action? checkpoint = null)
    {
        try
        {
            RefuseReparse(cacheDir);
            _ = Directory.GetFileSystemEntries(cacheDir);
            string dataRoot = Path.Combine(cacheDir, "vfs"), metaRoot = Path.Combine(cacheDir, "vfsMeta");
            var data = Files(dataRoot, checkpoint).ToDictionary(p => Path.GetRelativePath(dataRoot, p), StringComparer.OrdinalIgnoreCase);
            var meta = Files(metaRoot, checkpoint).ToDictionary(p => Path.GetRelativePath(metaRoot, p), StringComparer.OrdinalIgnoreCase);
            var entries = new List<CacheEntry>();
            foreach (string name in data.Keys.Union(meta.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            {
                checkpoint?.Invoke();
                bool hasData = data.TryGetValue(name, out string? body), hasMeta = meta.TryGetValue(name, out string? info);
                var entry = new CacheEntry(name, body ?? Path.Combine(dataRoot, name), info ?? Path.Combine(metaRoot, name),
                    hasData, hasMeta, false, null, null, null, null, false, null);
                try
                {
                    if (hasData && !hasMeta)
                    {
                        var physical = new FileInfo(body!);
                        entry = entry with { ObservedBytes = physical.Length, ObservedModifiedUtc = physical.LastWriteTimeUtc };
                    }
                    if (!hasData || !hasMeta) throw new InvalidDataException("\u30d5\u30a1\u30a4\u30eb\u3068\u7ba1\u7406\u60c5\u5831\u304c\u5bfe\u5fdc\u3057\u3066\u3044\u307e\u305b\u3093\u3002");
                    using var doc = JsonDocument.Parse(ReadMetadata(info!, live));
                    var item = doc.RootElement;
                    string[] fields = ["ModTime", "ATime", "Size", "Rs", "Fingerprint", "Dirty"];
                    if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != fields.Length ||
                        !item.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()))
                        throw new InvalidDataException("\u7ba1\u7406\u60c5\u5831\u306b\u3001\u5bfe\u5fdc\u3057\u3066\u3044\u306a\u3044\u9805\u76ee\u304c\u3042\u308a\u307e\u3059\u3002");
                    foreach (string key in new[] { "ModTime", "ATime" })
                        if (item.GetProperty(key).ValueKind != JsonValueKind.String ||
                            !DateTimeOffset.TryParse(item.GetProperty(key).GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                            throw new InvalidDataException("\u7ba1\u7406\u60c5\u5831\u306e\u65e5\u6642\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002");
                    if (!Nonnegative(item.GetProperty("Size")) || item.GetProperty("Fingerprint").ValueKind != JsonValueKind.String ||
                        item.GetProperty("Dirty").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("\u7ba1\u7406\u60c5\u5831\u306e\u5024\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002");
                    long size = item.GetProperty("Size").GetInt64(), covered = 0;
                    var ranges = item.GetProperty("Rs");
                    if (ranges.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var range in ranges.EnumerateArray())
                        {
                            if (range.ValueKind != JsonValueKind.Object || range.EnumerateObject().Count() != 2 ||
                                !range.TryGetProperty("Pos", out var pos) || !range.TryGetProperty("Size", out var len) ||
                                !Nonnegative(pos) || !Nonnegative(len) || pos.GetInt64() > size || len.GetInt64() > size - pos.GetInt64())
                                throw new InvalidDataException("\u7ba1\u7406\u60c5\u5831\u306e\u7bc4\u56f2\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002");
                            if (pos.GetInt64() <= covered) covered = Math.Max(covered, pos.GetInt64() + len.GetInt64());
                        }
                    }
                    else if (ranges.ValueKind != JsonValueKind.Null) throw new InvalidDataException("\u7ba1\u7406\u60c5\u5831\u306e\u7bc4\u56f2\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002");
                    long physicalSize = new FileInfo(body!).Length;
                    if (physicalSize != size) throw new InvalidDataException("\u30d5\u30a1\u30a4\u30eb\u672c\u4f53\u3068\u7ba1\u7406\u60c5\u5831\u306e\u5927\u304d\u3055\u304c\u4e00\u81f4\u3057\u307e\u305b\u3093\u3002");
                    entry = entry with { MetadataValid = true, Dirty = item.GetProperty("Dirty").GetBoolean(), Size = size,
                        ModTime = DateTimeOffset.Parse(item.GetProperty("ModTime").GetString()!, CultureInfo.InvariantCulture),
                        Fingerprint = item.GetProperty("Fingerprint").GetString(), CompleteData = covered == size };
                }
                catch (Exception e) when (ReadableError(e))
                {
                    entry = entry with { Problem = Reason(e) };
                }
                entries.Add(entry);
            }
            int dirty = entries.Count(e => e.Dirty == true);
            string? problem = entries.FirstOrDefault(e => !e.MetadataValid)?.Problem;
            string detail = problem is not null ? RecoveryText.CacheUnknown(problem, cacheDir) :
                dirty == 0 ? RecoveryText.CacheClean(entries.Count, Path.GetFullPath(cacheDir)) : RecoveryText.CachePending(dirty, Path.GetFullPath(cacheDir));
            return new(problem is null && dirty == 0, dirty, detail) { Entries = entries };
        }
        catch (Exception e) when (ReadableError(e))
        {
            return new(false, 0, RecoveryText.CacheUnknown(Reason(e), cacheDir)) { CompleteScan = false };
        }
    }

    private static bool ReadableError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;
    private static string Reason(Exception e) => e switch
    {
        CacheLinkException link => "\u5225\u306e\u5834\u6240\u3092\u53c2\u7167\u3059\u308b\u30ea\u30f3\u30af\u304c\u542b\u307e\u308c\u3066\u3044\u307e\u3059: " + link.Target,
        InvalidDataException => e.Message,
        JsonException => "\u7ba1\u7406\u60c5\u5831\u306e\u5f62\u5f0f\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002",
        UnauthorizedAccessException => "\u30ad\u30e3\u30c3\u30b7\u30e5\u3092\u8aad\u307f\u53d6\u308b\u6a29\u9650\u304c\u3042\u308a\u307e\u305b\u3093\u3002",
        FileNotFoundException or DirectoryNotFoundException => "\u691c\u67fb\u3059\u308b\u5834\u6240\u307e\u305f\u306f\u30d5\u30a1\u30a4\u30eb\u304c\u898b\u3064\u304b\u308a\u307e\u305b\u3093\u3002",
        _ => $"\u30d5\u30a1\u30a4\u30eb\u306e\u8aad\u307f\u53d6\u308a\u306b\u5931\u6557\u3057\u307e\u3057\u305f\uff08\u30a8\u30e9\u30fc\u756a\u53f7 {e.HResult}\uff09\u3002"
    };

    internal static FileStream OpenObservation(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static byte[] ReadMetadata(string path, bool live)
    {
        byte[] Read()
        {
            using var stream = live ? OpenObservation(path) : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
        }
        byte[] first = Read();
        if (live && !first.AsSpan().SequenceEqual(Read())) throw new IOException("Cache metadata changed during observation.");
        return first;
    }
    private static bool Nonnegative(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) && n >= 0;
    private sealed class CacheLinkException(string target) : IOException { public string Target { get; } = target; }
    internal static void RefuseReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CacheLinkException(path);
    }
    private static string[] Files(string root, Action? checkpoint = null)
    {
        try { _ = File.GetAttributes(root); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        var result = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? dir))
        {
            RefuseReparse(dir);
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                checkpoint?.Invoke();
                RefuseReparse(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) pending.Push(entry);
                else result.Add(entry);
            }
        }
        return result.ToArray();
    }
}
