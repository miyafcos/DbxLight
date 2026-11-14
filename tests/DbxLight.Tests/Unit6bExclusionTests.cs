using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void ExclusionParity()
    {
        using var f = new Unit5Fixture("u6b-exclude");
        f.Seed("Secret/private.txt"); f.Seed("Visible/public.txt"); f.Seed("open.txt");
        f.Store.Save(f.Config with { ExcludePatterns = ["/secret/**"] });
        string[] Paths(AgentMetadata metadata, bool includeHidden = false) =>
            metadata.List("", new(Recursive: true, IncludeHidden: includeHidden)).Entries.Select(e => e.Path).ToArray();
        string[] stopped;
        using (var offline = new AgentMetadata(f.Controller))
        {
            stopped = Paths(offline);
            TestRunner.Equal(0, offline.Find("private").Entries.Count);
        }
        f.Start();
        using (var live = new AgentMetadata(f.Controller))
        {
            TestRunner.Assert(stopped.SequenceEqual(Paths(live)));
            TestRunner.Assert(stopped.SequenceEqual(Paths(live, includeHidden: true)));
            TestRunner.Equal(0, live.Find("private").Entries.Count);
        }
        f.Controller.Stop();
        using var after = new AgentMetadata(f.Controller);
        TestRunner.Assert(stopped.SequenceEqual(Paths(after)));
        Console.WriteLine("Q3 /secret/** excludes cloud Secret case-insensitively in ls/find: stopped == mounted == rc-remote == stopped again; visible=" + string.Join(',', stopped));
    }
}
