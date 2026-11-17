using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

internal static class DropboxSnapshotReader
{
    // ConfigStore.Load also recovers credential transactions; snapshot must never run that writer.
    internal static DbxConfig ReadConfiguration(string? path)
    {
        string file = new ConfigStore(path).PathName;
        void CheckAuthentication()
        {
            if (File.Exists(CredentialSwitch.TransactionPath(file)) || File.Exists(CredentialSwitch.ReloginPath(file)))
                throw new SnapshotException(3, "auth_unavailable");
        }
        CheckAuthentication();
        if (!File.Exists(file)) throw new SnapshotException(2, "not_configured");
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            DbxConfig config = (JsonSerializer.Deserialize<DbxConfig>(stream, JsonFiles.Options) ??
                throw new SnapshotException(2, "invalid_config")).Validate();
            CheckAuthentication();
            if (string.IsNullOrWhiteSpace(config.Remote)) throw new SnapshotException(2, "not_configured");
            return config;
        }
        catch (SnapshotException) { throw; }
        catch { throw new SnapshotException(2, "invalid_config"); }
    }

    internal static async Task<SnapshotResult> RunAsync(DbxConfig config, SnapshotRequest request,
        CancellationToken cancellation = default, ISnapshotTokenBroker? broker = null, HttpMessageHandler? handler = null,
        Action<string>? progress = null, string? configPath = null, string? statePath = null, TimeSpan? httpTimeout = null)
    {
        var result = new SnapshotResult { Schema = "dbxl.snapshot." + request.Command + ".v1",
            Mode = request.Command == "list" ? request.CursorFile is null ? "full" : "delta" : request.Command };
        var watch = Stopwatch.StartNew();
        double seconds = double.IsFinite(request.DeadlineSeconds) && request.DeadlineSeconds is > 0 and <= 86400 ? request.DeadlineSeconds : 600;
        using var budget = new SnapshotBudget(seconds, cancellation);
        DropboxReadClient? client = null;
        SnapshotInput? input = null;
        try
        {
            Validate(request);
            var destination = new SnapshotDestination(config, configPath, statePath);
            string? folder = request.Command == "list" ? SnapshotJson.Relative(request.Folder ?? "") : null;
            SnapshotCursor? savedCursor = null;
            FetchOutputs? fetchOutputs = null;
            var outputPaths = new List<string>();
            if (request.Command == "list")
            {
                string entries = destination.NewFile(request.Entries!);
                string cursorOut = destination.NewFile(request.CursorOut!);
                outputPaths.AddRange([entries, cursorOut]);
                if (entries.Equals(cursorOut, StringComparison.OrdinalIgnoreCase)) throw new SnapshotException(2, "unsafe_destination");
                if (request.CursorFile is not null) savedCursor = ReadCursor(request.CursorFile);
            }
            if (request.Command == "metadata")
            {
                outputPaths.Add(destination.NewFile(request.Output!));
                input = new SnapshotInput(request.PathsFile!);
                foreach (string line in input.Lines()) { _ = SnapshotJson.InputPath(line); result.Planned++; }
            }
            if (request.Command == "fetch")
            {
                string store = destination.Check(request.Store!);
                string run = Guid.NewGuid().ToString("N");
                string resultsFile = destination.NewFile(Path.Combine(store, "fetch-results." + run + ".jsonl"));
                string failuresFile = destination.NewFile(request.Failures ?? Path.Combine(Path.GetDirectoryName(resultsFile)!, "fetch-failures." + run + ".jsonl"));
                if (resultsFile.Equals(failuresFile, StringComparison.OrdinalIgnoreCase)) throw new SnapshotException(2, "unsafe_destination");
                fetchOutputs = new(resultsFile, failuresFile);
                outputPaths.AddRange([store, resultsFile, failuresFile]);
                input = new SnapshotInput(request.Plan!);
                foreach (string line in input.Lines()) { _ = SnapshotJson.Plan(line); result.Planned++; }
            }
            // Keep the validated input handle open without write/delete sharing through all requests.
            ISnapshotTokenBroker selectedBroker = broker ?? new SnapshotTokenBroker(config);
            if (selectedBroker is SnapshotTokenBroker rcloneBroker)
            {
                destination.ProtectBrokerConfig(await rcloneBroker.ResolveConfigPathAsync(budget.Token));
                foreach (string path in outputPaths) destination.Check(path);
            }
            client = new DropboxReadClient(selectedBroker, budget, request.MaxRps, handler, httpTimeout);
            await client.InitializeAsync();
            result.BindingId = client.BindingId;
            switch (request.Command)
            {
                case "list": await List(client, request, result, destination, budget, folder!, savedCursor, progress); break;
                case "metadata": await Metadata(client, request, result, destination, budget, input!); break;
                case "fetch": await Fetch(client, request, result, destination, budget, input!, fetchOutputs!); break;
            }
            result.Ok = result.Complete = true; result.Code = "complete"; result.ExitCode = 0;
        }
        catch (SnapshotException e) { result.Code = e.Code; result.ExitCode = e.ExitCode; }
        catch (OperationCanceledException) { result.Code = budget.CancellationCode; result.ExitCode = 3; }
        catch (JsonException) { result.Code = "invalid_json"; result.ExitCode = 3; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Code = "io_error"; result.ExitCode = 4; }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        { result.Code = "usage"; result.ExitCode = 2; }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        { result.Code = "invalid_response"; result.ExitCode = 3; }
        catch (Exception e) when (e is not OutOfMemoryException) { result.Code = "internal_error"; result.ExitCode = 1; }
        finally
        {
            input?.Dispose();
            if (client is not null)
            {
                result.Requests = client.Requests; result.Throttled = client.Throttled; result.Retries = client.Retries; client.Dispose();
            }
            result.PartialFiles.RemoveAll(p => !File.Exists(p));
            result.ObservedThroughUtc = DateTimeOffset.UtcNow;
            result.ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 6);
        }
        return result;
    }

    private static void Validate(SnapshotRequest r)
    {
        if (r.Command is not ("list" or "metadata" or "fetch") ||
            !double.IsFinite(r.MaxRps) || r.MaxRps is < 0.01 or > 100 ||
            !double.IsFinite(r.DeadlineSeconds) || r.DeadlineSeconds is <= 0 or > 86400 ||
            r.Limit is < 1 or > 2000 || r.Parallel is < 1 or > 32 ||
            r.Command == "list" && (r.Entries is null || r.CursorOut is null) ||
            r.Command == "metadata" && (r.PathsFile is null || r.Output is null) ||
            r.Command == "fetch" && (r.Plan is null || r.Store is null)) throw new SnapshotException(2, "usage");
    }

    private static bool LowerPath(string? path) => path is not null && (path == "" ||
        path.StartsWith('/') && !path.EndsWith('/') && !path.Any(char.IsControl) &&
        path.Split('/').Skip(1).All(p => p is not ("" or "." or "..")));

    private static SnapshotCursor ReadCursor(string path)
    {
        try
        {
            if (!File.Exists(path)) throw new SnapshotException(2, "input_unavailable");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 1024 * 1024) throw new SnapshotException(2, "invalid_cursor");
            SnapshotCursor? cursor = JsonSerializer.Deserialize<SnapshotCursor>(stream, SnapshotJson.Options);
            if (cursor is null || cursor.Schema != "dbxl.snapshot.cursor.v1" || string.IsNullOrEmpty(cursor.Cursor) ||
                !SnapshotJson.ValidHash(cursor.BindingId) || cursor.Folder is null || cursor.CreatedUtc == default ||
                !LowerPath(cursor.RootPathLower) || cursor.Entries is null or < 0 || !SnapshotJson.ValidHash(cursor.EntriesSha256))
                throw new SnapshotException(2, "invalid_cursor");
            SnapshotJson.Relative(cursor.Folder);
            return cursor;
        }
        catch (SnapshotException) { throw; }
        catch { throw new SnapshotException(2, "invalid_cursor"); }
    }

    private static async Task List(DropboxReadClient client, SnapshotRequest r, SnapshotResult result,
        SnapshotDestination destination, SnapshotBudget budget, string folder, SnapshotCursor? saved, Action<string>? progress)
    {
        string? cursor = null;
        int rootDepth = SnapshotJson.ApiPath(client.Prefix, folder).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        result.RootPathLower = rootDepth == 0 ? "" : null;
        if (saved is not null)
        {
            if (saved.BindingId != client.BindingId || !saved.Folder.Equals(folder, StringComparison.Ordinal))
                throw new SnapshotException(3, "binding_changed");
            cursor = saved.Cursor; result.RootPathLower = saved.RootPathLower;
        }
        using var entries = new SnapshotStagedFile(destination, r.Entries!, result);
        using var cursorOutput = new SnapshotStagedFile(destination, r.CursorOut!, result);
        bool more;
        do
        {
            budget.Check();
            using var page = await client.ListAsync(folder, r.Limit, cursor);
            JsonElement root = page.RootElement;
            cursor = SnapshotJson.Text(root, "cursor"); more = SnapshotJson.Boolean(root, "has_more");
            if (!root.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new SnapshotException(3, "invalid_response");
            foreach (JsonElement item in rows.EnumerateArray())
            {
                budget.Check();
                SnapshotEntry entry = SnapshotJson.Entry(item, result.Entries + 1);
                if (result.RootPathLower is null)
                {
                    string[] parts = entry.PathLower.Split('/');
                    if (parts.Length <= rootDepth) throw new SnapshotException(3, "invalid_response");
                    result.RootPathLower = string.Join('/', parts.Take(rootDepth + 1));
                }
                await entries.Writer.WriteLineAsync(JsonSerializer.Serialize(entry, SnapshotJson.Options).AsMemory(), budget.Token);
                result.Entries++;
                switch (entry.Type) { case "file": result.Files++; break; case "folder": result.Folders++; break; case "deleted": result.Deleted++; break; }
            }
            result.Pages++;
            if (result.Pages % 10 == 0) progress?.Invoke("snapshot pages=" + result.Pages + " entries=" + result.Entries);
        } while (more);
        if (result.RootPathLower is null)
        {
            // An empty full listing has no path_lower to borrow. Ask for the folder itself.
            using var root = await client.MetadataAsync(folder);
            if (root is null) throw new SnapshotException(4, "not_found");
            if (SnapshotJson.Text(root.RootElement, ".tag") != "folder") throw new SnapshotException(3, "invalid_response");
            string lower = SnapshotJson.Text(root.RootElement, "path_lower");
            if (!LowerPath(lower)) throw new SnapshotException(3, "invalid_response");
            result.RootPathLower = lower;
        }
        budget.Check(); entries.Commit(); result.EntriesFile = entries.Final;
        string entriesHash;
        using (var file = File.OpenRead(entries.Final)) entriesHash = Convert.ToHexString(await SHA256.HashDataAsync(file, budget.Token)).ToLowerInvariant();
        await cursorOutput.Writer.WriteLineAsync(JsonSerializer.Serialize(new SnapshotCursor(
            "dbxl.snapshot.cursor.v1", client.BindingId, folder, cursor, DateTimeOffset.UtcNow,
            result.RootPathLower, result.Entries, entriesHash), SnapshotJson.Options).AsMemory(), budget.Token);
        budget.Check(); cursorOutput.Commit(); result.CursorFile = cursorOutput.Final;
    }

    private sealed class SnapshotInput : IDisposable
    {
        private readonly StreamReader reader;
        private readonly long start;
        internal SnapshotInput(string path)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> preamble = stackalloc byte[3];
                int read = stream.Read(preamble);
                start = read == 3 && preamble.SequenceEqual(Encoding.UTF8.Preamble) ? 3 : 0;
                stream.Position = start;
                reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            }
            catch { stream?.Dispose(); throw new SnapshotException(2, "input_unavailable"); }
        }
        internal IEnumerable<string> Lines()
        {
            reader.DiscardBufferedData(); reader.BaseStream.Position = start;
            while (true)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch { throw new SnapshotException(2, "invalid_input"); }
                if (line is null) yield break;
                if (line.Length == 0 || line.Length > 1024 * 1024) throw new SnapshotException(2, "invalid_input");
                yield return line;
            }
        }
        public void Dispose() => reader.Dispose();
    }

    private static async Task Metadata(DropboxReadClient client, SnapshotRequest r, SnapshotResult result,
        SnapshotDestination destination, SnapshotBudget budget, SnapshotInput input)
    {
        using var output = new SnapshotStagedFile(destination, r.Output!, result);
        foreach (string line in input.Lines())
        {
            budget.Check(); string path = SnapshotJson.InputPath(line);
            using var metadata = await client.MetadataAsync(path);
            object row;
            if (metadata is null) row = new { path, exists = false, type = (string?)null, id = (string?)null, observedUtc = DateTimeOffset.UtcNow };
            else
            {
                string type = SnapshotJson.Text(metadata.RootElement, ".tag");
                if (type is not ("file" or "folder")) throw new SnapshotException(3, "unknown_entry");
                row = new { path, exists = true, type, id = SnapshotJson.Text(metadata.RootElement, "id"), observedUtc = DateTimeOffset.UtcNow };
            }
            await output.Writer.WriteLineAsync(JsonSerializer.Serialize(row, SnapshotJson.Options).AsMemory(), budget.Token); result.Entries++;
        }
        budget.Check(); output.Commit(); result.ResultsFile = output.Final;
    }

    private sealed record FetchOutputs(string Results, string Failures);

    private static SnapshotException FetchFailure(Exception error, SnapshotBudget budget) => error switch
    {
        SnapshotException known => known,
        OperationCanceledException => new(3, budget.CancellationCode),
        IOException or UnauthorizedAccessException => new(4, "io_error"),
        JsonException or KeyNotFoundException or InvalidOperationException => new(3, "invalid_response"),
        _ => new(1, "internal_error")
    };

    private static async Task Fetch(DropboxReadClient client, SnapshotRequest r, SnapshotResult result,
        SnapshotDestination destination, SnapshotBudget budget, SnapshotInput input, FetchOutputs paths)
    {
        string store = destination.Check(r.Store!);
        string objects = destination.Check(Path.Combine(store, client.BindingId, "objects"));
        destination.Parent(Path.Combine(objects, "placeholder"));
        using var output = new SnapshotStagedFile(destination, paths.Results, result);
        SnapshotStagedFile? failures = null;
        var partials = new ConcurrentBag<string>();
        var faults = new ConcurrentBag<(long Line, SnapshotException Error)>();
        using var resultGate = new SemaphoreSlim(1);
        var active = new List<Task>();
        async Task One(SnapshotPlan plan, long planLine)
        {
            SnapshotFetchRow? row = null; SnapshotException? error = null;
            try { row = await FetchOne(client, plan, objects, destination, budget.Token, partials); }
            catch (Exception e) when (e is not OutOfMemoryException) { error = FetchFailure(e, budget); }
            await resultGate.WaitAsync();
            try
            {
                if (error is not null)
                {
                    faults.Add((planLine, error)); result.Failed++;
                    failures ??= new SnapshotStagedFile(destination, paths.Failures, result);
                    await failures.Writer.WriteLineAsync(JsonSerializer.Serialize(new SnapshotFetchFailure(
                        planLine, plan.Id, plan.Rev, error.Code, error.LocalPath), SnapshotJson.Options));
                }
                else
                {
                    await output.Writer.WriteLineAsync(JsonSerializer.Serialize(row!, SnapshotJson.Options));
                    result.Verified++; result.Entries++; result.Bytes += row!.Size;
                    if (row.Reused) result.Reused++; else result.Fetched++;
                }
            }
            finally { resultGate.Release(); }
        }
        Exception? failure = null;
        try
        {
            long planLine = 0;
            foreach (string line in input.Lines())
            {
                budget.Check(); active.Add(One(SnapshotJson.Plan(line), ++planLine));
                if (active.Count >= r.Parallel)
                {
                    Task finished = await Task.WhenAny(active); await finished; active.Remove(finished);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { failure = e; }
        try { await Task.WhenAll(active); }
        catch (Exception e) when (e is not OutOfMemoryException) { failure ??= e; }
        finally
        {
            foreach (string partial in partials) if (File.Exists(partial)) result.PartialFiles.Add(partial);
            if (failures is not null)
            {
                using (failures) { failures.Commit(); result.FailuresFile = failures.Final; }
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (!faults.IsEmpty) throw faults.OrderBy(f => f.Line).First().Error;
        budget.Check(); output.Commit(); result.ResultsFile = output.Final;
    }

    private static async Task<SnapshotFetchRow> FetchOne(DropboxReadClient client, SnapshotPlan plan, string objects,
        SnapshotDestination destination, CancellationToken token, ConcurrentBag<string> partials)
    {
        token.ThrowIfCancellationRequested();
        string final = destination.Check(Path.Combine(objects, plan.ContentHash + ".blob"));
        if (File.Exists(final))
        {
            try
            {
                using var existing = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read);
                SnapshotHashes hash = await SnapshotHash.ComputeAsync(existing, expectedSize: plan.Size, cancellation: token);
                if (hash.ContentHash != plan.ContentHash) throw new SnapshotException(4, "content_hash_mismatch");
                return new(plan.Id, plan.Rev, plan.CloudPath, plan.Size, plan.ContentHash, hash.Sha256, final, true);
            }
            catch (SnapshotException e) when (e.Code is "content_hash_mismatch" or "size_mismatch")
            { throw new SnapshotException(e.ExitCode, e.Code, final); }
        }
        return await client.DownloadAsync(plan.Rev, async (response, cancellation) =>
        {
            if (!response.Headers.TryGetValues("Dropbox-API-Result", out var values)) throw new SnapshotException(3, "invalid_response");
            string[] headers = values.ToArray();
            if (headers.Length != 1 || headers[0].Length > 1024 * 1024) throw new SnapshotException(3, "invalid_response");
            using var metadata = JsonDocument.Parse(headers[0]); JsonElement info = metadata.RootElement;
            if (SnapshotJson.Text(info, "id") != plan.Id) throw new SnapshotException(4, "id_mismatch");
            if (SnapshotJson.Text(info, "rev") != plan.Rev) throw new SnapshotException(4, "rev_mismatch");
            if (SnapshotJson.Size(info) != plan.Size) throw new SnapshotException(4, "size_mismatch");
            if (!SnapshotJson.Boolean(info, "is_downloadable")) throw new SnapshotException(4, "not_downloadable");
            if (SnapshotJson.Text(info, "content_hash") != plan.ContentHash) throw new SnapshotException(4, "content_hash_mismatch");
            // Each attempt gets a fresh partial. Interrupted attempts are never appended to.
            string partial = destination.NewFile(final + ".partial." + Guid.NewGuid().ToString("N")); partials.Add(partial);
            SnapshotHashes downloaded;
            using (Stream body = await client.BodyAsync(response, cancellation))
            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                downloaded = await SnapshotHash.ComputeAsync(body, file, plan.Size, cancellation);
                await file.FlushAsync(cancellation); file.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            if (downloaded.ContentHash != plan.ContentHash) throw new SnapshotException(4, "content_hash_mismatch");
            bool reused = false;
            try { destination.Promote(partial, final); }
            catch (Exception e) when ((e is IOException || e is SnapshotException { Code: "destination_exists" }) && File.Exists(final))
            {
                destination.Check(final);
                using var existing = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read);
                SnapshotHashes hash;
                try { hash = await SnapshotHash.ComputeAsync(existing, expectedSize: plan.Size, cancellation: cancellation); }
                catch (SnapshotException error) { throw new SnapshotException(error.ExitCode, error.Code, final); }
                if (hash != downloaded) throw new SnapshotException(4, "content_hash_mismatch", final);
                reused = true;
            }
            return new SnapshotFetchRow(plan.Id, plan.Rev, plan.CloudPath, plan.Size, plan.ContentHash, downloaded.Sha256, final, reused);
        }, token);
    }
}
