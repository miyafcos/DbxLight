using System.Diagnostics;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static class Unit5cProbe
{
    internal static readonly string[] Names =
    [
        "\u8b70\u4e8b\u9332\uff1a\u7b2c1\u56de.txt", "A\uff0fB \u6bd4\u8f03.txt", "\u3053\u308c\u3067\u3088\u3044\uff1f.txt",
        "\u6ce8\uff0a\u5370.txt", "\u8868\uff5c\u88cf.txt", "\uff1c\u91cd\u8981\uff1e.txt", "\u5186\uff3c\u8a18\u53f7.txt",
        "\u5f15\u7528\uff02\u7b26.txt", "\u3075\u3064\u3046\u306e\u540d\u524d\uff08\u5168\u89d2\u62ec\u5f27\uff09.txt"
    ];

    internal static void Run()
    {
        using var f = new Unit5Fixture("5c-probe", cloudNames: true);
        foreach (string name in Names) { f.Seed(name); f.Seed("dir-" + name + "/child.txt"); }
        for (int i = 0; i < 170; i++) f.Seed($"bench/d{i:D3}/file.txt");
        f.Start();
        using var rc = new RcClient(f.State.Load()!);
        DbxStatus before = f.Controller.Status();
        var cold = Stopwatch.StartNew();
        var seen = Enumerate(f.MountRoot, recursive: true);
        double coldMs = cold.Elapsed.TotalMilliseconds;
        DbxStatus nativeAfter = f.Controller.Status();
        Console.WriteLine("native sample names=" + JsonSerializer.Serialize(new DirectoryInfo(Extended(f.MountRoot))
            .EnumerateFileSystemInfos().Select(e => e.Name), JsonFiles.Options));
        Console.WriteLine("native cold=" + coldMs + "ms; entries=" + seen.Count);
        PrintStatus("before", before); PrintStatus("after native enumeration", nativeAfter);
        TestRunner.Equal(0, nativeAfter.CacheFiles); TestRunner.Equal(0L, nativeAfter.CacheBytes);
        TestRunner.Equal(0, nativeAfter.DownloadActivity!.Files);
        foreach (string command in new[] { "ls", "find" })
        {
            var call = command == "ls" ? f.Cli("ls", "", "--json") : f.Cli("find", "txt", "--depth", "3", "--json");
            Console.WriteLine("BASELINE " + command + " exit=" + call.Exit + " stdout=" + call.Output + " stderr=" + call.Error);
            TestRunner.Equal(4, call.Exit);
        }
        var timings = new Dictionary<string, double[]>();
        foreach (bool native in new[] { true, false })
        {
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                if (native) _ = Enumerate(Path.Combine(f.MountRoot, "bench"), recursive: true);
                else
                {
                    foreach (string relative in new[] { "bench" }.Concat(Enumerable.Range(0, 170).Select(n => $"bench/d{n:D3}")))
                        _ = rc.Call("operations/list", new { fs = f.MountRoot, remote = relative,
                            opt = new { recurse = false, noMimeType = true, showHash = false } });
                }
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            timings[native ? "native" : "unit5-rc-local"] = times.ToArray();
        }
        Console.WriteLine("timingsMs=" + JsonSerializer.Serialize(timings));
        PrintStatus("after both five-run measurements", f.Controller.Status());
        foreach (string name in Names) { f.Write(name, "saved " + name); f.Write("dir-" + name + "/child.txt", "nested " + name); }
        // Additional encoder sentinel characters: actual disk measurements decide support.
        foreach (string name in new[] { "quote\u201b.txt", "symbol\u2401.txt", "tilde\uff5e.txt", "space\u2420.txt", "dot\uff0e.txt" }) f.Write(name, "sentinel");
        JsonElement cache = rc.Call("vfs/stats").GetProperty("diskCache");
        string data = cache.GetProperty("path").GetString()!, meta = cache.GetProperty("pathMeta").GetString()!;
        foreach (var pair in new[] { (Kind: "vfs", Root: data), (Kind: "vfsMeta", Root: meta) })
        {
            string[] paths = Directory.GetFiles(pair.Root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(pair.Root, p)).Order(StringComparer.Ordinal).ToArray();
            Console.WriteLine(pair.Kind + " names=" + JsonSerializer.Serialize(paths, JsonFiles.Options));
        }
        Unit5Fixture.Wait(() => f.Controller.Status() is { UploadsQueued: 0, UploadsInProgress: 0 }, "Probe uploads not drained.", 25);
        Console.WriteLine("probe native cache stayed zero; baseline failed; cache names captured; owned mount will stop.");
    }

    private static List<object> Enumerate(string path, bool recursive)
    {
        var result = new List<object>();
        foreach (FileSystemInfo entry in new DirectoryInfo(Extended(path)).EnumerateFileSystemInfos("*",
            new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
            result.Add(new { entry.Name, Directory = directory, Size = directory ? -1 : ((FileInfo)entry).Length,
                ModifiedUtc = entry.LastWriteTimeUtc });
            if (recursive && directory) result.AddRange(Enumerate(entry.FullName, true));
        }
        return result;
    }

    internal static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static void PrintStatus(string label, DbxStatus status) => Console.WriteLine(label + "=" +
        JsonSerializer.Serialize(new { status.CacheFiles, status.CacheBytes, status.DownloadActivity }, JsonFiles.Options));
}
