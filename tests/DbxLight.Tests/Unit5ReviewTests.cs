using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void StoppedWithoutRemote()
    {
        using var f = new Unit5Fixture("stopped-unconfigured");
        f.Store.Save(f.Config with { Remote = "", TestOverrides = null });
        var wait = f.Cli("wait", "file.txt", "--json"); TestRunner.Equal(2, wait.Exit); AssertFailure(wait.Output, "not_configured");
        var refresh = f.Cli("refresh", "--json"); TestRunner.Equal(2, refresh.Exit); AssertFailure(refresh.Output, "not_configured");
        Console.WriteLine("Review updated by A2: unconfigured wait/refresh both return 2 / not_configured.");
    }
    private static void UnidentifiedDirtyCache()
    {
        using var f = new Unit5Fixture("unknown-wait-all"); f.Start();
        Directory.CreateDirectory(Path.Combine(f.Config.CacheDir, "vfsMeta", "unidentified"));
        File.WriteAllText(Path.Combine(f.Config.CacheDir, "vfsMeta", "unidentified", "broken"), "{}");
        var result = f.Cli("wait", "--all", "--timeout", "0", "--json");
        TestRunner.Equal(3, result.Exit); AssertFailure(result.Output, "unknown");
        using var doc = JsonDocument.Parse(result.Output);
        TestRunner.Equal("unknown", doc.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
        Console.WriteLine("Review: empty upload queue plus unidentified cache is not confirmed uploaded.");
    }
    private static void MalformedStateJson()
    {
        using var f = new Unit5Fixture("malformed-state"); f.Start();
        RuntimeState original = f.State.Load()!;
        try
        {
            RuntimeSnapshots.Save(f.State, original with { ActiveConfig = null! });
            var agent = f.Cli("ls", "", "--json"); TestRunner.Equal(4, agent.Exit); AssertFailure(agent.Output, "failed");
            var old = f.Cli("status", "--json"); TestRunner.Equal(1, old.Exit); AssertFailure(old.Output, "failed");
        }
        finally { RuntimeSnapshots.Save(f.State, original); }
        Console.WriteLine("Review: malformed runtime returns one failure JSON object for new and existing commands.");
    }
}
