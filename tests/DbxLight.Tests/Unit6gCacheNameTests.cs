using System.Diagnostics;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6gTests
{
    private static void RunCacheNames(Action<string, string, Action> test)
    {
        test("J4", "real rclone standard names and inverse disk spellings are an independent oracle", RcloneNameOracle);
        foreach (bool parent in new[] { false, true })
            test("J4", "U+201B pending disk name is preserved with the explicit policy reason; parent=" + parent, () => QuotedPending(parent));
    }

    private static string LocalRclone(Unit5Fixture f, params string[] args)
    {
        var reply = LocalRcloneReply(f, args); TestRunner.Equal(0, reply.Exit); return reply.Output;
    }

    private static (int Exit, string Output) LocalRcloneReply(Unit5Fixture f, params string[] args)
    {
        using var child = JobProcess.Start(RcloneArguments.ProcessInfo(f.Config.RclonePath,
            args.Concat(new[] { "--config", f.Config.RcloneConfigPath!, "--ask-password=false" })));
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
        TestRunner.Assert(child.WaitForExit(30000), "Owned local name helper timed out.");
        errors.GetAwaiter().GetResult();
        return (child.ExitCode, output.GetAwaiter().GetResult());
    }

    private static void RcloneNameOracle()
    {
        using var f = new Unit5Fixture("u6g-name-oracle");
        TestRunner.Equal("rclone v1.74.3", LocalRclone(f, "version").Split('\n')[0].Trim());
        string[] names = ["\u65e5\u672c\u8a9e.txt", "colon\uff1a.txt", "question\uff1f.txt", "star\uff0a.txt",
            "pipe\uff5c.txt", "less\uff1c.txt", "greater\uff1e.txt", "back\uff3c.txt", "double\uff02.txt",
            "slash\uff0f.txt", "q\u201b.txt", "q\u201b\u201b.txt", "q\u201b\uff1f.txt", "tail\uff0e", "tail\u2420"];
        string source = Path.Combine(f.Root, "body.txt"); File.WriteAllText(source, "oracle body");
        var rows = new List<object>(); int index = 0, picked = 0;
        foreach (bool parent in new[] { false, true })
        foreach (string name in names)
        {
            string disk = parent ? name + "/body.txt" : name;
            string forward = Path.Combine(f.Root, "forward-" + index), inverse = Path.Combine(f.Root, "inverse-" + index++);
            string file = Path.Combine(forward, disk.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "oracle body");
            using var list = JsonDocument.Parse(LocalRclone(f, "lsjson", forward));
            string standard = list.RootElement.EnumerateArray().Single().GetProperty("Path").GetString()! + (parent ? "/body.txt" : "");
            bool accepted = RecoveryCacheNames.TryDecode(disk, out string decoded) && decoded == disk && !decoded.Contains('\\');
            if (accepted) { TestRunner.Equal(disk, standard); picked++; }
            if (disk.Contains('\u201b')) TestRunner.Assert(!accepted && RecoveryCacheNames.PreservationReason(disk) is not null);
            Directory.CreateDirectory(inverse);
            var inverseReply = LocalRcloneReply(f, "rc", "--loopback", "operations/copyfile", "--json", JsonSerializer.Serialize(new
            { srcFs = f.Root, srcRemote = "body.txt", dstFs = inverse, dstRemote = standard }));
            string? returned = inverseReply.Exit == 0 ? Path.GetRelativePath(inverse, Directory.GetFiles(inverse, "*", SearchOption.AllDirectories).Single()).Replace('\\', '/') : null;
            if (accepted) { TestRunner.Equal(0, inverseReply.Exit); TestRunner.Equal(disk, returned); }
            rows.Add(new { disk, parent, accepted, standard, inverse = returned, inverseExit = inverseReply.Exit, inverseMatches = returned == disk });
        }
        string evidence = Path.Combine(f.Root, "J4-names.json");
        JsonFiles.Write(evidence, new { version = "v1.74.3", artifacts = f.Root, count = rows.Count, accepted = picked, rows });
        Console.WriteLine("J4 rclone oracle: " + rows.Count + " fixed disk names; accepted=" + picked + "; accepted forward and inverse identities match; every excluded spelling is recorded.");
    }

    private static void QuotedPending(bool parent)
    {
        using var f = new Unit5Fixture("u6g-quoted-pending", writeBack: "5m");
        f.Start(); f.Write("template.txt", "quoted pending body");
        Unit5Fixture.Wait(() => PendingCacheInspector.Observe(f.Config.CacheDir).Entries.Any(e => e.Dirty == true && e.CompleteData), "Pending quoted pair missing.");
        f.Controller.Stop(true);
        var state = f.State.Load()!;
        var entry = PendingCacheInspector.InspectStopped(f.Config.CacheDir).Entries.Single(e => e.Dirty == true);
        string disk = parent ? "q\u201b\u201b/body.txt" : "q\u201b\u201b.txt";
        string data = Path.Combine(state.CacheDataRoot!, disk.Replace('/', '\\')), meta = Path.Combine(state.CacheMetadataRoot!, disk.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.Move(entry.DataPath, data); File.Move(entry.MetadataPath, meta);
        f.Start();
        var record = f.Controller.RescueList().Single();
        TestRunner.Equal(RecoveryCacheNames.PreservationReason(disk), record.Reason);
        TestRunner.Assert(record.MoveCompleted);
        TestRunner.Equal("quoted pending body", File.ReadAllText(record.DestinationPath));
        TestRunner.Equal(0, Directory.GetFiles(f.RemoteRoot, "*", SearchOption.AllDirectories).Length);
    }
}
