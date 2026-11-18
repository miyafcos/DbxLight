using System.Diagnostics;
using System.Text.Json;

namespace DbxLight.Core;

public sealed class AgentCommandException(int exitCode, string code, string message, DbxStatus? status = null, int skippedUnsafe = 0)
    : InvalidOperationException(message)
{
    public int ExitCode { get; } = exitCode;
    public string Code { get; } = code;
    public DbxStatus? Status { get; } = status;
    public int SkippedUnsafe { get; } = skippedUnsafe;
}

public sealed record AgentEntry(string Name, string Path, string Type, long? Size, DateTimeOffset? ModifiedUtc,
    bool? Cached, bool Hidden);
public sealed record AgentContinuation(int? Offset, string Hint)
{
    public static AgentContinuation ForOffset(int offset, bool timeLimited = false, bool noProgress = false)
    {
        string prefix = timeLimited ? "Time limit reached; there is more to search, even when entries is empty. " : "";
        return offset <= 1000000
            ? new(offset, prefix + (timeLimited && noProgress
                ? "Increase --time-limit or narrow the scope with --in, then continue with --offset "
                : "Repeat the same command and options with --offset ") + offset + "; listing must remain stable.")
            : new(null, prefix + "Offset pagination limit reached. Continue within narrower child folders using the path or --in option.");
    }
}
public sealed record AgentListing(bool Ok, string Source, IReadOnlyList<AgentEntry> Entries, bool Truncated,
    AgentContinuation? Continuation, int Depth, bool DepthLimited, DbxStatus Status, int SkippedUnsafe = 0, bool TimeLimited = false);
public sealed record ListingOptions(bool Recursive = false, int? Depth = null, int Max = 1000,
    bool IncludeHidden = false, int Offset = 0, double TimeLimitSeconds = 45);

/// <summary>Only metadata operations live here; never open a mounted file for content.</summary>
public sealed partial class AgentMetadata : IDisposable
{
    private readonly MountController controller;
    private readonly bool initiallyRunning;
    public DbxConfig Config { get; }
    public DbxStatus Status { get; private set; }
    public RuntimeState? Runtime { get; }
    private readonly RcClient? rc;
    private readonly string? cacheDataPath;
    private readonly MountMetadata? mount;
    private readonly Lazy<CloudWebMapping?> webMapping;
    private readonly Dictionary<string, int> unsafeFolders = new(StringComparer.Ordinal);
    private int SkippedUnsafe => unsafeFolders.Values.Sum();
    internal Func<string, IReadOnlyList<MetadataItem>>? ChildrenReader { get; init; }
    public string Source => rc is null ? "lsjson" : "mount";

    public AgentMetadata(MountController controller, bool requireRemote = true)
        : this(controller, requireRemote, DropboxFolderClient.OpenWithoutRenewal) { }

    internal AgentMetadata(MountController controller, bool requireRemote, Func<DbxConfig, DropboxFolderClient> openFolder)
    {
        this.controller = controller;
        var observed = controller.ReadAgentRuntime();
        Status = observed.Status; Runtime = observed.Runtime; initiallyRunning = Status.Running;
        if (Runtime is not null && !Runtime.ConfigPath.Equals(controller.Config.PathName, StringComparison.OrdinalIgnoreCase))
            throw new AgentCommandException(3, "state_mismatch", "State belongs to another config; ask a person to check it.");
        Config = (Status.Running || Status.RecoveryRequired || Status.ProcessUncertain ? Runtime?.ActiveConfig : null)
            ?? controller.Config.Load();
        if (string.IsNullOrWhiteSpace(Config.Remote))
            throw new AgentCommandException(2, "not_configured", "Remote is not configured. Check --config or DBXL_CONFIG.", Status);
        EnsureSafe();
        Config = Config.Validate(forMount: requireRemote);
        webMapping = new(() => ReadWebMapping(openFolder));
        if (Status.Running && Status.Ready && !Status.ProcessUncertain)
        {
            mount = new MountMetadata(Config.MountPoint);
            rc = new RcClient(Runtime!);
            try
            {
                JsonElement disk = rc.Call("vfs/stats").GetProperty("diskCache");
                cacheDataPath = disk.GetProperty("path").GetString();
                if (cacheDataPath is null || !MountPaths.IsSameOrBelow(WithoutExtendedPrefix(cacheDataPath), WithoutExtendedPrefix(Config.CacheDir)))
                    throw new InvalidDataException("Cache path is outside the active cache directory.");
            }
            catch { rc.Dispose(); throw; }
        }
    }

    private static string WithoutExtendedPrefix(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private void EnsureSafe()
    {
        if (Status.RecoveryRequired || Status.ProcessUncertain)
            throw new AgentCommandException(3, Status.ProcessUncertain ? "process_uncertain" : "recovery_required",
                "Recovery or process verification is incomplete; tell a person. Do not clear recovery automatically.", Status);
        if (Status.Running && (!Status.Ready || Status.StatusError is not null))
            throw new AgentCommandException(3, "status_unknown", "The running mount cannot be verified.", Status);
    }

    public string Relative(string path, bool allowRoot = true)
    {
        // Root-relative and extended/device paths are ambiguous and must not escape the mapping.
        if (path.StartsWith('/') || path.StartsWith('\\') && !path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Use a cloud-relative path or a fully qualified path inside the mount.");
        return MountPaths.ToRelative(path, Config.MountPoint, allowRoot);
    }

    private void EnsureVisible(string relative, bool includeHidden = false)
    {
        if (!includeHidden && HiddenFolders.IsHidden(relative, Config.HiddenPaths))
            throw new AgentCommandException(3, "hidden", "This folder is hidden. Tell a person if it is needed.", Status);
    }

    public AgentListing List(string path, ListingOptions options) => StableRead(() => ListCore(path, options)) with { Status = Status };

    private AgentListing ListCore(string path, ListingOptions options)
    {
        int depth = options.Depth ?? (options.Recursive ? 32 : 1);
        if (depth is < 1 or > 128 || options.Max is < 1 or > 10000 || options.Offset is < 0 or > 1000000)
            throw new ArgumentException("depth=1..128, max=1..10000, offset=0..1000000.");
        AgentTraversalBudget.Validate(options.TimeLimitSeconds);
        string relative = Relative(path);
        EnsureVisible(relative, options.IncludeHidden);
        unsafeFolders.Clear();
        string listingSource = options.IncludeHidden && rc is not null ? "rc-remote" : Source;
        var entries = new List<AgentEntry>();
        int visited = 0;
        bool depthLimited = false;
        var budget = new AgentTraversalBudget(depth > 1 ? options.TimeLimitSeconds : null);
        foreach (AgentEntry entry in Walk(relative, depth, options.IncludeHidden, budget))
        {
            if (entry.Type == "dir" && entry.Path.Count(c => c == '/') - relative.Count(c => c == '/') >= depth - (relative.Length == 0 ? 1 : 0))
                depthLimited = true;
            if (visited++ < options.Offset) continue;
            if (entries.Count == options.Max)
                return new(true, listingSource, entries, true, AgentContinuation.ForOffset(options.Offset + entries.Count), depth, depthLimited, Status, SkippedUnsafe);
            entries.Add(entry);
        }
        return new(true, listingSource, entries, budget.TimeLimited,
            budget.TimeLimited ? AgentContinuation.ForOffset(Math.Max(options.Offset, visited), timeLimited: true, noProgress: entries.Count == 0 && visited <= options.Offset) : null,
            depth, depthLimited, Status, SkippedUnsafe, budget.TimeLimited);
    }

    private IEnumerable<AgentEntry> Walk(string relative, int depth, bool includeHidden, AgentTraversalBudget budget)
    {
        // Keep at most four prefetched folders, including completed results. Parse and
        // consume on this thread in the original DFS order so pagination never races.
        var pending = new Dictionary<string, Task<IReadOnlyList<MetadataItem>?>>(StringComparer.Ordinal);
        var work = new Stack<(AgentEntry Entry, int Remaining)>();
        Push(ReadListingChildren(relative, includeHidden), relative, depth);
        try
        {
            while (work.Count > 0)
            {
                if (budget.StopIfExpired()) yield break;
                var next = work.Pop();
                yield return next.Entry;
                if (next.Entry.Type != "dir" || next.Remaining <= 1) continue;
                if (budget.StopIfExpired()) yield break;
                IReadOnlyList<MetadataItem>? items;
                try
                {
                    items = pending.Remove(next.Entry.Path, out var task)
                        ? task.GetAwaiter().GetResult() : ReadListingChildren(next.Entry.Path, includeHidden);
                }
                catch (Exception) when (budget.StopIfExpired())
                {
                    // Its contents were not consumed before the deadline. A later page
                    // still reports a real failure if that folder cannot then be read.
                    items = null;
                }
                if (budget.StopIfExpired()) yield break;
                Push(items!, next.Entry.Path, next.Remaining - 1);
            }
        }
        finally
        {
            // An early max/scan stop must leave no readers using a disposed RC client.
            // Failures in unconsumed speculative folders do not change the requested page.
            try { Task.WaitAll(pending.Values.ToArray()); } catch (AggregateException) { }
        }

        void Push(IReadOnlyList<MetadataItem> items, string parent, int remaining)
        {
            foreach (AgentEntry entry in ParseChildren(items, parent, includeHidden).OrderByDescending(e => e.Path, StringComparer.Ordinal))
                work.Push((entry, remaining));
            foreach (var candidate in work.Where(w => w.Entry.Type == "dir" && w.Remaining > 1 &&
                !pending.ContainsKey(w.Entry.Path)).Take(4 - pending.Count))
            {
                if (budget.Expired) break;
                string path = candidate.Entry.Path;
                pending.Add(path, Task.Run(() => budget.Expired ? null : ReadListingChildren(path, includeHidden)));
            }
        }
    }

    private IReadOnlyList<AgentEntry> Children(string relative, bool includeHidden = false, bool cloud = false)
    {
        return ParseChildren(ReadChildren(relative, includeHidden, cloud), relative, includeHidden);
    }

    private IReadOnlyList<AgentEntry> ParseChildren(IReadOnlyList<MetadataItem> items, string relative, bool includeHidden)
    {
        int skipped = items.Count(item => !SafeName(item.Name));
        if (skipped > 0 && unsafeFolders.TryAdd(relative, skipped))
            Console.Error.WriteLine($"Skipped {skipped} unsafe entries in relative folder {JsonSerializer.Serialize(relative)}.");
        return items.Where(item => SafeName(item.Name)).Select(item => ParseEntry(item, relative))
            .Where(e => includeHidden || !e.Hidden).ToArray();
    }

    private AgentCommandException NotFound() => new(4, "not_found",
        "The path is not in the current listing. Check its name or ls the parent folder.", Status, SkippedUnsafe);

    private IReadOnlyList<MetadataItem> ReadListingChildren(string relative, bool includeHidden = false)
    {
        try { return ReadChildren(relative, includeHidden); }
        catch (DirectoryNotFoundException) { throw NotFound(); }
    }

    private IReadOnlyList<MetadataItem> ReadChildren(string relative, bool includeHidden = false, bool cloud = false)
    {
        if (ChildrenReader is not null) return ChildrenReader(relative);
        if (mount is not null && !includeHidden && !cloud) return mount.Read(relative);
        JsonElement items;
        if (rc is not null)
        {
            // Only explicit hidden listing and cloud verification bypass the mounted view.
            items = rc.Call("operations/list", new
            {
                fs = Config.Remote,
                remote = relative, opt = new { recurse = false, noMimeType = true, showHash = false },
                _filter = RcloneFilters.RemoteOptions(Config)
            }).GetProperty("list");
        }
        else items = RunLsJson(relative, stat: false);
        return items.EnumerateArray().Select(ReadItem).ToArray();
    }

    private static MetadataItem ReadItem(JsonElement item)
    {
        string name = item.TryGetProperty("Name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        bool directory = item.GetProperty("IsDir").GetBoolean();
        DateTimeOffset? modified = item.TryGetProperty("ModTime", out var stamp) &&
            DateTimeOffset.TryParse(stamp.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : null;
        return new(name, directory, item.GetProperty("Size").GetInt64(), modified);
    }

    private AgentEntry ParseEntry(MetadataItem item, string parent)
    {
        string name = item.Name;
        if (!SafeName(name))
            throw new InvalidDataException("Entry contains an unsafe path component.");
        string path = parent.Length == 0 ? name : parent + "/" + name;
        string? cacheFile = cacheDataPath is null ? null : CacheNames.PathIn(cacheDataPath, path);
        bool? cached = item.IsDir ? false : cacheFile is null ? null : File.Exists(cacheFile);
        long? size = item.IsDir ? null : item.Size;
        DateTimeOffset? modified = item.ModifiedUtc;
        // rclone uses the Unix metadata fallback date for directories with no known time.
        if (item.IsDir && (modified == new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) || modified == DateTimeOffset.MinValue))
            modified = null;
        return new(name, path, item.IsDir ? "dir" : "file", size, modified,
            cached, HiddenFolders.IsHidden(path.Normalize(), Config.HiddenPaths));
    }

    private static bool SafeName(string name) => name is not ("" or "." or "..") &&
        name.IndexOfAny(['/', '\\', ':']) < 0 && !name.Any(char.IsControl);

    private JsonElement RunLsJson(string relative, bool stat)
    {
        var args = new List<string> { "lsjson", FolderBrowser.JoinRemote(Config.Remote, relative), "--no-mimetype", "--ask-password=false", "--ftp-ask-password=false", "--sftp-ask-password=false" };
        if (stat) args.Add("--stat");
        if (Config.RcloneConfigPath is not null) { args.Add("--config"); args.Add(Config.RcloneConfigPath); }
        args.AddRange(RcloneFilters.Arguments(Config));
        using var process = JobProcess.Start(RcloneArguments.ProcessInfo(Config.RclonePath, args));
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (!process.WaitForExit(30000))
        {
            process.Kill(); process.WaitForExit(10000);
            throw new TimeoutException("rclone lsjson timed out.");
        }
        string json = output.GetAwaiter().GetResult();
        error.GetAwaiter().GetResult();
        if (!stat && process.ExitCode is 3 or 4) throw NotFound();
        if (process.ExitCode != 0) throw new IOException("rclone lsjson failed; exit=" + process.ExitCode + ".");
        return RcloneOutput.Json(json, "lsjson");
    }

    private T StableRead<T>(Func<T> read)
    {
        T result;
        try { result = read(); }
        catch
        {
            try { CheckReadGeneration(); }
            catch (ControlBusyException) { } // A later busy check cannot replace the read failure.
            throw;
        }
        CheckReadGeneration();
        return result;
    }

    private void CheckReadGeneration()
    {
        (DbxStatus Status, RuntimeState? Runtime) latest;
        try { latest = controller.ReadAgentRuntime(); }
        catch (AgentCommandException e) when (e.Code == "state_mismatch")
        { throw new AgentCommandException(3, "mount_changed", "The mount now belongs to another configuration; discard these results.", Status); }
        Status = latest.Status;
        if (latest.Runtime is not null && !latest.Runtime.ConfigPath.Equals(controller.Config.PathName, StringComparison.OrdinalIgnoreCase) ||
            Status.Running != initiallyRunning ||
            latest.Runtime?.RclonePid != Runtime?.RclonePid || latest.Runtime?.ProcessStartedUtc != Runtime?.ProcessStartedUtc ||
            JsonSerializer.Serialize(latest.Runtime?.ActiveConfig, JsonFiles.Options) != JsonSerializer.Serialize(Runtime?.ActiveConfig, JsonFiles.Options) ||
            (!initiallyRunning && JsonSerializer.Serialize(controller.Config.Load(), JsonFiles.Options) != JsonSerializer.Serialize(Config, JsonFiles.Options)))
            throw new AgentCommandException(3, "mount_changed", "The active mount or its configuration changed; discard these results and check again.", Status);
        EnsureSafe();
    }

    public void Dispose() => rc?.Dispose();
}
