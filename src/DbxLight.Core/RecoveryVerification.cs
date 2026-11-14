using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

internal enum RecoveryMatch { Unknown, Same, Different }

internal static class RecoveryVerification
{
    internal const long NoModTime = 3153600000000000000L;

    internal static RecoveryMatch CompareFingerprint(string? saved, RemoteInfo info, JsonElement? remote)
    {
        if (saved is null) return RecoveryMatch.Unknown;
        if (saved.Length == 0) return remote is null ? RecoveryMatch.Same : RecoveryMatch.Different;
        string[] parts = saved.Split(',');
        if (parts.Length > 3 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
            return RecoveryMatch.Unknown;
        bool timePresent = parts.Length >= 2 && Regex.IsMatch(parts[1],
            @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d{1,9})? \+0000 UTC$", RegexOptions.CultureInvariant);
        // v1.74.3 writes the time before the optional hash. A malformed time must
        // not be reinterpreted as a hash when this backend supports timestamps.
        if (parts.Length >= 2 && !timePresent && info.Precision != NoModTime && parts[1].Length > 0)
            return RecoveryMatch.Unknown;
        string? savedHash = timePresent ? (parts.Length == 3 ? parts[2] : null) :
            info.Precision == NoModTime && parts.Length == 2 ? parts[1] : null;
        bool hasEvidence = size >= 0 || timePresent || !string.IsNullOrEmpty(savedHash);
        if (!hasEvidence) return RecoveryMatch.Unknown;
        if (remote is null || remote.Value.GetProperty("IsDir").GetBoolean()) return RecoveryMatch.Different;
        JsonElement item = remote.Value;
        int compared = 0;
        if (size >= 0 && item.TryGetProperty("Size", out var s) && s.TryGetInt64(out long currentSize) && currentSize >= 0)
        {
            compared++;
            if (size != currentSize) return RecoveryMatch.Different;
        }
        if (timePresent && item.TryGetProperty("ModTime", out var t) && t.ValueKind == JsonValueKind.String)
        {
            string currentTime;
            try { currentTime = RecoveryRemoteProbe.GoUtc(t.GetString()!); }
            catch (RecoveryComparisonException) { return RecoveryMatch.Unknown; }
            compared++;
            if (parts[1] != currentTime) return RecoveryMatch.Different;
        }
        string? hash = RemoteHash(item, info.Hashes.FirstOrDefault());
        if (!string.IsNullOrEmpty(savedHash) && !string.IsNullOrEmpty(hash))
        {
            compared++;
            if (!savedHash.Equals(hash, StringComparison.OrdinalIgnoreCase)) return RecoveryMatch.Different;
        }
        return compared == 0 ? RecoveryMatch.Unknown : RecoveryMatch.Same;
    }

    internal static string? ResolveHashName(string? type) => type?.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "dropboxhash" or "dropbox" => "dropbox",
        var other => other
    };

    internal static string? RemoteHash(JsonElement item, string? type)
    {
        if (type is null || !item.TryGetProperty("Hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in hashes.EnumerateObject())
            if (ResolveHashName(property.Name) == ResolveHashName(type) && property.Value.ValueKind == JsonValueKind.String)
                return string.IsNullOrEmpty(property.Value.GetString()) ? null : property.Value.GetString();
        return null;
    }

}
