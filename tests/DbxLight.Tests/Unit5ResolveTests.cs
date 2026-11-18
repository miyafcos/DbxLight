using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit5Tests
{
    private static void ResolvePaths()
    {
        using var f = new Unit5Fixture("resolve");
        string relative = "\u65e5\u672c\u8a9e \u7a7a\u767d/a#%&+.txt";
        string local = Path.Combine(f.MountRoot, relative.Replace('/', '\\'));
        using (var localOnly = new AgentMetadata(f.Controller)) TestRunner.Assert(localOnly.Resolve(relative).WebUrl is null);
        File.WriteAllText(f.Config.RcloneConfigPath!, "[sample]\ntype = dropbox\ntoken = {\"access_token\":\"SAMPLE_UNIT9_TOKEN\"}\n",
            new System.Text.UTF8Encoding(false));
        f.Store.Save(f.Config with { Remote = "sample:", TestOverrides = null, WriteAllowedRemotes = [] });
        string url = DbxLight.Tray.ShellPaths.WebUrl(local, f.Config.MountPoint);
        (int Exit, string Output, string Error) Resolve(string input) => Unit3cTests.Capture(() =>
            DbxLight.Cli.Program.RunCommand(["resolve", input, "--json"], f.Controller, Unit9Tests.FakePersonalWebClient));
        foreach (string input in new[] { relative, local, url })
        {
            var result = Resolve(input); TestRunner.Equal(0, result.Exit);
            using var doc = JsonDocument.Parse(result.Output);
            TestRunner.Equal(relative, doc.RootElement.GetProperty("remotePath").GetString());
            TestRunner.Equal(local, doc.RootElement.GetProperty("localPath").GetString());
            string? web = doc.RootElement.GetProperty("webUrl").GetString();
            TestRunner.Equal(url, web);
        }
        foreach (string input in new[] { Path.Combine(f.Root, "outside.txt"), "../escape", "https://example.org/home/file", "https://www.dropbox.com/home/a%2Fb", "https://www.dropbox.com/other/file", "https://www.dropbox.com/home/file?preview=other" })
        {
            var refused = Resolve(input); TestRunner.Equal(2, refused.Exit); AssertFailure(refused.Output, "usage");
        }
        TestRunner.Equal(0, Resolve("").Exit);
        Console.WriteLine("G6 local / relative / web round-trip and six invalid forms passed; fake Dropbox account; no live cloud connection.");
    }

}
