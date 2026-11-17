using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit4Tests
{
    internal const string Marker = "OAUTH_SECRET_UNIT4_NEVER_DISPLAY";
    public static void Run(TestRunner runner, string group = "all")
    {
        if (group is "all" or "L1a")
        {
            runner.Test("L1a prerequisites, navigation and closing do not save configuration", Prerequisites);
            runner.Test("L1a fake login success and close roll back opaque credentials", LoginSuccess);
            runner.Test("L1a failed login is sanitized and can be retried", LoginFailure);
            runner.Test("L1a cancelling authorization terminates the owned process tree", () => LoginInterrupted(false));
            runner.Test("L1a timeout terminates the owned process tree", () => LoginInterrupted(true));
            runner.Test("L1a dedicated configuration has private ACL at creation", PrivateCreation);
            runner.Test("L1a existing remote names come only from fake rclone", ExistingNames);
            runner.Test("L1a real local backend creates only a private isolated configuration", LocalConfiguration);
        }
        if (group is "all" or "review")
        {
            runner.Test("Lreview secret markers never reach console trace log or form text", PrivacyAcrossSurfaces);
            runner.Test("Lreview choosing an existing remote first still creates a private future key parent", ExistingFirstPrivateParent);
            runner.Test("Lreview write access turns off after live zero and a clean stop are verified", WriteOffVerifiedZero);
        }
        if (group is "all" or "L3")
        {
            runner.Test("L3 enabling write access requires an explicit confirmation", WriteConfirmationRequired);
            runner.Test("L3 only the current remote is allowed and CLI agent-guide follows both switch states", WriteExactAndGuide);
            runner.Test("L3 pending and unknown uploads refuse switching write access off", WriteOffRefusals);
            runner.Test("L3 recovery refuses both permission changes", WriteRecoveryRefusal);
            runner.Test("L3 changing connection invalidates an older confirmation", WriteStaleConfirmation);
            runner.Test("L3 confirmation window states all three consequences", WriteConfirmationUi);
        }
        if (group is "all" or "L2")
        {
            runner.Test("L2 logout deletes only dbxlight in the dedicated private configuration", LogoutDedicated);
            runner.Test("L2 logout of an existing remote never invokes config delete", LogoutExisting);
            runner.Test("L2 pending and unknown uploads refuse logout without stopping", LogoutQueueRefusals);
            runner.Test("L2 recovery refuses logout with the existing Japanese explanation", LogoutRecovery);
            runner.Test("L2 deletion failure restores opaque credentials and exposes no output", LogoutFailureRollback);
            runner.Test("L2 a live mount is normally stopped before detaching", LogoutStopsFirst);
        }
        if (group is "all" or "L1b")
        {
            runner.Test("L1b both Dropbox scopes list their immediate folders", ScopeChoices);
            runner.Test("L1b failed team root returns to own folders without leaking output", TeamFallback);
            runner.Test("L1b forward and back preserve draft; closing saves nothing", NavigationRollback);
            runner.Test("L1b Finish saves all options and always starts read-only", CommitReadOnly);
            runner.Test("L1b selecting an existing remote uses only fake default-config commands", ExistingSelection);
            runner.Test("L1b overwriting dbxlight requires confirmation and rolls back on close", OverwriteConfirmation);
            runner.Test("L1b login and existing-remote selection refuse recovery", SetupRecovery);
            runner.Test("L1b final apply rechecks recovery and mounts refuse a login in progress", FinalGuardAndLease);
        }
    }

    private static void Prerequisites()
    {
        using var f = new LoginFixture();
        using var session = f.Session();
        session.CheckRequirements();
        TestRunner.Assert(session.Checks.Any(c => c.Name == "rclone" && c.Passed));
        TestRunner.Assert(session.Checks.Any(c => c.Name == "WinFsp" && c.Passed));
        TestRunner.Assert(session.Next()); TestRunner.Equal(SetupStep.Login, session.Step);
        session.Back(); TestRunner.Equal(SetupStep.Requirements, session.Step);
        TestRunner.Assert(!File.Exists(f.Controller.Config.PathName));
        FormTests.Sta(() => { using var form = new SetupForm(f.Session()); TestRunner.Equal(SetupStep.Requirements, form.Session.Step); });
        TestRunner.Assert(!File.Exists(f.Controller.Config.PathName));
    }

    private static void LoginSuccess()
    {
        using var f = new LoginFixture();
        using (SetupSession session = f.AtLogin())
        {
            LoginResult result = session.AuthorizeAsync(false).GetAwaiter().GetResult();
            TestRunner.Assert(result.Succeeded); TestRunner.Equal(SetupStep.Scope, session.Step);
            TestRunner.Equal("dbxlight:", session.Draft.Remote);
            TestRunner.Equal(f.Conf, session.Draft.RcloneConfigPath);
            TestRunner.Assert(!File.Exists(f.Conf));
            TestRunner.Assert(File.ReadAllText(session.BrowsingConfig.RcloneConfigPath!).Contains(Marker));
            TestRunner.Assert(!File.Exists(f.Controller.Config.PathName));
            TestRunner.Assert(!result.ToString().Contains(Marker));
        }
        TestRunner.Assert(!File.Exists(f.Conf));
        f.AssertCalls("create");
        TestRunner.Assert(f.Calls().Any(c => c.Args.Length == 7 && c.Args[5].StartsWith(f.Conf + ".work-", StringComparison.Ordinal) &&
            c.Args.SequenceEqual(new[] { "config", "create", "dbxlight", "dropbox", "--config", c.Args[5], "--no-output" })));
    }

    private static void LoginFailure()
    {
        using var f = new LoginFixture("failure");
        using var session = f.AtLogin();
        LoginResult result = session.AuthorizeAsync(false).GetAwaiter().GetResult();
        TestRunner.Equal(LoginOutcome.Failed, result.Outcome); TestRunner.Equal(SetupStep.Login, session.Step);
        TestRunner.Assert(result.Message.Contains("17"), "Login failure omitted its safe numeric reason.");
        TestRunner.Assert(!File.Exists(f.Conf));
        TestRunner.Assert(!result.ToString().Contains(Marker) && !session.Message.Contains(Marker));
        f.Scenario("success");
        TestRunner.Assert(session.AuthorizeAsync(false).GetAwaiter().GetResult().Succeeded);
    }

    private static void LoginInterrupted(bool timeout)
    {
        using var f = new LoginFixture("wait");
        using var session = f.AtLogin(timeout ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(5));
        Task<LoginResult> task = session.AuthorizeAsync(false);
        if (!timeout)
        {
            var readiness = Stopwatch.StartNew();
            while (!f.Calls().Any(c => c.Args.Contains("--fake-child")))
            {
                if (task.IsCompleted)
                    throw new InvalidOperationException("Fake authorization ended before child readiness: " + task.GetAwaiter().GetResult().Message);
                if (readiness.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Owned fake child did not become ready.");
                Thread.Sleep(25);
            }
            session.CancelLogin();
        }
        LoginResult result = task.GetAwaiter().GetResult();
        TestRunner.Equal(timeout ? LoginOutcome.TimedOut : LoginOutcome.Cancelled, result.Outcome);
        TestRunner.Assert(!File.Exists(f.Conf));
        f.AssertExited();
        TestRunner.Assert(!result.ToString().Contains(Marker));
    }

    private static void PrivateCreation()
    {
        using var f = new LoginFixture();
        var seen = new List<string>();
        var login = new RcloneLogin(f.Executable, f.Conf) { FileCreated = path => { AssertPrivate(path); seen.Add(path); } };
        using var session = new SetupSession(f.Controller, login);
        session.CheckRequirements(); session.Next();
        TestRunner.Assert(session.AuthorizeAsync(false).GetAwaiter().GetResult().Succeeded);
        TestRunner.Assert(seen.Any(path => path.StartsWith(f.Conf + ".work-", StringComparison.Ordinal)));
        AssertPrivate(session.BrowsingConfig.RcloneConfigPath!);
        TestRunner.Assert(!File.Exists(f.Conf));
    }

    internal static void AssertPrivate(string path)
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        foreach (FileSystemSecurity acl in new FileSystemSecurity[] { new FileInfo(path).GetAccessControl(), new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl() })
        {
            TestRunner.Assert(sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))));
            if (acl is DirectorySecurity) TestRunner.Assert(acl.AreAccessRulesProtected);
            TestRunner.Assert(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Allow).All(r => sid.Equals(r.IdentityReference)));
        }
    }

    private static void LocalConfiguration()
    {
        using var f = new LoginFixture();
        bool created = false;
        var login = new RcloneLogin(new DbxConfig().RclonePath, f.Conf)
        {
            FileCreated = path => { AssertPrivate(path); if (path.StartsWith(f.Conf + ".work-", StringComparison.Ordinal)) created = true; }
        };
        using var change = login.BeginFileChange(f.Controller.Config);
        using var process = Process.Start(RcloneArguments.ProcessInfo(new DbxConfig().RclonePath,
            ["config", "create", "dbxlight", "local", "--config", change.WorkingPath, "--no-output"]))!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        TestRunner.Assert(process.WaitForExit(30000));
        TestRunner.Equal(0, process.ExitCode);
        TestRunner.Equal("", output.GetAwaiter().GetResult());
        TestRunner.Assert(!errors.GetAwaiter().GetResult().Contains(Marker));
        TestRunner.Assert(created);
        AssertPrivate(change.WorkingPath);
        TestRunner.Assert(!File.Exists(f.Conf));
        TestRunner.Assert(File.ReadAllText(change.WorkingPath).Contains("[dbxlight]") && File.ReadAllText(change.WorkingPath).Contains("type = local"));
        change.Commit(f.Controller, f.Controller.Config.Load(), f.Controller.Config.Load() with { Remote = "dbxlight:", RcloneConfigPath = f.Conf, WriteAllowedRemotes = [] });
        Console.WriteLine("Real rclone: config create dbxlight local --config <isolated private conf> --no-output => exit=0; ACL private before and after creation.");
    }

    private static void ExistingNames()
    {
        using var f = new LoginFixture();
        using var session = f.AtLogin();
        var names = session.ExistingRemotesAsync().GetAwaiter().GetResult();
        TestRunner.Assert(names.SequenceEqual(new[] { "sample-existing:", "team-archive:" }));
        TestRunner.Assert(f.Calls().Last().Args.SequenceEqual(new[] { "listremotes" }));
        TestRunner.Assert(!File.Exists(f.Conf));
    }
}

internal sealed class LoginFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(TestPaths.WorkRoot, "run-unit4-" + Guid.NewGuid().ToString("N"));
    internal string Executable { get; }
    internal string Conf => CredentialSwitch.KeyPath(Controller.Config.PathName);
    internal MountController Controller { get; }
    internal LoginFixture(string scenario = "success")
    {
        string fake = Path.Combine(Root, "fake");
        Directory.CreateDirectory(fake);
        string source = Path.Combine(UnitTests.RepositoryRoot(), "tests", "DbxLight.FakeRclone", "bin", "Release", "net8.0-windows");
        foreach (string path in Directory.GetFiles(source)) File.Copy(path, Path.Combine(fake, Path.GetFileName(path)));
        Executable = Path.Combine(fake, "DbxLight.FakeRclone.exe");
        Scenario(scenario);
        Controller = new(new ConfigStore(Path.Combine(Root, "config.json"), initialConfig: new DbxConfig
        {
            UpdateCheck = false, RclonePath = Executable, CacheDir = Path.Combine(Root, "cache"), RescueDir = Path.Combine(Root, "rescue"), LogDir = Path.Combine(Root, "logs") }),
            new StateStore(Path.Combine(Root, "runtime", "state.json")));
        // Missing app config is intentional: the wizard must not save anything before Finish.
    }
    internal void Seed(string remote = "", string? conf = null)
    {
        Directory.CreateDirectory(Path.Combine(Root, "cache"));
        Controller.Config.Save(new DbxConfig { UpdateCheck = false, RclonePath = Executable, RcloneConfigPath = conf, Remote = remote,
            CacheDir = Path.Combine(Root, "cache"), RescueDir = Path.Combine(Root, "rescue"), LogDir = Path.Combine(Root, "logs"), WriteAllowedRemotes = ["previous:"] });
    }
    internal void MarkPendingCache()
    {
        DbxConfig config = Controller.Config.Load();
        string body = Path.Combine(config.CacheDir, "vfs", "sample", "pending.txt");
        string meta = Path.Combine(config.CacheDir, "vfsMeta", "sample", "pending.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(body)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(body, "x", new UTF8Encoding(false));
        File.WriteAllText(meta, JsonSerializer.Serialize(new { ModTime = "2026-01-01T00:00:00Z", ATime = "2026-01-01T00:00:00Z", Size = 1,
            Rs = new[] { new { Pos = 0, Size = 1 } }, Fingerprint = "", Dirty = true }), new UTF8Encoding(false));
    }
    internal void Scenario(string value) => File.WriteAllText(Path.Combine(Path.GetDirectoryName(Executable)!, "scenario.txt"), value, new UTF8Encoding(false));
    internal SetupSession Session(TimeSpan? timeout = null)
    {
        // No real user paths are accessed; only the fake executable is changed in the unsaved draft.
        return new SetupSession(Controller, new RcloneLogin(Executable, Conf, timeout ?? TimeSpan.FromMinutes(5)));
    }
    internal SetupSession AtLogin(TimeSpan? timeout = null)
    {
        SetupSession session = Session(timeout); session.CheckRequirements();
        TestRunner.Assert(session.Next()); return session;
    }
    internal sealed record Call(int Pid, string[] Args, DateTime StartedUtc);
    internal Call[] Calls()
    {
        string path = Path.Combine(Path.GetDirectoryName(Executable)!, "calls.jsonl");
        if (!File.Exists(path)) return [];
        try
        {
            // Readiness polling must allow the fake to append its child's PID concurrently.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string[] lines = reader.ReadToEnd().Split('\n');
            return lines.Take(lines.Length - 1).Where(s => s.Length > 0).Select(s =>
            {
                using var doc = JsonDocument.Parse(s);
                return new Call(doc.RootElement.GetProperty("pid").GetInt32(), doc.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                    doc.RootElement.GetProperty("startedUtc").GetDateTime().ToUniversalTime());
            }).ToArray();
        }
        catch (IOException) { return []; }
    }
    internal void AssertCalls(string command) => TestRunner.Assert(Calls().Any(c => c.Args.Contains(command)));
    internal void AssertExited()
    {
        // Helpers are job-owned before resuming. PID reuse must not make an unrelated
        // process look like an owned child, and job termination may finish after its parent.
        foreach (Call call in Calls().DistinctBy(c => c.Pid))
        {
            using var process = ProcessIdentity.Open(new RuntimeState { RclonePid = call.Pid,
                ProcessStartedUtc = call.StartedUtc, ExecutablePath = Executable });
            if (process is not null) TestRunner.Assert(process.WaitForExit(10000), "Owned fake PID still running: " + call.Pid);
        }
    }
    public void Dispose()
    {
        AssertExited();
        long bytes = new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        TestRunner.Assert(bytes < 50 * 1024 * 1024);
        Console.WriteLine($"Unit4 owned fake PIDs={string.Join(',', Calls().Select(c => c.Pid).Distinct())}: exited; bytes={bytes}; artifacts={Root}");
    }
}
