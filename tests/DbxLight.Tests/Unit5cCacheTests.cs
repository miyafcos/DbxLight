using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5cTests
{
    private static void CacheSpellings()
    {
        using var f = new Unit5Fixture("5c-cache", cloudNames: true);
        foreach (string name in Unit5cProbe.Names) f.Seed("dir-" + name + "/child.txt");
        f.Start();
        using var rc = new RcClient(f.State.Load()!);
        string[] paths = Unit5cProbe.Names.Concat(Unit5cProbe.Names.Select(n => "dir-" + n + "/child.txt")).ToArray();
        foreach (string path in paths) f.Write(path, "saved " + path);
        foreach (JsonElement queued in rc.Call("vfs/queue").GetProperty("queue").EnumerateArray())
            rc.Call("vfs/queue-set-expiry", new { id = queued.GetProperty("id").GetInt64(), expiry = 60 });
        JsonElement disk = rc.Call("vfs/stats").GetProperty("diskCache");
        string data = disk.GetProperty("path").GetString()!, meta = disk.GetProperty("pathMeta").GetString()!;
        foreach (string root in new[] { data, meta })
        {
            var physical = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                TestRunner.Assert(CacheNames.TryEncode(path, out string encoded));
                TestRunner.Assert(physical.Contains(encoded), "Cache spelling not observed: " + encoded);
                TestRunner.Assert(CacheNames.TryDecode(encoded, out string decoded)); TestRunner.Equal(path, decoded);
            }
        }
        using (var metadata = new AgentMetadata(f.Controller))
            foreach (string path in paths) TestRunner.Equal(true, metadata.Stat(path).Entry.Cached);
        foreach (string path in Unit5cProbe.Names.Where(p => p.Contains('\uff1a') || p.Contains('\uff1f')))
        {
            var pending = f.Cli("wait", path, "--timeout", "0", "--json"); TestRunner.Equal(3, pending.Exit);
            using var state = JsonDocument.Parse(pending.Output);
            TestRunner.Assert(!state.RootElement.GetProperty("items")[0].GetProperty("verified").GetBoolean());
            using var dirty = JsonDocument.Parse(File.ReadAllText(CacheNames.PathIn(meta, path)!));
            TestRunner.Assert(dirty.RootElement.GetProperty("Dirty").GetBoolean());
            Console.WriteLine("N3 pending fullwidth name wait exit=" + pending.Exit + "; Dirty=true; verified=false");
        }
        foreach (JsonElement queued in rc.Call("vfs/queue").GetProperty("queue").EnumerateArray())
            rc.Call("vfs/queue-set-expiry", new { id = queued.GetProperty("id").GetInt64(), expiry = -1 });
        var uploaded = f.Cli(new[] { "wait" }.Concat(paths).Concat(["--timeout", "20", "--json"]).ToArray());
        TestRunner.Equal(0, uploaded.Exit);
        Console.WriteLine("N3 fullwidth files and directories verified wait exit=" + uploaded.Exit + "; count=" + paths.Length);
        Console.WriteLine("N3 cache spelling pairs=" + JsonSerializer.Serialize(paths.Select(p => new { name = p, cache = CacheNames.TryEncode(p, out string c) ? c : null }), JsonFiles.Options));
        TestRunner.Equal(0, f.Cli("wait", "--all", "--timeout", "0", "--json").Exit);
        foreach (string path in paths)
            TestRunner.Equal("saved " + path, File.ReadAllText(MountMetadata.Extended(Path.Combine(f.RemoteRoot, path.Replace('/', '\\')))));

        string unknown = "unverified\u2402.txt";
        f.Write(unknown, "unknown cache spelling");
        Unit5Fixture.Wait(() => f.Controller.Status() is { UploadsQueued: 0, UploadsInProgress: 0 }, "Unknown-name upload not drained.");
        using (var metadata = new AgentMetadata(f.Controller)) TestRunner.Equal(null, metadata.Stat(unknown).Entry.Cached);
        TestRunner.Equal(3, f.Cli("wait", unknown, "--timeout", "0", "--json").Exit);
        TestRunner.Equal(3, f.Cli("wait", "--all", "--timeout", "0", "--json").Exit);
        Console.WriteLine("N3 unmeasured symbol: cached=null; explicit wait=3; wait --all=3 even after real upload drained.");
    }

    private static void UnknownCacheSpellings()
    {
        foreach (string path in new[] { "a\u2402.txt", "a\uf03f.txt", "end.", "end ", "a\u201b\uff0f.txt", "../outside", "a/b:c", "a//b" })
            TestRunner.Assert(!CacheNames.TryEncode(path, out _), "Unmeasured spelling must be unknown.");
        foreach (string path in new[] { "a\uff1a.txt", "dir\uff1f/a\uff3c.txt", "A\uff0fB.txt", "quote\u201b.txt", "symbol\u2401.txt", "dot\uff0e.txt", "space\u2420.txt", "tilde\uff5e.txt" })
        {
            TestRunner.Assert(CacheNames.TryEncode(path, out string encoded));
            TestRunner.Assert(CacheNames.TryDecode(encoded, out string decoded)); TestRunner.Equal(path, decoded);
        }
        TestRunner.Assert(!CacheNames.TryDecode("unquoted\uff1a.txt", out _));
        TestRunner.Assert(!CacheNames.TryDecode("bad\u201bX.txt", out _));
    }
}
