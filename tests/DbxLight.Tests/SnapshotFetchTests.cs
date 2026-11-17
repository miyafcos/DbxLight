using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;
internal static partial class SnapshotTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("abc");
    private static string ContentHash(byte[] bytes) => SnapshotHash.ComputeAsync(new MemoryStream(bytes)).GetAwaiter().GetResult().ContentHash;
    private static void FetchTests(TestRunner runner)
    {
        runner.Test("snapshot: fetch rev/id/hash and verified reuse", FetchReuse);
        foreach (string issue in new[] { "id", "rev", "size", "content_hash", "body_size", "body_hash", "downloadable" })
        {
            string fault = issue; runner.Test("snapshot: fetch refuses " + fault, () => FetchMismatch(fault));
        }
        runner.Test("snapshot: fetch cancellation leaves partial body only", FetchCancel);
        runner.Test("snapshot: parallel fetch accounts for every plan row", ParallelFetch);
        runner.Test("snapshot: content_hash empty and 4 MiB boundaries", Hashes);
    }
    private static SnapshotRequest FetchRequest(Fixture f, int count = 1)
    {
        f.Write("plan", string.Concat(Enumerable.Range(0, count).Select(i => JsonSerializer.Serialize(new SnapshotPlan(
            "id:sample" + (count > 1 ? i : ""), "a12345678", "sample/a" + i + ".md", 3, ContentHash(Content)), SnapshotJson.Options) + "\n")));
        return new("fetch", Plan: f.PathFor("plan"), Store: f.PathFor("store"), MaxRps: 100, Parallel: 4, DeadlineSeconds: 5);
    }
    private static HttpResponseMessage Download(byte[] body, string id = "id:sample", string rev = "a12345678",
        long size = 3, string? hash = null, bool downloadable = true, HttpContent? content = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = content ?? new ByteArrayContent(body) };
        r.Headers.Add("Dropbox-API-Result", JsonSerializer.Serialize(new { id, rev, size, content_hash = hash ?? ContentHash(Content), is_downloadable = downloadable }));
        return r;
    }
    private static void FetchReuse()
    {
        var f = new Fixture(); var r = FetchRequest(f); int downloads = 0;
        var first = f.Run(r, request =>
        {
            TestRunner.Equal("content.dropboxapi.com", request.RequestUri!.Host);
            using var arg = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            TestRunner.Equal("rev:a12345678", arg.RootElement.GetProperty("path").GetString()); downloads++; return Download(Content);
        });
        TestRunner.Equal(0, first.ExitCode); TestRunner.Equal(1L, first.Fetched);
        var second = f.Run(r, _ => { downloads++; return Download(Content); });
        TestRunner.Equal(0, second.ExitCode); TestRunner.Equal(1L, second.Reused); TestRunner.Equal(1, downloads);
        using var row = JsonDocument.Parse(File.ReadAllText(second.ResultsFile!));
        TestRunner.Equal(Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant(), row.RootElement.GetProperty("sha256").GetString());
        File.WriteAllText(row.RootElement.GetProperty("localPath").GetString()!, "xyz");
        Failure(f.Run(r, _ => Download(Content)), 4, "content_hash_mismatch");
    }
    private static void FetchMismatch(string issue)
    {
        var f = new Fixture(); var r = FetchRequest(f);
        var result = f.Run(r, _ => Download(issue == "body_size" ? [1, 2] : issue == "body_hash" ? [1, 2, 3] : Content,
            id: issue == "id" ? "id:wrong" : "id:sample", rev: issue == "rev" ? "b12345678" : "a12345678",
            size: issue == "size" ? 4 : 3, hash: issue == "content_hash" ? new string('0', 64) : null, downloadable: issue != "downloadable"));
        Failure(result, 4, issue switch { "body_size" => "size_mismatch", "body_hash" => "content_hash_mismatch", "downloadable" => "not_downloadable", _ => issue + "_mismatch" });
        TestRunner.Assert(!Directory.EnumerateFiles(r.Store!, "*.blob", SearchOption.AllDirectories).Any());
        TestRunner.Assert(!Directory.EnumerateFiles(r.Store!, "fetch-results.*.jsonl").Any());
    }
    private sealed class InterruptedStream(CancellationTokenSource source) : MemoryStream(Content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { source.Cancel(); return base.ReadAsync(buffer, cancellationToken); }
    }
    private static void FetchCancel()
    {
        var f = new Fixture(); var r = FetchRequest(f); using var cancel = new CancellationTokenSource();
        Failure(f.Run(r, _ => Download(Content, content: new StreamContent(new InterruptedStream(cancel))), token: cancel.Token), 3, "cancelled");
        TestRunner.Assert(!Directory.EnumerateFiles(r.Store!, "*.blob", SearchOption.AllDirectories).Any());
        TestRunner.Assert(Directory.EnumerateFiles(r.Store!, "*.partial.*", SearchOption.AllDirectories).Any());
    }
    private static void ParallelFetch()
    {
        var f = new Fixture(); var r = FetchRequest(f, 9);
        // Distinct remote identities with the same content exercise concurrent object reuse.
        f.Write("plan", string.Concat(Enumerable.Range(0, 9).Select(i => JsonSerializer.Serialize(new SnapshotPlan(
            "id:sample" + i, (0xa12345678L + i).ToString("x"), "sample/a" + i, 3, ContentHash(Content)), SnapshotJson.Options) + "\n")));
        int arrivals = 0;
        var together = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = f.Run(r, async (request, ct) =>
        {
            using var arg = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            string rev = arg.RootElement.GetProperty("path").GetString()![4..];
            int i = (int)(Convert.ToInt64(rev, 16) - 0xa12345678L);
            if (Interlocked.Increment(ref arrivals) == 4) together.TrySetResult();
            await together.Task.WaitAsync(ct); return Download(Content, "id:sample" + i, rev);
        });
        TestRunner.Assert(result.ExitCode == 0, "Parallel fetch failed: " + result.Code); TestRunner.Equal(9L, result.Verified);
        TestRunner.Equal(9, File.ReadAllLines(result.ResultsFile!).Length);
    }
    private static void Hashes()
    {
        foreach (int size in new[] { 0, 1, SnapshotHash.BlockSize - 1, SnapshotHash.BlockSize, SnapshotHash.BlockSize + 1, SnapshotHash.BlockSize * 2 + 23 })
        {
            byte[] data = new byte[size]; new Random(931).NextBytes(data);
            using var hashes = new MemoryStream();
            for (int offset = 0; offset < size; offset += SnapshotHash.BlockSize)
                hashes.Write(SHA256.HashData(data.AsSpan(offset, Math.Min(SnapshotHash.BlockSize, size - offset))));
            string expected = Convert.ToHexString(SHA256.HashData(hashes.ToArray())).ToLowerInvariant();
            var actual = SnapshotHash.ComputeAsync(new MemoryStream(data)).GetAwaiter().GetResult();
            TestRunner.Equal(expected, actual.ContentHash); TestRunner.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), actual.Sha256);
        }
    }
}
