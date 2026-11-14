using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6gTests
{
    private static void RunPagination(Action<string, string, Action> test) =>
        test("J6", "9999, 10000 and 10001 preserved pairs report exact truncation at --max 10000", RescueBoundary);

    private static void RescueBoundary()
    {
        using var f = new Unit5Fixture("u6g-rescue-boundary");
        var records = new List<RescueRecord>();
        for (int total = 9999; total <= 10001; total++)
        {
            while (records.Count < total)
            {
                string name = "item-" + records.Count + ".txt";
                string data = Path.Combine(f.Config.CacheDir, "vfs", "sample", name), meta = Path.Combine(f.Config.CacheDir, "vfsMeta", "sample", name);
                Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
                File.WriteAllText(data, "x"); File.WriteAllText(meta, "{}");
                records.Add(RescueStore.Rescue(f.Config, new CacheEntry("sample/" + name, data, meta, true, true, false, null, null, null, null, false, "sample"), name, "boundary sample"));
            }
            var machine = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(
                ["--config", f.Store.PathName, "--state", f.State.PathName, "rescue", "list", "--max", "10000", "--json"]));
            TestRunner.Equal(0, machine.Exit);
            using var json = JsonDocument.Parse(machine.Output);
            var result = json.RootElement.GetProperty("result");
            TestRunner.Equal(Math.Min(total, 10000), result.GetProperty("items").GetArrayLength());
            TestRunner.Equal(total > 10000, result.GetProperty("truncated").GetBoolean());
            var text = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(
                ["--config", f.Store.PathName, "--state", f.State.PathName, "rescue", "list", "--max", "10000"]));
            TestRunner.Equal(0, text.Exit);
            TestRunner.Equal(total > 10000, text.Output.Contains("rescue list --all", StringComparison.Ordinal));
            Console.WriteLine("J6 records=" + total + "; returned=" + Math.Min(total, 10000) + "; truncated=" + (total > 10000) + "; text continuation=" + (total > 10000));
        }
        var all = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(
            ["--config", f.Store.PathName, "--state", f.State.PathName, "rescue", "list", "--all", "--json"]));
        using var full = JsonDocument.Parse(all.Output);
        TestRunner.Equal(10001, full.RootElement.GetProperty("result").GetProperty("items").GetArrayLength());
        TestRunner.Assert(!full.RootElement.GetProperty("result").GetProperty("truncated").GetBoolean());
        var invalid = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(
            ["--config", f.Store.PathName, "--state", f.State.PathName, "rescue", "list", "--max", "10001", "--json"]));
        TestRunner.Equal(1, invalid.Exit);
        foreach (var record in records)
            TestRunner.Assert(File.Exists(record.RecordPath) && File.Exists(record.DestinationPath) && File.Exists(record.MetadataDestinationPath));
        Console.WriteLine("J6 all=10001; public max=10000; every original and record retained.");
    }
}
