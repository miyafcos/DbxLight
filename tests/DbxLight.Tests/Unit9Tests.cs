using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;
using Microsoft.Win32;

namespace DbxLight.Tests;

internal static partial class Unit9Tests
{
    private const string Team = "サンプル社";
    private const string Home = "サンプル　太郎";
    private const string Recipient = "reader@example.test";
    private const string SecondRecipient = "editor@example.test";
    private const string RelativeFolder = "資料　見本";
    private const string Folder = @"T:\資料　見本";
    private const string Prefix = @"\サンプル社 Dropbox\サンプル　太郎";
    private const string DropboxHeader = "Dropbox-API-Path-Root";
    private const string Share = "sharing/share_folder", Poll = "sharing/check_share_job_status", Add = "sharing/add_folder_member";

    internal static void Run(TestRunner runner)
    {
        runner.Test("9 A: team home matches the namespace under the team root with Japanese names", TeamHome);
        runner.Test("9 A: explicit namespace match can resolve a home without home_path", TeamListing);
        runner.Test("9 A: the team space starts at its own root for slash-prefixed remotes", TeamRoot);
        runner.Test("9 A: personal and remote subfolder paths keep their full-width spaces", Personal);
        runner.Test("9 A: manual prefix, clearing it, and loading an older config", ManualAndLegacy);
        runner.Test("9 A: unknown team home asks for the prefix without guessing", UnknownHome);
        runner.Test("9 A: an outside path rejects the whole selection before touching APIs", OutsidePaths);
        runner.Test("9 A: a mixed Shell item array arrives together and yields one line per item", ShellSelection);
        runner.Test("9 A/B/C: verbs, DelegateExecute, stale registration, and removal use only TestHive", Registration);
        runner.Test("9 B: copy and open use the same encoded Dropbox location without writes", WebCopy);
        runner.Test("9 C: CLI defaults to a dry run with zero write requests", Preview);
        runner.Test("9 C: CLI only executes an explicit switch, including when a note resembles a switch", PreviewFlags);
        runner.Test("9 C: CLI execute returns JSON and creates before adding members", Execute);
        runner.Test("9 C: async sharing completes before the invitation is sent", AsyncShare);
        runner.Test("9 C: shared-folder status reads users, groups, pending invitees, and continuation", Members);
        runner.Test("9 C: an existing shared folder skips creation and preserves the team root", AlreadyShared);
        runner.Test("9 C: an inherited share can be shown, but cannot invite to its parent", Inherited);
        runner.Test("9 C: rejected address and existing member stop before a write", Recipients);
        runner.Test("9 C: permission, team policy, and nested-folder errors provide next actions", DropboxErrors);
        runner.Test("9 C: an async share error stops before invitations", FailedJob);
        runner.Test("9 C: creation followed by a rejected invitation reports both outcomes", Partial);
        runner.Test("9 C: loss of a write response reports uncertainty without retrying", LostResponse);
        runner.Test("9 C: a change of folder identity after preview stops before a write", ReplacedFolder);
        runner.Test("9 C: a file cannot be shared from the CLI", NotAFolder);
        runner.Test("9 C: default and Cancel in the actual confirmation dialog send nothing", CancelFromForm);
        ReviewTests(runner);
        Review2Tests(runner);
        Review3Tests(runner);
        Review4Tests(runner);
    }

    private sealed class Fixture
    {
        internal string Root { get; } = Path.Combine(TestPaths.WorkRoot, "run-unit9-" + Guid.NewGuid().ToString("N"));
        internal string ConfigPath => Path.Combine(Root, "config.json");
        internal DbxConfig Config { get; private set; }
        internal string StatePath => Path.Combine(Root, "runtime", "state.json");
        internal string LocalFolder => Path.Combine(Config.MountPoint, RelativeFolder);
        internal string Device = @"\Device\Volume{sample}";
        internal string MountedConfig => Path.Combine(Root, "mounted-rclone.conf");
        internal string? LastClientConfig { get; private set; }
        internal MountController Controller { get; }
        internal FolderShareMount Mount => new(ConfigPath, StatePath, Controller.FolderMountInspector);
        internal ShareFolderSession Session => new(LocalFolder, Mount, Open);
        internal readonly FakeDropbox Api = new();
        internal Func<DbxConfig, DropboxFolderClient>? ClientFactory;

        internal Fixture()
        {
            Config = new() { MountPoint = Path.Combine(Root, "mounted"), NetworkMode = false, Remote = "sample:",
                RcloneConfigPath = MountedConfig,
                CacheDir = Path.Combine(Root, "cache"), RescueDir = Path.Combine(Root, "rescue"), LogDir = Path.Combine(Root, "logs") };
            Controller = new(new ConfigStore(ConfigPath), new StateStore(StatePath),
                processQuery: _ => throw new InvalidOperationException("Folder preferences must not touch the lifecycle."))
                { FolderMountInspector = _ => new(Device, MountedConfig) };
            Save(Config);
        }

        internal void Save(DbxConfig value, bool updateState = true)
        {
            Config = value.Validate();
            JsonFiles.Write(ConfigPath, Config);
            if (!updateState) return;
            Directory.CreateDirectory(LocalFolder);
            var state = Controller.State.Load() ?? new RuntimeState
            {
                ConfigPath = ConfigPath, OwnerStatePath = StatePath, GenerationId = "sample-generation",
                GenerationNumber = 1, RclonePid = 1234, ProcessStartedUtc = DateTime.UnixEpoch,
                MountDevice = Device, StopDisposition = StopDisposition.Running, RecoveryRequired = false
            };
            Controller.State.Save(state with { ActiveConfig = Config, MountDropboxBinding = MountedBinding(Config),
                MountDropboxCursor = SelectionCursor });
        }

        internal string MountedBinding(DbxConfig config) => FolderShareMount.BindAccount(Api.AccountFor(config.Remote), MountedConfig);

        internal DropboxFolderClient Open(DbxConfig config)
        {
            LastClientConfig = config.RcloneConfigPath;
            return ClientFactory?.Invoke(config) ?? Client(config, Api);
        }

        internal (int Exit, JsonElement Output) Cli(params string[] args)
        {
            var (exit, output, error) = Run(args.Contains("--execute") ? args : [.. args, "--json"]);
            TestRunner.Equal("", error);
            using var doc = JsonDocument.Parse(output);
            return (exit, doc.RootElement.Clone());
        }

        internal string[] LocalArguments(string[] args) => args.Select(arg =>
            arg.StartsWith(@"T:\", StringComparison.Ordinal) ? Path.Combine(Config.MountPoint, arg[3..]) : arg).ToArray();

        internal (int Exit, string Output, string Error) Run(params string[] args) => Unit3cTests.Capture(() =>
            DbxLight.Cli.Program.RunCommand(["--config", ConfigPath, .. LocalArguments(args)], Controller, Open));

        internal (int Exit, JsonElement Output) Confirmed(params string[] args)
        {
            var preview = Cli(args);
            if (preview.Exit != 0) return preview;
            string confirmation = Success(preview).GetProperty("confirmation").GetString()!;
            return Cli([.. args, "--execute", "--confirm", confirmation]);
        }
    }

    private static DropboxFolderClient Client(DbxConfig config, FakeDropbox api)
    {
        var parts = config.Remote.Split(':', 2);
        string suffix = parts[1];
        return new(new FakeBroker(new(parts[0], suffix.Trim('/'), suffix.StartsWith('/'), api.NamespaceId, api.CustomApp), api, config.RcloneConfigPath),
            new Handler(api));
    }

    private sealed record Call(string Endpoint, string? NamespaceId, JsonElement Body);

    private const string FakeAccess = "SAMPLE_UNIT9_TOKEN", UpdatedAccess = "SAMPLE_UNIT9_UPDATED";
    private sealed class FakeBroker(SnapshotRemote remote, FakeDropbox api, string? configPath = null) : ISnapshotTokenBroker
    {
        public SnapshotRemote Remote => remote;
        public Task<SnapshotToken> ReadAsync(CancellationToken token) =>
            Task.FromResult(new SnapshotToken(api.Credential.AccessToken, api.Credential.ExpiresUtc));
        public Task<SnapshotCredentialSource?> CopySourceAsync(byte[] nonce, CancellationToken token) =>
            Task.FromResult(api.CacheSource is { } source && configPath is not null ?
                new SnapshotCredentialSource(configPath, source) : null);
        public Task RenewAsync(CancellationToken token)
        {
            api.Renewals++;
            if (api.RenewalProblem is { } problem) throw new SnapshotException(3, problem);
            if (api.RenewalFails) throw new SnapshotException(3, "auth_unavailable");
            api.Credential = new(UpdatedAccess, DateTimeOffset.MaxValue);
            if (api.AccountAfterRenewal is { } changed) api.AccountId = changed;
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(FakeDropbox server) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string route = request.RequestUri?.AbsolutePath ?? "(missing)";
            server.Requests.Add(route);
            TestRunner.Equal("https://api.dropboxapi.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            TestRunner.Equal(HttpMethod.Post, request.Method);
            string? access = request.Headers.Authorization?.Parameter;
            TestRunner.Assert(access is FakeAccess or UpdatedAccess, "Expected a fake credential.");
            string? ns = null;
            if (request.Headers.TryGetValues(DropboxHeader, out var roots))
            {
                using var root = JsonDocument.Parse(roots.Single());
                TestRunner.Equal("namespace_id", root.RootElement.GetProperty(".tag").GetString());
                ns = root.RootElement.GetProperty("namespace_id").GetString();
            }
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return server.Receive(new(route["/2/".Length..], ns, json.RootElement.Clone()), access!);
        }
    }

    private sealed class FakeDropbox
    {
        internal readonly List<string> Requests = [];
        internal readonly List<Call> Calls = [];
        internal List<Call> Writes => Calls.Where(call => call.Endpoint is Share or Add).ToList();
        internal bool TeamAccount = true, ListHome, HideHome, IsFile, Async, LostAdd, LoseShare, FailJob, Paged;
        internal bool PageHome, UnknownCursor, CustomApp, RenewalFails, RejectCurrent, InheritedAccess, ParentAsShared, MovedAfterJob;
        internal string? NamespaceId, SharedId, ParentId, CacheSource, AccountAfterRenewal, ExpireAt, MissingPath, DifferentShareAfterJob, MetadataShareOverride;
        internal string AccountId = "sample-account", DisplayName = "見本のアカウント", FolderId = "id:sample";
        internal SnapshotToken Credential = new(FakeAccess, DateTimeOffset.MaxValue);
        internal string? RenewalProblem;
        internal HashSet<string>? MetadataPaths;
        internal int Polls, Renewals;
        internal Action<Call>? BeforeReply;
        internal Func<Call, HttpResponseMessage?>? OverrideReply;
        internal (string Code, HttpStatusCode Status)? ShareError, AddError, MembersError;

        internal DropboxFolderAccount AccountFor(string remote)
        {
            string root = TeamAccount ? "team" : "home";
            string ns = NamespaceId ?? (remote.Split(':', 2)[1].StartsWith('/') ? root : "home");
            return new(AccountId, ns, "home", root, TeamAccount, TeamAccount ? Team : null, TeamAccount ? "/" + Home : null, DisplayName);
        }

        internal HttpResponseMessage Receive(Call call, string bearer)
        {
            Calls.Add(call);
            BeforeReply?.Invoke(call);
            if (call.Endpoint == SnapshotEndpoints.Account && RejectCurrent)
                return Error("invalid_access_token", HttpStatusCode.Unauthorized);
            if (call.Endpoint == ExpireAt && bearer == FakeAccess)
                return Error("expired_access_token", HttpStatusCode.Unauthorized);
            if (OverrideReply?.Invoke(call) is { } custom) return custom;
            switch (call.Endpoint)
            {
                case "files/list_folder/get_latest_cursor":
                    TestRunner.Assert(call.Body.GetProperty("recursive").GetBoolean() && call.Body.GetProperty("include_deleted").GetBoolean());
                    return Reply(new { cursor = SelectionCursor });
                case SnapshotEndpoints.Account:
                    return Reply(new
                    {
                        account_id = AccountId,
                        name = new { display_name = DisplayName },
                        email = "account@example.test",
                        team = TeamAccount ? new { name = Team } : null,
                        root_info = new Dictionary<string, string?>
                        {
                            [".tag"] = TeamAccount ? "team" : "user", ["root_namespace_id"] = TeamAccount ? "team" : "home",
                            ["home_namespace_id"] = "home", ["home_path"] = TeamAccount && !ListHome ? "/" + Home : null
                        }
                    });
                case SnapshotEndpoints.Metadata:
                    string path = call.Body.GetProperty("path").GetString()!;
                    if (path == "/" + Home && call.NamespaceId == "team")
                        return HideHome ? Error("path/not_found") : Reply(Tag("folder", ("name", Home)));
                    if (path == MissingPath || MetadataPaths is not null && !MetadataPaths.Contains(path)) return Error("path/not_found");
                    return Reply(Tag(IsFile ? "file" : "folder", ("id",
                            MovedAfterJob && Polls >= 2 ? "id:replacement" : FolderId),
                        ("path_lower", path.ToLowerInvariant()),
                        ("sharing_info", new { shared_folder_id = MetadataShareOverride ?? (Polls >= 2 ? DifferentShareAfterJob ?? SharedId : SharedId),
                            parent_shared_folder_id = ParentId })));
                case SnapshotEndpoints.List:
                    var other = Tag("folder", ("name", "別の見本"), ("sharing_info", new { shared_folder_id = "other" }));
                    var home = Tag("folder", ("name", Home), ("sharing_info", new { shared_folder_id = HideHome ? "other" : "home" }));
                    return Reply(new { entries = PageHome ? new[] { other } : new[] { other, home },
                        has_more = PageHome, cursor = PageHome ? "home-next" : null });
                case SnapshotEndpoints.Continue:
                    if (call.Body.GetProperty("cursor").GetString() == SelectionCursor)
                        return Reply(new { entries = Array.Empty<object>(), has_more = false, cursor = SelectionCursor });
                    TestRunner.Equal("home-next", call.Body.GetProperty("cursor").GetString());
                    return Reply(new { entries = UnknownCursor ? Array.Empty<object>() :
                        [Tag("folder", ("name", Home), ("sharing_info", new { shared_folder_id = "home" }))],
                        has_more = UnknownCursor, cursor = UnknownCursor ? "home-next" : null });
                case "sharing/get_folder_metadata":
                    return Reply(new { shared_folder_id = call.Body.GetProperty("shared_folder_id").GetString(), access_type = Tag("owner") });
                case "sharing/list_folder_members":
                    if (MembersError is { } memberError) return Error(memberError.Code, memberError.Status);
                    return Reply(MemberPage(Paged, InheritedAccess));
                case "sharing/list_folder_members/continue":
                    TestRunner.Equal("sample-next", call.Body.GetProperty("cursor").GetString());
                    return Reply(new { users = Array.Empty<object>(), invitees = new[]
                    {
                        new { invitee = Tag("email", ("email", "pending@example.test")), access_type = Tag("viewer") }
                    }, groups = Array.Empty<object>() });
                case Share:
                    TestRunner.Equal(FolderId, call.Body.GetProperty("path").GetString());
                    if (ShareError is { } shareError) return Error(shareError.Code, shareError.Status);
                    if (LoseShare) throw new HttpRequestException("PRIVATE TRANSPORT DETAIL " + FakeAccess);
                    if (Async) return Reply(Tag("async_job_id", ("async_job_id", "sample-job")));
                    SharedId = ParentAsShared ? ParentId : "sample-shared";
                    return Reply(Tag("complete", ("shared_folder_id", SharedId)));
                case Poll:
                    TestRunner.Equal("sample-job", call.Body.GetProperty("async_job_id").GetString());
                    if (++Polls == 1) return Reply(Tag("in_progress"));
                    if (FailJob) return Reply(Tag("failed", ("failed", Tag("bad_path", ("bad_path", Tag("contains_shared_folder"))))));
                    SharedId = "sample-shared";
                    return Reply(Tag("complete", ("shared_folder_id", SharedId)));
                case Add:
                    TestRunner.Equal(SharedId, call.Body.GetProperty("shared_folder_id").GetString());
                    if (AddError is { } addError) return Error(addError.Code, addError.Status);
                    if (LostAdd) throw new HttpRequestException("PRIVATE TRANSPORT DETAIL " + FakeAccess);
                    return new(HttpStatusCode.OK) { Content = new StringContent("null") };
                default: throw new InvalidOperationException("Unexpected fake Dropbox request");
            }
        }

        internal static object MemberPage(bool paged, bool inherited = false) => new
        {
            users = new[] { new { user = new { display_name = "サンプル 花子", email = "owner@example.test" }, access_type = Tag(inherited ? "traverse" : "owner") } },
            groups = new[] { new { group = new { group_name = "サンプル担当" }, access_type = Tag(inherited ? "no_access" : "editor") } },
            invitees = Array.Empty<object>(), cursor = paged ? "sample-next" : null
        };

        private static HttpResponseMessage Error(string error, HttpStatusCode status = HttpStatusCode.Conflict)
        {
            string[] tags = error.Split('/');
            object body = tags.Length == 1 ? Tag(tags[0]) : Tag(tags[0], (tags[0], Tag(tags[1])));
            return Reply(new { error = body, error_summary = "PRIVATE API DETAIL " + FakeAccess }, status);
        }
        internal static Dictionary<string, object?> Tag(string tag, params (string Name, object? Value)[] fields)
        {
            var result = new Dictionary<string, object?> { [".tag"] = tag };
            foreach (var (key, value) in fields) result.Add(key, value);
            return result;
        }
        internal static HttpResponseMessage Reply(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }

    private static JsonElement Success((int Exit, JsonElement Output) call)
    {
        TestRunner.Equal(0, call.Exit);
        TestRunner.Assert(call.Output.GetProperty("ok").GetBoolean(), call.Output.GetRawText());
        return call.Output.GetProperty("result");
    }

    private static JsonElement Failure((int Exit, JsonElement Output) call, int exit, string code, string text)
    {
        TestRunner.Equal(exit, call.Exit);
        TestRunner.Assert(!call.Output.GetProperty("ok").GetBoolean());
        TestRunner.Equal(code, call.Output.GetProperty("code").GetString());
        TestRunner.Assert(call.Output.GetProperty("message").GetString()!.Contains(text, StringComparison.Ordinal));
        TestRunner.Assert(!call.Output.GetRawText().Contains("PRIVATE", StringComparison.Ordinal));
        TestRunner.Assert(!call.Output.GetRawText().Contains(FakeAccess, StringComparison.Ordinal) &&
            !call.Output.GetRawText().Contains(UpdatedAccess, StringComparison.Ordinal));
        return call.Output.TryGetProperty("result", out var result) ? result : default;
    }

    private static void TeamHome()
    {
        var f = new Fixture();
        var output = Success(f.Cli("path", Folder));
        TestRunner.Equal(Prefix + @"\資料　見本", output.GetProperty("path").GetString());
        TestRunner.Equal(2, f.Api.Calls.Count);
        TestRunner.Equal(SnapshotEndpoints.Account, f.Api.Calls[0].Endpoint);
        TestRunner.Equal("team", f.Api.Calls[1].NamespaceId);
        TestRunner.Equal("/" + Home, f.Api.Calls[1].Body.GetProperty("path").GetString());
    }
    private static void TeamListing()
    {
        var f = new Fixture(); f.Api.ListHome = true;
        TestRunner.Equal(Prefix + @"\資料　見本", Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Equal(SnapshotEndpoints.List, f.Api.Calls[1].Endpoint);
    }
    private static void TeamRoot()
    {
        var f = new Fixture(); f.Save(f.Config with { Remote = "sample:/上位" });
        TestRunner.Equal(@"\サンプル社 Dropbox\上位\資料　見本", Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Equal(SnapshotEndpoints.Account, f.Api.Calls.Single().Endpoint);
    }
    private static void Personal()
    {
        var f = new Fixture(); f.Api.TeamAccount = false;
        f.Save(f.Config with { Remote = "sample:サブ　フォルダ/見本" });
        var (exit, stdout, stderr) = f.Run("path", Folder + @"\用紙　1.pdf");
        TestRunner.Equal(0, exit); TestRunner.Equal("", stderr);
        TestRunner.Equal(@"\Dropbox\サブ　フォルダ\見本\資料　見本\用紙　1.pdf", stdout.TrimEnd());
    }
    private static void ManualAndLegacy()
    {
        var f = new Fixture();
        f.Save(ConfigStore.Set(f.Config, "copyPathPrefix", @"\手入力 Dropbox\見本\"));
        TestRunner.Equal(Prefix + @"\資料　見本", Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        Success(f.Cli("set", "copyPathPrefix", @"\手入力 Dropbox\見本\"));
        TestRunner.Equal(@"\手入力 Dropbox\見本\資料　見本", Success(f.Cli("path", Folder)).GetProperty("path").GetString());
        TestRunner.Assert(f.Controller.Config.Load().CopyPathBinding is not null);
        var auto = ConfigStore.Set(f.Config, "copyPathPrefix", "");
        TestRunner.Equal<string?>(null, auto.CopyPathPrefix);
        TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(f.Config, "copyPathPrefix", @"T:\bad"));
        TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(f.Config, "copyPathPrefix", @"\Dropbox\.."));
        TestRunner.Throws<ArgumentException>(() => ConfigStore.Set(f.Config, "copyPathPrefix", @"\\Dropbox"));
        var legacy = JsonSerializer.SerializeToNode(auto, JsonFiles.Options)!.AsObject();
        TestRunner.Assert(!legacy.ContainsKey("copyPathPrefix") && !legacy.ContainsKey("copyPathBinding"));
        JsonFiles.Write(f.ConfigPath, legacy);
        TestRunner.Equal(Prefix + @"\資料　見本", Success(f.Cli("path", Folder)).GetProperty("path").GetString());
    }
    private static void UnknownHome()
    {
        var f = new Fixture(); f.Api.HideHome = true;
        Failure(f.Cli("path", Folder), 3, "copy_path_prefix_unknown", "コピーするパスの頭");
        f.Api.NamespaceId = "unmapped";
        f.Save(f.Config);
        Failure(f.Cli("path", Folder), 3, "copy_path_prefix_unknown", "コピーするパスの頭");
    }
    private static void OutsidePaths()
    {
        var f = new Fixture();
        foreach (string candidate in new[] { @"U:\資料　見本", RelativeFolder, "//server/share/folder", @"\\.\T:\資料" })
            Failure(f.Cli("path", candidate), 2, "outside_mount", "マウント先");
        TestRunner.Throws<FolderOperationException>(() => DropboxCopyPaths.CopyAsync(f.Config, [f.LocalFolder, @"U:\outside"], f.Open).GetAwaiter().GetResult());
        string mount = Path.Combine(f.Root, "mount");
        f.Save(f.Config with { MountPoint = mount, NetworkMode = false });
        Failure(f.Cli("path", Path.Combine(mount + "-other", "sample")), 2, "outside_mount", "マウント先");
        TestRunner.Equal(0, f.Api.Calls.Count);
    }
    private static void ShellSelection() => FormTests.Sta(() =>
    {
        Application.OleRequired();
        var f = new Fixture();
        string mount = Path.Combine(f.Root, "mount");
        f.Save(f.Config with { MountPoint = mount, NetworkMode = false });
        string folder = Path.Combine(mount, RelativeFolder);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(mount, "見本　1.txt");
        File.WriteAllText(file, "sample");
        var pidls = new IntPtr[2];
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(folder, IntPtr.Zero, out pidls[0], 0, out _));
            Marshal.ThrowExceptionForHR(SHParseDisplayName(file, IntPtr.Zero, out pidls[1], 0, out _));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists(2, pidls, out var array));
            string? copied = null;
            var factory = new ShellCopyServer.CopyFactory(paths =>
                copied = string.Join(Environment.NewLine, DropboxCopyPaths.CopyAsync(f.Config, paths, f.Open).GetAwaiter().GetResult()));
            Guid iid = typeof(ShellCopyServer.IExecuteCommand).GUID;
            Marshal.ThrowExceptionForHR(factory.CreateInstance(IntPtr.Zero, in iid, out IntPtr instance));
            try
            {
                object command = Marshal.GetObjectForIUnknown(instance);
                ((ShellCopyServer.IObjectWithSelection)command).SetSelection(array);
                ((ShellCopyServer.IExecuteCommand)command).Execute();
                TestRunner.Equal(Prefix + @"\資料　見本" + Environment.NewLine + Prefix + @"\見本　1.txt", copied);
                TestRunner.Equal(2, f.Api.Calls.Count);
                AssertReadOnly(f.Api);
            }
            finally { Marshal.Release(instance); }
        }
        finally { foreach (IntPtr pidl in pidls) Marshal.FreeCoTaskMem(pidl); }
    });
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr binding, out IntPtr pidl, uint mask, out uint attributes);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out ShellCopyServer.IShellItemArray array);

    private static void Registration()
    {
        var f = new Fixture();
        string root = RegistrationService.TestHive + @"\Unit9-" + Guid.NewGuid().ToString("N");
        var registry = new RegistrationService(root);
        var options = new AppOptions(f.ConfigPath, Path.Combine(f.Root, "state.json"), RegistryRoot: root);
        string exe = Path.Combine(f.Root, "DbxLight.exe"), classes = root + @"\Software\Classes\";
        string copyPath = classes + @"AllFilesystemObjects\shell\DbxLight.CopyPath";
        string share = classes + @"Directory\shell\DbxLight.Share";
        string copyServer = classes + @"CLSID\" + ShellCopyServer.ClassId + @"\LocalServer32";
        registry.InstallShell(f.Config, exe, options);
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(copyPath)!)
            using (var command = key.OpenSubKey("command")!)
            {
                TestRunner.Equal("パスをコピー", (string?)key.GetValue(""));
                TestRunner.Equal("Player", (string?)key.GetValue("MultiSelectModel"));
                TestRunner.Equal(ShellCopyServer.ClassId, (string?)command.GetValue("DelegateExecute"));
            }
            using (var key = Registry.CurrentUser.OpenSubKey(copyServer)!)
                TestRunner.Assert(((string)key.GetValue("")!).EndsWith(" --copy-path-server", StringComparison.Ordinal));
            TestRunner.Assert(AppOptions.Parse(["--config", options.ConfigPath, "--copy-path-server", "-Embedding"]).CopyPathServer);
            using (var key = Registry.CurrentUser.OpenSubKey(share)!)
            using (var command = key.OpenSubKey("command")!)
            {
                TestRunner.Equal("このフォルダを共有…", (string?)key.GetValue(""));
                TestRunner.Equal("Single", (string?)key.GetValue("MultiSelectModel"));
                TestRunner.Assert(((string)command.GetValue("")!).EndsWith(" --share-shell \"%1|\"", StringComparison.Ordinal));
            }
            using (var key = Registry.CurrentUser.OpenSubKey(classes + @"*\shell\DbxLight.Share")) TestRunner.Assert(key is null);
            foreach (string type in new[] { "Directory", "*" })
            {
                using var key = Registry.CurrentUser.OpenSubKey(classes + type + @"\shell\DbxLight.CopyWeb")!;
                using var command = key.OpenSubKey("command")!;
                TestRunner.Equal("Web のリンクをコピー", (string?)key.GetValue(""));
                TestRunner.Assert(((string)command.GetValue("")!).EndsWith(" --web-copy-shell \"%1|\"", StringComparison.Ordinal));
            }
            TestRunner.Equal("", registry.ShellRegistrationNotice(f.Config, exe, options));
            using (var key = Registry.CurrentUser.OpenSubKey(copyServer, writable: true)!)
            {
                string expected = (string)key.GetValue("")!;
                key.SetValue("", expected[..^" --copy-path-server".Length]);
                TestRunner.Assert(registry.ShellRegistrationNotice(f.Config, exe, options).Contains("登録が揃っていません"));
                key.SetValue("", expected.Replace(options.ConfigPath, options.ConfigPath + ".other", StringComparison.Ordinal));
                TestRunner.Assert(registry.ShellRegistrationNotice(f.Config, exe, options).Contains("登録が揃っていません"));
                key.SetValue("", expected.Replace(f.Root, f.Root.ToUpperInvariant(), StringComparison.Ordinal));
                TestRunner.Equal("", registry.ShellRegistrationNotice(f.Config, exe, options));
                key.SetValue("", expected + " --extra");
                TestRunner.Assert(registry.ShellRegistrationNotice(f.Config, exe, options).Contains("登録が揃っていません"));
                key.SetValue("", expected);
            }
            Registry.CurrentUser.DeleteSubKey(copyPath + @"\command");
            Registry.CurrentUser.DeleteSubKey(copyPath);
            TestRunner.Assert(registry.ShellRegistrationNotice(f.Config, exe, options).Contains("登録が揃っていません"));
        }
        finally { registry.UninstallShell(); }
        foreach (string path in new[] { copyPath, share, copyServer })
        {
            using var gone = Registry.CurrentUser.OpenSubKey(path);
            TestRunner.Assert(gone is null);
        }
        TestRunner.Assert(registry.AccessedPaths.All(path => path.StartsWith(root + "\\", StringComparison.Ordinal)));
    }
    private static void WebCopy()
    {
        string instance = Folder + @"\A #資料%.pdf";
        string open = AppOptions.Parse(["--web-shell", instance + "|"]).WebPath!;
        string copy = AppOptions.Parse(["--web-copy-shell", instance + "|"]).WebCopyPath!;
        var f = new Fixture();
        f.Api.TeamAccount = false;
        f.Save(f.Config with { Remote = "sample:Dept/Group #1" });
        const string expected = "https://www.dropbox.com/home/Dept/Group%20%231/%E8%B3%87%E6%96%99%E3%80%80%E8%A6%8B%E6%9C%AC/A%20%23%E8%B3%87%E6%96%99%25.pdf";
        TestRunner.Equal(expected, ShellWeb(f, f.LocalArguments([open])[0]));
        TestRunner.Equal(expected, ShellWeb(f, f.LocalArguments([copy])[0]));
        AssertReadOnly(f.Api);
        TestRunner.Throws<ArgumentException>(() => AppOptions.Parse(["--web-copy-shell", "T:\\"]));
    }

    private static void Preview()
    {
        var f = new Fixture();
        var result = Success(f.Cli("share", "invite", Folder, "--to", Recipient, "--access", "viewer"));
        TestRunner.Assert(result.GetProperty("wouldShareFolder").GetBoolean() && !result.GetProperty("executed").GetBoolean());
        TestRunner.Equal(Recipient, result.GetProperty("to")[0].GetString());
        TestRunner.Equal("viewer", result.GetProperty("access").GetString());
        TestRunner.Equal(64, result.GetProperty("confirmation").GetString()!.Length);
        AssertReadOnly(f.Api);
    }
    private static void PreviewFlags()
    {
        var f = new Fixture();
        var (exit, output, error) = f.Run("share", "invite", Folder, "--to", Recipient, "--access", "viewer", "--message", "--execute", "--json");
        TestRunner.Equal("", error);
        using var doc = JsonDocument.Parse(output);
        var plan = Success((exit, doc.RootElement));
        TestRunner.Equal("--execute", plan.GetProperty("message").GetString());
        TestRunner.Assert(!plan.GetProperty("executed").GetBoolean());
        var text = f.Run("share", "invite", Folder, "--to", Recipient, "--access", "viewer", "--message", "--json");
        TestRunner.Equal(0, text.Exit);
        TestRunner.Assert(text.Output.StartsWith("招待の確認", StringComparison.Ordinal));
        TestRunner.Assert(text.Output.Contains("確認値 "));
        AssertReadOnly(f.Api);
    }
    private static void Execute()
    {
        var f = new Fixture(); f.Save(f.Config with { Remote = "sample:/見本" });
        var result = Success(f.Confirmed("share", "invite", Folder, "--to", Recipient, "--to", SecondRecipient,
            "--access", "editor", "--message", "資料をご覧ください。"));
        TestRunner.Assert(f.Api.Writes.Select(call => call.Endpoint).SequenceEqual(new[] { Share, Add }));
        TestRunner.Assert(f.Api.Writes.All(call => call.NamespaceId == "team"));
        TestRunner.Assert(result.GetProperty("sharedFolderCreated").GetBoolean());
        TestRunner.Equal("sent", result.GetProperty("invitationState").GetString());
        TestRunner.Equal(2, result.GetProperty("invited").GetArrayLength());
        TestRunner.Equal("editor", result.GetProperty("access").GetString());
        var body = f.Api.Writes[1].Body;
        TestRunner.Assert(!body.GetProperty("quiet").GetBoolean());
        TestRunner.Equal("資料をご覧ください。", body.GetProperty("custom_message").GetString());
        TestRunner.Equal("email", body.GetProperty("members")[1].GetProperty("member").GetProperty(".tag").GetString());
        TestRunner.Equal(SecondRecipient, body.GetProperty("members")[1].GetProperty("member").GetProperty("email").GetString());
        TestRunner.Equal("editor", body.GetProperty("members")[1].GetProperty("access_level").GetProperty(".tag").GetString());
    }
    private static void AsyncShare()
    {
        var f = new Fixture(); f.Api.Async = true;
        var result = Success(Invite(f));
        TestRunner.Assert(f.Api.Requests.Where(route => !ReadRoutes.Contains(route)).SequenceEqual(new[] { Share, Poll, Poll, Add }.Select(Endpoint)));
        TestRunner.Equal("created", result.GetProperty("sharedFolderState").GetString());
    }
    private static void Members()
    {
        var f = new Fixture(); f.Api.SharedId = "sample-shared"; f.Api.Paged = true;
        var result = Success(f.Cli("share", "show", Folder));
        TestRunner.Assert(result.GetProperty("shared").GetBoolean());
        TestRunner.Equal("owner", result.GetProperty("access").GetString());
        var members = result.GetProperty("members");
        TestRunner.Equal(3, members.GetArrayLength());
        TestRunner.Equal("サンプル 花子", members[0].GetProperty("name").GetString());
        TestRunner.Equal("editor", members[1].GetProperty("access").GetString());
        TestRunner.Equal("invitee", members[2].GetProperty("kind").GetString());
        TestRunner.Equal(0, f.Api.Writes.Count);
    }
    private static void AlreadyShared()
    {
        var f = new Fixture(); f.Api.SharedId = "sample-shared"; f.Save(f.Config with { Remote = "sample:下位" });
        var result = Success(Invite(f));
        TestRunner.Assert(!result.GetProperty("sharedFolderCreated").GetBoolean());
        TestRunner.Equal(Add, f.Api.Writes.Single().Endpoint); TestRunner.Equal("home", f.Api.Writes[0].NamespaceId);
        TestRunner.Equal("/下位/" + RelativeFolder, f.Api.Calls.First(call => call.Endpoint == SnapshotEndpoints.Metadata).Body.GetProperty("path").GetString());
    }
    private static void Inherited()
    {
        var f = new Fixture(); f.Api.ParentId = "parent";
        var shown = Success(f.Cli("share", "show", Folder));
        TestRunner.Assert(shown.GetProperty("inherited").GetBoolean() && !shown.GetProperty("shared").GetBoolean());
        TestRunner.Equal("/" + RelativeFolder, f.Api.Calls.Last().Body.GetProperty("path").GetString());
        f.Api.ShareError = ("bad_path/inside_shared_folder", HttpStatusCode.Conflict);
        Failure(Invite(f), 4, "inside_shared_folder", "入れ子");
        TestRunner.Equal(Share, f.Api.Writes.Single().Endpoint);
        TestRunner.Equal("id:sample", f.Api.Writes.Single().Body.GetProperty("path").GetString());
    }
    private static void Recipients()
    {
        var f = new Fixture(); f.Api.SharedId = "sample-shared";
        Failure(f.Cli("share", "invite", Folder, "--to", "invalid", "--access", "viewer"), 2, "invalid_email", "形式");
        TestRunner.Equal(0, f.Api.Calls.Count);
        Failure(f.Cli("share", "invite", Folder, "--to", "OWNER@example.test", "--access", "viewer"), 4, "already_member", "すでに");
        TestRunner.Equal(0, f.Api.Writes.Count);
    }
    private static (int Exit, JsonElement Output) Invite(Fixture f) =>
        f.Confirmed("share", "invite", Folder, "--to", Recipient, "--access", "viewer");

    private static void DropboxErrors()
    {
        var cases = new (string Tag, HttpStatusCode HttpStatus, int Exit, string Code, string Text)[]
        {
            ("bad_path/inside_shared_folder", HttpStatusCode.Conflict, 4, "inside_shared_folder", "入れ子"),
            ("bad_path/contains_shared_folder", HttpStatusCode.Conflict, 4, "contains_shared_folder", "入れ子"),
            ("missing_scope", HttpStatusCode.Unauthorized, 3, "missing_scope", "もう一度ログイン"),
            ("team_policy_disallows_member_policy", HttpStatusCode.Conflict, 4, "team_policy", "管理者")
        };
        foreach (var item in cases)
        {
            var f = new Fixture(); f.Api.ShareError = (item.Tag, item.HttpStatus);
            var state = Failure(Invite(f), item.Exit, item.Code, item.Text);
            TestRunner.Equal("not_sent", state.GetProperty("invitationState").GetString());
            TestRunner.Equal(Share, f.Api.Writes.Single().Endpoint);
        }
        var missingRead = new Fixture();
        missingRead.Api.SharedId = "sample-shared";
        missingRead.Api.MembersError = ("missing_scope", HttpStatusCode.Unauthorized);
        Failure(missingRead.Cli("share", "show", Folder), 3, "missing_scope", "もう一度ログイン");
        TestRunner.Equal(0, missingRead.Api.Writes.Count);
    }
    private static void FailedJob()
    {
        var f = new Fixture(); f.Api.Async = f.Api.FailJob = true;
        var state = Failure(Invite(f), 4, "contains_shared_folder", "入れ子");
        TestRunner.Equal("not_created", state.GetProperty("sharedFolderState").GetString());
        TestRunner.Assert(f.Api.Requests.Where(route => !ReadRoutes.Contains(route)).SequenceEqual(new[] { Share, Poll, Poll }.Select(Endpoint)));
    }
    private static void Partial()
    {
        var f = new Fixture(); f.Api.AddError = ("cant_share_outside_team", HttpStatusCode.Conflict);
        var state = Failure(Invite(f), 4, "team_policy", "共有フォルダになりました。招待は送っていません。");
        TestRunner.Assert(state.GetProperty("sharedFolderCreated").GetBoolean());
        TestRunner.Equal(0, state.GetProperty("invited").GetArrayLength());
        TestRunner.Equal("not_sent", state.GetProperty("invitationState").GetString());
    }
    private static void LostResponse()
    {
        var f = new Fixture(); f.Api.LostAdd = true;
        var state = Failure(Invite(f), 4, "network_error", "招待が送られたかは確認できていません");
        TestRunner.Equal("unknown", state.GetProperty("invitationState").GetString());
        TestRunner.Equal(2, f.Api.Writes.Count);
        var lostShare = new Fixture(); lostShare.Api.LoseShare = true;
        state = Failure(Invite(lostShare), 4, "network_error", "共有フォルダになったかは確認できていません");
        TestRunner.Equal("unknown", state.GetProperty("sharedFolderState").GetString());
        TestRunner.Equal(Share, lostShare.Api.Writes.Single().Endpoint);
    }
    private static void ReplacedFolder()
    {
        var f = new Fixture();
        string confirmation = Confirm(f, Invitation());
        int reads = 0;
        f.Api.BeforeReply = call =>
        {
            if (call.Endpoint == SnapshotEndpoints.Metadata && ++reads == 3) f.Api.FolderId = "id:replacement";
        };
        Failure(f.Cli([.. Invitation(), "--execute", "--confirm", confirmation]), 3, "sharing_changed", "確認し直して");
        TestRunner.Equal(0, f.Api.Writes.Count);
    }
    private static void NotAFolder()
    {
        var f = new Fixture(); f.Api.IsFile = true;
        Failure(Invite(f), 2, "not_folder", "フォルダを指定");
        TestRunner.Equal(0, f.Api.Writes.Count);
    }
    private static void CancelFromForm() => FormTests.Sta(() =>
    {
        var f = new Fixture();
        f.Api.ParentId = "parent";
        f.Api.InheritedAccess = true;
        var session = f.Session;
        int sendCalls = 0;
        using var form = new ShareFolderForm(f.LocalFolder, token => session.LoadAsync(false, true, token),
            (info, plan, token) => { sendCalls++; return session.InviteAsync(info, plan, token); });
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        ((TextBox)form.Controls.Find("inviteEmails", true).Single()).Text = Recipient;
        var invite = (Button)form.Controls.Find("previewInvitation", true).Single();
        using var tick = new System.Windows.Forms.Timer { Interval = 40 };
        var start = DateTime.UtcNow;
        bool clicked = false, sawConfirmation = false, cancelWasDefault = false, hasPath = false, inheritedNotice = false, connection = false, levels = false;
        tick.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5))
            {
                foreach (var popup in Application.OpenForms.OfType<ShareInvitationConfirmation>().ToArray()) popup.Close();
                form.Close();
                return;
            }
            if (Application.OpenForms.OfType<ShareInvitationConfirmation>().SingleOrDefault() is { } confirm)
            {
                sawConfirmation = true;
                cancelWasDefault = ReferenceEquals(confirm.AcceptButton, confirm.CancelButton) && ((Button)confirm.CancelButton!).Text == "やめる";
                string shown = confirm.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TextBox>().Single().Text;
                hasPath = shown.Contains(Prefix, StringComparison.Ordinal);
                inheritedNotice = shown.Contains(FolderSharing.InheritedNotice, StringComparison.Ordinal);
                connection = shown.Contains(f.Api.DisplayName, StringComparison.Ordinal) && shown.Contains("チームのホーム") &&
                    !shown.Contains("account@example.test", StringComparison.Ordinal);
                var members = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ListView>().Single();
                levels = members.Items[0].SubItems[2].Text == "配下の一部にアクセス可" &&
                    members.Items[1].SubItems[2].Text == "この場所へのアクセスなし";
                confirm.CancelButton!.PerformClick();
                return;
            }
            if (!invite.Enabled) return;
            if (clicked) { form.Close(); return; }
            clicked = true;
            invite.PerformClick();
        };
        tick.Start();
        Application.Run(form);
        TestRunner.Assert(clicked && sawConfirmation && cancelWasDefault && hasPath && inheritedNotice && connection && levels,
            "The confirmation must show the target, verified connection, parent notice and Cancel as its default.");
        TestRunner.Equal(0, sendCalls);
        AssertReadOnly(f.Api);
    });

    internal static int Screens(string output)
    {
        FormTests.Sta(() =>
        {
            Application.EnableVisualStyles();
            Application.SetDefaultFont(Ui.AppFont);
            var f = new Fixture();
            f.Api.ParentId = "parent";
            f.Api.InheritedAccess = true;
            var session = f.Session;
            var info = session.LoadAsync(false, true, default).GetAwaiter().GetResult();
            using var share = new ShareFolderForm(f.LocalFolder, _ => Task.FromResult(info), session.InviteAsync);
            ((TextBox)share.Controls.Find("inviteEmails", true).Single()).Text = Recipient + Environment.NewLine + SecondRecipient;
            ((TextBox)share.Controls.Find("inviteMessage", true).Single()).Text = "資料をご覧ください。";
            using var confirmation = new ShareInvitationConfirmation(FolderSharing.Plan(info.View, info.CopyPath!,
                FolderInviteRequest.Create([Recipient, SecondRecipient], "viewer", "資料をご覧ください。")));
            using var settings = new SettingsForm(f.Config with { CopyPathPrefix = Prefix, CopyPathBinding = "sample", MountPoint = "T:", CacheDir = @"D:\DbxLight\Cache" },
                new RegistrationService(RegistrationService.TestHive + @"\Unit9-Screens"), null,
                new AppOptions(f.ConfigPath, Path.Combine(f.Root, "state.json")), _ => Task.CompletedTask);
            using var context = new ApplicationContext();
            using var dispatcher = new Control(); _ = dispatcher.Handle;
            Exception? error = null;
            dispatcher.BeginInvoke(async () =>
            {
                try { await SmokeRunner.CaptureShareScreensAsync(output, share, confirmation, settings); }
                catch (Exception e) { error = e; }
                finally { context.ExitThread(); }
            });
            Application.Run(context);
            if (error is not null) throw error;
            TestRunner.Equal(0, f.Api.Writes.Count);
        });
        return 0;
    }
}
