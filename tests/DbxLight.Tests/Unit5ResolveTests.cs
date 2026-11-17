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
        File.WriteAllText(f.Config.RcloneConfigPath!, "[sample]\ntype = dropbox\n", new System.Text.UTF8Encoding(false));
        f.Store.Save(f.Config with { Remote = "sample:", TestOverrides = null, WriteAllowedRemotes = [] });
        string url = DbxLight.Tray.ShellPaths.WebUrl(local, f.Config.MountPoint);
        foreach (string input in new[] { relative, local, url })
        {
            var result = f.Cli("resolve", input, "--json"); TestRunner.Equal(0, result.Exit);
            using var doc = JsonDocument.Parse(result.Output);
            TestRunner.Equal(relative, doc.RootElement.GetProperty("remotePath").GetString());
            TestRunner.Equal(local, doc.RootElement.GetProperty("localPath").GetString());
            string? web = doc.RootElement.GetProperty("webUrl").GetString();
            if (web is null) DiagnoseListRemotes(f);
            TestRunner.Equal(url, web);
        }
        foreach (string input in new[] { Path.Combine(f.Root, "outside.txt"), "../escape", "https://example.org/home/file", "https://www.dropbox.com/home/a%2Fb", "https://www.dropbox.com/other/file", "https://www.dropbox.com/home/file?preview=other" })
        {
            var refused = f.Cli("resolve", input, "--json"); TestRunner.Equal(2, refused.Exit); AssertFailure(refused.Output, "usage");
        }
        TestRunner.Equal(0, f.Cli("resolve", "", "--json").Exit);
        Console.WriteLine("G6 local / relative / web round-trip and six invalid forms passed; no mount or cloud connection required.");
    }

    private static void DiagnoseListRemotes(Unit5Fixture fixture)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            DbxConfig config = fixture.Store.Load();
            using var process = System.Diagnostics.Process.Start(RcloneArguments.ProcessInfo(config.RclonePath,
                ["listremotes", "--long", "--config", config.RcloneConfigPath!, "--ask-password=false"]))!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(5000);
            if (!exited) { process.Kill(); process.WaitForExit(10000); }
            string errors = error.GetAwaiter().GetResult(); _ = output.GetAwaiter().GetResult();
            Console.WriteLine("G6 listremotes diagnostic: exit=" + process.ExitCode + "; elapsedMs=" + timer.ElapsedMilliseconds
                + "; timedOut=" + !exited + "; stderrHead=" + errors[..Math.Min(240, errors.Length)].Replace("\r", " ").Replace("\n", " "));
        }
        catch (Exception error)
        { Console.WriteLine("G6 listremotes diagnostic: exception=" + error.GetType().Name + "; elapsedMs=" + timer.ElapsedMilliseconds); }
    }
}
