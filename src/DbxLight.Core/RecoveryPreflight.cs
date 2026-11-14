using System.Diagnostics;
using System.Text.Json;

namespace DbxLight.Core;

internal sealed class RecoveryFreshnessExpiredException : Exception { }
internal sealed record RecoveryComparedItem(CacheEntry Entry, string Path, DateTimeOffset ComparedUtc, long Timestamp);
internal sealed record RecoveryLaunchBatch(string[] RescuePaths, string? DataRoot, string? MetaRoot, RecoveryComparedItem[] Items)
{
    internal DateTimeOffset? OldestUtc => Items.Length == 0 ? null : Items.Min(i => i.ComparedUtc);
    internal double AgeSeconds => Items.Length == 0 ? 0 : Items.Max(i => Stopwatch.GetElapsedTime(i.Timestamp).TotalSeconds);
}

public sealed partial class MountController
{
    internal static readonly TimeSpan RecoveryFreshnessLimit = TimeSpan.FromSeconds(30);
    internal const int RecoveryFreshnessRetries = 3;
    internal TimeSpan? RecoveryFreshnessTestLimit { get; set; }
    internal Action<string, int>? RecoveryComparisonCheckpoint { get; set; }
    internal Action<string, string>? RecoveryNameCheckpoint { get; set; }
    private const string ComparisonLimitReason = "数が多い・遅いので、自動では上げない";

    private RecoveryLaunchBatch PrepareRecoveryComparison(DbxConfig config, RuntimeState? before, int attempt)
    {
        RuntimeState? view = before;
        RecoveryBudget budget = preparation?.Budget ?? new(StartupPreparation.MaximumComparisonTime, StartupPreparation.MaximumComparisonFolders);
        void CheckComparison()
        {
            preparation?.CheckCancellation();
            budget.Check();
        }
        try
        {
            while (true)
            {
                var records = PrepareCache(config, view);
                var scan = PendingCacheInspector.InspectStopped(config.CacheDir, () => preparation?.CheckCancellation());
                if (!scan.CompleteScan) throw new InvalidDataException("キャッシュを完全に走査できないため起動しません。");
                var dirty = scan.Entries.Where(e => e.MetadataValid && e.Dirty == true).ToArray();
                string[] rescuePaths = records.Where(r => !r.IsForeign).Select(r => r.RecordPath).ToArray();
                if (dirty.Length == 0) return new(rescuePaths, view?.CacheDataRoot, view?.CacheMetadataRoot, []);
                if (attempt > RecoveryFreshnessRetries || budget.Expired) return PreserveComparisonRemainder(config, view);
                var probe = new RecoveryRemoteProbe(config, budget, () => preparation?.CheckCancellation(), RecoveryNameCheckpoint);
                RemoteInfo info = RecoveryInfo(probe);
                var roots = RecoveryRemoteProbe.Roots(config, info);
                view = (view ?? new RuntimeState { ActiveConfig = config }) with { CacheDataRoot = roots.Data, CacheMetadataRoot = roots.Meta };
                var inputs = dirty.Select(e => (Entry: e, Cloud: RescueStore.CloudPath(view, e))).ToArray();
                var decisions = new List<(CacheEntry Entry, string? Cloud, string Reason, bool Conflict)>();
                var comparisons = new List<RecoveryComparedItem>();
                foreach (var input in inputs.Where(i => i.Cloud is null))
                    decisions.Add((input.Entry, null, RecoveryCacheNames.PreservationReason(input.Entry.Relative) ?? "名前の対応が不明のため取り出しました。", false));
                bool exhausted = false;
                int compared = 0;
                foreach (var group in inputs.Where(i => i.Cloud is not null).GroupBy(i => Parent(i.Cloud!),
                    StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    preparation?.Update("雲との照合", compared, dirty.Length);
                    RecoveryFolder folder;
                    try
                    {
                        if (exhausted) throw new RecoveryBudgetExceededException();
                        folder = RecoveryFolderTestOverride?.Invoke(group.Key, info) ?? probe.List(group.Key, info.Hashes.FirstOrDefault(), info);
                    }
                    catch (RecoveryBudgetExceededException)
                    {
                        exhausted = true;
                        foreach (var input in group) decisions.Add((input.Entry, input.Cloud, ComparisonLimitReason, false));
                        continue;
                    }
                    RecoveryNameCheckpoint?.Invoke("listed", group.Key);
                    CheckComparison();
                    DateTimeOffset comparedUtc = folder.ComparedUtc;
                    long timestamp = folder.Timestamp;
                    foreach (var input in group)
                    {
                        bool comparable = RecoveryRemoteProbe.ResolveItem(folder, info, input.Cloud!, out JsonElement? current, CheckComparison, RecoveryNameCheckpoint);
                        RecoveryMatch match = !folder.Comparable ? RecoveryMatch.Unknown : folder.Blocked ? RecoveryMatch.Different : !comparable ? RecoveryMatch.Unknown :
                            RecoveryVerification.CompareFingerprint(input.Entry.Fingerprint, info, current);
                        if (match == RecoveryMatch.Unknown)
                            decisions.Add((input.Entry, input.Cloud, "雲の側と比べる材料がないため取り出しました。", false));
                        else if (match == RecoveryMatch.Different)
                            decisions.Add((input.Entry, input.Cloud, "止めている間に雲の側の版が変わったため取り出しました。", true));
                        else if (NeedsPreflightNameRescue(view, input.Entry, input.Cloud!))
                            decisions.Add((input.Entry, input.Cloud, "起動し直しで拾えない名前のため取り出しました。", false));
                        else comparisons.Add(new(input.Entry, input.Cloud!, comparedUtc, timestamp));
                    }
                    CheckComparison();
                    compared += group.Count();
                    preparation?.Update("雲との照合", compared, dirty.Length);
                    RecoveryComparisonCheckpoint?.Invoke(group.Key, attempt);
                }
                if (decisions.Count == 0) return new(rescuePaths, roots.Data, roots.Meta, comparisons.ToArray());
                int moved = 0;
                foreach (var decision in decisions)
                {
                    preparation?.Update("取り出し", moved, decisions.Count);
                    RescueStore.Rescue(config, decision.Entry, decision.Cloud, decision.Reason, decision.Conflict);
                    preparation?.Update("取り出し", ++moved, decisions.Count);
                }
                if (exhausted)
                {
                    string[] paths = RescueStore.Pending(config, before?.RescueRecords).Where(r => !r.IsForeign).Select(r => r.RecordPath).ToArray();
                    return new(paths, roots.Data, roots.Meta, comparisons.ToArray());
                }
                // Moves precede the final comparison; re-list the remaining candidates.
            }
        }
        catch (RecoveryBudgetExceededException) { return PreserveComparisonRemainder(config, view); }
        catch (RecoveryComparisonException) { throw; }
        catch (Exception e) when (StartupFailure.Get(e) is not null) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StartupFailure.ForUser(e, "local_record_failed", State.PathName,
                "\u624b\u5143\u306e\u8a18\u9332\u3092\u8aad\u307f\u66f8\u304d\u3067\u304d\u306a\u3044\u305f\u3081\u8d77\u52d5\u3092\u7d42\u3048\u307e\u3059\u3002\u81ea\u52d5\u3067\u306f\u518d\u8a66\u884c\u3057\u307e\u305b\u3093\u3002",
                "\u624b\u5143\u306e\u8a18\u9332\u3092\u6b8b\u3057\u3001\u4fdd\u5b58\u5148\u3092\u78ba\u8a8d\u3057\u3066\u304b\u3089\u660e\u793a\u306e\u64cd\u4f5c\u3067\u3084\u308a\u76f4\u3057\u3066\u304f\u3060\u3055\u3044\u3002");
            throw;
        }
    }

    private RecoveryLaunchBatch PreserveComparisonRemainder(DbxConfig config, RuntimeState? view)
    {
        var scan = PendingCacheInspector.InspectStopped(config.CacheDir, () => preparation?.CheckCancellation());
        if (!scan.CompleteScan) throw new InvalidDataException("キャッシュを完全に走査できないため起動しません。");
        var remaining = scan.Entries.Where(e => e.Dirty == true).ToArray();
        for (int i = 0; i < remaining.Length; i++)
        {
            preparation?.Update("取り出し", i, remaining.Length);
            RescueStore.Rescue(config, remaining[i], RescueStore.CloudPath(view, remaining[i]), ComparisonLimitReason);
            preparation?.Update("取り出し", i + 1, remaining.Length);
        }
        return new(RescueStore.Pending(config, view?.RescueRecords).Where(r => !r.IsForeign).Select(r => r.RecordPath).ToArray(),
            view?.CacheDataRoot, view?.CacheMetadataRoot, []);
    }
    private static string Parent(string path) => path.LastIndexOf('/') is int slash && slash >= 0 ? path[..slash] : "";
}
