using DbxLight.Core;

namespace DbxLight.Tests;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Every child CLI inherits a closed loopback feed before any resident can start.
        var updatePort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        updatePort.Start(); int unusedUpdatePort = ((System.Net.IPEndPoint)updatePort.LocalEndpoint).Port; updatePort.Stop();
        Environment.SetEnvironmentVariable("DBXL_UPDATE_FEED", $"http://127.0.0.1:{unusedUpdatePort}/");
        if (args is ["--all"])
        {
            int Count(string name)
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(name);
                foreach (var process in processes) process.Dispose();
                return processes.Length;
            }
            Console.WriteLine("External process counts: DbxLight=" + Count("DbxLight") + "; dbxl=" + Count("dbxl"));
        }
        try { _ = TestPaths.WorkRoot; }
        catch (IOException error) { Console.Error.WriteLine(error.Message); return 1; }
        Environment.SetEnvironmentVariable("DBXL_RESCUE_DIR", Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "unit6a-rescue-default"));
        if (args.Length == 3 && args[0] == "--unit8-lock")
        {
            using var locked = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.None);
            Console.WriteLine("locked"); Console.Out.Flush();
            while (!File.Exists(args[2])) Thread.Sleep(25);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--unit8-child")
        {
            using var release = EventWaitHandle.OpenExisting(args[1]);
            Console.WriteLine("ready"); Console.Out.Flush(); return release.WaitOne(TimeSpan.FromSeconds(30)) ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0] == "--unit8")
        {
            var focused = new TestRunner(); Unit8Tests.Run(focused, args.Length == 2 ? args[1] : null);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 4 && args[0] == "--unit6d-key-child") return Unit6dTests.KeyChild(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--unit6d-process-child") return Unit6dTests.ProcessChild(args[1], args[2], args[3]);
        if (args.Length == 2 && args[0] == "--snapshot-review")
        {
            var focused = new TestRunner(); SnapshotTests.ReviewTests(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 1 && args[0] == "--snapshot")
        {
            var snapshot = new TestRunner(); SnapshotTests.Run(snapshot);
            Console.WriteLine($"RESULT: {(snapshot.Failures == 0 ? "PASS" : "FAIL")} ({snapshot.Passed}/{snapshot.Count})");
            return snapshot.Failures == 0 ? 0 : 1;
        }
        if (args.Length == 1 && args[0] == "--unit6f-exit") { Thread.Sleep(500); return 0; }
        if (args.Length == 2 && args[0] == "--unit6q-gate-child") return TestControlGate.Child(args[1]);
        if (args.Length is 1 or 2 && args[0] == "--unit6s7rc")
        {
            var focused = new TestRunner(); Unit6s7rTests.RunReview3(focused, args.Length == 2 ? args[1] : null);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0] == "--unit6s7rb")
        {
            var focused = new TestRunner(); Unit6s7rTests.RunReview2(focused, args.Length == 2 ? args[1] : null);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0] == "--unit6s7r")
        {
            var focused = new TestRunner(); Unit6s7rTests.Run(focused, args.Length == 2 ? args[1] : null);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0] == "--unit7")
        {
            var focused = new TestRunner(); Unit7Tests.Run(focused, args.Length == 2 ? args[1] : null);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length is 1 or 2 && args[0] == "--unit6s")
        {
            var focused = new TestRunner(); Unit6sTests.Run(focused, args.Length == 2 ? args[1] : null);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6q")
        {
            var focused = new TestRunner(); Unit6qTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6o")
        {
            var focused = new TestRunner(); Unit6oTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6n")
        {
            var focused = new TestRunner(); Unit6nTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6m")
        {
            var focused = new TestRunner(); Unit6iTests.RunUnit6m(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6l")
        {
            var focused = new TestRunner(); Unit6iTests.RunUnit6l(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6k")
        {
            var focused = new TestRunner(); Unit6iTests.RunUnit6k(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6j")
        {
            var focused = new TestRunner(); Unit6iTests.RunUnit6j(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6i")
        {
            var focused = new TestRunner(); Unit6iTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6h")
        {
            var focused = new TestRunner(); Unit6hTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6g")
        {
            var focused = new TestRunner(); Unit6gTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6f")
        {
            var focused = new TestRunner(); Unit6fTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6e")
        {
            var focused = new TestRunner(); Unit6eTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6d")
        {
            var focused = new TestRunner(); Unit6dTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6c")
        {
            var focused = new TestRunner(); Unit6cTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit6b")
        {
            var focused = new TestRunner(); Unit6bTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 3 && args[0] == "--unit6a-startup-child") return Unit6aTests.StartupChild(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--unit6a")
        {
            var focused = new TestRunner(); Unit6aTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit4")
        {
            var focused = new TestRunner(); Unit4Tests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit5d")
        {
            var focused = new TestRunner(); Unit5dTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 1 && args[0] is "--e2e" or "--unit5c-e2e")
        {
            var focused = new TestRunner(); EndToEnd.Run(focused);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit5c")
        {
            var focused = new TestRunner(); Unit5cTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 1 && args[0] == "--unit5c-probe") { Unit5cProbe.Run(); return 0; }
        if (args.Length == 2 && args[0] == "--unit5")
        {
            var focused = new TestRunner(); Unit5Tests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit3c")
        {
            var focused = new TestRunner(); Unit3cTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length >= 3 && args[0] == "--unit3b-argv") return Unit3bTests.ArgvChild(args[1], args[2..]);
        if (args.Length == 2 && args[0] == "--unit3b-gap") { Unit3bTests.MeasureGap(args[1]); return 0; }
        if (args.Length == 1 && args[0] == "--unit3b-cache-probe") { Unit3bTests.ProbeCache(); return 0; }
        if (args.Length == 2 && args[0] == "--unit3b")
        {
            var focused = new TestRunner(); Unit3bTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--unit3a-child") return Unit3aTests.Child(args[1]);
        if (args.Length == 2 && args[0] == "--unit3a")
        {
            var focused = new TestRunner(); Unit3aTests.Run(focused, args[1]);
            Console.WriteLine($"RESULT: {(focused.Failures == 0 ? "PASS" : "FAIL")} ({focused.Passed}/{focused.Count})");
            return focused.Failures == 0 && focused.Count > 0 ? 0 : 1;
        }
        if (args.Length == 5 && args[0] == "--pipe-host") return TrayTests.PipeHost(args);
        if (args.Length != 1 || args[0] is not ("--unit" or "--all"))
        {
            Console.Error.WriteLine("Usage: DbxLight.Tests --unit|--all|--e2e");
            return 2;
        }
        var runner = new TestRunner();
        UnitTests.Run(runner);
        SnapshotTests.Run(runner);
        Unit4Tests.Run(runner);
        TrayTests.Run(runner);
        Unit3aTests.Run(runner, includeMounts: args[0] == "--all");
        Unit3bTests.Run(runner, includeMounts: args[0] == "--all");
        Unit3cTests.Run(runner, includeMounts: args[0] == "--all");
        Unit5Tests.Run(runner, includeMounts: args[0] == "--all");
        Unit5cTests.Run(runner, includeMounts: args[0] == "--all");
        Unit5dTests.Run(runner, includeMounts: args[0] == "--all");
        Unit6aTests.Run(runner, includeMounts: args[0] == "--all");
        Unit6bTests.Run(runner, includeMounts: args[0] == "--all");
        Unit6cTests.Run(runner, includeMounts: args[0] == "--all");
        Unit6dTests.Run(runner);
        Unit6eTests.Run(runner);
        Unit6gTests.Run(runner);
        Unit6hTests.Run(runner);
        Unit6iTests.Run(runner);
        Unit6iTests.RunUnit6j(runner);
        Unit6iTests.RunUnit6k(runner);
        Unit6iTests.RunUnit6l(runner);
        Unit6iTests.RunUnit6m(runner);
        Unit6nTests.Run(runner);
        Unit6oTests.Run(runner);
        Unit6qTests.Run(runner);
        Unit6sTests.Run(runner);
        Unit7Tests.Run(runner);
        Unit8Tests.Run(runner);
        Unit6s7rTests.Run(runner);
        Unit6s7rTests.RunReview2(runner);
        Unit6s7rTests.RunReview3(runner);
        Unit6fTests.Run(runner);
        if (args[0] == "--all" && runner.Failures == 0) EndToEnd.Run(runner);
        Console.WriteLine($"RESULT: {(runner.Failures == 0 ? "PASS" : "FAIL")} ({runner.Passed}/{runner.Count})");
        return runner.Failures == 0 ? 0 : 1;
    }
}

internal sealed class TestRunner
{
    public int Count { get; private set; }
    public int Failures { get; private set; }
    public int Passed => Count - Failures;
    public bool Test(string name, Action action)
    {
        Count++;
        try { action(); Console.WriteLine("PASS " + name); return true; }
        catch (Exception e) { Failures++; Console.WriteLine("FAIL " + name + ": " + e); return false; }
    }
    public static void Assert(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public static void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}");
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}

internal static class UnitTests
{
    public static void Run(TestRunner runner)
    {
        (string Name, string[] Paths, string Expected)[] cases =
        [
            ("Japanese", ["\u898b\u672c\u30d5\u30a9\u30eb\u30c0"], "- /\u898b\u672c\u30d5\u30a9\u30eb\u30c0/**\n"),
            ("spaces", ["My PC"], "- /My PC/**\n"),
            ("parentheses", ["My PC (host)"], "- /My PC (host)/**\n"),
            ("corner brackets", ["\u3010\u30ca\u30d3\u30b9\u30bf\uff01\u3011"], "- /\u3010\u30ca\u30d3\u30b9\u30bf\uff01\u3011/**\n"),
            ("fullwidth", ["\uff08\u8cc7\u6599\uff09\uff1f\uff1a\uff0a\uff0f\uff3c"], "- /\uff08\u8cc7\u6599\uff09\uff1f\uff1a\uff0a\uff0f\uff3c/**\n"),
            ("star", ["a*b"], "- /a\\*b/**\n"),
            ("question", ["a?b"], "- /a\\?b/**\n"),
            ("brackets", ["a[b]"], "- /a\\[b\\]/**\n"),
            ("braces", ["a{b}"], "- /a\\{b\\}/**\n"),
            ("parent suppresses child", ["parent/child", "parent"], "- /parent/**\n"),
            ("case duplicates", ["folder", "Folder", "FOLDER"], "- /FOLDER/**\n"),
            ("NFC NFD", ["caf\u00e9", "cafe\u0301"], "- /caf\u00e9/**\n"),
            ("mixed separators", ["/parent\\child/", "\\other\\"], "- /other/**\n- /parent/child/**\n"),
            ("case parent", ["PARENT/child", "parent", "parental"], "- /parent/**\n- /parental/**\n"),
            ("stable order", ["z", "A", "b"], "- /A/**\n- /b/**\n- /z/**\n"),
            ("empty", [], "")
        ];
        foreach (var c in cases) runner.Test("filter: " + c.Name, () => TestRunner.Equal(c.Expected, HiddenFolders.Render(c.Paths)));
        runner.Test("filter: backslash escape", () => TestRunner.Equal("a\\\\b", HiddenFolders.Escape("a\\b")));
        runner.Test("filter: sample all 26 rules", SampleFilter);
        runner.Test("filter: reject root, traversal and newline", () =>
        {
            foreach (string path in new[] { "", "/", "../data", "a/../b", "a//b", "a\nb" })
                TestRunner.Throws<ArgumentException>(() => HiddenFolders.Render([path]));
        });
        runner.Test("filter: exclusions default and explicit", () =>
        {
            TestRunner.Equal("- /a/**\n- ~$*\n", HiddenFolders.Render(["a"], ["~$*"]));
            TestRunner.Throws<ArgumentException>(() => HiddenFolders.Render([], ["a\n+ **"]));
        });
        runner.Test("arguments: default read-only", () => TestRunner.Assert(Args(new DbxConfig()).Contains("--read-only")));
        runner.Test("arguments: exact allowed remote is writable", () => TestRunner.Assert(!Args(new DbxConfig { WriteAllowedRemotes = ["sample:"] }).Contains("--read-only")));
        runner.Test("arguments: partial or case match stays read-only", () =>
        {
            foreach (string allowed in new[] { "sample", "sample:sub", "SAMPLE:" })
                TestRunner.Assert(Args(new DbxConfig { WriteAllowedRemotes = [allowed] }).Contains("--read-only"));
        });
        runner.Test("arguments: rc secrets only in environment", () =>
        {
            RcCredentials credentials = RcCredentials.Create();
            var info = RcloneArguments.ProcessInfo("rclone.exe", Args(new DbxConfig()), credentials);
            TestRunner.Equal(credentials.User, info.Environment["RCLONE_RC_USER"]);
            TestRunner.Equal(credentials.Password, info.Environment["RCLONE_RC_PASS"]);
            string command = string.Join(' ', info.ArgumentList);
            TestRunner.Assert(!command.Contains(credentials.User) && !command.Contains(credentials.Password) && !command.Contains("--rc-pass") && !command.Contains("--rc-user"));
            TestRunner.Assert(!credentials.ToString().Contains(credentials.Password));
        });
        runner.Test("arguments: options and test overrides", () =>
        {
            var args = Args(new DbxConfig { TestOverrides = new() { WriteBack = "20s", CacheMaxAgeOverride = "2s", CachePollInterval = "1s" } });
            TestRunner.Assert(args.Contains("--vfs-write-back") && args.Contains("20s") && args.Contains("2s") && args.Contains("--vfs-cache-poll-interval"));
            TestRunner.Assert(args.Contains("--network-mode") && args.Contains("--volname") && args.Contains("--ignore-case"));
        });
        runner.Test("config: defaults", () =>
        {
            var c = new DbxConfig().Validate();
            TestRunner.Equal("1h", c.Retention);
            TestRunner.Equal(0, c.WriteAllowedRemotes.Length);
            TestRunner.Equal(0, c.ExcludePatterns.Length);
            TestRunner.Assert(c.TestOverrides is null && c.RcloneConfigPath is null);
            TestRunner.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbxLight", "config.json"), DbxConfig.DefaultConfigPath);
        });
        runner.Test("config: save, reread, backup", () =>
        {
            string root = Path.Combine(RepositoryRoot(), "_test-work", "unit-" + Guid.NewGuid().ToString("N"));
            var store = new ConfigStore(Path.Combine(root, "config.json"));
            var c = new DbxConfig { Remote = Path.Combine(TestPaths.WorkRoot, @"unit-config"), RcloneConfigPath = Path.Combine(TestPaths.WorkRoot, @"unit-empty.conf"), Retention = "1d", HiddenPaths = ["\u898b\u672c\u30d5\u30a9\u30eb\u30c0"], TestOverrides = new() { WriteBack = "1s" } };
            store.Save(c);
            store.Save(store.Load() with { Retention = "3h" });
            TestRunner.Equal("3h", store.Load().Retention);
            TestRunner.Equal(c.HiddenPaths[0], store.Load().HiddenPaths[0]);
            TestRunner.Equal("1s", store.Load().TestOverrides!.WriteBack);
            TestRunner.Equal(1, Directory.GetFiles(root, "*.bak-*").Length);
        });
        runner.Test("config: reject invalid retention and durations", () =>
        {
            TestRunner.Throws<ArgumentException>(() => new DbxConfig { Retention = "2h" }.Validate());
            TestRunner.Throws<ArgumentException>(() => new DbxConfig { PollInterval = "2h" }.Validate());
            TestRunner.Throws<ArgumentException>(() => new DbxConfig { TestOverrides = new() { CachePollInterval = "0" } }.Validate());
        });
        runner.Test("config: typed set and unknown key", () =>
        {
            var c = ConfigStore.Set(new DbxConfig { Remote = Path.Combine(TestPaths.WorkRoot, @"typed-config"), RcloneConfigPath = Path.Combine(TestPaths.WorkRoot, @"typed-empty.conf") }, "hiddenPaths", "[\"a\",\"a/b\"]");
            TestRunner.Equal(1, c.HiddenPaths.Length);
            c = ConfigStore.Set(c, "testOverrides.writeBack", "10s");
            TestRunner.Equal("10s", c.TestOverrides!.WriteBack);
            c = ConfigStore.Set(c, "testOverrides.cachePollInterval", "2s");
            TestRunner.Equal("10s", c.TestOverrides!.WriteBack);
            TestRunner.Equal("2s", c.TestOverrides!.CachePollInterval);
            TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(c, "writeBack", "1s"));
            TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(c, "retention", "2h"));
        });
        runner.Test("state: private ACL without owner privilege", () =>
        {
            string path = Path.Combine(RepositoryRoot(), "_test-work", "acl-" + Guid.NewGuid().ToString("N"), "state.json");
            var store = new StateStore(path);
            store.Save(new RuntimeState { RclonePid = 1234, RcUser = "unit", RcPassword = "unit-only" });
            TestRunner.Equal("unit-only", store.Load()!.RcPassword);
        });
        runner.Test("state: concurrent readers tolerate atomic replacement", () =>
        {
            string path = Path.Combine(RepositoryRoot(), "_test-work", "atomic-" + Guid.NewGuid().ToString("N"), "state.json");
            var store = new StateStore(path);
            store.Save(new RuntimeState());
            using var cancellation = new CancellationTokenSource();
            using var ready = new ManualResetEventSlim();
            Task reader = Task.Run(() =>
            {
                ready.Set();
                while (!cancellation.IsCancellationRequested) TestRunner.Assert(store.Load() is not null);
            });
            ready.Wait();
            try { for (int n = 0; n < 30; n++) store.Save(new RuntimeState { RestartCount = n }); }
            finally { cancellation.Cancel(); reader.GetAwaiter().GetResult(); }
            TestRunner.Equal(29, store.Load()!.RestartCount);
        });
        runner.Test("paths: drive mount local to remote", () => TestRunner.Equal("\u898b\u672c\u30d5\u30a9\u30eb\u30c0/foo", MountPaths.ToRelative("T:\\\u898b\u672c\u30d5\u30a9\u30eb\u30c0\\foo", "T:")));
        runner.Test("paths: folder mount and relative", () =>
        {
            TestRunner.Equal("child", MountPaths.ToRelative("D:\\mount\\child", "D:\\mount"));
            TestRunner.Equal("a/b", MountPaths.ToRelative("/a\\b/", "T:"));
            TestRunner.Equal("", MountPaths.ToRelative("T:\\", "T:", true));
        });
        runner.Test("paths: reject outside mount", () =>
        {
            foreach (string path in new[] { "U:\\a", "D:\\mount-other\\a", "D:\\mount\\..\\a", "D:relative" })
                TestRunner.Throws<ArgumentException>(() => MountPaths.ToRelative(path, "D:\\mount"));
        });
        runner.Test("config: environment path and explicit precedence", () =>
        {
            string? previous = Environment.GetEnvironmentVariable("DBXL_CONFIG");
            try
            {
                string env = Path.Combine(RepositoryRoot(), "_test-work", "env-config.json");
                string explicitPath = Path.Combine(RepositoryRoot(), "_test-work", "explicit-config.json");
                Environment.SetEnvironmentVariable("DBXL_CONFIG", env);
                TestRunner.Equal(env, new ConfigStore().PathName);
                TestRunner.Equal(explicitPath, new ConfigStore(explicitPath).PathName);
            }
            finally { Environment.SetEnvironmentVariable("DBXL_CONFIG", previous); }
        });
    }

    internal static void SampleFilter()
    {
            string original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.filter")).Replace("\r\n", "\n");
            string[] paths = original.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p[3..^3]).ToArray();
            TestRunner.Equal(26, paths.Length);
            TestRunner.Equal(original, HiddenFolders.Render(paths));
    }

    private static IReadOnlyList<string> Args(DbxConfig c) => RcloneArguments.Mount(c with { Remote = c.TestOverrides is null ? "sample:" : Path.Combine(TestPaths.WorkRoot, @"arguments"), RcloneConfigPath = c.TestOverrides is null ? c.RcloneConfigPath : Path.Combine(TestPaths.WorkRoot, @"arguments-empty.conf") }, "C:\\test\\hidden.filter");

    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbxLight.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
