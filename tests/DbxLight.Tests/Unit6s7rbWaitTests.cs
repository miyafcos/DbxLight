using System.Reflection;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6s7rTests
{
    internal static void RunReview2Wait(TestRunner runner, string? issue = null)
    {
        if (issue is null or "Q1")
        {
            foreach (bool all in new[] { false, true })
            foreach (double budget in new[] { 0d, 2d })
            {
                bool entire = all; double seconds = budget;
                runner.Test($"6s7rb Q1: exhausted status timeout keeps status_unknown; all={all}; timeout={budget}",
                    () => Review2StatusTimeout(entire, seconds, "first"));
            }
            foreach (string stage in new[] { "metadata-retry", "final" })
            foreach (bool all in new[] { false, true })
            {
                string point = stage; bool entire = all;
                runner.Test($"6s7rb Q1: exhausted {stage} status timeout keeps status_unknown; all={all}",
                    () => Review2StatusTimeout(entire, 2, point));
            }
        }
        if (issue is null or "Q7")
        {
            runner.Test("6s7rb Q7: hidden body-only entry returns hidden before any retry", Review2Hidden);
            runner.Test("6s7rb Q7: a mismatched round-trip retains the path and stays unknown", Review2MismatchedRoundTrip);
        }
        if (issue is null or "Q8")
        {
            runner.Test("6s7rb Q8: multiple normalized cache pairs are unknown", Review2AmbiguousPair);
            runner.Test("6s7rb Q8: NFC target cannot bypass a dirty NFD cache pair", Review2UnicodePair);
            runner.Test("6s7rb Q8: invalid cache comparison stays unknown inside Wait", Review2InvalidCachePath);
        }
    }

    private static void Review2StatusTimeout(bool all, double budget, string stage)
    {
        using var f = new TimeoutFixture();
        using var agent = f.Agent();
        if (stage == "metadata-retry") f.Plan = (command, count) => command == "vfs/stats" && count == 1;
        if (stage != "final") f.DelayUntaggedStats = true;
        f.OnCheckpoint = point => { if (point == "after-final-queue") f.DelayUntaggedStats = true; };
        f.OnRetry = () =>
        {
            // Only logical time determines the wait budget; real status errors retain their original Status.
            f.Elapsed += 1;
            if (stage != "final") f.DelayUntaggedStats = true;
        };
        AgentWaitOutcome result = agent.Wait(all ? [] : ["saved.txt"], all, budget);
        Console.WriteLine("Q1 outcome=" + JsonSerializer.Serialize(result, JsonFiles.Options));
        TestRunner.Equal(3, result.ExitCode);
        TestRunner.Equal("status_unknown", result.Result.Code);
        TestRunner.Equal("The running mount cannot be verified.", result.Result.Message);
        TestRunner.Assert(ReferenceEquals(agent.Status, result.Result.Status));
        TestRunner.Assert(result.Result.Status is { Running: true, Ready: false, StatusError: not null });
        TestRunner.Assert(result.Result.Status!.StatusError!.StartsWith("The request was canceled due to the configured HttpClient.Timeout of ", StringComparison.Ordinal));
        TestRunner.Equal(budget == 0 ? 1 : 2, f.Retries);
        TestRunner.Assert(result.Result.Items.All(i => !i.Verified) && result.Result.RemnantCount == 0);
        if (stage == "metadata-retry") TestRunner.Equal(1, f.Count("vfs/stats"));
    }

    private static void Review2Hidden()
    {
        using var f = new WaitFixture();
        f.Body("secret/old.tmp");
        var config = f.Config with { HiddenPaths = ["secret"] };
        f.Rc.Config.Save(config);
        RuntimeSnapshots.Save(f.Rc.State, f.Rc.State.Load()! with { ActiveConfig = config });
        using var agent = new AgentMetadata(f.Rc.Controller)
        {
            ChildrenReader = _ => [],
            WaitElapsedTestOverride = () => 0,
            WaitDelayTestOverride = _ => throw new InvalidOperationException("Hidden entry must not wait for another round.")
        };
        AgentWaitOutcome result = agent.Wait([], all: true, timeoutSeconds: 120);
        Console.WriteLine("Q7 outcome=" + JsonSerializer.Serialize(result, JsonFiles.Options));
        TestRunner.Equal(3, result.ExitCode); TestRunner.Equal("hidden", result.Result.Code);
        TestRunner.Equal("This folder is hidden. Tell a person if it is needed.", result.Result.Message);
        TestRunner.Equal(0, result.Result.RemnantCount);
    }

    private static void Review2MismatchedRoundTrip()
    {
        using var f = new WaitFixture();
        f.Body("cafe\u0301.tmp");
        using var agent = f.Agent();
        AgentWaitOutcome result = agent.Wait([], all: true, timeoutSeconds: 0);
        Unknown(result);
        TestRunner.Equal("caf\u00e9.tmp", result.Result.Items.Single().Path);
        TestRunner.Assert(!result.Result.Items.Single().Verified);
        Console.WriteLine("Q7 mismatched round-trip: original target retained; unknown; remnantCount=0.");
    }

    private static void Review2AmbiguousPair()
    {
        using var f = new WaitFixture();
        using var agent = f.Agent();
        string data = Path.Combine(f.Data, "named.txt"), meta = Path.Combine(f.Meta, "named.txt");
        var clean = new CacheEntry("named.txt", data, meta, true, true, true, false, 7, f.Modified, "", true, null);
        var dirty = clean with
        {
            DataPath = Path.Combine(f.Data, "child", "..", "named.txt"),
            MetadataPath = Path.Combine(f.Meta, "child", "..", "named.txt"), Dirty = true
        };
        var inspection = new CacheInspection(false, 1, "ambiguous") { Entries = [clean, dirty] };
        MethodInfo method = typeof(AgentMetadata).GetMethod("IsDirty", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? actual = method.Invoke(agent, [inspection, f.Meta, "named.txt"]);
        TestRunner.Assert(actual is null, "Multiple normalized cache pairs must be unknown; actual=" + actual);
        Console.WriteLine("Q8 normalized pairs=2; IsDirty=null.");
    }

    private static void Review2Pair(WaitFixture f, string cacheName, string localName)
    {
        f.Body(cacheName);
        File.WriteAllText(Path.Combine(f.Meta, cacheName), JsonSerializer.Serialize(new
        {
            ModTime = f.Modified, ATime = f.Modified, Size = 7,
            Rs = new[] { new { Pos = 0, Size = 7 } }, Fingerprint = "", Dirty = true
        }));
        f.Children = _ => [new(localName, false, 7, f.Modified)];
        f.Cloud = () => (200, JsonSerializer.Serialize(new { item = new { Name = localName, IsDir = false, Size = 7, ModTime = f.Modified } }));
    }

    private static void Review2UnicodePair()
    {
        using var f = new WaitFixture();
        const string nfc = "caf\u00e9.txt", nfd = "cafe\u0301.txt";
        Review2Pair(f, nfd, nfc);
        using var agent = f.Agent();
        AgentWaitOutcome result = agent.Wait([nfc], timeoutSeconds: 0);
        Console.WriteLine("Q8 NFC/NFD outcome=" + JsonSerializer.Serialize(result, JsonFiles.Options));
        Unknown(result); TestRunner.Assert(result.Result.Items.All(i => !i.Verified));
        object? dirty = typeof(AgentMetadata).GetMethod("IsDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [PendingCacheInspector.Observe(f.Config.CacheDir), f.Meta, nfc]);
        TestRunner.Assert(dirty is null, "A Unicode alias must be unknown even when a scan is complete.");
    }

    private static void Review2InvalidCachePath()
    {
        using var f = new WaitFixture(); Review2Pair(f, "named.txt", "named.txt");
        using var agent = f.Agent();
        // A synthetic corrupt comparison operand avoids creating an invalid filesystem path.
        typeof(AgentMetadata).GetField("cacheDataPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, f.Data + "\0");
        AgentWaitOutcome result = agent.Wait(["named.txt"], timeoutSeconds: 0);
        Console.WriteLine("Q8 invalid path outcome=" + JsonSerializer.Serialize(result, JsonFiles.Options));
        Unknown(result); TestRunner.Assert(result.Result.Items.All(i => !i.Verified));
    }
}
