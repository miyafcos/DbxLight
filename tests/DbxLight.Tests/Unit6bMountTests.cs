using System.Diagnostics;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void MountReplacement()
    {
        foreach (string command in new[] { "ls", "ls-max", "ls-time", "find", "find-max", "find-time", "stat" })
        {
            using var f = new Unit5Fixture("u6b-replace-" + command);
            f.Seed("folder/old.txt"); f.Seed("one.txt"); f.Start();
            string other = Path.Combine(f.Root, "other");
            Directory.CreateDirectory(Path.Combine(other, "folder"));
            File.WriteAllText(Path.Combine(other, "folder", "new.txt"), "new mount");
            File.WriteAllText(Path.Combine(other, "one.txt"), "replacement");
            var native = new MountMetadata(f.Config.MountPoint);
            int swaps = 0;
            using var metadata = new AgentMetadata(f.Controller)
            {
                ChildrenReader = relative =>
                {
                    var result = native.Read(relative);
                    if (Interlocked.Increment(ref swaps) == 1)
                    {
                        f.Controller.Stop();
                        f.Store.Save(f.Config with { Remote = other, WriteAllowedRemotes = [other] });
                        f.Start();
                        if (command.EndsWith("-time", StringComparison.Ordinal)) Thread.Sleep(1100);
                    }
                    return result;
                }
            };
            try
            {
                if (command.StartsWith("ls", StringComparison.Ordinal))
                    _ = metadata.List("", new(Depth: 3, Max: command == "ls-max" ? 1 : 1000, TimeLimitSeconds: command == "ls-time" ? 1 : 45));
                else if (command.StartsWith("find", StringComparison.Ordinal))
                    _ = metadata.Find("o", depth: 3, max: command == "find-max" ? 1 : 1000, timeLimitSeconds: command == "find-time" ? 1 : 45);
                else _ = metadata.Stat("one.txt");
                throw new InvalidOperationException("Replaced mount returned usable results.");
            }
            catch (AgentCommandException e)
            {
                TestRunner.Equal(3, e.ExitCode); TestRunner.Equal("mount_changed", e.Code);
                Console.WriteLine("Q2 " + command + ": same drive replacement => exit=3 mount_changed; results discarded.");
            }
        }
    }

    private static void ForeignAgentState()
    {
        using var f = new Unit5Fixture("u6b-foreign-agent"); f.Start();
        RuntimeState original = f.State.Load()!;
        RuntimeSnapshots.Save(f.State, original with { ConfigPath = Path.Combine(f.Root, "another-config.json") });
        byte[] before = File.ReadAllBytes(f.State.PathName);
        try
        {
            foreach (string[] command in new[] { new[] { "ls", "" }, ["find", "file"], ["stat", "file.txt"],
                ["wait", "file.txt"], ["refresh"], ["resolve", "file.txt"], ["agent-guide"] })
            {
                var result = f.Cli(command.Concat(["--json"]).ToArray());
                TestRunner.Equal(3, result.Exit);
                using var json = System.Text.Json.JsonDocument.Parse(result.Output);
                TestRunner.Equal("state_mismatch", json.RootElement.GetProperty("code").GetString());
                TestRunner.Assert(before.AsSpan().SequenceEqual(File.ReadAllBytes(f.State.PathName)),
                    "A foreign runtime was observed or saved before rejection.");
            }
        }
        finally { RuntimeSnapshots.Save(f.State, original); }
        Console.WriteLine("Q2 all seven commands: foreign ConfigPath rejected before runtime side effects; state bytes unchanged.");
    }

    private static void EnumerationOrigins()
    {
        using var f = new Unit5Fixture("u6b-origins");
        string root = Path.Combine(f.Root, "mount"), other = Path.Combine(f.Root, "other");
        Directory.CreateDirectory(Path.Combine(root, "parent", "child"));
        Directory.CreateDirectory(Path.Combine(other, "child"));
        var native = new MountMetadata(root);
        _ = native.Read("parent/child"); // Populate the directory cache before the replacement.
        void MoveOwned(string source, string destination)
        {
            TestRunner.Assert(MountPaths.IsSameOrBelow(Path.GetFullPath(source), f.Root) &&
                MountPaths.IsSameOrBelow(Path.GetFullPath(destination), f.Root));
            Directory.Move(source, destination);
        }
        void Junction(string link, string target)
        {
            var info = RcloneArguments.ProcessInfo("cmd.exe", ["/c", "mklink", "/J", link, target]);
            using Process child = Process.Start(info)!;
            Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(10000)) { child.Kill(); child.WaitForExit(); throw new TimeoutException("Own junction helper timed out."); }
            TestRunner.Equal(0, child.ExitCode); _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
        }
        void Refused(string relative)
        {
            try { _ = native.Read(relative); throw new InvalidOperationException("Replacement junction was followed."); }
            catch (AgentCommandException e) { TestRunner.Equal(3, e.ExitCode); TestRunner.Equal("mount_changed", e.Code); }
        }
        MoveOwned(Path.Combine(root, "parent"), Path.Combine(root, "parent.original"));
        Junction(Path.Combine(root, "parent"), other);
        Refused("parent/child");
        MoveOwned(root, root + ".original");
        Junction(root, other);
        Refused("");
        Console.WriteLine("Q2 cached child ancestor and root replaced by junction: both rejected before enumeration; original directories retained.");
    }
}
