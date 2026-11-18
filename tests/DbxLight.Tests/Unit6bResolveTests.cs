using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void WebMappings()
    {
        using var f = new Unit5Fixture("u6b-resolve");
        void Configure(string backend)
        {
            File.WriteAllText(f.Config.RcloneConfigPath!, "[sample]\ntype = " + backend +
                "\ntoken = {\"access_token\":\"SAMPLE_UNIT9_TOKEN\"}\n", new UTF8Encoding(false));
            f.Store.Save(f.Config with { Remote = "sample:Dept/共有", TestOverrides = null, WriteAllowedRemotes = [] });
        }
        AgentMetadata Metadata() => new(f.Controller, requireRemote: true, Unit9Tests.FakePersonalWebClient);
        Configure("dropbox");
        using (var metadata = Metadata())
        {
            foreach (string relative in new[] { "", "原稿 a#%&+.txt", "folder./file.", "space /name " })
            {
                AgentResolution forward = metadata.Resolve(relative);
                TestRunner.Assert(forward.WebUrl!.StartsWith("https://www.dropbox.com/home/Dept/", StringComparison.Ordinal));
                AgentResolution reverse = metadata.Resolve(forward.WebUrl);
                TestRunner.Equal(relative, reverse.RemotePath);
                TestRunner.Equal(forward.LocalPath, reverse.LocalPath);
                bool extended = relative.Split('/').Any(p => p.EndsWith('.') || p.EndsWith(' '));
                TestRunner.Equal(extended, reverse.RequiresExtendedPath);
                TestRunner.Equal(extended, reverse.LocalPath.StartsWith(@"\\?\", StringComparison.Ordinal));
            }
            TestRunner.Throws<ArgumentException>(() => metadata.Resolve("https://www.dropbox.com/home/Outside/file.txt"));
        }
        Configure("local");
        using (var other = Metadata())
        {
            TestRunner.Assert(other.Resolve("file.txt").WebUrl is null);
            try { _ = other.Resolve("https://www.dropbox.com/home/Dept/file.txt"); throw new Exception("Expected mapping refusal."); }
            catch (AgentCommandException e) { TestRunner.Equal(3, e.ExitCode); TestRunner.Equal("web_mapping_unknown", e.Code); }
        }
        File.WriteAllText(f.Config.RcloneConfigPath!, "", new UTF8Encoding(false));
        using (var unknown = Metadata()) TestRunner.Assert(unknown.Resolve("file.txt").WebUrl is null);
        f.Store.Save(f.Store.Load() with { RcloneConfigPath = null });
        using (var unspecified = Metadata()) TestRunner.Assert(unspecified.Resolve("file.txt").WebUrl is null);
        Console.WriteLine("Q4 Dropbox mapping confirmed by a fake account reply; Dept/subfolder round-trips; trailing dot/space uses exact extended path; non-Dropbox/missing type/default config mapping=null; no cloud request.");
    }
}
