using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tray;

internal static class SmokeRunner
{
    public static int Run(AppOptions options)
    {
        string workRoot;
        try { workRoot = TestWorkPaths.WorkRoot; }
        catch (IOException error) { Console.Error.WriteLine(error.Message); return 1; }
        string output = Path.GetFullPath(options.SmokeOutput!);
        Directory.CreateDirectory(output);
        string root = Path.Combine(workRoot, "run-tray-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        string remote = Path.Combine(root, "remote");
        foreach (string path in new[] { "01 業務資料", "01 業務資料/見本", "02 共有", "03 記録" }) Directory.CreateDirectory(Path.Combine(remote, path));
        string emptyConfig = Path.Combine(root, "isolated.conf");
        File.WriteAllText(emptyConfig, "", new UTF8Encoding(false));
        string drive = new[] { "U:", "V:", "W:" }.FirstOrDefault(MountPaths.IsAvailable) ?? throw new IOException("試験用の空きドライブがありません。");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var config = new DbxConfig
        {
            Remote = remote, WriteAllowedRemotes = [], MountPoint = drive, RcloneConfigPath = emptyConfig,
            CacheDir = Path.Combine(root, "cache"), RescueDir = Path.Combine(root, "rescue"), LogDir = Path.Combine(root, "logs"),
            CacheMaxSize = "8M", CacheMinFreeSpace = "16M", PollInterval = "0", DirCacheTime = "1m", RcPort = port,
            HiddenPaths = ["03 記録"], TestOverrides = new() { WriteBack = "10m", CachePollInterval = "1s" }
        };
        var local = options with
        {
            ConfigPath = Path.Combine(root, "config.json"), StatePath = Path.Combine(root, "runtime", "state.json"),
            RegistryRoot = RegistrationService.TestHive + "\\Smoke-" + Guid.NewGuid().ToString("N")
        };
        new ConfigStore(local.ConfigPath).Save(config);
        JsonFiles.Write(Path.Combine(output, "smoke-plan.json"), new { root, configPath = local.ConfigPath, statePath = local.StatePath, trayPid = Environment.ProcessId });
        var evidence = new SmokeEvidence { RunFolder = root, Drive = drive, ConfigPath = local.ConfigPath, StatePath = local.StatePath, TrayPid = Environment.ProcessId };
        using var instance = new SingleInstance(local.StatePath);
        if (!instance.IsFirst) throw new IOException("Smoke runtime already has an owner.");
        using var context = new ResidentContext(local, instance, async resident =>
        {
            var owned = new Dictionary<int, RuntimeState>();
            void Track()
            {
                RuntimeState? state = resident.Controller.State.Load();
                if (state is not null) owned.TryAdd(state.RclonePid, state);
            }
            try
            {
                await Task.Run(Track);
                foreach (int percent in new[] { 100, 150 })
                {
                    string suffix = percent == 100 ? "" : "-150";
                    using (StatusForm form = SampleStatus(resident))
                        await CaptureAsync(form, Path.Combine(output, "status-readonly" + suffix + ".png"), percent, evidence);
                    using (SettingsForm form = SampleSettings(resident, local))
                        await CaptureAsync(form, Path.Combine(output, "settings-readonly" + suffix + ".png"), percent, evidence);
                    using (var form = new StopChoiceForm(1, exiting: true, reason: "Stop refused: uploadsQueued=1, uploadsInProgress=0. Wait for uploads or use stop --force explicitly."))
                        await CaptureAsync(form, Path.Combine(output, "force-stop" + suffix + ".png"), percent, evidence);
                    using (var form = new StopChoiceForm(1, exiting: false, reason: "Stop refused: uploadsQueued=1, uploadsInProgress=0. Wait for uploads or use stop --force explicitly."))
                        await CaptureAsync(form, Path.Combine(output, "force-pause" + suffix + ".png"), percent, evidence);
                }
                await resident.ApplyConfigAsync(c => c with { WriteAllowedRemotes = [remote] });
                await Task.Run(Track);
                await Task.Run(() => File.WriteAllText(Path.Combine(MountPaths.CanonicalMount(drive), "アップロード待ち サンプル.txt"), "local smoke queue", new UTF8Encoding(false)));
                var wait = Stopwatch.StartNew();
                do { await Task.Delay(200); await resident.RefreshAsync(); }
                while (resident.Snapshot.Status.UploadsQueued != 1 && wait.Elapsed < TimeSpan.FromSeconds(15));
                if (resident.Snapshot.Status.UploadsQueued != 1 || resident.Snapshot.Queue.Count != 1) throw new InvalidOperationException("The smoke queue must contain exactly one real item.");
                evidence.QueueCount = resident.Snapshot.Queue.Count;
                foreach (int percent in new[] { 100, 150 })
                {
                    string suffix = percent == 100 ? "" : "-150";
                    using (SettingsForm form = SampleSettings(resident, local))
                        await CaptureAsync(form, Path.Combine(output, "settings" + suffix + ".png"), percent, evidence);
                    using (HiddenFoldersForm form = resident.CreateHiddenForm())
                        await CaptureAsync(form, Path.Combine(output, "hidden-folders" + suffix + ".png"), percent, evidence,
                            async () => await form.ExpandFirstFolderAsync());
                    using (StatusForm form = SampleStatus(resident))
                        await CaptureAsync(form, Path.Combine(output, "status" + suffix + ".png"), percent, evidence);
                }
                SaveIcons(Path.Combine(output, "tray-icons.png"));
                using Process self = Process.GetCurrentProcess();
                TimeSpan remaining = TimeSpan.FromSeconds(30) - (DateTime.UtcNow - self.StartTime.ToUniversalTime());
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                self.Refresh();
                evidence.PrivateBytesAt30Seconds = self.PrivateMemorySize64;
                evidence.MeasuredAtSeconds = (DateTime.UtcNow - self.StartTime.ToUniversalTime()).TotalSeconds;
                await Task.Run(() => resident.Controller.Stop(true));
                TraySnapshot recovery = await Task.Run(() => SnapshotReader.Read(resident.Controller));
                if (!recovery.Status.RecoveryRequired) throw new InvalidOperationException("Forced pending stop must require recovery.");
                foreach (int percent in new[] { 100, 150 })
                {
                    using var form = new StatusForm();
                    form.UpdateSnapshot(SampleSnapshot(recovery, resident.Config, resident.Controller.State.Load()), null, SampleConfig(resident.Config));
                    string suffix = percent == 100 ? "" : "-150";
                    await CaptureAsync(form, Path.Combine(output, "status-recovery" + suffix + ".png"), percent, evidence);
                }
                var extracted = new RescueRecord[]
                {
                    new() { Id = "sample-damaged", TrustedConfig = SampleConfig(resident.Config), CloudPath = "\u898b\u672c/\u4fdd\u5b58\u5206.txt",
                        OriginalPath = @"D:\Sample\cache\saved.txt", RecordPath = @"D:\Sample\rescue\.dbxl-records\sample.json",
                        DestinationPath = @"D:\Sample\rescue\20261113\保存分.txt",
                        Reason = "\u7ba1\u7406\u60c5\u5831\u306e\u5f62\u5f0f\u3092\u8aad\u307f\u53d6\u308c\u307e\u305b\u3093\u3002", MoveCompleted = true },
                    new() { Id = "sample-conflict", TrustedConfig = SampleConfig(resident.Config), CloudPath = "\u5171\u6709/\u539f\u7a3f.txt",
                        OriginalPath = @"D:\Sample\cache\draft.txt", RecordPath = @"D:\Sample\rescue\.dbxl-records\sample2.json",
                        DestinationPath = @"D:\Sample\rescue\20261113\原稿.txt",
                        Reason = "\u5143\u306b\u3057\u305f Dropbox \u4e0a\u306e\u30d5\u30a1\u30a4\u30eb\u304c\u66f4\u65b0\u3055\u308c\u3066\u3044\u307e\u3059\u3002",
                        Conflict = true, CompleteData = true, MoveCompleted = true }
                };
                foreach (int percent in new[] { 100, 150 })
                {
                    var sample = SampleSnapshot(recovery, resident.Config, resident.Controller.State.Load());
                    using var form = new StatusForm();
                    form.UpdateSnapshot(sample with { Status = sample.Status with { RescueFiles = extracted.Length }, Rescued = extracted }, null, SampleConfig(resident.Config));
                    await CaptureAsync(form, Path.Combine(output, percent == 100 ? "status-rescue.png" : "status-rescue-150.png"), percent, evidence,
                        () => { form.ScrollToHistory(); return Task.CompletedTask; });
                }
                foreach (int percent in new[] { 100, 150 })
                {
                    var sample = SampleSnapshot(recovery, resident.Config, resident.Controller.State.Load());
                    using var form = new StatusForm { WorkAreaOverride = new Rectangle(0, 0, 1600, 1400) };
                    form.UpdateSnapshot(sample with { Status = sample.Status with { RescueFiles = extracted.Length }, Rescued = extracted },
                        null, SampleConfig(resident.Config));
                    await CaptureAsync(form, Path.Combine(output, "status-rescue-large-" + percent + ".png"), percent, evidence);
                }
                foreach (int percent in new[] { 100, 125, 150 })
                {
                    var sample = SampleSnapshot(recovery, resident.Config, resident.Controller.State.Load());
                    using var form = new StatusForm { WorkAreaOverride = new Rectangle(0, 0, 1366, 700) };
                    form.UpdateSnapshot(sample with { Status = sample.Status with { RescueFiles = extracted.Length }, Rescued = extracted },
                        null, SampleConfig(resident.Config));
                    await CaptureAsync(form, Path.Combine(output, "status-rescue-small-" + percent + ".png"), percent, evidence, closeAfter: false);
                    form.ScrollToHistory();
                    await CaptureAsync(form, Path.Combine(output, "status-rescue-small-" + percent + "-history.png"), 100, evidence, closeAfter: false);
                    form.ScrollToEnd();
                    await CaptureAsync(form, Path.Combine(output, "status-rescue-small-" + percent + "-end.png"), 100, evidence);
                }
                foreach (int percent in new[] { 100, 125, 150 })
                {
                    using var form = new StatusForm();
                    var sample = SampleSnapshot(recovery, resident.Config);
                    var progress = new PreparationProgress("Dropbox 上との照合", 12, 40, DateTimeOffset.UtcNow, false);
                    form.UpdateSnapshot(sample with { Status = sample.Status with { Running = false, Ready = false,
                        RecoveryRequired = false, RecoveryLocation = null, Preparation = progress, RescueCountsKnown = false, LogCapture = new() }, Rescued = [] }, null, SampleConfig(resident.Config));
                    await CaptureAsync(form, Path.Combine(output, "status-preparing-" + percent + ".png"), percent, evidence);
                }
                foreach (int percent in new[] { 100, 150 })
                {
                    using var form = new StatusForm();
                    var sample = SampleSnapshot(resident.Snapshot, resident.Config);
                    form.UpdateSnapshot(sample with { Queue = [], QueueKnown = false, Errors = [ErrorLogTail.ProviderError], Error = null,
                        LogReadSucceeded = false, Status = sample.Status with
                        { LogCapture = new(true, false, "ログはありますが読めません。失敗の有無を確認できません。", @"D:\Sample\logs") } },
                        null, SampleConfig(resident.Config));
                    await CaptureAsync(form, Path.Combine(output, "status-log-unreadable-" + percent + ".png"), percent, evidence);
                }
                foreach (int percent in new[] { 100, 150 })
                {
                    using var form = new StatusForm();
                    var sample = new TraySnapshot(new() { ReadOnly = true, ReadOnlySource = "configured" },
                        [], [], false, null, ActiveConfig: SampleConfig(resident.Config));
                    form.UpdateSnapshot(sample, null, SampleConfig(resident.Config));
                    await CaptureAsync(form, Path.Combine(output, "status-stopped-" + percent + ".png"), percent, evidence);
                }
                await CaptureSetupAsync(root, output, drive, evidence);
                if (evidence.LayoutIssues.Count != 0) throw new InvalidOperationException("Layout validation failed: " + string.Join("\n", evidence.LayoutIssues));
                evidence.Success = true;
            }
            catch (Exception e) { evidence.Error = e.ToString(); Environment.ExitCode = 1; }
            finally
            {
                await Task.Run(Track);
                try { await resident.ShutdownOwnedAsync(); }
                catch (Exception e) { evidence.Success = false; evidence.Error += "\nCleanup: " + e; Environment.ExitCode = 1; }
                evidence.OwnedRclonePids = owned.Keys.ToArray();
                evidence.RemainingPids = owned.Values.Where(state =>
                {
                    using Process? alive = ProcessIdentity.Open(state);
                    return alive is not null;
                }).Select(s => s.RclonePid).ToArray();
                evidence.TrayExitScheduled = true;
                evidence.Success &= evidence.RemainingPids.Length == 0 && evidence.RemainingLoginPids.Length == 0;
                long bytes = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                evidence.RunBytes = bytes;
                evidence.Success &= bytes <= 50 * 1024 * 1024;
                if (!evidence.Success) Environment.ExitCode = 1;
                JsonFiles.Write(Path.Combine(output, "smoke.json"), evidence, backup: true);
                JsonFiles.Write(Path.Combine(output, "ui-text.json"), evidence.ImageTexts, backup: true);
            }
        });
        Application.Run(context);
        // Startup failure before the capture callback still produces diagnostic evidence.
        if (!File.Exists(Path.Combine(output, "smoke.json")))
        {
            evidence.Error = "Startup failed before image capture."; evidence.Success = false;
            JsonFiles.Write(Path.Combine(output, "smoke.json"), evidence, backup: true);
            return 1;
        }
        return evidence.Success ? 0 : 1;
    }

    private static DbxConfig SampleConfig(DbxConfig config) => config with
    {
        Remote = "dbxlight:", RclonePath = "rclone.exe", RcloneConfigPath = @"D:\Sample\rclone.conf",
        CacheDir = @"D:\Sample\cache", RescueDir = @"D:\Sample\rescue", LogDir = @"D:\Sample\logs", TestOverrides = null,
        WriteAllowedRemotes = config.WriteAllowedRemotes.Contains(config.Remote, StringComparer.Ordinal) ? ["dbxlight:"] : []
    };

    private static TraySnapshot SampleSnapshot(TraySnapshot snapshot, DbxConfig config, RuntimeState? recovery = null)
    {
        DbxConfig sample = SampleConfig(snapshot.ActiveConfig ?? config);
        return snapshot with
        {
            ActiveConfig = sample,
            Status = snapshot.Status with
            {
                RecoveryLocation = recovery is null ? null : RecoveryDestination.Explain(recovery with
                {
                    ActiveConfig = sample, CacheInspection = RecoveryText.CachePending(1, @"D:\Sample\cache")
                })
            }
        };
    }

    private static StatusForm SampleStatus(ResidentContext resident)
    {
        var form = new StatusForm();
        form.UpdateSnapshot(SampleSnapshot(resident.Snapshot, resident.Config), resident.Oldest, SampleConfig(resident.Config));
        return form;
    }

    private static SettingsForm SampleSettings(ResidentContext resident, AppOptions options)
    {
        DbxConfig sample = SampleConfig(resident.Config);
        var shown = options with { ConfigPath = @"D:\Sample\config.json", StatePath = @"D:\Sample\runtime\state.json" };
        var form = new SettingsForm(sample, new RegistrationService(options.RegistryRoot), @"D:\Sample\DbxLight.exe", shown,
            _ => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
        form.UpdateSnapshot(SampleSnapshot(resident.Snapshot, resident.Config), sample);
        return form;
    }

    private static async Task CaptureSetupAsync(string root, string output, string drive, SmokeEvidence evidence)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DbxLight.sln"))) repository = repository.Parent;
        if (repository is null) throw new IOException("Smoke setup requires the solution build including the fake rclone project.");
        string source = Path.Combine(repository.FullName, "tests", "DbxLight.FakeRclone", "bin", "Release", "net8.0-windows");
        string fake = Path.Combine(root, "setup", "fake"); Directory.CreateDirectory(fake);
        foreach (string path in Directory.GetFiles(source)) File.Copy(path, Path.Combine(fake, Path.GetFileName(path)));
        string executable = Path.Combine(fake, "DbxLight.FakeRclone.exe");
        var controller = new MountController(new ConfigStore(Path.Combine(root, "setup", "config.json")),
            new StateStore(Path.Combine(root, "setup", "runtime", "state.json")));
        controller.Config.Save(new DbxConfig
        {
            RclonePath = executable, MountPoint = drive, CacheDir = Path.Combine(root, "setup", "cache"), RescueDir = Path.Combine(root, "setup", "rescue"),
            LogDir = Path.Combine(root, "setup", "logs")
        });
        try
        {
            using var form = new SetupForm(new SetupSession(controller, Path.Combine(root, "setup", "private", "rclone.conf")));
            await CaptureAsync(form, Path.Combine(output, "setup-1.png"), 100, evidence, closeAfter: false);
            if (!form.Session.Next()) throw new InvalidOperationException("Smoke prerequisites failed.");
            form.Render();
            ComboBox remotes = Descendants(form).OfType<ComboBox>().Single();
            remotes.Items.AddRange((await form.Session.ExistingRemotesAsync()).Cast<object>().ToArray()); remotes.SelectedIndex = 0;
            await CaptureAsync(form, Path.Combine(output, "setup-2.png"), 100, evidence, closeAfter: false);
            if (!(await form.Session.AuthorizeAsync(false)).Succeeded) throw new InvalidOperationException("Fake smoke login failed.");
            await form.Session.SelectScopeAsync(false); form.Render();
            await CaptureAsync(form, Path.Combine(output, "setup-3.png"), 100, evidence, closeAfter: false);
            form.Session.Next(); form.Session.SetHidden(["02 共有"]); form.Render();
            await CaptureAsync(form, Path.Combine(output, "setup-4.png"), 100, evidence, closeAfter: false);
            form.Session.SetOptions(drive, "1h"); form.Session.Next(); form.Render();
            await CaptureAsync(form, Path.Combine(output, "setup-5.png"), 100, evidence, closeAfter: false);
            form.Close();
            foreach (int percent in new[] { 100, 125, 150 })
            {
                using var confirmation = new WriteConfirmationForm("dbxlight:");
                await CaptureAsync(confirmation, Path.Combine(output, percent == 100 ? "write-confirm.png" : "write-confirm-" + percent + ".png"), percent, evidence);
            }
        }
        finally
        {
            string calls = Path.Combine(fake, "calls.jsonl");
            if (File.Exists(calls))
            {
                int[] ids = File.ReadAllLines(calls).Where(s => s.Length > 0).Select(s =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(s); return doc.RootElement.GetProperty("pid").GetInt32();
                }).Distinct().ToArray();
                evidence.OwnedLoginPids = ids;
                evidence.RemainingLoginPids = ids.Where(id =>
                {
                    try { using var process = Process.GetProcessById(id); return !process.HasExited && ProcessIdentity.ImagePath(process) == executable; }
                    catch (ArgumentException) { return false; }
                }).ToArray();
            }
        }
    }

    private static IEnumerable<string> VisibleText(Form form)
    {
        foreach (Control control in Descendants(form).Prepend(form).Where(c => c.Visible))
        {
            if (control.Text.Length > 0) yield return control.Text;
            if (control is DataGridView grid)
                foreach (DataGridViewRow row in grid.Rows)
                    foreach (DataGridViewCell cell in row.Cells)
                        if (cell.Value is { } value) yield return value.ToString()!;
            if (control is ListView list)
                foreach (ListViewItem item in list.Items)
                    foreach (ListViewItem.ListViewSubItem cell in item.SubItems) yield return cell.Text;
            if (control is TreeView tree)
            {
                IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes) => nodes.Cast<TreeNode>().SelectMany(n => new[] { n }.Concat(Nodes(n.Nodes)));
                foreach (TreeNode node in Nodes(tree.Nodes)) yield return node.Text;
            }
        }
    }

    internal static async Task CaptureShareScreensAsync(string output, Form share, Form confirm, Form settings)
    {
        Directory.CreateDirectory(output);
        var evidence = new SmokeEvidence { RunFolder = Path.GetFullPath(output) };
        await CaptureAsync(share, Path.Combine(output, "share-folder.png"), 100, evidence);
        await CaptureAsync(confirm, Path.Combine(output, "share-confirm.png"), 100, evidence);
        await CaptureAsync(settings, Path.Combine(output, "copy-path-settings.png"), 100, evidence);
        if (evidence.LayoutIssues.Count != 0) throw new InvalidOperationException(string.Join("\n", evidence.LayoutIssues));
    }

    private static async Task CaptureAsync(Form form, string path, int percent, SmokeEvidence evidence, Func<Task>? prepare = null, bool closeAfter = true)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
        if (percent != 100)
        {
            var controls = Descendants(form).Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
            form.Scale(new SizeF(percent / 100f, percent / 100f));
            foreach (var pair in controls) pair.Control.Font = new Font(pair.Font.FontFamily, pair.Font.Size * percent / 100f, pair.Font.Style);
        }
        form.Show();
        await Task.Delay(150);
        form.PerformLayout();
        if (prepare is not null) { await prepare(); await Task.Delay(50); form.PerformLayout(); }
        if (form is StatusForm status && status.WorkAreaOverride is { } area)
        {
            bool fits = form.Width <= area.Width && form.Height <= area.Height;
            evidence.WorkAreaLayouts.Add($"{Path.GetFileName(path)}: workArea={area.Width}x{area.Height}, window={form.Width}x{form.Height}, fits={fits}");
            if (!fits) evidence.LayoutIssues.Add(Path.GetFileName(path) + ": window exceeds small work area");
        }
        foreach (Label label in Descendants(form).OfType<Label>().Where(l => l.Visible && l.Text.Length > 0))
        {
            Size required = label.GetPreferredSize(new Size(label.Width, 0));
            if (required.Height > label.Height + 2 || required.Width > label.Width + 2)
                evidence.LayoutIssues.Add($"{Path.GetFileName(path)}: {label.Text}: required={required}, actual={label.Size}");
        }
        foreach (TextBox text in Descendants(form).OfType<TextBox>().Where(t => t.Visible && t.Name == "recovery"))
        {
            int lastLineY = text.GetPositionFromCharIndex(Math.Max(0, text.TextLength - 1)).Y;
            int requiredHeight = lastLineY + text.Font.Height + 4;
            if (requiredHeight > text.ClientSize.Height)
                evidence.LayoutIssues.Add($"{Path.GetFileName(path)}: recovery text clipped: required={requiredHeight}, actual={text.ClientSize.Height}");
            evidence.RecoveryLayouts.Add($"{Path.GetFileName(path)}: logicalLines={text.Lines.Length}, requiredHeight={requiredHeight}, clientHeight={text.ClientSize.Height}");
        }
        foreach (Button button in Descendants(form).OfType<Button>().Where(b => b.Visible))
        {
            if (button.Parent is { } parent && !parent.ClientRectangle.Contains(button.Bounds))
            {
                bool reachable = false;
                if (parent is ScrollableControl { AutoScroll: true } scroll)
                {
                    Point previous = scroll.AutoScrollPosition;
                    scroll.ScrollControlIntoView(button);
                    scroll.PerformLayout();
                    reachable = scroll.ClientRectangle.Contains(button.Bounds);
                    scroll.AutoScrollPosition = new Point(-previous.X, -previous.Y);
                    scroll.PerformLayout();
                }
                if (!reachable) evidence.LayoutIssues.Add($"{Path.GetFileName(path)}: button outside parent: {button.Text}");
            }
        }
        string[] shown = VisibleText(form).ToArray();
        evidence.ImageTexts[Path.GetFileName(path)] = shown;
        foreach (string text in shown)
            if (text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) ||
                text.Contains(evidence.RunFolder, StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)C:[\\/]Users[\\/]|run-\d{8}|OAUTH_SECRET_UNIT4"))
                evidence.LayoutIssues.Add(Path.GetFileName(path) + ": private or real path in visible text");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        // WM_PRINT can let an off-screen scrolled child cover sibling chrome in the bitmap.
        // Repaint the actual fixed footer with its native control renderer at its real position.
        int border = (form.Width - form.ClientSize.Width) / 2;
        var chrome = new Size(border, form.Height - form.ClientSize.Height - border);
        foreach (Control footer in form.Controls.Cast<Control>().Where(c => c.Visible && c.Dock == DockStyle.Bottom))
        {
            Point position = form.PointToClient(footer.PointToScreen(Point.Empty)) + chrome;
            footer.DrawToBitmap(bitmap, new Rectangle(position, footer.Size));
            foreach (Button button in Descendants(footer).OfType<Button>().Where(b => b.Visible))
            {
                position = form.PointToClient(button.PointToScreen(Point.Empty)) + chrome;
                var bounds = new Rectangle(position, button.Size);
                if (!new Rectangle(Point.Empty, bitmap.Size).Contains(bounds))
                    evidence.LayoutIssues.Add(Path.GetFileName(path) + ": footer bitmap position is outside the form");
                button.DrawToBitmap(bitmap, bounds);
            }
        }
        Backup(path); bitmap.Save(path, ImageFormat.Png);
        evidence.Images.Add(path);
        if (closeAfter) form.Close();
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
    private static void Backup(string path)
    {
        if (File.Exists(path)) File.Copy(path, path + ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff"));
    }
    private static void SaveIcons(string path)
    {
        using var bitmap = new Bitmap(864, 128);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(SystemColors.Window);
        string[] names = ["稼働", "アップロード中", "注意", "失敗または停止", "準備中", "ダウンロード中"];
        for (int i = 0; i < names.Length; i++)
        {
            using Bitmap small = TrayIconFactory.Draw((TrayState)i, 16);
            using Bitmap large = TrayIconFactory.Draw((TrayState)i, 32);
            g.DrawImageUnscaled(small, i * 144 + 32, 24); g.DrawImageUnscaled(large, i * 144 + 64, 16);
            g.DrawString(names[i], Ui.AppFont, Brushes.Black, i * 144 + 16, 64);
            g.DrawString("16 px / 32 px", Ui.AppFont, Brushes.Black, i * 144 + 16, 88);
        }
        Backup(path); bitmap.Save(path, ImageFormat.Png);
    }
    private sealed class SmokeEvidence
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string RunFolder { get; set; } = "";
        public string Drive { get; set; } = "";
        public string ConfigPath { get; set; } = "";
        public string StatePath { get; set; } = "";
        public int TrayPid { get; set; }
        public int QueueCount { get; set; }
        public long PrivateBytesAt30Seconds { get; set; }
        public double MeasuredAtSeconds { get; set; }
        public int[] OwnedRclonePids { get; set; } = [];
        public int[] RemainingPids { get; set; } = [];
        public int[] OwnedLoginPids { get; set; } = [];
        public int[] RemainingLoginPids { get; set; } = [];
        public Dictionary<string, string[]> ImageTexts { get; set; } = [];
        public bool TrayExitScheduled { get; set; }
        public long RunBytes { get; set; }
        public List<string> Images { get; set; } = [];
        public List<string> LayoutIssues { get; set; } = [];
        public List<string> RecoveryLayouts { get; set; } = [];
        public List<string> WorkAreaLayouts { get; set; } = [];
    }
}
