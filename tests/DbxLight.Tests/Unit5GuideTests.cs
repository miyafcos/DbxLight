using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void GuidePrivacy()
    {
        using var f = new Unit5Fixture("guide");
        string remote = Path.Combine(f.Config.Remote, "REMOTE_SENTINEL_8372");
        DbxConfig readOnly = f.Config with
        {
            Remote = remote, WriteAllowedRemotes = [], HiddenPaths = ["FOLDER_SENTINEL_9021"],
            CacheDir = Path.Combine(f.Root, "USER_SENTINEL_7413", "cache")
        };
        f.Store.Save(readOnly);
        var ro = f.Cli("agent-guide"); TestRunner.Equal(0, ro.Exit);
        TestRunner.Assert(ro.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 40);
        TestRunner.Assert(ro.Output.Contains("\u8aad\u307f\u53d6\u308a\u5c02\u7528"));
        TestRunner.Assert(ro.Output.Contains("\u524d\u304b\u3089"));
        f.Store.Save(readOnly with { WriteAllowedRemotes = [remote] });
        var rw = f.Cli("agent-guide", "--format", "json"); TestRunner.Equal(0, rw.Exit);
        using var doc = JsonDocument.Parse(rw.Output);
        TestRunner.Assert(!doc.RootElement.GetProperty("readOnly").GetBoolean());
        string markdown = doc.RootElement.GetProperty("markdown").GetString()!;
        TestRunner.Assert(markdown.Split('\n').Length <= 40);
        TestRunner.Assert(markdown.Contains("\u66f8\u304d\u8fbc\u307f\u53ef"));
        foreach (string secret in new[] { "REMOTE_SENTINEL_8372", "FOLDER_SENTINEL_9021", "USER_SENTINEL_7413", f.Root, Environment.UserName })
        {
            TestRunner.Assert(!ro.Output.Contains(secret, StringComparison.OrdinalIgnoreCase), "Read-only guide disclosed setting marker.");
            TestRunner.Assert(!rw.Output.Contains(secret, StringComparison.OrdinalIgnoreCase), "Writable JSON guide disclosed setting marker.");
        }
        TestRunner.Equal(2, f.Cli("agent-guide", "--format", "xml", "--json").Exit);
        Console.WriteLine("G7 read-only guide output:\n" + ro.Output);
        Console.WriteLine("G7 writable guide output:\n" + markdown);
    }
}
