using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

/// <summary>Publish an intentionally synthesized fixture snapshot as one record set.
/// Generation-disagreement tests deliberately use StateStore/RecoveryJournal directly.</summary>
internal static class RuntimeSnapshots
{
    internal static void Save(StateStore store, RuntimeState value) => ControlGate.Execute(() =>
    {
        if (value.ActiveConfig is null || !RecoveryDestination.Resolved(value.ActiveConfig)) { store.Save(value); return 0; }
        var copies = new List<RuntimeState>();
        try { if (store.Load() is { } current) copies.Add(current); }
        catch (Exception e) when (e is JsonException or InvalidDataException) { }
        foreach (string path in new[] { RecoveryJournal.CachePath(value.ActiveConfig.CacheDir), RecoveryJournal.ConfigPath(value.ConfigPath) })
            if (PrivateJsonFile.Read<RuntimeState>(path) is { } copy) copies.Add(copy);
        RuntimeState? newest = copies.OrderByDescending(s => s.GenerationNumber).ThenByDescending(s => s.RevisionNumber).FirstOrDefault();
        if (value.GenerationNumber == 0 && newest is { GenerationNumber: > 0 })
            value = value with { GenerationId = newest.GenerationId, GenerationNumber = newest.GenerationNumber, OwnerStatePath = newest.OwnerStatePath };
        if (value.GenerationNumber > 0)
        {
            long revision = copies.Where(s => s.GenerationId == value.GenerationId).Select(s => s.RevisionNumber).DefaultIfEmpty(value.RevisionNumber).Max();
            value = value with { RevisionNumber = Math.Max(value.RevisionNumber, revision) + 1, RecoveryJournalVersion = 1, RecoveryRevisionUtc = DateTimeOffset.UtcNow };
            RecoveryJournal.Save(value.ConfigPath, value);
        }
        store.Save(value);
        return 0;
    });
}
