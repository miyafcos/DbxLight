using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit9Tests
{
    private const string SelectionCursor = "sample-selection-start";

    internal static void Review3Tests(TestRunner runner)
    {
        runner.Test("R3-P2-4: a malformed optional RC reply leaves a ready mount running", MalformedStartupReply);
        runner.Test("R3-P1-1: a change during the last rejected spelling stops the invitation", LastSpellingChanged);
        runner.Test("R3-P1-2: a device change during the final cloud read prevents either write", DeviceDuringFinalRead);
        runner.Test("R3-P1-3: a stale selection with the same path cannot preview a different folder ID", ReplacedCachedSelection);
        runner.Test("R3-P2-1: the real broker forces renewal when the saved expiry would reuse a rejected token", ForcedBrokerRenewal);
        runner.Test("R3-P2-2: CLI and COM reject a running mount without a verified account", UnboundMountCopy);
        runner.Test("R3-P2-3: both Web verbs distinguish team home, team root, and a mounted shared namespace", NamespaceWebLinks);
        runner.Test("R3-P2-5: a locked prefix cache must be invalidated before logout completes", LockedCacheLogout);
    }

    private static void MalformedStartupReply() => CheckOptionalStartupReply("mount-bad-config-paths");

    private static void CheckOptionalStartupReply(string scenario)
    {
        using var f = new LoginFixture(scenario);
        string fakeRoot = Path.GetDirectoryName(f.Executable)!;
        File.WriteAllText(Path.Combine(fakeRoot, "real-rclone.txt"), new DbxConfig().RclonePath);
        string settings = Path.Combine(f.Root, "sample.conf");
        File.WriteAllText(settings, "[sample]\ntype = local\n");
        f.Seed("sample:", settings);
        string drive = new[] { "T:", "U:", "V:", "W:" }.First(MountPaths.IsAvailable);
        var config = f.Controller.Config.Load() with { MountPoint = drive, NetworkMode = true, RcPort = FreeFolderTestPort() };
        f.Controller.Config.Save(config);
        bool started = false;
        try
        {
            try { _ = f.Controller.Start(); started = true; }
            finally { TestRunner.Assert(File.Exists(Path.Combine(fakeRoot, "config-paths-observed")), "The optional RC route was not reached."); }
            var state = f.Controller.State.Load()!;
            TestRunner.Assert(state.MountDevice is not null && state.MountDropboxBinding is null);
            TestRunner.Assert(f.Controller.ReadRuntime().Status is { Running: true, Ready: true });
        }
        finally
        {
            if (started) f.Controller.Stop();
            TestRunner.Assert(SpinWait.SpinUntil(() => MountPaths.IsAvailable(drive), TimeSpan.FromSeconds(10)),
                "The isolated test volume is still assigned.");
        }
    }

    private static void LastSpellingChanged()
    {
        const string decoded = "/Q?";
        const string literal = "/Q\uf03f";
        foreach (string changed in new[] { "folder", "share", "parent" })
        {
            var f = new Fixture();
            f.Api.SharedId = "sample-share";
            f.Api.MetadataPaths = [decoded];
            string path = Path.Combine(f.Config.MountPoint, literal[1..]);
            Directory.CreateDirectory(path);
            var session = new ShareFolderSession(path, f.Mount, f.Open);
            var info = session.LoadAsync(true, false, default).GetAwaiter().GetResult();
            TestRunner.Equal(decoded, info.View.Target.Path);
            var plan = FolderSharing.Plan(info.View, info.LocalPath, FolderInviteRequest.Create([Recipient], "viewer", null));
            f.Api.Calls.Clear();
            int rejected = 0;
            f.Api.BeforeReply = call =>
            {
                if (call.Endpoint != SnapshotEndpoints.Metadata || call.Body.GetProperty("path").GetString() != literal || ++rejected != 2)
                    return;
                if (changed == "folder") f.Api.FolderId = "id:replacement";
                if (changed == "share") f.Api.MetadataShareOverride = "other-share";
                if (changed == "parent") f.Api.ParentId = "other-parent";
            };
            var (result, error) = session.InviteAsync(info, plan, default).GetAwaiter().GetResult();
            TestRunner.Equal("not_sent", result.InvitationState);
            TestRunner.Equal("sharing_changed", error?.Code);
            TestRunner.Equal(2, rejected);
            TestRunner.Equal(decoded, f.Api.Calls.Last().Body.GetProperty("path").GetString());
            TestRunner.Equal(0, f.Api.Writes.Count);
        }
    }

    private static void DeviceDuringFinalRead()
    {
        foreach (bool existing in new[] { true, false })
        {
            var f = new Fixture();
            if (existing) f.Api.SharedId = "sample-share";
            var session = f.Session;
            var info = session.LoadAsync(true, false, default).GetAwaiter().GetResult();
            var plan = FolderSharing.Plan(info.View, info.LocalPath, FolderInviteRequest.Create([Recipient], "viewer", null));
            int reads = 0;
            f.Api.BeforeReply = call =>
            {
                if (call.Endpoint == SnapshotEndpoints.Metadata && ++reads == 2)
                    f.Device = @"\Device\Volume{replacement}";
            };
            var (result, error) = session.InviteAsync(info, plan, default).GetAwaiter().GetResult();
            TestRunner.Equal("not_sent", result.InvitationState);
            TestRunner.Equal(existing ? "existing" : "not_created", result.SharedFolderState);
            TestRunner.Equal("sharing_changed", error?.Code);
            TestRunner.Equal(0, f.Api.Writes.Count);
            TestRunner.Equal(2, reads);
            TestRunner.Equal(info.Mount!.Device, f.Controller.State.Load()!.MountDevice);
        }

        var waiting = new Fixture();
        waiting.Api.SharedId = "sample-share";
        var waitingSession = waiting.Session;
        var waitingInfo = waitingSession.LoadAsync(true, false, default).GetAwaiter().GetResult();
        var waitingPlan = FolderSharing.Plan(waitingInfo.View, waitingInfo.LocalPath,
            FolderInviteRequest.Create([Recipient], "viewer", null));
        using var held = new ManualResetEventSlim();
        Task? observer = null;
        int metadataReads = 0;
        waiting.Api.BeforeReply = call =>
        {
            if (call.Endpoint != SnapshotEndpoints.Metadata || ++metadataReads != 2) return;
            observer = Task.Run(() => ControlGate.Execute(() =>
            {
                held.Set();
                Thread.Sleep(500);
                return 0;
            }));
            TestRunner.Assert(held.Wait(TimeSpan.FromSeconds(5)), "The isolated observer did not acquire the control gate.");
        };
        try
        {
            var (result, error) = waitingSession.InviteAsync(waitingInfo, waitingPlan, default).GetAwaiter().GetResult();
            TestRunner.Equal<string?>(null, error?.Code);
            TestRunner.Equal("sent", result.InvitationState);
            TestRunner.Equal(Add, waiting.Api.Writes.Single().Endpoint);
        }
        finally { observer?.GetAwaiter().GetResult(); }
    }

    private static void ReplacedCachedSelection()
    {
        var f = new Fixture();
        const string parent = "Dept";
        f.Save(f.Config with { Remote = "sample:" + parent });
        string cachedChild = Path.Combine(f.LocalFolder, "cached.txt");
        File.WriteAllText(cachedChild, "old entry");
        string old = Confirm(f, Invitation());
        f.Api.FolderId = "id:replacement";
        f.Api.OverrideReply = call =>
        {
            if (call.Endpoint != SnapshotEndpoints.Continue) return null;
            TestRunner.Equal("home", call.NamespaceId);
            return call.Body.GetProperty("cursor").GetString() == SelectionCursor
                ? FakeDropbox.Reply(new { entries = new[] { FakeDropbox.Tag("file", ("path_lower", "/dept/unrelated.txt")) },
                    has_more = true, cursor = "sample-selection-next" })
                : FakeDropbox.Reply(new { entries = new[] { FakeDropbox.Tag("folder", ("path_lower", "/dept/" + RelativeFolder), ("id", f.Api.FolderId)) },
                    has_more = false, cursor = "sample-selection-end" });
        };
        var failed = f.Cli(Invitation());
        Failure(failed, 3, "selection_unverified", "選び直してください");
        TestRunner.Assert(!failed.Output.TryGetProperty("result", out _) && File.Exists(cachedChild));
        TestRunner.Assert(f.Api.Calls.Any(call => call.Endpoint == SnapshotEndpoints.Continue &&
            call.Body.GetProperty("cursor").GetString() == "sample-selection-next"));
        var gui = f.Session.LoadAsync(false, true, default).GetAwaiter().GetResult();
        TestRunner.Assert(gui.CopyPath is null && gui.PathError?.Contains("選び直してください", StringComparison.Ordinal) == true);
        AssertReadOnly(f.Api);
        TestRunner.Equal(0, f.Api.Writes.Count);

        // A new generation with a current view can confirm the replacement.
        f.Api.OverrideReply = null;
        f.Controller.State.Save(f.Controller.State.Load()! with { GenerationId = "renewed-generation", GenerationNumber = 2 });
        TestRunner.Assert(Confirm(f, Invitation()) != old);
    }

    private static void ForcedBrokerRenewal()
    {
        using var helper = new LoginFixture("folder-refresh");
        foreach (string expiry in new[] { "", ",\"expiry\":\"2099-01-01T00:00:00Z\"" })
        {
            var f = new Fixture();
            f.Save(f.Config with { RclonePath = helper.Executable });
            string original = "[sample]\ntype = dropbox\ntoken = {\"access_token\":\"" + FakeAccess +
                "\",\"refresh_token\":\"SAMPLE_UNIT9_REFRESH\"" + expiry + "}\n";
            File.WriteAllText(f.MountedConfig, original);
            f.ClientFactory = config => new DropboxFolderClient(new SnapshotTokenBroker(config), new Handler(f.Api));
            f.Api.ExpireAt = SnapshotEndpoints.Metadata;
            int aboutCalls = helper.Calls().Count(call => call.Args[0] == "about");
            var probe = f.Cli("share", "show", Folder);
            Success(probe);
            TestRunner.Assert(f.Api.Requests.SequenceEqual(new[] { SnapshotEndpoints.Account, SnapshotEndpoints.Metadata,
                SnapshotEndpoints.Account, SnapshotEndpoints.Metadata }.Select(Endpoint)));
            TestRunner.Equal(aboutCalls + 1, helper.Calls().Count(call => call.Args[0] == "about"));
            TestRunner.Equal(original, File.ReadAllText(f.MountedConfig));
            var args = helper.Calls().Last().Args;
            string scratch = args[Array.IndexOf(args, "--config") + 1];
            TestRunner.Assert(!Directory.Exists(Path.GetDirectoryName(scratch)));
            TestRunner.Assert(!args.Any(arg => arg.Contains(FakeAccess) || arg.Contains(UpdatedAccess)));
            AssertReadOnly(f.Api);
        }
    }

    private static void UnboundMountCopy()
    {
        var f = new Fixture();
        f.Api.CacheSource = "sample-original";
        string expected = Success(f.Cli("path", Folder)).GetProperty("path").GetString()!;
        var original = f.Controller.State.Load()!;
        f.Controller.State.Save(original with { MountDropboxBinding = null });
        f.Api.AccountId = "another-test-account";
        f.Api.CacheSource = "sample-replaced";
        f.Api.Requests.Clear();
        var cli = f.Cli("path", Folder);
        var options = AppOptions.Parse(["--config", f.ConfigPath, "--state", f.StatePath]);
        string? shellError = null;
        try { _ = ShellCopyServer.CopyTextAsync(options, [f.LocalFolder], f.Open, f.Controller.FolderMountInspector).GetAwaiter().GetResult(); }
        catch (FolderOperationException e) { shellError = e.Code; }
        TestRunner.Equal("mount_unverified", shellError);
        Failure(cli, 3, "mount_unverified", "マウントし直して");
        TestRunner.Equal(0, f.Api.Requests.Count);

        f.Api.CacheSource = "sample-original";
        f.Api.RejectCurrent = true;
        f.Controller.State.Save(original with { StopRequested = true, StopDisposition = StopDisposition.Clean });
        TestRunner.Equal(expected, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Equal(expected, ShellCopyServer.CopyTextAsync(options, [f.LocalFolder], f.Open, f.Controller.FolderMountInspector)
            .GetAwaiter().GetResult());
        TestRunner.Equal(0, f.Api.Requests.Count);
    }

    private static string ShellWeb(Fixture f, string path) =>
        ShellPaths.WebUrlAsync(path, f.Config, f.Mount, f.Open, default).GetAwaiter().GetResult();

    private static void NamespaceWebLinks()
    {
        var f = new Fixture();
        const string origin = "https://www.dropbox.com/home/";
        const string item = "/Reports";
        string local = Path.Combine(f.Config.MountPoint, item[1..]);
        string HomeUrl() => origin + Uri.EscapeDataString(Home) + "/Dept" + item;
        foreach (string verb in new[] { "--web-shell", "--web-copy-shell" })
        {
            var parsed = AppOptions.Parse([verb, local + "|"]);
            string selected = (parsed.WebPath ?? parsed.WebCopyPath)!;
            f.Api.NamespaceId = null;
            f.Api.OverrideReply = null;
            f.Save(f.Config with { Remote = "sample:Dept" });
            string home = ShellWeb(f, selected);
            f.Save(f.Config with { Remote = "sample:/Dept" });
            string team = ShellWeb(f, selected);
            TestRunner.Equal(HomeUrl(), home);
            TestRunner.Equal(origin + "Dept" + item, team);

            f.Api.NamespaceId = "sample-explicit";
            f.Save(f.Config);
            f.Api.OverrideReply = call => call.Endpoint switch
            {
                "sharing/get_folder_metadata" when call.NamespaceId == "team" =>
                    FakeDropbox.Reply(new { shared_folder_id = "sample-explicit", path_display = "/Shared Example" }),
                SnapshotEndpoints.Metadata when call.NamespaceId == "team" &&
                    call.Body.GetProperty("path").GetString() == "/Shared Example" =>
                    FakeDropbox.Reply(FakeDropbox.Tag("folder", ("sharing_info", new { shared_folder_id = "sample-explicit" }))),
                _ => null
            };
            TestRunner.Equal(origin + "Shared%20Example/Dept" + item, ShellWeb(f, selected));
            f.Api.OverrideReply = null;
            try { _ = ShellWeb(f, selected); }
            catch (FolderOperationException e) { TestRunner.Equal("web_location_unknown", e.Code); continue; }
            throw new InvalidOperationException("An unmounted explicit namespace was given a Web path.");
        }
        AssertReadOnly(f.Api);
    }

    private static void LockedCacheLogout()
    {
        var f = new Fixture();
        f.Api.CacheSource = "sample-source";
        Success(f.Cli("path", Folder));
        string cache = DropboxCopyPaths.CacheFile(f.ConfigPath);
        var controller = new MountController(new ConfigStore(f.ConfigPath), new StateStore(Path.Combine(f.Root, "stopped", "state.json")));
        var account = new AccountSettings(controller);
        using (var reader = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            TestRunner.Throws<InvalidOperationException>(() => account.LogoutAsync().GetAwaiter().GetResult());
            TestRunner.Equal(f.Config.Remote, controller.Config.Load().Remote);
            AssertNoPending();
        }
        TestRunner.Equal("", account.LogoutAsync().GetAwaiter().GetResult().Remote);
        TestRunner.Assert(!File.ReadAllText(cache).Contains("Dropbox", StringComparison.Ordinal));
        AssertNoPending();

        void AssertNoPending() => TestRunner.Equal(0, Directory.EnumerateFiles(Path.GetDirectoryName(cache)!,
            Path.GetFileName(cache) + ".pending-*").Count());
    }
}
