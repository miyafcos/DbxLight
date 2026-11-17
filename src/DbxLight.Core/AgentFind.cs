using System.Text;

namespace DbxLight.Core;

public sealed record AgentFindResult(bool Ok, string Query, string Source, IReadOnlyList<AgentEntry> Entries,
    bool Truncated, AgentContinuation? Continuation, int Depth, bool DepthLimited, DbxStatus Status, int SkippedUnsafe = 0, bool TimeLimited = false);

public sealed partial class AgentMetadata
{
    public AgentFindResult Find(string query, string path = "", string? type = null, int depth = 8, int max = 1000, int offset = 0, double timeLimitSeconds = 45)
        => StableRead(() => FindCore(query, path, type, depth, max, offset, timeLimitSeconds)) with { Status = Status };

    private AgentFindResult FindCore(string query, string path, string? type, int depth, int max, int offset, double timeLimitSeconds)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Any(char.IsControl) || type is not (null or "file" or "dir") ||
            depth is < 1 or > 128 || max is < 1 or > 10000 || offset is < 0 or > 1000000)
            throw new ArgumentException("find needs a name fragment; type=file|dir; depth=1..128; max=1..10000; offset=0..1000000.");
        AgentTraversalBudget.Validate(timeLimitSeconds);
        string relative = Relative(path); EnsureVisible(relative);
        unsafeFolders.Clear();
        string needle = Fold(query);
        var entries = new List<AgentEntry>();
        int position = 0, scanned = 0;
        bool depthLimited = false;
        AgentFindResult Result(bool truncated, int next = 0, bool timeLimited = false) => new(true, query, Source, entries, truncated,
            truncated ? AgentContinuation.ForOffset(next, timeLimited, noProgress: entries.Count == 0 && next == offset) : null,
            depth, depthLimited, Status, SkippedUnsafe, timeLimited);
        var budget = new AgentTraversalBudget(timeLimitSeconds);
        foreach (AgentEntry entry in Walk(relative, depth, includeHidden: false, budget))
        {
            int level = entry.Path[(relative.Length == 0 ? 0 : relative.Length + 1)..].Count(c => c == '/') + 1;
            if (entry.Type == "dir" && level >= depth) depthLimited = true;
            if (position++ < offset) continue;
            if (scanned++ >= 10000) return Result(true, position - 1);
            if ((type is null || type == entry.Type) && Fold(entry.Name).Contains(needle, StringComparison.Ordinal))
            {
                if (entries.Count == max) return Result(true, position - 1);
                entries.Add(entry);
            }
        }
        return Result(budget.TimeLimited, Math.Max(offset, position), budget.TimeLimited);
    }

    private static string Fold(string value) => value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
}
