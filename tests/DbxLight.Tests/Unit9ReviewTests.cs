using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit9Tests
{
    private static void ReviewTests(TestRunner runner)
    {
        runner.Test("9r P1-1: separate CLI processes reject a replacement and accept the confirmed folder", SeparateConfirmations);
        runner.Test("9r P1-1/L-3: missing confirmation, change of plan, and argument failure JSON", ConfirmationArguments);
        runner.Test("9r P1-2: CLI state owner, stopped mount, active connection, device and generation", MountBinding);
        runner.Test("9r P1-2/L-3: GUI checks before and after confirmation; CLI reports progress after acceptance", MountDuringConfirmation);
        runner.Test("9r P1-3: async job completes after a folder move or sharing ID change", TargetAfterJob);
        runner.Test("9r P1-4: copy prefix and GUI verification follow the account and namespace", PrefixBinding);
        runner.Test("9r P1-4: login, existing remote, logout and reconfiguration clear the bound prefix", LoginSettings);
        runner.Test("9r P2-1: hidden, non-folder and reparse selections stop before writes", SelectVisibleFolder);
        runner.Test("9r P2-2: WinFsp and rclone spellings are shared by copy and sharing, ambiguity stops both", Names);
        runner.Test("9r P2-4: a child can be shared and its parent can never receive the invitation", ShareChild);
        runner.Test("9r P2-5: verified auto prefix survives offline copies until the connection changes", OfflinePrefix);
        runner.Test("9r P2-6 / 9r2 P2-3: expiry, fresh token objects, changed identity and no write retry", Refresh);
        runner.Test("9r P2-8: unknown custom app scope needs a bound manual prefix", CustomApp);
        runner.Test("9r P2-9: preference changes save with a running mount and mixed config uses the lifecycle", Preferences);
        runner.Test("9r P2-10: CLI reports the inherited traverse and no-access levels separately", InheritedAccess);
        runner.Test("9r L-1: home search follows the cursor and rejects a repeated page", HomeOnLaterPage);
        runner.Test("9r L-2: COM stops on selection count and total length before copying", ShellLimits);
        runner.Test("9r L-4: CLI invite documentation has two table columns and the confirmation recipe", CliDocumentation);
        runner.Test("9r L-5: snapshot accepts an empty admin option and rejects any nonempty value", SnapshotTests.AdminOverride);
    }

    private static string Endpoint(string route) => "/2/" + route;

    private static readonly HashSet<string> ReadRoutes = new(new[]
    {
        SnapshotEndpoints.Account, SnapshotEndpoints.Metadata, SnapshotEndpoints.List, SnapshotEndpoints.Continue,
        "sharing/get_folder_metadata", "sharing/list_folder_members", "sharing/list_folder_members/continue",
        "files/list_folder/get_latest_cursor"
    }.Select(Endpoint), StringComparer.Ordinal);

    private static void AssertReadOnly(FakeDropbox api) =>
        TestRunner.Assert(api.Requests.Count > 0 && api.Requests.All(ReadRoutes.Contains), "The transport received an unexpected route.");

    private static string[] Invitation(string path = Folder) =>
        ["share", "invite", path, "--to", Recipient, "--access", "viewer"];

    private static string Confirm(Fixture f, params string[] args) =>
        Success(f.Cli(args)).GetProperty("confirmation").GetString()!;

    internal static int InviteChild(string[] args)
    {
        var api = new FakeDropbox { FolderId = args[3] };
        var controller = new MountController(new ConfigStore(args[0]), new StateStore(args[1]))
        {
            FolderMountInspector = state => new(state.MountDevice!, state.ActiveConfig.RcloneConfigPath!)
        };
        try { return DbxLight.Cli.Program.RunCommand(args[4..], controller, config => Client(config, api)); }
        finally { File.WriteAllLines(args[2], api.Requests, new UTF8Encoding(false)); }
    }

    private static void SeparateConfirmations()
    {
        var f = new Fixture();
        var (preview, reads) = Run("preview", "id:sample", [.. Invitation(), "--json"]);
        string confirmation = Success(preview).GetProperty("confirmation").GetString()!;
        TestRunner.Assert(reads.Length > 0 && reads.All(ReadRoutes.Contains));
        string[] execute = [.. Invitation(), "--execute", "--confirm", confirmation];
        var (replaced, requests) = Run("replacement", "id:replacement", execute);
        Failure(replaced, 3, "confirmation_changed", "確認値");
        TestRunner.Assert(requests.Length > 0 && requests.All(ReadRoutes.Contains));
        var (valid, sends) = Run("confirmed", "id:sample", execute);
        TestRunner.Equal("sent", Success(valid).GetProperty("invitationState").GetString());
        TestRunner.Assert(sends.Where(route => !ReadRoutes.Contains(route)).SequenceEqual(new[] { Share, Add }.Select(Endpoint)));

        ((int Exit, JsonElement Output) result, string[] routes) Run(string slug, string folderId, string[] arguments)
        {
            string routes = Path.Combine(f.Root, slug + ".routes");
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "--unit9-invite-child", f.ConfigPath, f.StatePath, routes, folderId }
                .Concat(f.LocalArguments(arguments)))
                start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000))
            {
                child.Kill();
                throw new TimeoutException("The isolated CLI did not exit.");
            }
            TestRunner.Equal("", stderr.GetAwaiter().GetResult());
            using var document = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            return ((child.ExitCode, document.RootElement.Clone()), File.ReadAllLines(routes));
        }
    }

    private static void ConfirmationArguments()
    {
        var f = new Fixture();
        string[] args = [.. Invitation(), "--to", SecondRecipient, "--message", "sample note"];
        var missing = Failure(f.Cli([.. args, "--execute"]), 2, "confirmation_required", "--confirm");
        TestRunner.Equal(JsonValueKind.Undefined, missing.ValueKind);
        var bad = Failure(f.Cli([.. args, "--unknown", "--execute", "--confirm", "sample"]),
            2, "usage", "使い方");
        TestRunner.Equal(JsonValueKind.Undefined, bad.ValueKind);
        TestRunner.Equal(0, f.Api.Requests.Count);
        string confirmation = Confirm(f, args);
        foreach (string[] changed in new[]
        {
            new[] { "share", "invite", Folder, "--to", SecondRecipient, "--to", Recipient, "--access", "viewer", "--message", "sample note" },
            new[] { "share", "invite", Folder, "--to", Recipient, "--to", SecondRecipient, "--access", "editor", "--message", "sample note" },
            new[] { "share", "invite", Folder, "--to", Recipient, "--to", SecondRecipient, "--access", "viewer", "--message", "changed" }
        })
            Failure(f.Cli([.. changed, "--execute", "--confirm", confirmation]), 3, "confirmation_changed", "確認値");
        f.Api.NamespaceId = "another";
        Failure(f.Cli([.. args, "--execute", "--confirm", confirmation]), 3, "mount_unverified", "稼働中");
        f.Api.NamespaceId = null;
        f.Api.AccountId = "another-account";
        Failure(f.Cli([.. args, "--execute", "--confirm", confirmation]), 3, "mount_unverified", "稼働中");
        f.Api.AccountId = "sample-account";
        f.Api.SharedId = "newly-shared";
        Failure(f.Cli([.. args, "--execute", "--confirm", confirmation]), 3, "confirmation_changed", "確認値");
        AssertReadOnly(f.Api);
    }

    private static void MountBinding()
    {
        var f = new Fixture();
        var baseline = f.Controller.State.Load()!;
        string confirmation = Confirm(f, Invitation());
        var badState = Path.Combine(f.Root, "wrong-state", "state.json");
        new StateStore(badState).Save(baseline);
        Failure(f.Cli([.. Invitation(), "--state", badState]), 3, "mount_unverified", "稼働中");
        f.Controller.State.Save(baseline with { StopRequested = true, StopDisposition = StopDisposition.Clean });
        Failure(f.Cli(Invitation()), 3, "mount_unverified", "稼働中");
        f.Controller.State.Save(baseline with { ConfigPath = badState });
        Failure(f.Cli(Invitation()), 3, "mount_unverified", "稼働中");
        f.Controller.State.Save(baseline);
        f.Device = @"\Device\Volume{reassigned}";
        Failure(f.Cli([.. Invitation(), "--execute", "--confirm", confirmation]), 3, "mount_unverified", "稼働中");
        f.Device = baseline.MountDevice!;
        f.Controller.State.Save(baseline with { GenerationId = "next-generation", GenerationNumber = 2 });
        Failure(f.Cli([.. Invitation(), "--execute", "--confirm", confirmation]), 3, "confirmation_changed", "確認値");

        var saved = f.Config;
        f.Save(saved with { RcloneConfigPath = badState });
        Failure(f.Cli(Invitation()), 3, "mount_unverified", "稼働中");
        f.Save(saved with { RcloneConfigPath = null });
        string? oldDefault = Environment.GetEnvironmentVariable("RCLONE_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", badState);
            _ = Confirm(f, Invitation());
            TestRunner.Equal(f.MountedConfig, f.LastClientConfig);
        }
        finally { Environment.SetEnvironmentVariable("RCLONE_CONFIG", oldDefault); }

        f.Save(saved with { Remote = "other:Dept" }, updateState: false);
        Failure(f.Cli(Invitation()), 3, "mount_unverified", "稼働中");
        var showed = Success(f.Cli("share", "show", Folder)).GetProperty("connection");
        TestRunner.Equal(f.Api.DisplayName, showed.GetProperty("displayName").GetString());
        TestRunner.Equal("other:Dept", showed.GetProperty("remote").GetString());
        TestRunner.Assert(!showed.GetProperty("mountVerified").GetBoolean());
        AssertReadOnly(f.Api);
    }

    private static void MountDuringConfirmation()
    {
        var f = new Fixture();
        var session = f.Session;
        var info = session.LoadAsync(false, true, default).GetAwaiter().GetResult();
        TestRunner.Assert(info.Mount is not null && info.CopyPath is not null);
        var plan = FolderSharing.Plan(info.View, info.CopyPath!, FolderInviteRequest.Create([Recipient], "viewer", null));
        f.Save(f.Config with { Remote = "other:Dept" }, updateState: false);
        var (result, error) = session.InviteAsync(info, plan, default).GetAwaiter().GetResult();
        TestRunner.Equal("sharing_changed", error?.Code);
        TestRunner.Equal("not_sent", result.InvitationState);
        var read = session.LoadAsync(false, true, default).GetAwaiter().GetResult();
        TestRunner.Assert(read.Mount is null && read.View.Target.Path.StartsWith("/Dept/"));
        AssertReadOnly(f.Api);

        var cli = new Fixture();
        string confirmation = Confirm(cli, Invitation());
        int reads = 0;
        cli.Api.BeforeReply = call =>
        {
            if (call.Endpoint == SnapshotEndpoints.Metadata && ++reads == 3)
                cli.Save(cli.Config with { Remote = "other:" }, updateState: false);
        };
        var stopped = Failure(cli.Cli([.. Invitation(), "--execute", "--confirm", confirmation]), 3, "sharing_changed", "招待は送っていません");
        TestRunner.Equal("not_sent", stopped.GetProperty("invitationState").GetString());
        AssertReadOnly(cli.Api);
    }

    private static void TargetAfterJob()
    {
        foreach (bool moved in new[] { true, false })
        {
            var f = new Fixture();
            f.Save(f.Config with { Remote = "sample:Dept" });
            f.Api.Async = true;
            f.Api.MovedAfterJob = moved;
            f.Api.DifferentShareAfterJob = moved ? null : "different-share";
            var progress = Failure(Invite(f), 3, "sharing_changed", "共有フォルダになりました。招待は送っていません");
            TestRunner.Equal("created", progress.GetProperty("sharedFolderState").GetString());
            TestRunner.Equal("not_sent", progress.GetProperty("invitationState").GetString());
            TestRunner.Equal(Share, f.Api.Writes.Single().Endpoint);
            var metadata = f.Api.Calls.Last();
            TestRunner.Equal(SnapshotEndpoints.Metadata, metadata.Endpoint);
            TestRunner.Equal("home", metadata.NamespaceId);
            TestRunner.Equal("/Dept/" + RelativeFolder, metadata.Body.GetProperty("path").GetString());
        }
    }

    private static void PrefixBinding() => FormTests.Sta(() =>
    {
        var f = new Fixture();
        const string entered = @"\見本の手入力";
        Success(f.Cli("set", "copyPathPrefix", entered));
        var saved = f.Controller.Config.Load();
        var session = f.Session;
        TestRunner.Equal(entered + "\\" + RelativeFolder, session.LoadAsync(false, true, default).GetAwaiter().GetResult().CopyPath);
        f.Api.AccountId = "other-account";
        f.Api.DisplayName = "別のアカウント";
        var other = session.LoadAsync(false, true, default).GetAwaiter().GetResult();
        TestRunner.Equal(Prefix + "\\" + RelativeFolder, other.CopyPath);
        TestRunner.Equal(f.Api.DisplayName, other.View.Target.Account.DisplayName);
        f.Api.AccountId = "sample-account";
        f.Api.NamespaceId = "team";
        var team = session.LoadAsync(false, true, default).GetAwaiter().GetResult();
        TestRunner.Assert(team.View.Target.Account.TeamSpace);
        TestRunner.Equal(@"\サンプル社 Dropbox\" + RelativeFolder, team.CopyPath);

        var options = new AppOptions(f.ConfigPath, f.StatePath);
        using var form = new SettingsForm(saved, new RegistrationService(RegistrationService.TestHive), null, options, _ => Task.CompletedTask);
        var field = (TextBox)form.Controls.Find("copyPathPrefix", true).Single();
        TestRunner.Equal(entered, field.Text);
        form.UpdateSnapshot(new(new(), [], [], false, null), DropboxCopyPaths.ClearBinding(saved) with { Remote = "other:" });
        TestRunner.Equal("", field.Text);
        AssertReadOnly(f.Api);
    });

    private static void LoginSettings()
    {
        using var f = new LoginFixture();
        f.Seed("dbxlight:", f.Conf);
        var store = f.Controller.Config;
        DbxConfig Bound(DbxConfig config) => config with { CopyPathPrefix = Prefix, CopyPathBinding = "sample-binding" };
        store.Save(Bound(store.Load()));
        using (var session = f.AtLogin())
        {
            TestRunner.Assert(session.AuthorizeAsync(true).GetAwaiter().GetResult().Succeeded);
            TestRunner.Assert(session.Draft.CopyPathPrefix is null && session.Draft.CopyPathBinding is null);
            session.SelectScopeAsync(true).GetAwaiter().GetResult();
            TestRunner.Assert(session.Next());
            session.SetOptions("T:", "1h");
            TestRunner.Assert(session.Next());
            TestRunner.Assert(session.Commit().CopyPathPrefix is null);
        }
        store.Save(Bound(store.Load()));
        var loggedOut = new AccountSettings(f.Controller).LogoutAsync().GetAwaiter().GetResult();
        TestRunner.Assert(loggedOut.Remote.Length == 0 && loggedOut.CopyPathPrefix is null && loggedOut.CopyPathBinding is null);

        store.Save(Bound(loggedOut with { Remote = "previous:" }));
        using var existing = f.AtLogin();
        existing.UseExistingRemoteAsync("sample-existing:").GetAwaiter().GetResult();
        existing.SelectScopeAsync(false).GetAwaiter().GetResult();
        TestRunner.Assert(existing.Next());
        existing.SetOptions("T:", "1h");
        TestRunner.Assert(existing.Next());
        var newRemote = existing.Commit();
        TestRunner.Assert(newRemote.Remote == "sample-existing:" && newRemote.CopyPathPrefix is null && newRemote.CopyPathBinding is null);
    }

    private static void SelectVisibleFolder()
    {
        var f = new Fixture();
        var config = f.Config;
        string confirmation = Confirm(f, Invitation());
        f.Save(config with { HiddenPaths = [RelativeFolder] });
        Failure(f.Cli([.. Invitation(), "--execute", "--confirm", confirmation]), 3, "not_visible", "表示されるフォルダ");
        f.Save(config);
        File.WriteAllText(Path.Combine(config.MountPoint, "file.txt"), "sample", new UTF8Encoding(false));
        Failure(f.Cli(Invitation(Path.Combine(config.MountPoint, "file.txt"))), 3, "not_visible", "表示されるフォルダ");
        string outside = Path.Combine(f.Root, "outside"), junction = Path.Combine(config.MountPoint, "junction");
        Directory.CreateDirectory(Path.Combine(outside, "child"));
        using var helper = Process.Start(RcloneArguments.ProcessInfo("cmd.exe", ["/d", "/c", "mklink", "/J", junction, outside]))!;
        TestRunner.Assert(helper.WaitForExit(5000) && helper.ExitCode == 0);
        Failure(f.Cli(Invitation(Path.Combine(junction, "child"))), 3, "not_visible", "表示されるフォルダ");
        AssertReadOnly(f.Api);
    }

    private static void Names()
    {
        var f = new Fixture();
        f.Save(f.Config with { Remote = "sample:Dept" });
        foreach (var (local, cloud) in new[] { ("Q\uf03f", "Q?"), ("Q？", "Q？"), ("Q　", "Q　"), ("Q\u201b\u2401", "Q\u2401") })
        {
            string selected = Path.Combine(f.Config.MountPoint, local);
            Directory.CreateDirectory(selected);
            f.Api.MissingPath = "/Dept/Q\uf03f";
            f.Api.Calls.Clear();
            TestRunner.Equal(Prefix + @"\Dept\" + cloud, Success(f.Cli("path", selected)).GetProperty("path").GetString());
            _ = Confirm(f, Invitation(selected));
            TestRunner.Assert(f.Api.Calls.Any(call => call.Endpoint == SnapshotEndpoints.Metadata &&
                call.Body.GetProperty("path").GetString() == "/Dept/" + cloud));
        }
        string candidate = Path.Combine(f.Config.MountPoint, "Q\uf03f");
        f.Api.MissingPath = null;
        Failure(f.Cli("path", candidate), 2, "ambiguous_name", "名前を特定");
        Failure(f.Cli(Invitation(candidate)), 2, "ambiguous_name", "名前を特定");
        string mixed = Path.Combine(f.Config.MountPoint, "Q\uf03f\uf03f");
        Directory.CreateDirectory(mixed);
        f.Api.MissingPath = "/Dept/Q\uf03f\uf03f";
        Failure(f.Cli(Invitation(mixed)), 2, "ambiguous_name", "名前を特定");
        TestRunner.Equal("/Dept/Q\uf03f?", f.Api.Calls.Last().Body.GetProperty("path").GetString());
        string unsupported = Path.Combine(f.Config.MountPoint, "Q\uf005");
        Failure(f.Cli("path", unsupported), 2, "ambiguous_name", "名前を特定");
        AssertReadOnly(f.Api);
    }

    private static void ShareChild()
    {
        var f = new Fixture(); f.Api.ParentId = "parent";
        var result = Success(Invite(f));
        TestRunner.Equal("sent", result.GetProperty("invitationState").GetString());
        TestRunner.Equal("id:sample", f.Api.Writes[0].Body.GetProperty("path").GetString());
        TestRunner.Equal("sample-shared", f.Api.Writes[1].Body.GetProperty("shared_folder_id").GetString());

        var wrong = new Fixture(); wrong.Api.ParentId = "parent"; wrong.Api.ParentAsShared = true;
        Failure(Invite(wrong), 4, "invalid_response", "招待は送っていません");
        TestRunner.Equal(Share, wrong.Api.Writes.Single().Endpoint);
    }

    private static void OfflinePrefix()
    {
        var f = new Fixture();
        f.Api.CacheSource = "source-a";
        TestRunner.Equal(Prefix + "\\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Equal(2, f.Api.Requests.Count);
        f.Api.Requests.Clear();
        f.Api.RejectCurrent = true;
        Success(f.Cli("path", Folder));
        TestRunner.Equal(0, f.Api.Requests.Count);
        f.Api.CacheSource = "source-b";
        f.Api.RejectCurrent = false;
        f.Api.TeamAccount = false;
        f.Api.AccountId = "sample-new";
        f.Save(f.Config);
        TestRunner.Equal(@"\Dropbox\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Equal(1, f.Api.Requests.Count);
        f.Save(f.Config with { Remote = "sample:dept" });
        Success(f.Cli("path", Folder));
        TestRunner.Equal(2, f.Api.Requests.Count);
    }

    private static void Refresh()
    {
        var expired = new Fixture();
        expired.Api.Credential = new(FakeAccess, DateTimeOffset.MinValue);
        TestRunner.Equal("sent", Success(Invite(expired)).GetProperty("invitationState").GetString());
        TestRunner.Equal(1, expired.Api.Renewals);
        var stale = new Fixture(); stale.Api.ExpireAt = SnapshotEndpoints.Metadata;
        Success(stale.Cli("share", "show", Folder));
        TestRunner.Equal(1, stale.Api.Renewals);
        TestRunner.Assert(stale.Api.Requests.SequenceEqual(new[] { SnapshotEndpoints.Account, SnapshotEndpoints.Metadata,
            SnapshotEndpoints.Account, SnapshotEndpoints.Metadata }.Select(Endpoint)));
        var changed = new Fixture(); changed.Api.ExpireAt = SnapshotEndpoints.Metadata; changed.Api.AccountAfterRenewal = "other";
        Failure(changed.Cli(Invitation()), 3, "sharing_changed", "確認し直して");
        AssertReadOnly(changed.Api);

        var unavailable = new Fixture(); unavailable.Api.RenewalFails = true;
        unavailable.Api.Credential = new(FakeAccess, DateTimeOffset.MinValue);
        Failure(unavailable.Cli("share", "show", Folder), 3, "auth_refresh_unavailable", "接続を確認");
        TestRunner.Equal(0, unavailable.Api.Requests.Count);
        var revoked = new Fixture(); revoked.Api.RejectCurrent = true;
        Failure(revoked.Cli("share", "show", Folder), 3, "auth_unavailable", "もう一度ログイン");
        TestRunner.Equal(0, revoked.Api.Renewals);

        var atWrite = new Fixture(); atWrite.Api.ExpireAt = Add;
        var progress = Failure(Invite(atWrite), 3, "expired_access_token", "招待は送っていません");
        TestRunner.Equal("not_sent", progress.GetProperty("invitationState").GetString());
        TestRunner.Equal(1, atWrite.Api.Requests.Count(route => route == Endpoint(Add)));
        TestRunner.Equal(0, atWrite.Api.Renewals);
    }

    private static void CustomApp()
    {
        var f = new Fixture(); f.Api.TeamAccount = false; f.Api.CustomApp = true;
        f.Save(f.Config);
        Failure(f.Cli("path", Folder), 3, "copy_path_prefix_unknown", "コピーするパスの頭");
        Success(f.Cli("set", "copyPathPrefix", @"\Dropbox\Apps\Sample"));
        TestRunner.Equal(@"\Dropbox\Apps\Sample\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        AssertReadOnly(f.Api);

        string fake = Path.Combine(f.Root, "sample.conf");
        File.WriteAllText(fake, "[sample]\ntype = dropbox\nclient_id = sample-client\ntoken = {\"access_token\":\"" + FakeAccess + "\"}\n");
        var broker = new SnapshotTokenBroker(f.Config with { RcloneConfigPath = fake });
        _ = broker.ReadAsync(default).GetAwaiter().GetResult();
        TestRunner.Assert(broker.Remote.UsesCustomApp);
    }

    private static void Preferences()
    {
        var f = new Fixture();
        var before = File.ReadAllBytes(f.StatePath);
        Success(f.Cli("set", "copyPathPrefix", @"\Dropbox\Sample"));
        TestRunner.Assert(before.SequenceEqual(File.ReadAllBytes(f.StatePath)));
        var saved = f.Controller.Config.Load();
        TestRunner.Equal(@"\Dropbox\Sample", saved.CopyPathPrefix);
        var mixed = saved with { UpdateCheck = !saved.UpdateCheck, Retention = "3h" };
        TestRunner.Assert(f.Controller.TrySavePreferences(saved, mixed) is null);
        TestRunner.Equal(saved.Retention, f.Controller.Config.Load().Retention);
        TestRunner.Throws<InvalidOperationException>(() => f.Controller.UpdateConfig(_ => mixed));
        TestRunner.Assert(before.SequenceEqual(File.ReadAllBytes(f.StatePath)));
    }

    private static void InheritedAccess()
    {
        var f = new Fixture(); f.Api.ParentId = "parent"; f.Api.InheritedAccess = true;
        var (exit, output, error) = f.Run("share", "show", Folder);
        TestRunner.Equal(0, exit);
        TestRunner.Equal("", error);
        TestRunner.Assert(output.Contains("配下の一部にアクセス可") && output.Contains("この場所へのアクセスなし"));
        var members = Success(f.Cli("share", "show", Folder)).GetProperty("members");
        TestRunner.Equal("traverse", members[0].GetProperty("access").GetString());
        TestRunner.Equal("no_access", members[1].GetProperty("access").GetString());
        AssertReadOnly(f.Api);
    }

    private static void HomeOnLaterPage()
    {
        var f = new Fixture(); f.Api.ListHome = f.Api.PageHome = true;
        TestRunner.Equal(Prefix + "\\" + RelativeFolder, Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Equal("team", f.Api.Calls.Last().NamespaceId);
        TestRunner.Equal(SnapshotEndpoints.Continue, f.Api.Calls.Last().Endpoint);
        f.Api.UnknownCursor = true;
        Failure(f.Cli("path", Folder), 4, "invalid_response", "応答を確認");
        AssertReadOnly(f.Api);
    }

    private sealed class ShellItems(uint count, string path) : ShellCopyServer.IShellItemArray, ShellCopyServer.IShellItem
    {
        internal int Reads;
        public void GetCount(out uint result) => result = count;
        public void GetItemAt(uint index, out ShellCopyServer.IShellItem item) { Reads++; item = this; }
        public void GetDisplayName(uint displayNameType, out string name) => name = path;
        public void BindToHandler() { }
        public void GetParent() { }
        public void GetPropertyStore() { }
        public void GetPropertyDescriptionList() { }
        public void GetAttributes() { }
    }

    private static void ShellLimits()
    {
        int copies = 0, refused = 0;
        var selection = new ShellCopyServer.CopySelection(_ => copies++,
            e => { TestRunner.Equal("selection_limit", e.Code); refused++; });
        var tooMany = new ShellItems(ShellCopyServer.MaxItems + 1, "sample");
        selection.SetSelection(tooMany);
        selection.Execute();
        TestRunner.Equal(0, tooMany.Reads);
        var tooLong = new ShellItems(10, new string('x', ShellCopyServer.MaxPathCharacters / 2));
        selection.SetSelection(tooLong);
        selection.Execute();
        TestRunner.Equal(3, tooLong.Reads);
        TestRunner.Equal(2, refused);
        TestRunner.Equal(0, copies);
        selection.SetSelection(new ShellItems(2, "sample"));
        selection.Execute();
        TestRunner.Equal(1, copies);
    }

    private static void CliDocumentation()
    {
        string[] lines = File.ReadAllLines(Path.Combine(UnitTests.RepositoryRoot(), "docs", "cli.md"));
        string row = lines.Single(line => line.StartsWith("| `dbxl share invite ", StringComparison.Ordinal));
        TestRunner.Equal(4, row.Split('|').Length);
        TestRunner.Assert(row.Contains("confirmation") && row.Contains("wouldShareFolder") && row.Contains("executed"));
        TestRunner.Assert(lines.Any(line => line.Contains("--execute --confirm")));
    }
}
