using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void DirectoryMetadata()
    {
        using var f = new Unit5Fixture("u6b-directory");
        f.Seed("folder/file.txt", "data");
        using (var synthetic = new AgentMetadata(f.Controller)
        {
            ChildrenReader = _ => [new("unknown", true, -1, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                new("known", true, 0, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                new("zero.txt", false, 0, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero))]
        })
        {
            AgentEntry directory = synthetic.List("", new()).Entries.Single(e => e.Name == "unknown");
            TestRunner.Assert(directory.Size is null && directory.ModifiedUtc is null);
            TestRunner.Assert(synthetic.Stat("unknown").Entry == directory);
            TestRunner.Assert(synthetic.List("", new()).Entries.Single(e => e.Name == "known").ModifiedUtc is not null);
            TestRunner.Assert(synthetic.Stat("zero.txt").Entry.Size == 0 && synthetic.Stat("zero.txt").Entry.ModifiedUtc is not null);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(directory, JsonFiles.Options));
            TestRunner.Equal(JsonValueKind.Null, json.RootElement.GetProperty("size").ValueKind);
            TestRunner.Equal(JsonValueKind.Null, json.RootElement.GetProperty("modifiedUtc").ValueKind);
        }
        using (var stopped = new AgentMetadata(f.Controller))
            TestRunner.Assert(stopped.List("", new()).Entries.Single().Size is null && stopped.Stat("folder").Entry.Size is null);
        f.Start();
        using var live = new AgentMetadata(f.Controller);
        TestRunner.Assert(live.List("", new()).Entries.Single().Size is null && live.Stat("folder").Entry.Size is null);
        TestRunner.Assert(live.Stat("").Entry.Size is null && live.Stat("").Entry.ModifiedUtc is null);
        Console.WriteLine("A4 unknown directory: size=null modifiedUtc=null; known directory time and zero-byte file retained; stopped/live/root verified.");
    }
}
