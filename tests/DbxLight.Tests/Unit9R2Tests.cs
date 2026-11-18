using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;
using Microsoft.Win32;

namespace DbxLight.Tests;

internal static partial class Unit9Tests
{
    private static void Review2Tests(TestRunner runner)
    {
        runner.Test("9r2 P1-1: a changed account or namespace cannot use the running generation", ChangedMountAccount);
        runner.Test("9r2 P1-2: re-read a direct share after the final page of members", MovedDirectShare);
        runner.Test("9r2 P2-1: team subfolder succeeds through the VFS spelling comparison", () => FilteredMount("sample:/Dept", "team"));
        runner.Test("9r2 P2-12: the existing source is visible without the exclusion and hidden with it", () => FilteredMount("sample:Dept", "home"));
        runner.Test("9r2 P2-2: only rclone's effective file and options are used", EffectiveEnvironment);
        runner.Test("9r2 P2-4: failed refresh for expired credentials offers connection and login recovery", RefreshFailureInstructions);
        runner.Test("9r2 P2-5: resolve literal, mixed and ambiguous names one ancestor at a time", NameCandidates);
        runner.Test("9r2 P2-6: both web verbs encode verified names and stop on ambiguous names", WebNames);
        runner.Test("9r2 P2-7: changed same-size credentials with restored timestamps invalidate the prefix", RestoredCredentialFile);
        runner.Test("9r2 P2-8: prefix state stays private and logout invalidates it", PrivatePrefixCache);
        runner.Test("9r2 P2-9: custom app manual prefix works offline only for the verified connection", CustomManualOffline);
        runner.Test("9r2 P2-10: CLI and COM copy use the mounted file then reuse it offline", EffectiveCopy);
        runner.Test("9r2 P2-11: CLI JSON, CLI text and GUI distinguish three namespace locations", NamespaceLabels);
        runner.Test("9r2 L-1: applying a new remote and user-entered prefix preserves the new binding", ApplyNewPrefix);
        runner.Test("9r2 L-2: Windows path case shares a binding and legacy values can be renewed", PrefixPathCase);
        runner.Test("9r2 L-3: legacy network, fixed drive and directory mount have safe guidance", LegacyDevices);
        runner.Test("9r2 L-4: cancel a large parent scan and an invitation recheck", CancelVisibility);
        runner.Test("9r2 L-6: final copy preference changes only invalidate a GUI confirmation", DisplayOnlyChanges);
    }

    private static void ChangedMountAccount()
    {
        foreach (bool changeAccount in new[] { true, false })
        {
            var f = new Fixture();
            File.WriteAllText(f.MountedConfig, FakeConfig(FakeAccess));
            f.ClientFactory = config => new DropboxFolderClient(new SnapshotTokenBroker(config), new Handler(f.Api));
            var capture = FolderMountCredentialCapture.Begin(f.Config, default,
                broker => new DropboxFolderClient(broker, new Handler(f.Api)))!;
            var binding = capture.Confirm(f.MountedConfig, default, broker => new DropboxFolderClient(broker, new Handler(f.Api)));
            TestRunner.Equal(f.MountedBinding(f.Config), binding);
            f.Controller.State.Save(f.Controller.State.Load()! with { MountDropboxBinding = binding,
                MountDropboxCursor = capture.CursorFor(binding) });
            TestRunner.Equal(SelectionCursor, capture.CursorFor(binding));
            _ = Confirm(f, Invitation());
            f.Api.Requests.Clear();

            if (changeAccount) f.Api.AccountId = "another-account";
            File.WriteAllText(f.MountedConfig, changeAccount ? FakeConfig(UpdatedAccess) : FakeConfig(FakeAccess, "another-namespace"));
            TestRunner.Equal<string?>(null, capture.Confirm(f.MountedConfig, default, broker => new DropboxFolderClient(broker, new Handler(f.Api))));
            Failure(f.Cli(Invitation()), 3, "mount_unverified", "");
            Failure(f.Cli("path", Folder), 3, "mount_unverified", "");
            AssertReadOnly(f.Api);
        }
    }

    private static void MovedDirectShare()
    {
        foreach (bool changeFolder in new[] { true, false })
        {
            var f = new Fixture();
            f.Api.SharedId = "sample-shared";
            f.Api.Paged = true;
            var session = f.Session;
            var info = session.LoadAsync(true, false, default).GetAwaiter().GetResult();
            var plan = FolderSharing.Plan(info.View, info.LocalPath, FolderInviteRequest.Create([Recipient], "viewer", null));
            f.Api.Requests.Clear();
            f.Api.BeforeReply = call =>
            {
                if (call.Endpoint != "sharing/list_folder_members/continue") return;
                // The already-read share remains a valid target, but the original path now resolves differently.
                if (changeFolder) f.Api.FolderId = "id:replacement"; else f.Api.MetadataShareOverride = "replacement-share";
            };
            var (result, error) = session.InviteAsync(info, plan, default).GetAwaiter().GetResult();
            TestRunner.Equal("sharing_changed", error?.Code);
            TestRunner.Equal("not_sent", result.InvitationState);
            TestRunner.Equal("existing", result.SharedFolderState);
            AssertReadOnly(f.Api);
            TestRunner.Assert(f.Api.Requests.TakeLast(2).SequenceEqual(new[] { SnapshotEndpoints.Account, SnapshotEndpoints.Metadata }.Select(Endpoint)));
        }
    }

    private static void FilteredMount(string remote, string expectedNamespace)
    {
        var f = new Fixture();
        string source = Path.Combine(f.Root, "source"), credentials = Path.Combine(f.Root, "empty.conf");
        Directory.CreateDirectory(Path.Combine(source, "excluded", "child"));
        File.WriteAllText(credentials, "");
        string drive = new[] { "T:", "U:", "V:", "W:" }.First(MountPaths.IsAvailable);
        string root = MountPaths.CanonicalMount(drive);
        string selected = Path.Combine(root, "excluded", "child");
        var local = f.Config with { Remote = source, RcloneConfigPath = credentials, MountPoint = root,
            NetworkMode = true, RcPort = FreeFolderTestPort(), DirCacheTime = "1m", PollInterval = "0" };
        var controller = new MountController(new ConfigStore(Path.Combine(f.Root, "local.json")),
            new StateStore(Path.Combine(f.Root, "local-runtime", "state.json")));
        foreach (bool filtering in new[] { false, true })
        {
            string[] patterns = filtering ? ["/excluded/**"] : [];
            controller.Config.Save(local with { ExcludePatterns = patterns });
            controller.Start();
            try
            {
                TestRunner.Assert(Directory.Exists(Path.Combine(source, "excluded", "child")));
                TestRunner.Equal(!filtering, Directory.Exists(selected));
                TestRunner.Equal(!filtering, Directory.EnumerateDirectories(root).Any(path => Path.GetFileName(path) == "excluded"));

                var live = controller.State.Load()!;
                var configured = f.Config with { Remote = remote, MountPoint = root, NetworkMode = true, ExcludePatterns = patterns };
                JsonFiles.Write(f.ConfigPath, configured);
                using var rc = new FolderTestRc(live, "sample:Dept", f.MountedConfig);
                f.Controller.State.Save(live with { ConfigPath = f.ConfigPath, OwnerStatePath = f.StatePath,
                    RcPort = rc.Port, ActiveConfig = configured, MountDropboxBinding = f.MountedBinding(configured),
                    MountDropboxCursor = SelectionCursor });
                var cli = new MountController(new ConfigStore(f.ConfigPath), new StateStore(f.StatePath));
                f.Api.Requests.Clear();
                var (exit, output, _) = Unit3cTests.Capture(() => DbxLight.Cli.Program.RunCommand(
                    ["--config", f.ConfigPath, "--state", f.StatePath, .. Invitation(selected), "--json"], cli, f.Open));
                using var result = JsonDocument.Parse(output);
                if (filtering)
                {
                    Failure((exit, result.RootElement), 3, "not_visible", "");
                    TestRunner.Equal(0, f.Api.Requests.Count);
                }
                else
                {
                    var preview = Success((exit, result.RootElement));
                    TestRunner.Assert(preview.GetProperty("connection").GetProperty("mountVerified").GetBoolean());
                    TestRunner.Equal(expectedNamespace, f.Api.Calls.Last().NamespaceId);
                    AssertReadOnly(f.Api);
                }
            }
            finally { controller.Stop(); }
        }
    }

    private static void EffectiveEnvironment()
    {
        var f = new Fixture();
        var config = f.Config;
        f.Save(config with { RcloneConfigPath = null });
        var previous = new Dictionary<string, string?>
        {
            ["RCLONE_CONFIG"] = Path.Combine(f.Root, "other.conf"),
            ["RCLONE_DROPBOX_SHARED_FILES"] = "false",
            ["RCLONE_CONFIG_SAMPLE_ROOT_NAMESPACE"] = "unrelated",
            ["RCLONE_DROPBOX_ENCODING"] = "invalid"
        };
        try
        {
            foreach (string key in previous.Keys.ToArray())
            {
                string? value = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, previous[key]);
                previous[key] = value;
            }
            _ = Confirm(f, Invitation());
            TestRunner.Equal(f.MountedConfig, f.LastClientConfig);
            Success(f.Cli("path", Folder));
            TestRunner.Equal(f.MountedConfig, f.LastClientConfig);
            f.Save(config with { RcloneConfigPath = Path.Combine(f.Root, "explicit-other.conf") });
            Failure(f.Cli(Invitation()), 3, "mount_unverified", "");
            AssertReadOnly(f.Api);
        }
        finally
        {
            foreach (var variable in previous) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }
    }

    private static void RefreshFailureInstructions()
    {
        foreach (string problem in new[] { "revoked_refresh_grant", "network_error" })
        {
            var f = new Fixture();
            f.Api.Credential = new(FakeAccess, DateTimeOffset.UtcNow.AddMinutes(-1));
            f.Api.RenewalProblem = problem;
            var result = f.Cli("share", "show", Folder);
            Failure(result, 3, "auth_refresh_unavailable", "接続を確認");
            Failure(result, 3, "auth_refresh_unavailable", "ログインし直してください");
            TestRunner.Assert(!result.Output.GetRawText().Contains(problem, StringComparison.Ordinal));
            TestRunner.Equal(1, f.Api.Renewals);
            TestRunner.Equal(0, f.Api.Requests.Count);
        }
    }

    private static void NameCandidates()
    {
        var f = new Fixture();
        f.Save(f.Config with { Remote = "sample:Dept" });
        string parent = "Parent\uf03f", child = "Child\uf03f";
        string selected = Path.Combine(f.Config.MountPoint, parent, child);
        Directory.CreateDirectory(selected);
        string cloudParent = "/Dept/" + parent;
        string cloudChild = cloudParent + "/Child?";
        f.Api.MetadataPaths = [cloudParent, cloudChild];
        TestRunner.Equal(Prefix + @"\Dept\" + parent + @"\Child?",
            Success(f.Cli("path", selected)).GetProperty("path").GetString());
        var session = new ShareFolderSession(selected, f.Mount, f.Open);
        var target = session.LoadAsync(true, false, default).GetAwaiter().GetResult().View.Target;
        TestRunner.Equal(cloudChild, target.Path);
        TestRunner.Equal(f.Api.FolderId, target.FolderId);
        _ = Confirm(f, Invitation(selected));
        AssertReadOnly(f.Api);

        f.Api.MetadataPaths.Add("/Dept/Parent?");
        Failure(f.Cli(Invitation(selected)), 2, "ambiguous_name", "名前を特定");
        Failure(f.Cli("path", selected), 2, "ambiguous_name", "名前を特定");
        f.Api.MetadataPaths.Clear();
        Failure(f.Cli(Invitation(selected)), 2, "ambiguous_name", "名前を特定");
        f.Api.Requests.Clear();
        string unbounded = string.Join('\\', Enumerable.Repeat("\uf03f\uf03f\uf03f\uf03f", 5));
        Failure(f.Cli("path", Path.Combine(f.Config.MountPoint, unbounded)), 2, "ambiguous_name", "名前を特定");
        TestRunner.Equal(0, f.Api.Requests.Count);
    }

    private static void WebNames()
    {
        var f = new Fixture();
        f.Save(f.Config with { Remote = "sample:/Dept/Q?" });
        const string good = @"T:\Q？\Example.txt";
        const string ambiguous = "T:\\Q\uf03f";
        foreach (string option in new[] { "--web-shell", "--web-copy-shell" })
        {
            var args = AppOptions.Parse([option, good + "|"]);
            TestRunner.Equal("https://www.dropbox.com/home/Dept/Q%3F/Q%EF%BC%9F/Example.txt",
                ShellWeb(f, f.LocalArguments([(args.WebPath ?? args.WebCopyPath)!])[0]));
            args = AppOptions.Parse([option, ambiguous + "|"]);
            try { _ = ShellWeb(f, f.LocalArguments([(args.WebPath ?? args.WebCopyPath)!])[0]); }
            catch (FolderOperationException e) { TestRunner.Equal("ambiguous_name", e.Code); continue; }
            throw new InvalidOperationException("An ambiguous URL was created.");
        }
    }

    private static string FakeConfig(string access, string? ns = null) =>
        "[sample]\ntype = dropbox\n" + (ns is null ? "" : "root_namespace = " + ns + "\n") +
        "token = {\"access_token\":\"" + access + "\"}\n";

    private static void RestoredCredentialFile()
    {
        var f = new Fixture();
        File.WriteAllText(f.MountedConfig, FakeConfig(FakeAccess, "home"));
        f.ClientFactory = config => new DropboxFolderClient(new SnapshotTokenBroker(config), new Handler(f.Api));
        var state = f.Controller.State.Load()!;
        f.Controller.State.Save(state with { StopRequested = true, StopDisposition = StopDisposition.Clean });

        TestRunner.Equal(Prefix + "\\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        f.Api.Requests.Clear();
        Success(f.Cli("path", Folder));
        TestRunner.Equal(0, f.Api.Requests.Count);

        var original = new FileInfo(f.MountedConfig);
        long size = original.Length;
        DateTime written = original.LastWriteTimeUtc, created = original.CreationTimeUtc;
        File.WriteAllText(f.MountedConfig, FakeConfig(FakeAccess, "else"));
        File.SetLastWriteTimeUtc(f.MountedConfig, written);
        var restored = new FileInfo(f.MountedConfig);
        TestRunner.Equal(size, restored.Length);
        TestRunner.Equal(written, restored.LastWriteTimeUtc);
        TestRunner.Equal(created, restored.CreationTimeUtc);

        // The metadata looks the same. Only fresh authentication may confirm the prefix.
        f.Api.RejectCurrent = true;
        Failure(f.Cli("path", Folder), 3, "auth_unavailable", "ログイン");
        TestRunner.Equal(Endpoint(SnapshotEndpoints.Account), f.Api.Requests.Single());
    }

    private static void PrivatePrefixCache()
    {
        var f = new Fixture();
        f.Api.CacheSource = "sample-source";
        Success(f.Cli("path", Folder));
        string cache = DropboxCopyPaths.CacheFile(f.ConfigPath);
        TestRunner.Assert(MountPaths.IsSameOrBelow(cache, DbxConfig.LocalRoot));
        TestRunner.Assert(!Directory.Exists(f.ConfigPath + ".copy-path") && File.Exists(cache));
        PrivateStateFiles.ValidateExisting(cache);
        string before = File.ReadAllText(cache);
        TestRunner.Assert(before.Contains("Dropbox") && !before.Contains(FakeAccess) && !before.Contains(UpdatedAccess));

        var controller = new MountController(new ConfigStore(f.ConfigPath), new StateStore(Path.Combine(f.Root, "stopped", "state.json")));
        var after = new AccountSettings(controller).LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Equal("", after.Remote);
        string cleared = File.ReadAllText(cache);
        TestRunner.Assert(!cleared.Contains("Dropbox") && before != cleared);
        f.Save(f.Config);
        f.Api.Requests.Clear();
        f.Api.RejectCurrent = true;
        Failure(f.Cli("path", Folder), 3, "auth_unavailable", "ログイン");
        TestRunner.Equal(1, f.Api.Requests.Count);
    }

    private static void CustomManualOffline()
    {
        var f = new Fixture();
        f.Api.CustomApp = true;
        f.Api.CacheSource = "custom-first";
        const string manual = @"\Dropbox\Apps\Sample";
        Failure(f.Cli("path", Folder), 3, "copy_path_prefix_unknown", "コピーするパスの頭");
        Success(f.Cli("set", "copyPathPrefix", manual));
        TestRunner.Equal(manual + "\\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        f.Api.Requests.Clear();
        f.Api.RejectCurrent = true;
        TestRunner.Equal(manual + "\\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Equal(0, f.Api.Requests.Count);
        f.Api.CacheSource = "custom-replaced";
        f.Api.AccountId = "replacement-account";
        f.Save(f.Controller.Config.Load());
        f.Api.RejectCurrent = false;
        Failure(f.Cli("path", Folder), 3, "copy_path_prefix_unknown", "コピーするパスの頭");
        TestRunner.Equal(1, f.Api.Requests.Count);
    }

    private static void EffectiveCopy()
    {
        var f = new Fixture();
        f.Save(f.Config with { RcloneConfigPath = null });
        f.Api.CacheSource = "mounted-source";
        var options = new AppOptions(f.ConfigPath, f.StatePath);
        string wrong = Path.Combine(f.Root, "other-default.conf");
        string? previous = Environment.GetEnvironmentVariable("RCLONE_CONFIG");
        string expected = Prefix + "\\" + RelativeFolder;
        try
        {
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", wrong);
            TestRunner.Equal(expected, Success(f.Cli("path", Folder, "--state", options.StatePath)).GetProperty("path").GetString());
            TestRunner.Equal(f.MountedConfig, f.LastClientConfig);
            TestRunner.Equal(expected, ShellCopyServer.CopyTextAsync(options, [f.LocalFolder], f.Open, f.Controller.FolderMountInspector)
                .GetAwaiter().GetResult());
            TestRunner.Equal(f.MountedConfig, f.LastClientConfig);

            // Contacting the CLI's default would fail; a stopped mount can reuse the previously verified source.
            var state = f.Controller.State.Load()!;
            f.Controller.State.Save(state with { StopRequested = true, StopDisposition = StopDisposition.Clean });
            f.Api.Requests.Clear();
            f.Api.RejectCurrent = true;
            TestRunner.Equal(expected, Success(f.Cli("path", Folder, "--state", options.StatePath)).GetProperty("path").GetString());
            TestRunner.Equal(expected, ShellCopyServer.CopyTextAsync(options, [f.LocalFolder], f.Open, f.Controller.FolderMountInspector)
                .GetAwaiter().GetResult());
            TestRunner.Equal(f.MountedConfig, f.LastClientConfig);
            TestRunner.Equal(0, f.Api.Requests.Count);
        }
        finally { Environment.SetEnvironmentVariable("RCLONE_CONFIG", previous); }
    }

    private static void NamespaceLabels() => FormTests.Sta(() =>
    {
        foreach (var (remote, explicitNs, kind, label) in new[]
        {
            ("sample:", (string?)null, "team_home", "チームのホーム"),
            ("sample:/Dept", null, "team_space", "チームの空間"),
            ("sample:", "separate", "explicit_namespace", "指定した名前空間")
        })
        {
            var f = new Fixture();
            f.Api.NamespaceId = explicitNs;
            f.Save(f.Config with { Remote = remote });
            var account = f.Api.AccountFor(remote);
            f.Save(f.Config with { CopyPathPrefix = @"\見本", CopyPathBinding = DropboxCopyPaths.Binding(f.Config, account) });
            var connection = Success(f.Cli(Invitation())).GetProperty("connection");
            TestRunner.Equal(kind, connection.GetProperty("namespaceKind").GetString());
            TestRunner.Equal(kind == "team_space", connection.GetProperty("teamSpace").GetBoolean());
            var cli = f.Run(Invitation());
            TestRunner.Assert(cli.Exit == 0 && cli.Output.Contains(label));
            var info = f.Session.LoadAsync(false, true, default).GetAwaiter().GetResult();
            var plan = FolderSharing.Plan(info.View, info.CopyPath!, FolderInviteRequest.Create([Recipient], "viewer", null));
            using var confirmation = new ShareInvitationConfirmation(plan);
            string text = confirmation.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TextBox>().Single().Text;
            TestRunner.Assert(text.Contains(label));
            AssertReadOnly(f.Api);
        }
    });

    private static void ApplyNewPrefix()
    {
        var f = new Fixture();
        const string entered = @"\New Dropbox\Selected";
        var old = f.Config with { CopyPathPrefix = Prefix, CopyPathBinding = DropboxCopyPaths.Binding(f.Config, f.Api.AccountFor(f.Config.Remote)) };
        f.Save(old);
        var next = f.Config with { Remote = "other:Dept", CopyPathPrefix = entered };
        f.Api.AccountId = "new-account";
        var verified = DropboxCopyPaths.BindSettingsAsync(old, next, f.Open, default).GetAwaiter().GetResult();
        var controller = new MountController(new ConfigStore(f.ConfigPath), new StateStore(Path.Combine(f.Root, "stopped", "state.json")));
        var applied = controller.UpdateConfig(_ => verified);
        TestRunner.Equal(entered, applied.CopyPathPrefix);
        TestRunner.Equal(DropboxCopyPaths.Binding(applied, f.Api.AccountFor(next.Remote)), applied.CopyPathBinding);
        TestRunner.Equal(entered, controller.Config.Load().CopyPathPrefix);
        AssertReadOnly(f.Api);
    }

    private static void PrefixPathCase()
    {
        var f = new Fixture();
        var old = f.Config;
        var account = f.Api.AccountFor(old.Remote);
        var differentCase = old with { RclonePath = old.RclonePath.ToUpperInvariant(), RcloneConfigPath = f.MountedConfig.ToUpperInvariant() };
        TestRunner.Assert(DropboxCopyPaths.SameConnection(old, differentCase));
        TestRunner.Equal(DropboxCopyPaths.Binding(old, account), DropboxCopyPaths.Binding(differentCase, account));
        var oldAccount = new { old.Remote, old.RclonePath, old.RcloneConfigPath, account.AccountId, account.NamespaceId };
        string oldBinding = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldAccount)));
        const string entered = @"\Sample Manual\Folder";
        old = old with { CopyPathPrefix = entered, CopyPathBinding = oldBinding };
        using var client = f.Open(old);
        string legacy = DropboxCopyPaths.PrefixAsync(old, client, account, default).GetAwaiter().GetResult();
        TestRunner.Equal(entered, legacy);
        f.Api.Requests.Clear();
        var rebound = DropboxCopyPaths.BindSettingsAsync(old, differentCase with { CopyPathPrefix = entered, CopyPathBinding = oldBinding },
            f.Open, default).GetAwaiter().GetResult();
        TestRunner.Equal(DropboxCopyPaths.Binding(differentCase, account), rebound.CopyPathBinding);
        TestRunner.Equal(1, f.Api.Requests.Count);
        f.Save(rebound);
        TestRunner.Equal(entered + "\\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
    }

    private static void LegacyDevices()
    {
        foreach (var (directory, network) in new[] { (false, true), (false, false), (true, false) })
        {
            var f = new Fixture();
            var config = f.Config with { MountPoint = directory ? f.Config.MountPoint : @"S:\", NetworkMode = network };
            JsonFiles.Write(f.ConfigPath, config);
            f.Device = @"\Device\Volume{sample}" + (network ? @"\server\" + RcloneArguments.NetworkVolumeName(config) : "");
            var state = f.Controller.State.Load()!;
            f.Controller.State.Save(state with { ActiveConfig = config, MountDevice = null, MountDropboxBinding = null });
            TestRunner.Equal(network, f.Mount.Observe(config) is not null);
            var selected = Path.Combine(MountPaths.CanonicalMount(config.MountPoint), RelativeFolder);
            Failure(f.Cli(Invitation(selected)), 3, "mount_unverified", "現行版でマウントし直し");
            TestRunner.Assert(!Success(f.Cli("share", "show", selected)).GetProperty("connection").GetProperty("mountVerified").GetBoolean());
            AssertReadOnly(f.Api);
        }
    }

    private static void CancelVisibility()
    {
        var f = new Fixture();
        for (int i = 0; i < 1024; i++) Directory.CreateDirectory(Path.Combine(f.Config.MountPoint, "folder-" + i));
        using var cancellation = new CancellationTokenSource();
        int visited = 0;
        TestRunner.Throws<OperationCanceledException>(() =>
        {
            _ = FolderShareMount.VisibleChild(f.Config.MountPoint, RelativeFolder, true, cancellation.Token,
                () => { if (++visited == 20) cancellation.Cancel(); });
        });
        TestRunner.Equal(20, visited);

        var session = f.Session;
        var info = session.LoadAsync(true, false, default).GetAwaiter().GetResult();
        var plan = FolderSharing.Plan(info.View, info.LocalPath, FolderInviteRequest.Create([Recipient], "viewer", null));
        using var send = new CancellationTokenSource();
        f.Api.BeforeReply = call => { if (call.Endpoint == SnapshotEndpoints.Metadata) send.Cancel(); };
        var (result, error) = session.InviteAsync(info, plan, send.Token).GetAwaiter().GetResult();
        TestRunner.Equal("cancelled", error?.Code);
        TestRunner.Equal("not_sent", result.InvitationState);
        AssertReadOnly(f.Api);
    }

    private static void DisplayOnlyChanges()
    {
        foreach (var (gui, late) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var f = new Fixture();
            f.Api.SharedId = "sample-shared";
            f.Api.Paged = true;
            var session = f.Session;
            var info = session.LoadAsync(true, gui, default).GetAwaiter().GetResult();
            var plan = FolderSharing.Plan(info.View, gui ? info.CopyPath! : info.LocalPath,
                FolderInviteRequest.Create([Recipient], "viewer", null));
            int reads = 0;
            f.Api.BeforeReply = call =>
            {
                if (late ? call.Endpoint == SnapshotEndpoints.Metadata && ++reads == 2 :
                    call.Endpoint == "sharing/list_folder_members/continue")
                    f.Save(f.Config with { CopyPathPrefix = @"\Preferred" }, updateState: false);
            };
            var (result, error) = session.InviteAsync(info, plan, default).GetAwaiter().GetResult();
            TestRunner.Equal(gui ? "sharing_changed" : null, error?.Code);
            TestRunner.Equal(gui ? "not_sent" : "sent", result.InvitationState);
            TestRunner.Equal(gui ? 0 : 1, f.Api.Writes.Count);
        }
    }

    private static int FreeFolderTestPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class FolderTestRc : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task serve;
        internal int Port { get; } = FreeFolderTestPort();

        internal FolderTestRc(RuntimeState live, string remote, string credentials, bool runtimeStatus = false)
        {
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            serve = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var context = await listener.GetContextAsync();
                        object body = context.Request.Url!.AbsolutePath switch
                        {
                            "/core/pid" => new { pid = live.RclonePid },
                            "/vfs/stats" => new { fs = remote, diskCache = new { path = live.ActiveConfig.CacheDir,
                                uploadsQueued = 0, uploadsInProgress = 0, erroredFiles = 0, files = 0, bytesUsed = 0 } },
                            "/vfs/queue" when runtimeStatus => new { queue = Array.Empty<object>() },
                            "/core/transferred" when runtimeStatus => new { transferred = Array.Empty<object>() },
                            "/core/stats" when runtimeStatus => new { transfers = 0 },
                            "/config/paths" => new { config = credentials },
                            _ => throw new InvalidOperationException("Unexpected folder mount RC route")
                        };
                        await JsonSerializer.SerializeAsync(context.Response.OutputStream, body);
                        context.Response.Close();
                    }
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
            });
        }

        public void Dispose() { listener.Close(); serve.GetAwaiter().GetResult(); }
    }
}
