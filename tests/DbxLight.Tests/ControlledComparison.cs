using System.Security.Cryptography;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

// A local metadata peer removes helper process launch latency from freshness/budget tests.
// The production parser, parent resolution, fingerprint comparison and launch guard still run.
internal sealed class ControlledComparison : IDisposable
{
    private readonly Unit5Fixture fixture;
    private readonly Func<IReadOnlyList<string>, string>? previous = RecoveryRemoteProbe.RunTestReader.Value;
    internal ControlledComparison(Unit5Fixture fixture)
    {
        this.fixture = fixture;
        RecoveryRemoteProbe.RunTestReader.Value = Read;
    }
    private string Read(IReadOnlyList<string> args)
    {
        if (args[0] == "version") return "rclone v1.74.3\n";
        string method = args[2];
        int bodyIndex = args.ToList().IndexOf("--json") + 1;
        using var body = JsonDocument.Parse(args[bodyIndex]);
        TestRunner.Equal(fixture.Config.Remote, body.RootElement.GetProperty("fs").GetString());
        if (method == "operations/fsinfo") return JsonSerializer.Serialize(new
        {
            Name = "local", Root = "//?/" + Path.GetFullPath(fixture.RemoteRoot).Replace('\\', '/'),
            Precision = 100, Hashes = new[] { "MD5" }, Features = new { CaseInsensitive = true }
        });
        string relative = body.RootElement.GetProperty("remote").GetString()!;
        string physical = Path.GetFullPath(Path.Combine(fixture.RemoteRoot, relative.Replace('/', '\\')));
        TestRunner.Assert(MountPaths.IsSameOrBelow(physical, fixture.RemoteRoot));
        TestTimeline.Mark(fixture.Root, "controlled-peer", new { method, relative });
        if (method == "operations/stat") return JsonSerializer.Serialize(new { item = Entry(relative, physical) });
        TestRunner.Equal("operations/list", method);
        var items = Directory.EnumerateFileSystemEntries(physical).Select(path =>
            Entry(Path.GetRelativePath(fixture.RemoteRoot, path).Replace('\\', '/'), path)).ToArray();
        return JsonSerializer.Serialize(new { list = items });
    }
    private static object? Entry(string relative, string physical)
    {
        bool directory = Directory.Exists(physical);
        if (!directory && !File.Exists(physical)) return null;
        var hashes = new Dictionary<string, string>();
        if (!directory) hashes["MD5"] = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(physical))).ToLowerInvariant();
        return new
        {
            Path = relative, Name = relative.Split('/').Last(), IsDir = directory,
            Size = directory ? -1L : new FileInfo(physical).Length,
            ModTime = new DateTimeOffset(File.GetLastWriteTimeUtc(physical)), Hashes = hashes
        };
    }
    public void Dispose() => RecoveryRemoteProbe.RunTestReader.Value = previous;
}
