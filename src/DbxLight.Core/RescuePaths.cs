using System.Text.RegularExpressions;

namespace DbxLight.Core;

/// <summary>Only the read location and the caller's roots establish filesystem authority.</summary>
internal sealed class RescuePresenceException(string message) : IOException(message);

internal static class RescuePaths
{
    internal const string Unknown = "\u78ba\u8a8d\u3067\u304d\u306a\u3044\u8a18\u9332";
    internal static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.Split(['\\', '/']).Any(p => p is "." or "..") || path.Any(char.IsControl))
            throw new IOException(Unknown);
        string full = Path.GetFullPath(path);
        if (full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new IOException(Unknown);
        return full;
    }
    internal static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);
    internal static void Ancestors(string path)
    {
        string full = Full(path), current = Path.GetPathRoot(full)!;
        foreach (string segment in full[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try { PendingCacheInspector.RefuseReparse(current); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }
    private static string Cloud(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Any(char.IsControl) ||
            path.Split('/').Any(p => p is "" or "." or "..")) throw new IOException(Unknown);
        return path;
    }
    internal static void Require(RescueRecord r, string actual, DbxConfig config, bool checkPresence = true)
    {
        if (r.Origin is null || string.IsNullOrEmpty(r.CacheRelativePath) || r.Size < 0 ||
            !r.HadData && !r.HadMetadata || r.CompleteData && !r.HadData) throw new IOException(Unknown);
        string records = Path.Combine(Full(config.RescueDir), ".dbxl-records");
        if (!Guid.TryParseExact(r.Id, "N", out _) || !Same(actual, Path.Combine(records, r.Id + ".json")) ||
            !Same(r.RecordPath, actual) || !Same(r.Origin.RescueDir, config.RescueDir) ||
            !RecoveryDestination.Matches(r.Origin, config)) throw new IOException(Unknown);
        string relative = RescueStore.SafeRelative(r.CacheRelativePath.Replace('\\', '/'));
        if (!Same(r.OriginalPath, Path.Combine(config.CacheDir, "vfs", relative)) ||
            !Same(r.OriginalMetadataPath, Path.Combine(config.CacheDir, "vfsMeta", relative)) ||
            !Same(r.MetadataDestinationPath, Path.Combine(records, r.Id + ".metadata"))) throw new IOException(Unknown);
        string tail = r.CloudPath is null ? "unmapped/" + relative : RecoveryCacheNames.EncodeForDisk(r.CloudPath);
        tail = RescueStore.SafeRelative(tail);
        if (r.CloudPath is not null && !relative.Equals(tail, StringComparison.Ordinal) &&
            !relative.EndsWith("/" + tail, StringComparison.Ordinal)) throw new IOException(Unknown);
        string rescuedRelative = Path.GetRelativePath(Full(config.RescueDir), Full(r.DestinationPath)).Replace('\\', '/');
        int slash = rescuedRelative.IndexOf('/');
        if (slash <= 0 || !Regex.IsMatch(rescuedRelative[..slash], @"^\d{8}-\d{6}-\d{7}-" + r.Id[..6] + "$") ||
            !rescuedRelative[(slash + 1)..].Equals(tail, StringComparison.Ordinal)) throw new IOException(Unknown);
        if (r.CloudPath is not null) _ = Cloud(r.CloudPath);
        foreach (string path in new[] { actual, r.DestinationPath, r.MetadataDestinationPath })
            Ancestors(path);
        if (!checkPresence) return; // Foreign ownership is counted without opening its cache or body.
        Ancestors(r.OriginalPath); Ancestors(r.OriginalMetadataPath);
        void Present(bool had, string source, string destination)
        {
            if (!had) return;
            bool from = File.Exists(source), to = File.Exists(destination);
            if (r.MoveCompleted ? !to : from == to)
                throw new RescuePresenceException((Same(destination, r.DestinationPath) ? "ファイル本体" : "管理情報") +
                    (from && to ? ": 元と退避先の両方にあります。" : !from && !to ? ": 元にも退避先にもありません。" : ": 退避先にありません。"));
            if (to) PrivateStateFiles.ValidateExisting(destination);
        }
        Present(r.HadData, r.OriginalPath, r.DestinationPath);
        Present(r.HadMetadata, r.OriginalMetadataPath, r.MetadataDestinationPath);
        if (r.HadData && File.Exists(r.DestinationPath) && new FileInfo(r.DestinationPath).Length != r.Size)
            throw new RescuePresenceException("ファイル本体: 退避先の長さが記録と一致しません。");
    }
}
