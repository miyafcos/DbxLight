using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit8Tests
{
    private static void ResidentCases(Action<string, string, Action> test)
    {
        test("T8", "24 hours, manual bypass, notification once and silent failure", ResidentChecks);
        test("T8", "notification history survives release version returning", () =>
        {
            var store = new UpdateStore(Path.Combine(Root(), "state.json"));
            TestRunner.Assert(store.MarkNotified("0.1.1") && store.MarkNotified("0.1.2") && !store.MarkNotified("0.1.1"));
        });

        test("T8", "missing adjacent CLI is silent automatically and explains manual check", ResidentMissing);
        test("T8", "disabled automatic checking does not start CLI", ResidentDisabled);
        test("T8", "startup phase does not postpone the automatic check to tomorrow", ResidentStartupCheck);
        test("T8", "upload wait cancellation cancels update and keeps watcher", ResidentCancel);
        test("T8", "normal stop failure cancels update and retains resident", ResidentFailure);
        test("T8", "successful normal stop launches staged CLI before exit", ResidentSuccess);
        test("T8", "result is consumed and notified once", ResidentResult);
        test("T8", "real FakeRclone stays mounted during a version check", ResidentFakeRclone);
        test("T8", "real update CLI decodes UTF-8 paths under a legacy console encoding", UpdatePipeEncoding);
        test("T8", "update settings and confirmation use required wording", UpdateForms);
        test("T9", "CLI rejects update while owned mount is active", ActiveCli);
        test("T9", "apply without yes leaves state and target alone", ApplyPreview);
    }
    private static Unit3aTests.RcFixture ResidentFixture(bool automatic = false)
    {
        var fixture = new Unit3aTests.RcFixture(Root()); fixture.Config.Save(fixture.Config.Load() with { UpdateCheck = automatic }); Directory.CreateDirectory(fixture.Config.Load().CacheDir); return fixture;
    }
    private static JsonElement Result<T>(T value) => JsonSerializer.SerializeToElement(value, JsonFiles.Options);
    private static void ResidentChecks()
    {
        using var fixture = ResidentFixture(automatic: true);
        ResidentTests.Run(fixture, async resident =>
        {
            int calls = 0; var titles = new List<string>(); var store = new UpdateStore(fixture.State.PathName);
            resident.UpdateNoticeOverride = (title, _, _) => titles.Add(title);
            resident.UpdateCommandOverride = command => { TestRunner.Equal("check", command); calls++; return Task.FromResult(Result(new UpdateCheckResult("0.1.0", "0.1.1", true, false, UpdateSource.ReleasesUrl, false))); };
            await resident.CheckUpdateAsync(false); await resident.CheckUpdateAsync(false);
            TestRunner.Equal(1, calls); TestRunner.Equal(1, titles.Count);
            await resident.CheckUpdateAsync(true); TestRunner.Equal(2, calls); TestRunner.Equal(2, titles.Count);
            var last = store.LoadCheck().CheckedUtc!.Value;
            TestRunner.Assert(!store.IsDue(last.AddHours(24).AddTicks(-1)) && store.IsDue(last.AddHours(24)));
            store.ChangeCheck(s => s with { CheckedUtc = last.AddDays(-2) });
            resident.UpdateCommandOverride = _ => { calls++; throw new IOException("injected"); };
            await resident.CheckUpdateAsync(false); TestRunner.Equal(3, calls); TestRunner.Equal(2, titles.Count);
            await resident.CheckUpdateAsync(false); TestRunner.Equal(3, calls);
            await resident.CheckUpdateAsync(true); TestRunner.Equal("更新を確認できません", titles.Last());
        });
    }
    private static void ResidentMissing()
    {
        using var fixture = ResidentFixture();
        ResidentTests.Run(fixture, async resident =>
        {
            var titles = new List<string>(); resident.UpdateExecutableOverride = Path.Combine(fixture.Root, "missing.exe");
            resident.UpdateNoticeOverride = (title, _, _) => titles.Add(title);
            await resident.CheckUpdateAsync(false); TestRunner.Equal(0, titles.Count);
            await resident.CheckUpdateAsync(true); TestRunner.Equal("この場所では自動で更新できません", titles.Single());
            TestRunner.Assert(!Directory.Exists(new UpdateStore(fixture.State.PathName).Root));
        });
    }
    private static void ResidentStartupCheck()
    {
        using var fixture = ResidentFixture(automatic: true);
        ResidentTests.Run(fixture, async resident =>
        {
            int calls = 0;
            resident.UpdateCommandOverride = _ =>
            {
                calls++;
                return Task.FromResult(Result(new UpdateCheckResult(UpdateVersion.Current, null, false, false, UpdateSource.ReleasesUrl, false)));
            };
            var phase = typeof(ResidentContext).GetProperty("Phase", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            phase.SetValue(resident, ResidentPhase.Starting);
            try { await resident.CheckUpdateAsync(false); TestRunner.Equal(1, calls); }
            finally { phase.SetValue(resident, ResidentPhase.Watching); }
        });
    }
    private static void ResidentDisabled()
    {
        using var fixture = ResidentFixture(); fixture.Config.Save(fixture.Config.Load() with { UpdateCheck = false });
        ResidentTests.Run(fixture, async resident =>
        {
            resident.UpdateCommandOverride = _ => throw new InvalidOperationException("Unexpected invocation.");
            int notices = 0; resident.UpdateNoticeOverride = (_, _, _) => notices++;
            await resident.CheckUpdateAsync(false); TestRunner.Equal(0, notices);
            TestRunner.Assert(!File.Exists(Path.Combine(new UpdateStore(fixture.State.PathName).Root, "check.json")));
        });
    }
    private static UpdateDownloadResult ResidentDownload(Unit3aTests.RcFixture fixture, ResidentContext resident, Action launch)
    {
        var staged = UpdatePackage.Stage(Package("0.1.1"), new UpdateStore(fixture.State.PathName));
        resident.UpdateCommandOverride = command => { TestRunner.Equal("download", command); return Task.FromResult(Result(staged)); };
        resident.UpdateLaunchOverride = _ => { launch(); return 1; }; return staged;
    }
    private static void ResidentCancel()
    {
        using var fixture = ResidentFixture();
        int ownedPid = fixture.State.Load()!.RclonePid;
        fixture.Respond = path => path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":1,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}") :
            path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + ownedPid + "}");
        ResidentTests.Run(fixture, async resident =>
        {
            int launches = 0; _ = ResidentDownload(fixture, resident, () => launches++);
            resident.UpdateNoticeOverride = (_, _, _) => { };
            EventHandler choose = (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<StopChoiceForm>().FirstOrDefault();
                if (dialog is null) return;
                TestRunner.Assert(!Controls(dialog).OfType<Button>().Single(b => b.Text == "強制終了").Enabled);
                ((Button)dialog.AcceptButton!).PerformClick();
            };
            Application.Idle += choose;
            try { await resident.PrepareUpdateAsync(); } finally { Application.Idle -= choose; }
            TestRunner.Equal(ResidentPhase.WaitingToExit, resident.Phase); TestRunner.Assert(resident.HasPendingUpdate);
            FormTests.Field<ToolStripMenuItem>(resident, "pause").PerformClick(); await resident.MenuStopTask!;
            TestRunner.Assert(!resident.HasPendingUpdate); TestRunner.Equal(ResidentPhase.Watching, resident.Phase);
            TestRunner.Assert(ResidentTests.Watch(resident) is { IsCompleted: false }); TestRunner.Equal(0, launches);
            // Allow the harness to stop its own child after the cancellation assertion.
            fixture.Respond = null;
        });
    }
    private static void ResidentFailure()
    {
        using var fixture = ResidentFixture();
        ResidentTests.Run(fixture, async resident =>
        {
            int launches = 0; _ = ResidentDownload(fixture, resident, () => launches++);
            using var gate = new TestControlGate();
            // The gate exercises the same asynchronous stop boundary; release before the real stop.
            resident.StopAwaitCheckpoint = () => { gate.Acquire(); gate.Dispose(); return Task.CompletedTask; };
            await resident.PrepareUpdateAsync();
            TestRunner.Equal(ResidentPhase.ExitFailed, resident.Phase); TestRunner.Assert(!resident.HasPendingUpdate);
            TestRunner.Equal(0, launches); TestRunner.Assert(ResidentTests.Watch(resident) is { IsCompleted: false });
        });
    }
    private static void ResidentSuccess()
    {
        using var fixture = ResidentFixture();
        int ownedPid = fixture.State.Load()!.RclonePid;
        fixture.Respond = path =>
        {
            if (path == "/core/quit")
            { File.WriteAllText(Path.Combine(fixture.Root, "child-stop.txt"), "exit", new UTF8Encoding(false)); return (200, "{}"); }
            return path == "/vfs/stats" ? (200, "{\"diskCache\":{\"uploadsQueued\":0,\"uploadsInProgress\":0,\"erroredFiles\":0,\"files\":0,\"bytesUsed\":0}}") :
                path == "/vfs/queue" ? (200, "{\"queue\":[]}") : (200, "{\"pid\":" + ownedPid + "}");
        };
        ResidentTests.Run(fixture, async resident =>
        {
            int launches = 0; _ = ResidentDownload(fixture, resident, () => { launches++; TestRunner.Assert(ResidentTests.Watch(resident) is null); });
            await resident.PrepareUpdateAsync(); TestRunner.Equal(1, launches); TestRunner.Assert(!resident.HasPendingUpdate);
            TestRunner.Assert(fixture.State.Load()!.StopRequested);
        });
    }
    private static void UpdatePipeEncoding()
    {
        string root = Root(), state = Path.Combine(root, "更新の状態", "state.json");
        var options = new AppOptions(Path.Combine(root, "config.json"), state);
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        TestRunner.Assert(UpdateVersion.TryParse(UpdateVersion.Current, out var current));
        string version = (current with { Patch = checked(current.Patch + 1) }).ToString();
        string address = $"http://127.0.0.1:{port}/", name = $"DbxLight-{version}-win-x64.zip";
        byte[] zip = Package(version);
        byte[] checksum = Encoding.UTF8.GetBytes(UpdatePackage.Hash(zip) + "  " + name + "\n");
        byte[] release = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = "v" + version, draft = false, prerelease = false,
            assets = new[] {
                new { name, browser_download_url = address + name, size = zip.Length, digest = "sha256:" + UpdatePackage.Hash(zip) },
                new { name = name + ".sha256", browser_download_url = address + name + ".sha256", size = checksum.Length, digest = "sha256:" + UpdatePackage.Hash(checksum) }
            }
        });
        using var listener = new HttpListener(); listener.Prefixes.Add(address); listener.Start();
        Task server = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var request = await listener.GetContextAsync();
                    byte[] body = request.Request.Url!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) ? release :
                        request.Request.Url.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? checksum : zip;
                    request.Response.ContentLength64 = body.Length;
                    await request.Response.OutputStream.WriteAsync(body); request.Response.Close();
                }
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
        });
        string? previous = Environment.GetEnvironmentVariable("DBXL_UPDATE_FEED");
        Encoding encoding = Console.OutputEncoding;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = Encoding.GetEncoding(932);
            Environment.SetEnvironmentVariable("DBXL_UPDATE_FEED", address);
            string executable = Path.ChangeExtension(typeof(DbxLight.Cli.Program).Assembly.Location, ".exe");
            JsonElement result = new UpdateCli(executable, options).RunAsync("download", CancellationToken.None).GetAwaiter().GetResult();
            string staging = result.GetProperty("staging").GetString()!;
            TestRunner.Assert(staging.StartsWith(Path.GetDirectoryName(state)!, StringComparison.Ordinal) && staging.Contains("更新の状態", StringComparison.Ordinal));
            TestRunner.Equal(version, UpdatePackage.ValidateDirectory(staging).Version);
        }
        finally
        {
            Console.OutputEncoding = encoding;
            Environment.SetEnvironmentVariable("DBXL_UPDATE_FEED", previous);
            listener.Stop(); server.GetAwaiter().GetResult();
        }
    }
    private static void ResidentFakeRclone()
    {
        using var fake = new LoginFixture("mount");
        using var fixture = new Unit5Fixture("unit8-fake");
        fixture.Store.Save(fixture.Config with { RclonePath = fake.Executable, UpdateCheck = true });
        RunUpdateTray(fixture, async resident =>
        {
            int pid = fixture.State.Load()!.RclonePid;
            resident.UpdateCommandOverride = _ => Task.FromResult(Result(new UpdateCheckResult(UpdateVersion.Current, null, false, false, UpdateSource.ReleasesUrl, false)));
            await resident.CheckUpdateAsync(false);
            TestRunner.Equal(pid, fixture.State.Load()!.RclonePid);
            TestRunner.Assert(resident.Snapshot.Status.Running);
        });
        fake.AssertCalls("mount"); fake.AssertExited();
    }
    private static void ResidentResult()
    {
        using var fixture = ResidentFixture();
        ResidentTests.Run(fixture, async resident =>
        {
            var store = new UpdateStore(fixture.State.PathName); int notices = 0;
            resident.UpdateNoticeOverride = (title, _, _) => { notices++; TestRunner.Equal("バージョン 0.1.1 に更新しました", title); };
            store.WriteResult(new(true, "0.1.1", null, false, null, DateTimeOffset.UtcNow));
            await resident.ReadUpdateResultAsync(); await resident.ReadUpdateResultAsync(); TestRunner.Equal(1, notices);
            TestRunner.Assert(JsonFiles.Read<UpdateResult>(store.ResultPath).Read);
        });
    }
    private static IEnumerable<Control> Controls(Control parent)
    {
        foreach (Control control in parent.Controls) { yield return control; foreach (var nested in Controls(control)) yield return nested; }
    }
    private static void UpdateForms() => FormTests.Sta(() =>
    {
        string root = Root(); var config = new DbxConfig { CacheDir = Path.Combine(root, "cache"), RescueDir = Path.Combine(root, "rescue"), LogDir = Path.Combine(root, "logs") };
        var options = new AppOptions(Path.Combine(root, "config.json"), Path.Combine(root, "state.json"), RegistryRoot: RegistrationService.TestHive + "\\Unit8-" + Guid.NewGuid().ToString("N"));
        using var form = new SettingsForm(config, new RegistrationService(options.RegistryRoot), null, options, _ => Task.CompletedTask);
        TestRunner.Assert(Controls(form).OfType<CheckBox>().Single(c => c.Text == "新しいバージョンを自動で確認する").Checked);
        TestRunner.Assert(Controls(form).OfType<Label>().Any(l => l.Text == "バージョン " + UpdateVersion.Current));
        using var confirm = new UpdateConfirmationForm("0.1.1", "U:");
        TestRunner.Equal("更新する", ((Button)confirm.AcceptButton!).Text); TestRunner.Equal("キャンセル", ((Button)confirm.CancelButton!).Text);
    });
    private static void ActiveCli()
    {
        using var fixture = ResidentFixture();
        string executable = Path.Combine(fixture.Root, "target"); Directory.CreateDirectory(executable); File.WriteAllText(Path.Combine(executable, "DbxLight.exe"), "fake");
        var before = File.ReadAllBytes(fixture.State.PathName);
        var type = typeof(DbxLight.Cli.Program).Assembly.GetType("DbxLight.Cli.UpdateCommands")!;
        var method = type.GetMethod("RequireStopped", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var refusal = Fails<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [fixture.Controller, executable]), "target");
        TestRunner.Assert(refusal.InnerException is InvalidOperationException);
        const string reason = "常駐のメニューから更新するか、常駐とマウントを終了してから実行してください。";
        TestRunner.Equal(reason, refusal.InnerException!.Message);
        string target = Install(Root());
        string cliDirectory = Path.GetDirectoryName(typeof(DbxLight.Cli.Program).Assembly.Location)!;
        foreach (string name in new[] { "dbxl.exe", "dbxl.dll", "dbxl.deps.json", "dbxl.runtimeconfig.json", "DbxLight.Core.dll" })
            File.Copy(Path.Combine(cliDirectory, name), Path.Combine(target, name), overwrite: true);
        var targetBefore = Snapshot(target);
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(target, "dbxl.exe")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (string word in new[] { "--config", fixture.Config.PathName, "--state", fixture.State.PathName, "update", "apply", "--yes", "--from", "not-read.zip", "--json" })
            info.ArgumentList.Add(word);
        using var process = System.Diagnostics.Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        TestRunner.Assert(process.WaitForExit(15000)); TestRunner.Equal(1, process.ExitCode);
        using var json = JsonDocument.Parse(output.GetAwaiter().GetResult()); _ = errors.GetAwaiter().GetResult();
        TestRunner.Assert(!json.RootElement.GetProperty("ok").GetBoolean());
        TestRunner.Equal("failed", json.RootElement.GetProperty("code").GetString());
        TestRunner.Equal(reason, json.RootElement.GetProperty("message").GetString());
        Same(targetBefore, target);
        TestRunner.Assert(before.SequenceEqual(File.ReadAllBytes(fixture.State.PathName)));
    }
    private static void ApplyPreview()
    {
        string root = Root(); string state = Path.Combine(root, "state.json");
        var output = Cli("--state", state, "update", "apply", "--json");
        TestRunner.Equal(0, output.Exit); using var json = JsonDocument.Parse(output.Output);
        TestRunner.Assert(!json.RootElement.GetProperty("result").GetProperty("applied").GetBoolean()); TestRunner.Assert(!Directory.Exists(new UpdateStore(state).Root));
        var checksumPreview = Cli("--state", state, "update", "apply", "--from", Path.Combine(root, "not-read.zip"),
            "--sha256", new string('A', 64), "--json");
        TestRunner.Equal(0, checksumPreview.Exit); TestRunner.Assert(!Directory.Exists(new UpdateStore(state).Root));
    }
}
