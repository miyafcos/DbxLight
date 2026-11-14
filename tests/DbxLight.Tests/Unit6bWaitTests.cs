using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void UploadReceipt()
    {
        using var f = new Unit5Fixture("u6b-receipt", writeBack: "1s"); f.Start();
        f.Write("saved.txt", "saved version");
        DateTimeOffset before = DateTimeOffset.UtcNow;
        using var metadata = new AgentMetadata(f.Controller);
        AgentWaitOutcome outcome = metadata.Wait(["saved.txt"], timeoutSeconds: 10);
        TestRunner.Equal(0, outcome.ExitCode);
        AgentUploadItem item = outcome.Result.Items.Single();
        AgentUploadVerification receipt = item.Verification ?? throw new InvalidOperationException("Missing verified receipt.");
        TestRunner.Assert(item.Verified && item.State == "uploaded");
        TestRunner.Equal(13L, receipt.LocalSize); TestRunner.Equal(receipt.LocalSize, receipt.CloudSize);
        TestRunner.Equal(receipt.LocalModifiedUtc, receipt.CloudModifiedUtc);
        TestRunner.Assert(receipt.VerifiedUtc >= before && receipt.VerifiedUtc <= DateTimeOffset.UtcNow);
        Console.WriteLine("Q1 receipt=" + JsonSerializer.Serialize(item, JsonFiles.Options));
    }

    private static void WaitResaveRaces()
    {
        foreach (string scenario in new[] { "during", "multi", "queue", "all" })
        {
            using var f = new Unit5Fixture("u6b-resave-" + scenario, writeBack: "5m");
            f.Seed("a.txt", "old a"); f.Seed("b.txt", "old b"); f.Start();
            bool saved = false;
            using var metadata = new AgentMetadata(f.Controller)
            {
                WaitCheckpoint = point =>
                {
                    string trigger = scenario == "during" ? "after-cloud:a.txt" :
                        scenario == "multi" ? "after-verified:b.txt" : "after-final-queue";
                    if (saved || point != trigger) return;
                    saved = true;
                    f.Write("a.txt", "a new and much larger saved version");
                }
            };
            AgentWaitOutcome result = scenario == "all" ? metadata.Wait([], all: true, timeoutSeconds: 0) :
                metadata.Wait(scenario == "multi" ? ["a.txt", "b.txt"] : ["a.txt"], timeoutSeconds: scenario == "multi" ? 1 : 0);
            TestRunner.Assert(saved, "Race boundary was not reached.");
            TestRunner.Equal(3, result.ExitCode);
            TestRunner.Assert(result.Result.Items.All(i => i.Path != "a.txt" || !i.Verified));
            TestRunner.Assert(result.Result.Items.All(i => !i.Verified || i.Verification is not null));
            Console.WriteLine("Q1 " + scenario + " resave: exit=3; modified target never uploaded; status queued=" + result.Result.Status!.UploadsQueued);
        }
    }

    private static void WaitResaveRetry()
    {
        using var f = new Unit5Fixture("u6b-resave-retry", writeBack: "1s");
        f.Seed("retry.txt", "old"); f.Start();
        int checkpoints = 0;
        using var metadata = new AgentMetadata(f.Controller)
        {
            WaitCheckpoint = point =>
            {
                if (point == "after-final-queue" && ++checkpoints == 1) f.Write("retry.txt", "new version");
            }
        };
        AgentWaitOutcome result = metadata.Wait(["retry.txt"], timeoutSeconds: 10);
        TestRunner.Equal(0, result.ExitCode);
        TestRunner.Assert(checkpoints >= 2);
        TestRunner.Equal(11L, result.Result.Items.Single().Verification!.CloudSize);
        TestRunner.Equal("new version", File.ReadAllText(Path.Combine(f.RemoteRoot, "retry.txt")));
        Console.WriteLine("Q1 resave forces another verification; final receipt identifies the new 11-byte version; checkpoints=" + checkpoints);
    }

    private static void WaitCacheUnknown()
    {
        using var f = new Unit5Fixture("u6b-wait-cache", writeBack: "1s"); f.Start();
        f.Write("cached.txt", "cached saved version");
        using var metadata = new AgentMetadata(f.Controller);
        TestRunner.Equal(0, metadata.Wait(["cached.txt"], timeoutSeconds: 10).ExitCode);
        string path = CacheNames.PathIn(f.State.Load()!.CacheMetadataRoot!, "cached.txt")!;
        void Unknown(string kind)
        {
            foreach (bool all in new[] { false, true })
            {
                AgentWaitOutcome result = metadata.Wait(all ? [] : ["cached.txt"], all, timeoutSeconds: 0);
                TestRunner.Equal(3, result.ExitCode);
                TestRunner.Assert(result.Result.Items.All(i => !i.Verified && i.State != "uploaded"));
                Console.WriteLine("Q1 " + kind + " " + (all ? "--all" : "explicit path") + ": exit=3 unknown.");
            }
        }
        using (var denied = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) Unknown("metadata cannot open");
        string original = Path.Combine(f.Root, "metadata.original");
        File.Copy(path, original);
        try
        {
            File.WriteAllText(path, "{\"Dirty\":false}", new UTF8Encoding(false));
            Unknown("incomplete metadata");
        }
        finally { File.Copy(original, path, overwrite: true); }
        string held = Path.Combine(f.Root, "metadata.held");
        File.Move(path, held);
        try { Unknown("body without metadata"); }
        finally { File.Move(held, path); }
        TestRunner.Equal(0, metadata.Wait(["cached.txt"], timeoutSeconds: 2).ExitCode);
    }
}
