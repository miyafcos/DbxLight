using System.Globalization;
using DbxLight.Core;

namespace DbxLight.Cli;

internal static class AgentCommands
{
    internal static bool IsCommand(string command) => command is "ls" or "wait" or "refresh" or "agent-guide" or "stat" or "find" or "resolve";

    internal static int Run(MountController controller, string command, string[] args, bool json)
    {
        var options = new CommandOptions(args);
        if (command == "resolve")
        {
            string resolveInput = options.One();
            using var resolver = new AgentMetadata(controller);
            AgentResolution resolution = resolver.Resolve(resolveInput);
            if (json) Program.WriteJson(resolution);
            else Console.WriteLine($"local={resolution.LocalPath}\nrelative={resolution.RemotePath}\nweb={resolution.WebUrl}");
            return 0;
        }
        if (command == "find")
        {
            string searchIn = options.Value("--in") ?? "";
            string? searchType = options.Value("--type");
            int searchDepth = options.Number("--depth") ?? 8;
            int searchMax = options.Number("--max") ?? 1000;
            int searchOffset = options.Number("--offset") ?? 0;
            double searchTimeLimit = options.TimeLimit();
            string term = options.One();
            using var search = new AgentMetadata(controller);
            AgentFindResult found = search.Find(term, searchIn, searchType, searchDepth, searchMax, searchOffset, searchTimeLimit);
            if (json) Program.WriteJson(found);
            else
            {
                foreach (AgentEntry entry in found.Entries) Console.WriteLine($"{entry.Type}\t{entry.Size}\t{entry.Path}");
                if (found.Truncated) Console.Error.WriteLine("Truncated. " + found.Continuation!.Hint);
                if (found.DepthLimited) Console.Error.WriteLine("Depth limit reached; narrow --in or increase --depth.");
            }
            return 0;
        }
        if (command == "stat")
        {
            _ = options.TimeLimit(); // Accepted and validated; one folder read cannot be interrupted.
            string target = options.One();
            using var stat = new AgentMetadata(controller);
            AgentStatResult detail = stat.Stat(target);
            if (json) Program.WriteJson(detail);
            else Console.WriteLine($"{detail.Entry.Type}\t{detail.Entry.Size}\t{detail.Entry.ModifiedUtc:O}\tcached={detail.Entry.Cached}\t{detail.UploadState}\t{detail.WebUrl}");
            return 0;
        }
        if (command == "agent-guide")
        {
            string format = options.Value("--format") ?? (json ? "json" : "markdown");
            if (format is not ("markdown" or "json") || options.Rest().Length != 0)
                throw new ArgumentException("Usage: agent-guide [--format markdown|json]");
            AgentGuideResult guide = AgentGuide.Create(controller);
            if (json || format == "json") Program.WriteJson(guide); else Console.Write(guide.Markdown);
            return guide.Ok ? 0 : guide.Code == "not_configured" ? 2 : 3;
        }
        if (command == "refresh")
        {
            string[] paths = options.Rest();
            if (paths.Length > 1) throw new ArgumentException("Usage: refresh [path]");
            using var refresh = new AgentMetadata(controller, requireRemote: false);
            AgentRefreshResult refreshed = refresh.Refresh(paths.FirstOrDefault() ?? "");
            if (json) Program.WriteJson(refreshed); else Console.WriteLine("Listing refreshed: " + refreshed.Path);
            return 0;
        }
        if (command == "wait")
        {
            bool all = options.Flag("--all");
            double timeout = options.Value("--timeout") is { } value
                ? double.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : 120;
            string[] paths = options.Rest();
            if (all == (paths.Length > 0) || !double.IsFinite(timeout) || timeout is < 0 or > 86400)
                throw new ArgumentException("Usage: wait <file>... | wait --all [--timeout seconds]");
            var startedWaiting = System.Diagnostics.Stopwatch.StartNew();
            AgentWaitOutcome outcome;
            try
            {
                using var uploads = new AgentMetadata(controller, requireRemote: false);
                outcome = uploads.Wait(paths, all, timeout);
            }
            catch (Exception e) when (e is AgentCommandException or ControlBusyException)
            {
                var failure = e as AgentCommandException;
                var unknown = paths.Select(p => new AgentUploadItem(p, "unknown", false, "Not verified.")).ToArray();
                string message = failure?.Message ?? Program.BusyMessage;
                outcome = new(failure?.ExitCode ?? 3, new(false, failure?.Code ?? "busy", message, unknown,
                    Math.Round(startedWaiting.Elapsed.TotalSeconds, 3), failure?.Status));
            }
            if (outcome.Result.Code == "recovery_required")
                outcome = outcome with { Result = outcome.Result with { Message = Program.RecoveryMessage(outcome.Result.Status, outcome.Result.Message) } };
            if (json) Program.WriteJson(outcome.Result);
            else
            {
                foreach (AgentUploadItem item in outcome.Result.Items) Console.WriteLine($"{item.State}\t{item.Path}\t{item.Message}");
                (outcome.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(outcome.Result.Message);
                Console.WriteLine(outcome.Result.RescueScope);
            }
            return outcome.ExitCode;
        }
        bool recursive = options.Flag("--recursive");
        bool includeHidden = options.Flag("--include-hidden");
        int? depth = options.Number("--depth");
        int max = options.Number("--max") ?? 1000;
        int offset = options.Number("--offset") ?? 0;
        double timeLimit = options.TimeLimit();
        string path = options.One();
        using var metadata = new AgentMetadata(controller);
        AgentListing result = metadata.List(path, new(recursive, depth, max, includeHidden, offset, timeLimit));
        if (json) Program.WriteJson(result);
        else
        {
            foreach (AgentEntry entry in result.Entries)
                Console.WriteLine($"{entry.Type}\t{entry.Size}\t{entry.ModifiedUtc:O}\tcached={entry.Cached}\t{entry.Path}");
            if (result.Truncated) Console.Error.WriteLine("Truncated. " + result.Continuation!.Hint);
        }
        return 0;
    }
}

internal sealed class CommandOptions(string[] args)
{
    private readonly List<string> words = args.ToList();
    public bool Flag(string name)
    {
        bool found = words.Remove(name);
        if (words.Contains(name)) throw new ArgumentException("Duplicate option: " + name);
        return found;
    }
    public string? Value(string name)
    {
        int index = words.IndexOf(name);
        if (index < 0) return null;
        if (index + 1 == words.Count || words[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(name + " needs a value.");
        string value = words[index + 1]; words.RemoveRange(index, 2);
        if (words.Contains(name)) throw new ArgumentException("Duplicate option: " + name);
        return value;
    }
    public int? Number(string name) => Value(name) is { } value ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : null;
    public double TimeLimit()
    {
        double seconds = Value("--time-limit") is { } value
            ? double.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : 45;
        if (!double.IsFinite(seconds) || seconds is < 1 or > 3600)
            throw new ArgumentException("--time-limit must be 1..3600 seconds.");
        return seconds;
    }
    public string One()
    {
        string[] remaining = Rest();
        if (remaining.Length != 1) throw new ArgumentException("Expected one path or search term.");
        return remaining[0];
    }
    public string[] Rest()
    {
        if (words.Any(w => w.StartsWith("--", StringComparison.Ordinal))) throw new ArgumentException("Unknown option.");
        return words.ToArray();
    }
}
