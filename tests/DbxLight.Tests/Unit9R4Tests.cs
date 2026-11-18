using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit9Tests
{
    internal static void Review4Tests(TestRunner runner)
    {
        runner.Test("R4-P2-6: a folder created through the isolated drive receives one invitation", ShareNewFolder);
        runner.Test("R4-P2-6: more than sixteen pages outside the selection do not block an invitation", ShareAfterUnrelatedChanges);
        runner.Test("R4-P2-6: retry resumes with earlier ID evidence and blocks a replacement", ResumeSelectionHistory);
        runner.Test("R4-P1-1: Dropbox path_lower matches the selected folder or ancestor exactly", CanonicalHistoryPaths);
        runner.Test("R4-P2-1: an unresolved running state blocks CLI, COM and Web copying", UncertainMountCopy);
        runner.Test("R4-P2-2: a valid optional RC reply with an overlong path still leaves the mount ready",
            () => CheckOptionalStartupReply("mount-long-config-paths"));
        runner.Test("R4-P2-3: startup and logout recover abandoned private renewal configs", AbandonedRenewal);
        runner.Test("R4-P2-4: shell URLs round-trip through CLI resolve and stat", ShellUrlRoundTrip);
        runner.Test("R4-P2-5: custom app folders cannot be mapped to the account home", CustomAppWeb);
    }

    private static HttpResponseMessage HistoryPage(string cursor, bool more, params object[] entries) =>
        FakeDropbox.Reply(new { cursor, has_more = more, entries });

    private static HttpResponseMessage FolderMetadata(string id, string? path) =>
        FakeDropbox.Reply(FakeDropbox.Tag("folder", ("id", id), ("path_lower", path)));

    private static void ShareNewFolder()
    {
        var f = new Fixture();
        using var drive = new Unit5Fixture("share-new-folder");
        drive.Start();
        var runtime = drive.State.Load()!;
        var config = f.Config with { Remote = "sample:Dept", MountPoint = drive.MountRoot, NetworkMode = drive.Config.NetworkMode,
            RcPort = drive.Config.RcPort, CacheDir = drive.Config.CacheDir };
        f.Save(config, updateState: false);
        using var rc = new FolderTestRc(runtime, config.Remote, f.MountedConfig);
        f.Controller.State.Save(runtime with { ConfigPath = f.ConfigPath, OwnerStatePath = f.StatePath,
            RcPort = rc.Port, ActiveConfig = config, MountDropboxBinding = f.MountedBinding(config), MountDropboxCursor = SelectionCursor });

        string selected = Path.Combine(drive.MountRoot, "Project");
        TestRunner.Assert(!Directory.Exists(selected));
        Directory.CreateDirectory(selected);
        TestRunner.Assert(Directory.Exists(Path.Combine(drive.RemoteRoot, "Project")));
        f.Api.FolderId = "id:created";
        f.Api.OverrideReply = call => call.Endpoint switch
        {
            SnapshotEndpoints.Continue when call.Body.GetProperty("cursor").GetString() == SelectionCursor =>
                HistoryPage("after-create", false, FakeDropbox.Tag("folder", ("path_lower", "/dept/project"), ("id", "id:created"))),
            SnapshotEndpoints.Continue => HistoryPage("after-create", false),
            SnapshotEndpoints.Metadata when call.Body.GetProperty("path").GetString() == "/dept" =>
                FolderMetadata("id:parent", "/dept"),
            _ => null
        };
        var controller = new MountController(new ConfigStore(f.ConfigPath), new StateStore(f.StatePath));
        string[] invite = Invitation(selected);
        string confirm = Success(Cli(invite)).GetProperty("confirmation").GetString()!;
        TestRunner.Equal(0, f.Api.Writes.Count);
        var result = Success(Cli([.. invite, "--execute", "--confirm", confirm]));
        TestRunner.Equal("sent", result.GetProperty("invitationState").GetString());
        TestRunner.Equal(1, result.GetProperty("invited").GetArrayLength());
        TestRunner.Assert(f.Api.Writes.Select(call => call.Endpoint).SequenceEqual(new[] { Share, Add }));
        TestRunner.Equal("id:created", f.Api.Writes[0].Body.GetProperty("path").GetString());
        TestRunner.Equal("sample-shared", f.Api.Writes[1].Body.GetProperty("shared_folder_id").GetString());
        Console.WriteLine("New folder visible on isolated WinFsp drive; fake share=1, fake invitation=1.");

        (int Exit, JsonElement Output) Cli(string[] argv)
        {
            var call = Unit3cTests.Capture(() => DbxLight.Cli.Program.RunCommand([.. argv, "--json"], controller, f.Open));
            using var json = JsonDocument.Parse(call.Output);
            return (call.Exit, json.RootElement.Clone());
        }
    }

    private static void ShareAfterUnrelatedChanges()
    {
        var f = new Fixture();
        f.Save(f.Config with { Remote = "sample:Dept" });
        f.Api.SharedId = "sample-share";
        const int pages = 21;
        int unrelated = 0;
        f.Api.OverrideReply = call =>
        {
            if (call.Endpoint != SnapshotEndpoints.Continue) return null;
            string cursor = call.Body.GetProperty("cursor").GetString()!;
            if (cursor == "after-bulk") return HistoryPage(cursor, false);
            int page = cursor == SelectionCursor ? 0 : int.Parse(cursor);
            unrelated++;
            return HistoryPage(page == pages - 1 ? "after-bulk" : (page + 1).ToString(),
                page < pages - 1, FakeDropbox.Tag("deleted", ("path_lower", "/outside/" + page)));
        };
        var result = Success(f.Confirmed(Invitation()));
        TestRunner.Equal("sent", result.GetProperty("invitationState").GetString());
        TestRunner.Equal(pages, unrelated);
        TestRunner.Equal(Add, f.Api.Writes.Single().Endpoint);
        Console.WriteLine($"Unrelated history pages={unrelated}; fake invitation=1.");
    }

    private static void ResumeSelectionHistory()
    {
        var f = new Fixture();
        f.Save(f.Config with { Remote = "sample:Dept" });
        const string continued = "after-first-page";
        string path = "/dept/" + RelativeFolder;
        bool retry = false;
        int starts = 0, resumed = 0;
        f.Api.OverrideReply = call =>
        {
            if (call.Endpoint != SnapshotEndpoints.Continue) return null;
            if (call.Body.GetProperty("cursor").GetString() == SelectionCursor)
            {
                starts++;
                return HistoryPage(continued, true, FakeDropbox.Tag("folder", ("path_lower", path), ("id", "id:sample")));
            }
            resumed++;
            return retry ? HistoryPage("latest", false, FakeDropbox.Tag("folder", ("path_lower", path), ("id", "id:replacement"))) :
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };
        Failure(f.Cli(Invitation()), 4, "network_error", "");
        f.Api.FolderId = "id:replacement";
        retry = true;
        Failure(f.Cli(Invitation()), 3, "selection_unverified", "");
        Failure(f.Cli(Invitation()), 3, "selection_unverified", "");
        TestRunner.Equal(1, starts);
        TestRunner.Equal(2, resumed);
        AssertReadOnly(f.Api);

        var parent = new Fixture();
        parent.Save(parent.Config with { Remote = "sample:Dept" });
        string parentId = "id:parent";
        parent.Api.OverrideReply = call => call.Endpoint switch
        {
            SnapshotEndpoints.Continue when call.Body.GetProperty("cursor").GetString() == SelectionCursor =>
                HistoryPage("first-parent", false, FakeDropbox.Tag("folder", ("path_lower", path), ("id", "id:sample"))),
            SnapshotEndpoints.Continue => HistoryPage("first-parent", false),
            SnapshotEndpoints.Metadata when call.Body.GetProperty("path").GetString() == "/dept" =>
                FolderMetadata(parentId, "/dept"),
            _ => null
        };
        _ = Confirm(parent, Invitation());
        parentId = "id:replacement-parent";
        Failure(parent.Cli(Invitation()), 3, "selection_unverified", "");
        AssertReadOnly(parent.Api);
    }

    private static void CanonicalHistoryPaths()
    {
        foreach (string lower in new[] { "/i", "/i\u0307" })
        {
            foreach (bool ancestor in new[] { false, true })
            {
                var f = new Fixture();
                string selected = ancestor ? "/\u0130/Project" : "/\u0130";
                string canonical = ancestor ? lower + "/project" : lower;
                string local = Path.Combine(f.Config.MountPoint, selected[1..].Replace('/', '\\'));
                Directory.CreateDirectory(local);
                f.Api.OverrideReply = call => call.Endpoint switch
                {
                    SnapshotEndpoints.Metadata when call.Body.GetProperty("path").GetString() == selected =>
                        FolderMetadata("id:selected", canonical),
                    SnapshotEndpoints.Continue => HistoryPage("after-unicode", false,
                        FakeDropbox.Tag("deleted", ("path_lower", lower))),
                    _ => null
                };
                var result = f.Cli(Invitation(local));
                Failure(result, 3, "selection_unverified", "");
                TestRunner.Assert(!result.Output.TryGetProperty("result", out _));
                AssertReadOnly(f.Api);
            }
        }
        var missing = new Fixture();
        missing.Api.OverrideReply = call => call.Endpoint == SnapshotEndpoints.Metadata
            ? FolderMetadata("id:selected", null) : null;
        Failure(missing.Cli(Invitation()), 3, "selection_unverified", "");
        AssertReadOnly(missing.Api);
    }

    private static void UncertainMountCopy()
    {
        var f = new Fixture();
        var state = f.Controller.State.Load()!;
        f.Controller.State.Save(state with { MountDropboxBinding = null });
        f.Controller.FolderMountInspector = _ => null;
        f.Api.AccountId = "different-fake-account";
        f.Api.TeamAccount = false;
        var options = AppOptions.Parse(["--config", f.ConfigPath, "--state", f.StatePath]);
        Failure(f.Cli("path", Folder), 3, "mount_unverified", "");
        ExpectFolderError("mount_unverified", () =>
            ShellCopyServer.CopyTextAsync(options, [f.LocalFolder], f.Open, f.Controller.FolderMountInspector).GetAwaiter().GetResult());
        ExpectFolderError("mount_unverified", () => ShellWeb(f, f.LocalFolder));
        TestRunner.Equal(0, f.Api.Requests.Count);

        var stopped = new Fixture();
        var clean = stopped.Controller.State.Load()! with { StopRequested = true, StopDisposition = StopDisposition.Clean };
        stopped.Controller.State.Save(clean);
        stopped.Api.BeforeReply = call =>
        {
            if (call.Endpoint == SnapshotEndpoints.Account)
                stopped.Controller.State.Save(clean with { StopRequested = false, StopDisposition = StopDisposition.Running,
                    MountDropboxBinding = null });
        };
        Failure(stopped.Cli("path", Folder), 3, "mount_unverified", "");
        stopped.Controller.State.Save(clean);
        ExpectFolderError("mount_unverified", () => ShellWeb(stopped, stopped.LocalFolder));
        AssertReadOnly(stopped.Api);
    }

    private static void AbandonedRenewal()
    {
        using var f = new LoginFixture("folder-refresh-hold");
        string original = Path.Combine(f.Root, "sample.conf");
        File.WriteAllText(original, FakeConfig(FakeAccess));
        f.Seed("sample:", original);
        var config = f.Controller.Config.Load();
        f.Controller.Config.Save(config with { MountPoint = Path.Combine(f.Root, "unused-mount"), NetworkMode = false });
        var recovered = new List<bool>();
        bool inPrivateRoot = true;
        foreach (bool logout in new[] { false, true })
        {
            string signal = Path.Combine(Path.GetDirectoryName(f.Executable)!, "renewal-ready");
            File.Delete(signal);
            int about = f.Calls().Length;
            string? scratch = null;
            try
            {
                using var worker = Process.Start(RcloneArguments.ProcessInfo(Environment.ProcessPath!,
                    ["--unit9-renew-child", f.Controller.Config.PathName]))!;
                try
                {
                    TestRunner.Assert(SpinWait.SpinUntil(() => File.Exists(signal) || worker.HasExited, TimeSpan.FromSeconds(10)),
                        "The fake renewal did not reach its staging point.");
                    var call = f.Calls().Skip(about).Single(c => c.Args[0] == "about");
                    scratch = call.Args[Array.IndexOf(call.Args, "--config") + 1];
                    TestRunner.Assert(File.Exists(scratch) && File.Exists(scratch + ".staged"));
                    inPrivateRoot &= MountPaths.IsSameOrBelow(scratch, DbxConfig.LocalRoot);
                    PrivateStateFiles.ValidateExisting(scratch);
                    PrivateStateFiles.ValidateExisting(scratch + ".staged");
                    if (!logout)
                    {
                        _ = f.Controller.Config.Load();
                        TestRunner.Assert(File.Exists(scratch) && File.Exists(scratch + ".staged"), "Recovery disturbed an active renewal.");
                    }
                }
                finally
                {
                    if (!worker.HasExited) worker.Kill();
                    TestRunner.Assert(worker.WaitForExit(10000));
                    f.AssertExited();
                }
                if (logout) new AccountSettings(f.Controller).LogoutAsync().GetAwaiter().GetResult();
                else _ = f.Controller.Config.Load();
                recovered.Add(!Directory.Exists(Path.GetDirectoryName(scratch)));
            }
            finally
            {
                // The baseline test also isolates and removes the synthetic scratch.
                if (scratch is not null && Directory.Exists(Path.GetDirectoryName(scratch)))
                {
                    File.Delete(scratch);
                    File.Delete(scratch + ".staged");
                    File.Delete(scratch + ".login-lock");
                    Directory.Delete(Path.GetDirectoryName(scratch)!);
                }
            }
        }
        TestRunner.Equal(FakeConfig(FakeAccess), File.ReadAllText(original));
        Console.WriteLine($"Renewal private root={inPrivateRoot}, active lease preserved, startup recovery={recovered[0]}, logout recovery={recovered[1]}.");
        TestRunner.Assert(inPrivateRoot && recovered.All(done => done),
            "The private placement and both abandoned-file recovery paths must succeed.");
    }

    internal static int RenewalChild(string configPath)
    {
        var broker = new SnapshotTokenBroker(DropboxFolderClient.ReadConfiguration(configPath));
        var expired = broker.ReadAsync(default).GetAwaiter().GetResult();
        broker.RenewRejectedAsync(expired, default).GetAwaiter().GetResult();
        return 0;
    }

    private static void ShellUrlRoundTrip()
    {
        using var f = new LoginFixture("folder-web-roundtrip");
        string settings = Path.Combine(f.Root, "sample.conf");
        File.WriteAllText(settings, FakeConfig(FakeAccess));
        f.Seed("sample:Dept", settings);
        var api = new FakeDropbox();
        string selected = Path.Combine(f.Root, "unused-mount", "Reports");
        var mount = new FolderShareMount(f.Controller.Config.PathName, f.Controller.State.PathName);
        foreach (string remote in new[] { "sample:Dept", "sample:", "sample:/Dept" })
        {
            var config = f.Controller.Config.Load() with { Remote = remote, MountPoint = Path.GetDirectoryName(selected)!, NetworkMode = false };
            f.Controller.Config.Save(config);
            string url = ShellPaths.WebUrlAsync(selected, config, mount, Open, default).GetAwaiter().GetResult();
            var resolved = Command("resolve", url);
            TestRunner.Equal("Reports", resolved.GetProperty("remotePath").GetString());
            TestRunner.Equal(selected, resolved.GetProperty("localPath").GetString());
            TestRunner.Equal(url, resolved.GetProperty("webUrl").GetString());
            foreach (string path in new[] { url, "Reports" })
            {
                var stat = Command("stat", path);
                TestRunner.Equal("Reports", stat.GetProperty("entry").GetProperty("path").GetString());
                TestRunner.Equal(url, stat.GetProperty("webUrl").GetString());
            }
        }
        AssertReadOnly(api);

        DropboxFolderClient Open(DbxConfig config) => Client(config, api);

        JsonElement Command(string verb, string value)
        {
            var result = Unit3cTests.Capture(() => DbxLight.Cli.Program.RunCommand([verb, value, "--json"], f.Controller, Open));
            TestRunner.Equal(0, result.Exit);
            using var json = JsonDocument.Parse(result.Output);
            return json.RootElement.Clone();
        }
    }

    private static void CustomAppWeb()
    {
        var f = new Fixture();
        f.Api.TeamAccount = false;
        f.Api.CustomApp = true;
        f.Save(f.Config);
        foreach (string verb in new[] { "--web-shell", "--web-copy-shell" })
        {
            var options = AppOptions.Parse([verb, f.LocalFolder + "|"]);
            ExpectFolderError("web_location_unknown", () => ShellWeb(f, (options.WebPath ?? options.WebCopyPath)!));
        }
        TestRunner.Assert(f.Api.Calls.All(call => call.Endpoint == SnapshotEndpoints.Account));
        AssertReadOnly(f.Api);
    }

    private static void ExpectFolderError(string code, Func<string> work)
    {
        try { _ = work(); }
        catch (FolderOperationException error) { TestRunner.Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }

    internal static DropboxFolderClient FakePersonalWebClient(DbxConfig config) =>
        new(new SnapshotTokenBroker(config), new Handler(new FakeDropbox { TeamAccount = false }), allowRenewal: false);
}
