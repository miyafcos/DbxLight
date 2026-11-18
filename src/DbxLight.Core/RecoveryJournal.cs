using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

/// <summary>State and both private journals participate in the same generation election.</summary>
internal static class RecoveryJournal
{
    internal static string CachePath(string cacheDir) => Path.Combine(cacheDir, ".dbxl-recovery", "runtime.json");
    internal static string ConfigPath(string configPath)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToUpperInvariant())))[..20];
        return Path.Combine(Path.GetDirectoryName(configPath)!, ".dbxl-recovery-" + key, "runtime.json");
    }

    internal static void Save(string configPath, RuntimeState state)
    {
        PrivateJsonFile.Write(CachePath(state.ActiveConfig.CacheDir), state);
        PrivateJsonFile.Write(ConfigPath(configPath), state);
    }

    private static PendingUploadsException Unknown() => new("\u72b6\u614b\u3068\u56de\u5fa9\u306e\u8a18\u9332\u306e\u4e16\u4ee3\u304c\u98df\u3044\u9055\u3044\u3001\u6240\u6709\u30d7\u30ed\u30bb\u30b9\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002\u8a18\u9332\u3092\u4e0a\u66f8\u304d\u305b\u305a\u4fdd\u5168\u3057\u3066\u304f\u3060\u3055\u3044\u3002");

    private static bool Resolved(RuntimeState state) => state.ActiveConfig is not null && RecoveryDestination.Resolved(state.ActiveConfig);
    private static bool SameOwner(RuntimeState a, RuntimeState b) => a.RclonePid == b.RclonePid && a.ProcessStartedUtc == b.ProcessStartedUtc &&
        a.ExecutablePath.Equals(b.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    private static bool Same(RuntimeState a, RuntimeState b) => JsonSerializer.Serialize(a, JsonFiles.Options) == JsonSerializer.Serialize(b, JsonFiles.Options);

    private static List<RuntimeState> Candidates(string configPath, DbxConfig proposed, RuntimeState? state)
    {
        var values = new List<RuntimeState>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ValidateGeneration(RuntimeState value)
        {
            bool legacy = value.GenerationNumber == 0 && value.GenerationId.Length == 0 && value.RevisionNumber == 0;
            if (!legacy && (value.GenerationNumber <= 0 || !Guid.TryParseExact(value.GenerationId, "N", out _) ||
                value.RevisionNumber <= 0 || !Path.IsPathFullyQualified(value.OwnerStatePath))) throw Unknown();
        }
        void Add(string path, string? expectedCache = null)
        {
            if (!seen.Add(Path.GetFullPath(path))) return;
            var record = PrivateJsonFile.Read<RuntimeState>(path);
            if (record is null) return;
            if (record.RecoveryJournalVersion != 1 || record.RecoveryRevisionUtc is null || !Resolved(record) ||
                expectedCache is not null && !Path.GetFullPath(record.ActiveConfig.CacheDir).Equals(Path.GetFullPath(expectedCache), StringComparison.OrdinalIgnoreCase))
                throw Unknown();
            ValidateGeneration(record); values.Add(record);
        }
        if (state?.ActiveConfig is null && state is not null) throw new InvalidDataException("The runtime destination is invalid.");
        if (state is not null && !Resolved(state) && (state.RclonePid > 0 || state.ConfigPath.Length > 0)) throw Unknown();
        if (state is not null && Resolved(state)) { ValidateGeneration(state); values.Add(state); }
        Add(ConfigPath(configPath));
        Add(CachePath(proposed.CacheDir), proposed.CacheDir);
        foreach (string cache in values.Select(v => v.ActiveConfig.CacheDir).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            Add(CachePath(cache), cache);
        return values;
    }

    private static bool OwnerStopped(RuntimeState value)
    {
        if (value.LaunchPending && value.IdentityVersion == 1 && value.RclonePid <= 0 &&
            value.LaunchPhase == StartupPhase.Intent && MountController.ParentStopped(value)) return true;
        if (value.LaunchPending && (value.IdentityVersion != 1 || !ProcessIdentity.CompleteRecord(value.RclonePid, value.ProcessStartedUtc, value.ExecutablePath))) return false;
        try { using var process = ProcessIdentity.Open(value); return process is null; }
        catch (ProcessIdentityUnknownException) { return false; }
    }

    private static RuntimeState SelectOrigin(IReadOnlyList<RuntimeState> values)
    {
        var numbered = values.Where(v => v.GenerationNumber > 0).ToArray();
        if (numbered.Length == 0)
        {
            // Legacy timestamps are not ordering evidence. Different live or
            // uncertain owners are ambiguous; stopped snapshots retain all pins.
            var owners = values.DistinctBy(v => (v.RclonePid, v.ProcessStartedUtc, v.ExecutablePath)).ToArray();
            var living = owners.Where(v => !OwnerStopped(v)).ToArray();
            if (living.Length > 1) throw Unknown();
            RuntimeState chosen = living.FirstOrDefault() ?? values.FirstOrDefault(v => v.RecoveryRequired || v.LaunchPending) ?? values[0];
            return chosen with { RecoveryRequired = values.Any(v => v.RecoveryRequired),
                RescueRecords = values.SelectMany(v => v.RescueRecords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }
        long generation = numbered.Max(v => v.GenerationNumber);
        var newest = numbered.Where(v => v.GenerationNumber == generation).ToArray();
        if (newest.Select(v => v.GenerationId).Distinct(StringComparer.Ordinal).Count() != 1) throw Unknown();
        long revision = newest.Max(v => v.RevisionNumber);
        var latest = newest.Where(v => v.RevisionNumber == revision).ToArray();
        RuntimeState selected = MountProof.Retain(latest[0], newest);
        if (latest.Skip(1).Any(v => !Same(MountProof.Retain(v, newest), selected))) throw Unknown();
        foreach (var older in values.Where(v => v.GenerationNumber < generation && !SameOwner(v, selected)))
            if (!OwnerStopped(older)) throw Unknown();
        return selected;
    }

    internal static RuntimeState? Recover(string configPath, DbxConfig proposed, RuntimeState? state)
    {
        var values = Candidates(configPath, proposed, state);
        if (values.Count == 0) return state;
        var origins = new List<List<RuntimeState>>();
        foreach (var value in values)
        {
            var group = origins.FirstOrDefault(g => RecoveryDestination.Matches(g[0].ActiveConfig, value.ActiveConfig));
            if (group is null) origins.Add([value]); else group.Add(value);
        }
        RuntimeState[] chosen = origins.Select(g => SelectOrigin(g)).ToArray();
        var pending = chosen.Where(v => v.RecoveryRequired || v.LaunchPending || !OwnerStopped(v)).ToArray();
        if (pending.Length > 1) throw Unknown();
        if (pending.Length == 1) return pending[0];
        long number = chosen.Max(v => v.GenerationNumber);
        var newest = chosen.Where(v => v.GenerationNumber == number).ToArray();
        if (number > 0 && newest.Select(v => v.GenerationId).Distinct(StringComparer.Ordinal).Count() != 1) throw Unknown();
        return newest.OrderByDescending(v => v.RevisionNumber).First();
    }

    internal static (string Id, long Number) NextGeneration(string configPath, DbxConfig config, RuntimeState? local)
    {
        long prior = Candidates(configPath, config, local).Select(v => v.GenerationNumber).DefaultIfEmpty(0).Max();
        return (Guid.NewGuid().ToString("N"), checked(prior + 1));
    }

    internal static RuntimeState PrepareSave(string configPath, string statePath, RuntimeState value, RuntimeState? local)
    {
        var candidates = Candidates(configPath, value.ActiveConfig, local);
        long highest = candidates.Select(v => v.GenerationNumber).DefaultIfEmpty(0).Max();
        var latest = candidates.Where(v => v.GenerationNumber == highest).ToArray();
        if (value.GenerationNumber == 0)
        {
            if (highest > 0)
            {
                RuntimeState current = SelectOrigin(latest);
                if (!SameOwner(value, current) || !RecoveryDestination.Matches(value.ActiveConfig, current.ActiveConfig)) throw Unknown();
                value = value with { GenerationId = current.GenerationId, GenerationNumber = current.GenerationNumber, OwnerStatePath = current.OwnerStatePath };
            }
            else value = value with { GenerationId = Guid.NewGuid().ToString("N"), GenerationNumber = 1, OwnerStatePath = statePath };
        }
        else if (value.GenerationNumber < highest) throw Unknown();
        else if (value.GenerationNumber > highest)
        {
            if (value.GenerationNumber != checked(highest + 1) || value.RclonePid != 0 || !value.LaunchPending ||
                !value.OwnerStatePath.Equals(statePath, StringComparison.OrdinalIgnoreCase) || latest.Any(v => !OwnerStopped(v))) throw Unknown();
        }
        else if (latest.Any(v => v.GenerationId != value.GenerationId)) throw Unknown();
        RequireOwner(value, statePath);
        value = MountProof.Retain(value, candidates.OrderByDescending(v => v.RevisionNumber));
        long revision = candidates.Where(v => v.GenerationNumber == value.GenerationNumber && v.GenerationId == value.GenerationId)
            .Select(v => v.RevisionNumber).DefaultIfEmpty(0).Max();
        return value with { RevisionNumber = checked(revision + 1), RecoveryJournalVersion = 1, RecoveryRevisionUtc = DateTimeOffset.UtcNow };
    }

    internal static bool ForeignOwner(RuntimeState state, string path) => state.OwnerStatePath.Length > 0 &&
        !state.OwnerStatePath.Equals(path, StringComparison.OrdinalIgnoreCase) && !OwnerStopped(state);

    internal static string ForeignOwnerMessage(RuntimeState state) =>
        $"\u65b0\u3057\u3044\u8d77\u52d5\u306f\u5225\u306e\u72b6\u614b\u30d5\u30a1\u30a4\u30eb\u304c\u6240\u6709\u3057\u3066\u3044\u307e\u3059 (PID {state.RclonePid})\u3002\u505c\u6b62\u6e08\u307f\u3068\u306f\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002\u3053\u306e\u72b6\u614b\u30d5\u30a1\u30a4\u30eb\u304b\u3089\u306e\u505c\u6b62\u30fb\u63a5\u7d9a\u30fb\u8a2d\u5b9a\u5909\u66f4\u306f\u65ad\u308a\u307e\u3059\u3002\u5143\u306e\u72b6\u614b\u30d5\u30a1\u30a4\u30eb\u3067\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002";

    internal static void RequireOwner(RuntimeState state, string path)
    {
        if (ForeignOwner(state, path)) throw new PendingUploadsException(ForeignOwnerMessage(state));
    }
}
