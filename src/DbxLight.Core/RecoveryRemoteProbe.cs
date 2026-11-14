using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

internal sealed class RecoveryComparisonException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed record RemoteInfo(string Name, string Root, long Precision, string[] Hashes, bool? CaseInsensitive = null);
internal sealed record RecoveryFolder(bool Blocked, IReadOnlyDictionary<string, JsonElement> Items, bool Comparable = true,
    DateTimeOffset ComparedUtc = default, long Timestamp = 0)
{
    private Dictionary<string, List<JsonElement>>? foldedNames, exactNames;

    internal IReadOnlyList<JsonElement> Candidates(string key, bool caseInsensitive, Action? check, Action<string, string>? checkpoint)
    {
        var names = caseInsensitive ? foldedNames : exactNames;
        if (names is null)
        {
            names = new(StringComparer.Ordinal);
            foreach (var value in Items.Values)
            {
                check?.Invoke();
                string path = value.GetProperty("Path").GetString()!;
                string leaf = path[(path.LastIndexOf('/') + 1)..];
                string nameKey = caseInsensitive ? RecoveryRemoteProbe.BroadNameKey(leaf) : leaf;
                if (!names.TryGetValue(nameKey, out var values)) names[nameKey] = values = [];
                values.Add(value);
                checkpoint?.Invoke("key", path);
                check?.Invoke();
            }
            if (caseInsensitive) foldedNames = names; else exactNames = names;
        }
        return names.TryGetValue(key, out var candidates) ? candidates : [];
    }
}

/// <summary>Pinned rclone loopback RC calls query the original remote without starting a mount.</summary>
internal sealed class RecoveryRemoteProbe(DbxConfig config, RecoveryBudget? budget = null,
    Action? checkCancellation = null, Action<string, string>? nameCheckpoint = null)
{
    private readonly Dictionary<string, RecoveryFolder> folders = new(StringComparer.Ordinal);
    private void Check()
    {
        checkCancellation?.Invoke();
        budget?.Check();
    }
    internal RemoteInfo Info()
    {
        string version = Run(["version"]).Split('\n')[0].Trim();
        if (version != "rclone v1.74.3") throw new RecoveryComparisonException("Pending-cache verification requires the verified rclone v1.74.3.");
        JsonElement info = Call("operations/fsinfo", new { fs = config.Remote });
        // A missing root is unavailable, not proof that all original objects were deleted.
        JsonElement? root = Stat("");
        if (root is null || !root.Value.GetProperty("IsDir").GetBoolean())
            throw new RecoveryComparisonException("\u96f2\u306e\u5074\u306e\u6839\u3092\u78ba\u8a8d\u3067\u304d\u307e\u305b\u3093\u3002");
        return DecodeInfo(info);
    }

    internal static RemoteInfo DecodeInfo(JsonElement info) => new(info.GetProperty("Name").GetString()!, info.GetProperty("Root").GetString()!,
        info.TryGetProperty("Precision", out var precision) && precision.TryGetInt64(out long ns) ? ns : 0,
        info.GetProperty("Hashes").EnumerateArray().Select(x => RecoveryVerification.ResolveHashName(x.GetString())!).ToArray(),
        info.TryGetProperty("Features", out var features) && features.ValueKind == JsonValueKind.Object &&
        features.TryGetProperty("CaseInsensitive", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null);

    internal JsonElement? Stat(string relative, string? hash = null)
    {
        JsonElement result = Call("operations/stat", new { fs = config.Remote, remote = relative,
            opt = new { noMimeType = true, showHash = hash is not null, hashTypes = hash is null ? Array.Empty<string>() : new[] { RecoveryVerification.ResolveHashName(hash) } } });
        var item = result.GetProperty("item");
        return item.ValueKind == JsonValueKind.Null ? null : item.Clone();
    }

    internal RecoveryFolder List(string parent, string? hash, RemoteInfo info) =>
        ResolveFolder(parent, info, path =>
        {
            string key = path + "\n" + hash;
            if (folders.TryGetValue(key, out var found)) return found;
            budget?.Folder(path);
            DateTimeOffset utc = DateTimeOffset.UtcNow; long timestamp = Stopwatch.GetTimestamp();
            JsonElement result = Call("operations/list", new { fs = config.Remote, remote = path,
                opt = new { recurse = false, noMimeType = true, showHash = hash is not null,
                    hashTypes = hash is null ? Array.Empty<string>() : new[] { RecoveryVerification.ResolveHashName(hash) } } });
            var folder = DecodeFolder(path, info, result, Check) with { ComparedUtc = utc, Timestamp = timestamp };
            folders[key] = folder;
            return folder;
        }, Check, nameCheckpoint);

    // fsinfo supplies case sensitivity, but no versioned Unicode normalization
    // contract. Preserve spellings outside the verified ASCII case rule.
    internal static bool NameRuleKnown(RemoteInfo info, string path)
    {
        if (info.CaseInsensitive is null) return false;
        if (info.CaseInsensitive == false) return true;
        try
        {
            if (!path.IsNormalized(System.Text.NormalizationForm.FormC)) return false;
            return !path.EnumerateRunes().Any(r => r.Value > 127 && System.Text.Rune.GetUnicodeCategory(r) is
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);
        }
        catch (ArgumentException) { return false; }
    }

    internal static RecoveryFolder Uncomparable() => new(false, new Dictionary<string, JsonElement>(), false);

    internal static RecoveryFolder DecodeFolder(string parent, RemoteInfo info, JsonElement result, Action? check = null)
    {
        if (info.CaseInsensitive is null || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return Uncomparable();
        var items = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            check?.Invoke();
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Path", out var pathNode) ||
                pathNode.ValueKind != JsonValueKind.String || !item.TryGetProperty("IsDir", out var directory) ||
                directory.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Uncomparable();
            string path = pathNode.GetString()!;
            int slash = path.LastIndexOf('/');
            string leaf = path[(slash + 1)..], actualParent = slash < 0 ? "" : path[..slash];
            if (leaf is "" or "." or ".." || path.Any(char.IsControl) ||
                !(info.CaseInsensitive == true ? AsciiKey(parent) == AsciiKey(actualParent) : parent == actualParent) ||
                !names.Add(info.CaseInsensitive == true ? AsciiKey(path) : path) ||
                !items.TryAdd(path, item.Clone())) return Uncomparable();
        }
        return new(false, items);
    }

    internal static RecoveryFolder ResolveFolder(string parent, RemoteInfo info, Func<string, RecoveryFolder> load,
        Action? check = null, Action<string, string>? checkpoint = null)
    {
        string resolved = "";
        foreach (string segment in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            RecoveryFolder contents = load(resolved);
            if (!contents.Comparable || contents.Blocked) return contents;
            string wanted = resolved.Length == 0 ? segment : resolved + "/" + segment;
            if (!ResolveItem(contents, info, wanted, out var item, check, checkpoint)) return contents with { Comparable = false };
            if (item is null)
                return new(false, new Dictionary<string, JsonElement>(), ComparedUtc: contents.ComparedUtc, Timestamp: contents.Timestamp);
            if (!item.Value.GetProperty("IsDir").GetBoolean()) return contents with { Blocked = true };
            resolved = item.Value.GetProperty("Path").GetString()!;
        }
        return load(resolved);
    }

    private static string AsciiKey(string name) => string.Concat(name.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));

    // Deliberately over-fold possible aliases. This key never establishes identity.
    internal static string BroadNameKey(string name)
    {
        var key = new System.Text.StringBuilder();
        foreach (var rune in name.Normalize(System.Text.NormalizationForm.FormKC).EnumerateRunes())
        {
            if (rune.Value is 0x00df or 0x1e9e) key.Append("SS");
            else if (rune.Value is 0x0131 or 0x0130) key.Append('I');
            else key.Append(rune.ToString());
        }
        return key.ToString().ToUpperInvariant();
    }

    internal static bool ResolveItem(RecoveryFolder folder, RemoteInfo info, string wanted, out JsonElement? item,
        Action? check = null, Action<string, string>? checkpoint = null)
    {
        checkpoint?.Invoke("target", wanted);
        check?.Invoke();
        item = null;
        if (!folder.Comparable || info.CaseInsensitive is null) return false;
        string leaf = wanted[(wanted.LastIndexOf('/') + 1)..];
        string broad = info.CaseInsensitive == true ? BroadNameKey(leaf) : leaf;
        var candidates = folder.Candidates(broad, info.CaseInsensitive == true, check, checkpoint);
        if (info.CaseInsensitive == true && candidates.Any(value =>
        {
            check?.Invoke();
            string path = value.GetProperty("Path").GetString()!;
            return AsciiKey(path[(path.LastIndexOf('/') + 1)..]) != AsciiKey(leaf);
        })) return false;
        bool exactOnly = !NameRuleKnown(info, leaf);
        var matches = candidates.Where(value =>
        {
            check?.Invoke();
            string path = value.GetProperty("Path").GetString()!;
            string name = path[(path.LastIndexOf('/') + 1)..];
            return exactOnly || info.CaseInsensitive == false ? name == leaf : AsciiKey(name) == AsciiKey(leaf);
        }).ToArray();
        if (matches.Length > 1 || exactOnly && matches.Length == 0) return false;
        if (matches.Length == 1) item = matches[0];
        return true;
    }

    internal static (string Data, string Meta) Roots(DbxConfig config, RemoteInfo info)
    {
        // cache.go New: strip the leading // of a Windows //?/ root, clean, then toOSPath.
        string root = info.Root.StartsWith("//?/", StringComparison.Ordinal) ? info.Root[2..] : info.Root;
        string[] parts = (info.Name + "/" + root).Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        if (parts.Any(p => p == ".." || p.EndsWith(' ') || p.EndsWith('.') || p.Any(char.IsControl)))
            throw new RecoveryComparisonException("The remote cache-root spelling cannot be verified.");
        string relative = RecoveryCacheNames.EncodeForDisk(string.Join('/', parts)).Replace('/', '\\');
        return (Path.Combine(config.CacheDir, "vfs", relative), Path.Combine(config.CacheDir, "vfsMeta", relative));
    }

    internal static string GoUtc(string rfc3339)
    {
        var match = Regex.Match(rfc3339, @"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,9}))?(Z|[+-]\d\d:\d\d)$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new RecoveryComparisonException("The remote timestamp cannot be verified.");
        var seconds = DateTimeOffset.Parse(match.Groups[1].Value + match.Groups[3].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime;
        string fraction = match.Groups[2].Value.TrimEnd('0');
        return seconds.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
            (fraction.Length == 0 ? "" : "." + fraction) + " +0000 UTC";
    }

    private JsonElement Call(string method, object body)
    {
        try { return RcloneOutput.Json(Run(["rc", "--loopback", method, "--json", JsonSerializer.Serialize(body, JsonFiles.Options)]), "comparison"); }
        catch (InvalidDataException error) { throw new RecoveryComparisonException("Remote comparison response is unavailable.", error); }
    }

    private string Run(IEnumerable<string> arguments)
    {
        try { return RunCore(arguments); }
        catch (RecoveryComparisonException) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new RecoveryComparisonException("Remote comparison helper is unavailable.", error); }
    }

    private string RunCore(IEnumerable<string> arguments)
    {
        Check();
        var args = arguments.Concat(new[] { "--ask-password=false", "--ftp-ask-password=false", "--sftp-ask-password=false",
            "--contimeout", "5s", "--timeout", "10s", "--retries", "1", "--low-level-retries", "1" }).ToList();
        if (config.RcloneConfigPath is not null) { args.Add("--config"); args.Add(config.RcloneConfigPath); }
        using var process = JobProcess.Start(RcloneArguments.ProcessInfo(config.RclonePath, args));
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (!process.WaitForExit(budget?.RemainingMilliseconds ?? 30000))
        {
            process.Kill(); if (!process.WaitForExit(10000)) throw new RecoveryComparisonException("Owned comparison helper did not stop.");
            Check();
            throw new RecoveryComparisonException("\u96f2\u306e\u5074\u306e\u7167\u5408\u304c\u6642\u9593\u5207\u308c\u3067\u3059\u3002\u672a\u9001\u4fe1\u3092\u4fdd\u5168\u3057\u3001\u9593\u3092\u7a7a\u3051\u3066\u3084\u308a\u76f4\u3057\u307e\u3059\u3002");
        }
        string json = output.GetAwaiter().GetResult(); error.GetAwaiter().GetResult();
        Check();
        if (process.ExitCode != 0)
            throw new RecoveryComparisonException($"\u96f2\u306e\u5074\u3092\u7167\u5408\u3067\u304d\u307e\u305b\u3093\uff08\u7d42\u4e86\u5024 {process.ExitCode}\uff09\u3002\u672a\u9001\u4fe1\u3092\u4fdd\u5168\u3057\u3001\u8d77\u52d5\u3057\u307e\u305b\u3093\u3002");
        return json;
    }
}
