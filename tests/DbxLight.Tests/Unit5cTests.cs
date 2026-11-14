using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5cTests
{
    internal static void Run(TestRunner runner, string? only = null, bool includeMounts = true)
    {
        void Test(string group, string name, Action action, bool mount = false)
        {
            if ((only is null || only == group) && (!mount || includeMounts)) runner.Test("N" + group + ": " + name, action);
        }
        Test("1", "Windows names, long paths and trailing characters round-trip without downloads", MountNames, true);
        Test("1", "directory enumeration refuses reparse points including explicit ancestors", ReparsePoints);
        Test("1", "extended drive and UNC paths preserve long and trailing components", ExtendedPaths);
        Test("2", "unsafe components are skipped and counted without disclosing names", UnsafeEntries);
        Test("3", "measured cache spellings verify saved fullwidth names and retain unknown states", CacheSpellings, true);
        Test("3", "unmeasured and ambiguous cache spellings never imply absence", UnknownCacheSpellings);
        Test("4", "native DFS pagination, hidden folders, exclusions and depth flags remain stable", NativePagination, true);
        Test("4", "actual unsafe cloud names preserve other entries and one stderr line", RealUnsafeRemoteNames);
        Test("5", "four-reader prefetch keeps serial DFS and offset order on a mounted tree", ParallelOrder, true);
        Test("5", "scan and result limits stop bounded prefetch without leaving readers", ParallelLimits, true);
        Test("5", "unconsumed prefetch failures preserve page semantics", PrefetchFailures);
        Test("6", "guides describe exact Windows names and skipped entries within forty lines", NamesGuide);
    }

    private static void MountNames()
    {
        using var f = new Unit5Fixture("5c-names", cloudNames: true, hidden: ["secret"]);
        foreach (string name in Unit5cProbe.Names) { f.Seed(name); f.Seed("dir-" + name + "/child.txt"); }
        f.Seed("secret/private.txt");
        string deep = string.Join('/', Enumerable.Repeat("long folder 1234567890123456789", 11)) + "/deep-file.txt";
        f.Seed(deep);
        foreach (string relative in new[] { "ending.", "ending ", "folder./inside.txt", "folder /inside.txt" })
        {
            string target = MountMetadata.Extended(Path.Combine(f.RemoteRoot, relative.Replace('/', '\\')));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "trailing content", new UTF8Encoding(false));
        }
        f.Start();
        DbxStatus before = f.Controller.Status();
        var expected = WindowsPaths(f.MountRoot).Where(p => !p.StartsWith("secret", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var call = f.Cli("ls", "", "--recursive", "--json");
        TestRunner.Equal(0, call.Exit);
        using var doc = JsonDocument.Parse(call.Output);
        TestRunner.Equal("mount", doc.RootElement.GetProperty("source").GetString());
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        TestRunner.Assert(expected.SequenceEqual(entries.Select(e => e.GetProperty("path").GetString()).Order(StringComparer.Ordinal)));
        foreach (JsonElement entry in entries)
        {
            string path = entry.GetProperty("path").GetString()!;
            TestRunner.Equal(path.Split('/').Last(), entry.GetProperty("name").GetString());
            if (entry.GetProperty("type").GetString() == "file")
            {
                var stat = f.Cli("stat", path, "--json"); TestRunner.Equal(0, stat.Exit);
                using var detail = JsonDocument.Parse(stat.Output);
                TestRunner.Equal(path, detail.RootElement.GetProperty("entry").GetProperty("path").GetString());
                TestRunner.Equal(path.Split('/').Last(), detail.RootElement.GetProperty("entry").GetProperty("name").GetString());
            }
        }
        foreach (string term in new[] { ":", "?" })
        {
            var found = f.Cli("find", term, "--depth", "4", "--json"); TestRunner.Equal(0, found.Exit);
            using var result = JsonDocument.Parse(found.Output);
            TestRunner.Assert(result.RootElement.GetProperty("entries").EnumerateArray().Any(e =>
                e.GetProperty("name").GetString()!.Contains(term == ":" ? '\uff1a' : '\uff1f')));
        }
        var deepFind = f.Cli("find", "deep-file", "--depth", "20", "--json"); TestRunner.Equal(0, deepFind.Exit);
        using (var result = JsonDocument.Parse(deepFind.Output))
            TestRunner.Equal(deep, result.RootElement.GetProperty("entries")[0].GetProperty("path").GetString());
        DbxStatus after = f.Controller.Status();
        TestRunner.Equal(0L, before.CacheFiles); TestRunner.Equal(0L, before.CacheBytes);
        TestRunner.Equal(before.CacheFiles, after.CacheFiles); TestRunner.Equal(before.CacheBytes, after.CacheBytes);
        TestRunner.Equal(0L, after.DownloadActivity!.Files);
        Console.WriteLine($"N1 names={entries.Length}; ls/find/stat cache={before.CacheFiles}/{before.CacheBytes} -> {after.CacheFiles}/{after.CacheBytes}; downloads={after.DownloadActivity.Files}; long path length={deep.Length + 3}");
        // Only after the metadata/cache assertions, open every returned file path.
        foreach (JsonElement entry in entries.Where(e => e.GetProperty("type").GetString() == "file"))
        {
            string local = MountMetadata.Extended(Path.Combine(f.MountRoot, entry.GetProperty("path").GetString()!.Replace('/', '\\')));
            TestRunner.Assert(File.ReadAllText(local).Length > 0);
        }
        using var metadata = new AgentMetadata(f.Controller);
        TestRunner.Equal("rc-remote", metadata.List("", new(IncludeHidden: true)).Source);
        Console.WriteLine("N1 all returned file paths opened; fullwidth punctuation and same-character directories preserved.");
    }

    private static List<string> WindowsPaths(string root, string parent = "")
    {
        var result = new List<string>();
        foreach (FileSystemInfo info in new DirectoryInfo(MountMetadata.Extended(root)).EnumerateFileSystemInfos()
            .OrderBy(info => info.Name, StringComparer.Ordinal))
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string path = parent.Length == 0 ? info.Name : parent + "/" + info.Name;
            result.Add(path);
            if (info is DirectoryInfo) result.AddRange(WindowsPaths(info.FullName, path));
        }
        return result;
    }

    private static void ReparsePoints()
    {
        string root = Path.Combine(@"D:\dbxl-test", "run-u5c-links-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(root, "target"), view = Path.Combine(root, "view");
        Directory.CreateDirectory(outside); Directory.CreateDirectory(view);
        File.WriteAllText(Path.Combine(outside, "must-not-follow.txt"), "test");
        using Process process = Process.Start(RcloneArguments.ProcessInfo("cmd.exe", ["/d", "/c", "mklink", "/J", Path.Combine(view, "link"), outside]))!;
        string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); TestRunner.Equal(0, process.ExitCode);
        var mount = new MountMetadata(view);
        TestRunner.Equal(0, mount.Read("").Count);
        TestRunner.Throws<DirectoryNotFoundException>(() => mount.Read("link"));
        TestRunner.Throws<DirectoryNotFoundException>(() => mount.Read("link/subfolder"));
    }

    private static void ExtendedPaths()
    {
        TestRunner.Equal(@"\\?\T:\folder.\end ", MountMetadata.Extended(@"T:\folder.\end "));
        TestRunner.Equal(@"\\?\UNC\server\share\folder.\end ", MountMetadata.Extended(@"\\server\share\folder.\end "));
        TestRunner.Equal(@"\\?\T:\already", MountMetadata.Extended(@"\\?\T:\already"));
    }
}
