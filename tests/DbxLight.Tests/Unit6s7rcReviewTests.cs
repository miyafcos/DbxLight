using System.Diagnostics;
using System.Reflection;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6s7rTests
{
    internal static void RunReview3(TestRunner runner, string? issue = null)
    {
        if (issue is null or "R5")
        {
            runner.Test("6s7rc R5: invalid entry path remains unknown", Review3InvalidEntry);
            runner.Test("6s7rc R5: invalid Unicode aliases remain unknown on either cache side", Review3InvalidUnicode);
        }
        if (issue is null or "R8") Review3WorkRoots(runner);
        if (issue is null or "R8-mount" or "probe")
            runner.Test("6s7rc R8: real isolated mount is rejected before test or smoke work begins", Review3DriveProbe);
        if (issue is null or "R10")
            runner.Test("6s7rc R10: null executable disables startup and apply never accesses registration", Review3NullExecutable);
    }

    private static bool? Review3Dirty(AgentMetadata agent, string meta, params CacheEntry[] entries) =>
        (bool?)typeof(AgentMetadata).GetMethod("IsDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [new CacheInspection(false, 1, "synthetic") { Entries = entries }, meta, "named.txt"]);

    private static void Review3InvalidEntry()
    {
        using var f = new WaitFixture();
        using var agent = f.Agent();
        var entry = new CacheEntry("other.txt", Path.Combine(f.Data, "bad\0.txt"),
            Path.Combine(f.Meta, "other.txt"), true, true, true, false, 7, f.Modified, "", true, null);
        var clean = entry with { Relative = "named.txt", DataPath = Path.Combine(f.Data, "named.txt"), MetadataPath = Path.Combine(f.Meta, "named.txt") };
        TestRunner.Assert(Review3Dirty(agent, f.Meta, entry, clean) is null, "An invalid entry operand must remain unknown even alongside a valid clean pair.");
    }

    private static void Review3InvalidUnicode()
    {
        using var f = new WaitFixture();
        using var agent = f.Agent();
        foreach (bool metadata in new[] { true, false })
        {
            var entry = new CacheEntry("other.txt",
                Path.Combine(f.Data, metadata ? "other.txt" : "bad\ud800.txt"),
                Path.Combine(f.Meta, metadata ? "bad\ud800.txt" : "other.txt"),
                true, true, true, false, 7, f.Modified, "", true, null);
            TestRunner.Assert(Review3Dirty(agent, f.Meta, entry) is null,
                $"An invalid Unicode alias on metadata={metadata} must remain unknown.");
        }
    }

    private static void Review3WorkRoots(TestRunner runner)
    {
        const string configured = @"U:\configured", repo = @"C:\repository", temp = @"C:\temporary";
        foreach (string format in new[] { "NTFS", "ReFS" })
        {
            string fs = format;
            runner.Test("6s7rc R8: configured local fixed " + fs + " root wins", () =>
                TestRunner.Equal(configured, TestWorkPaths.ResolveWorkRoot(configured, true, repo, temp, _ => (DriveType.Fixed, fs))));
        }
        foreach (var facts in new[] { (DriveType.Network, "NTFS"), (DriveType.Removable, "NTFS"),
            (DriveType.CDRom, "CDFS"), (DriveType.Fixed, "FUSE-rclone"), (DriveType.Fixed, "FAT32"), (DriveType.Unknown, "") })
        {
            var rejected = facts;
            runner.Test($"6s7rc R8: configured {rejected.Item1}/{rejected.Item2} root stops without fallback", () =>
            {
                int probes = 0;
                try
                {
                    TestWorkPaths.ResolveWorkRoot(configured, true, repo, temp, _ => { probes++; return rejected; });
                    throw new InvalidOperationException("Unsafe configured root was accepted.");
                }
                catch (IOException error)
                {
                    TestRunner.Assert(error.Message.Contains(configured) &&
                        error.Message.Contains("DriveType=" + rejected.Item1) && error.Message.Contains("DriveFormat=" + rejected.Item2));
                }
                TestRunner.Equal(1, probes);
            });
        }
        runner.Test("6s7rc R8: eligible D keeps the existing work root", () =>
            TestRunner.Equal(@"D:\dbxl-test", TestWorkPaths.ResolveWorkRoot(null, true, repo, temp, _ => (DriveType.Fixed, "NTFS"))));
        runner.Test("6s7rc R8: ineligible D falls back to an eligible repository", () =>
            TestRunner.Equal(Path.Combine(repo, "_test-work"), TestWorkPaths.ResolveWorkRoot(null, true, repo, temp,
                root => root.StartsWith("D:", StringComparison.OrdinalIgnoreCase) ? (DriveType.Network, "FUSE-rclone") : (DriveType.Fixed, "NTFS"))));
        runner.Test("6s7rc R8: no D or repository uses the eligible temporary root", () =>
            TestRunner.Equal(Path.Combine(temp, "dbxl-test"), TestWorkPaths.ResolveWorkRoot(null, false, null, temp, _ => (DriveType.Fixed, "NTFS"))));
        runner.Test("6s7rc R8: ineligible D without a repository uses the eligible temporary root", () =>
            TestRunner.Equal(Path.Combine(temp, "dbxl-test"), TestWorkPaths.ResolveWorkRoot(null, true, null, temp,
                root => root.StartsWith("D:", StringComparison.OrdinalIgnoreCase) ? (DriveType.Removable, "NTFS") : (DriveType.Fixed, "NTFS"))));
        runner.Test("6s7rc R8: ineligible repository is skipped and an ineligible temporary root stops", () =>
        {
            TestRunner.Equal(@"D:\temporary\dbxl-test", TestWorkPaths.ResolveWorkRoot(null, false, repo, @"D:\temporary",
                root => root.StartsWith("C:", StringComparison.OrdinalIgnoreCase) ? (DriveType.Network, "NTFS") : (DriveType.Fixed, "NTFS")));
            TestRunner.Throws<IOException>(() => TestWorkPaths.ResolveWorkRoot(null, false, repo, temp, _ => (DriveType.Network, "NTFS")));
        });
        runner.Test("6s7rc R8: unavailable volume information never permits a work root", () =>
        {
            TestRunner.Assert(!TestWorkPaths.IsLocalFixedPath(configured, out string reason, _ => throw new IOException("unavailable")));
            TestRunner.Assert(reason.Contains(configured) && reason.Contains("unavailable"));
            TestRunner.Throws<IOException>(() => TestWorkPaths.ResolveWorkRoot(configured, true, repo, temp, _ => throw new IOException("unavailable")));
        });
    }

    private static void Review3DriveProbe()
    {
        using var f = new Unit5Fixture("6s7rc-drive-probe", writable: false);
        f.Start();
        var drive = new DriveInfo(f.Config.MountPoint);
        Console.WriteLine($"R8 mount path={f.Config.MountPoint}; DriveType={drive.DriveType}; DriveFormat={drive.DriveFormat}; WorkRoot={TestPaths.WorkRoot}");
        string unsafeRoot = Path.Combine(f.MountRoot, "rejected-test-root");
        TestRunner.Assert(!TestWorkPaths.IsLocalFixedPath(unsafeRoot, out string reason), reason);
        TestRunner.Throws<IOException>(() => TestWorkPaths.ResolveWorkRoot(unsafeRoot, true, UnitTests.RepositoryRoot(), Path.GetTempPath()));
        foreach (bool smoke in new[] { false, true })
        {
            string output = Path.Combine(f.Root, "smoke-rejected");
            string exe = smoke ? Path.ChangeExtension(typeof(DbxLight.Tray.Program).Assembly.Location, ".exe") : Environment.ProcessPath!;
            string[] args = smoke ? ["--smoke", output] : ["--unit6s7rc", "no-tests"];
            var info = RcloneArguments.ProcessInfo(exe, args);
            info.Environment["DBXL_TEST_ROOT"] = unsafeRoot;
            using Process child = Process.Start(info)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000)) { child.Kill(); child.WaitForExit(10000); throw new TimeoutException("Rejected-root child did not exit."); }
            TestRunner.Equal(1, child.ExitCode);
            string error = stderr.GetAwaiter().GetResult();
            _ = stdout.GetAwaiter().GetResult();
            TestRunner.Assert(error.Contains(unsafeRoot) && error.Contains("DriveType=" + drive.DriveType) &&
                error.Contains("DriveFormat=" + drive.DriveFormat), "Missing rejected-root reason: " + error);
            TestRunner.Assert(!Directory.Exists(unsafeRoot) && !Directory.Exists(output), "Rejected entry created work artifacts.");
            Console.WriteLine($"R8 rejected entry smoke={smoke}; exit={child.ExitCode}; reason={error.Trim()}");
        }
    }

    private static void Review3NullExecutable()
    {
        var f = new RegistrationFixture();
        var config = new DbxConfig { MountPoint = "U:" };
        f.Service.SetAutoStart(true, f.Exe, f.Options);
        int saves = 0;
        FormTests.Sta(() =>
        {
            using var form = new SettingsForm(config, f.Service, null, f.Options, _ => { saves++; return Task.CompletedTask; });
            var checkbox = FormTests.Field<CheckBox>(form, "autoStart");
            int before = f.Service.AccessedPaths.Count;
            TestRunner.Assert(!checkbox.Enabled && checkbox.Checked);
            checkbox.Checked = false; // A programmatic change also cannot pass null to registration.
            var apply = (Task)typeof(SettingsForm).GetMethod("ApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [])!;
            apply.GetAwaiter().GetResult();
            TestRunner.Equal(1, saves);
            TestRunner.Equal(before, f.Service.AccessedPaths.Count);
            TestRunner.Equal("\u8a2d\u5b9a\u3092\u9069\u7528\u3057\u307e\u3057\u305f\u3002", FormTests.Field<TextBox>(form, "result").Text);
        });
        TestRunner.Assert(f.Service.IsAutoStart(), "Existing startup registration changed.");
        f.Safe();
    }
}
