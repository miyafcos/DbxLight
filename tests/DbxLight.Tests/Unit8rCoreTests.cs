using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit8Tests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Fails<T>(Action action, string reason) where T : Exception
    {
        try { action(); }
        catch (T error)
        {
            TestRunner.Assert(error.Message.Contains(reason, StringComparison.Ordinal), typeof(T).Name + ": " + error.Message);
            return error;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + reason);
    }
    private static void ReviewFixCases(TestRunner runner, string? group)
    {
        void Test(string id, string name, Action action)
        { if (group is null or "review-fixes" || group == id) runner.Test("8r " + id + ": " + name, action); }
        foreach (string stage in new[] { "verify", "eligibility", "wait", "locked", "result-write" })
        { string value = stage; Test("R1", "unchanged failure restarts resident: " + value, () => RestartFailure(value)); }
        Test("R1", "PID reuse compares process start time", WaitIdentity);
        Test("R2", "successful rollback restores empty directory inventory", () => LateSwapFailure("backed-up"));
        Test("R2", "only complete readable backups consume retention slots", RetentionSlots);
        Test("R7", "recent incomplete stays; old incomplete goes; bad item does not stop cleanup", IncompleteCleanup);
        Test("R7", "junction is skipped without touching its destination", CleanupLink);
        Test("R8", "last-copy failure restores complete directory inventory", () => LateSwapFailure("before-create:release.json"));
        Test("R8", "partially created file is owned and removed on rollback", () => LateSwapFailure("created:payload.txt"));
        Test("R8", "installed manifest cannot redefine verified content", VerifiedManifest);
        Test("R8", "raced unmanaged file is never deleted by rollback", RacedFile);
        Test("R9", "stage messages never disclose raw reasons or ordinary backup paths", ResultMessages);
        Test("R10", "production release URL origin and repository boundaries without network", ProductionUrls);
        Test("R13", "direct Stage reaches newer-version validation", () =>
            Fails<InvalidDataException>(() => UpdatePackage.Stage(Package("0.0.9"), new UpdateStore(Path.Combine(Root(), "state.json")),
                "0.0.9", "0.1.0", requireNewer: true), "Only a newer release"));
        ResidentReviewCases(Test);
        NetworkReviewCases(Test);
    }
    private static void RestartFailure(string failure)
    {
        string root = Root(), target = Install(root);
        var store = new UpdateStore(Path.Combine(root, "state.json"));
        var incoming = UpdatePackage.Stage(Package("0.1.1"), store);
        if (failure == "verify") File.WriteAllText(Path.Combine(incoming.Staging, "payload.txt"), "corrupt", new UTF8Encoding(false));
        if (failure == "eligibility") File.WriteAllText(Path.Combine(target, "release.json"), "{}", new UTF8Encoding(false));
        if (failure == "result-write") Directory.CreateDirectory(store.ResultPath);
        var before = Snapshot(target); int launches = 0;
        var swap = new UpdateSwap(store) { RelaunchOverride = (_, _) => launches++ };
        if (failure == "wait") swap.WaitOverride = (_, limit) => { TestRunner.Equal(TimeSpan.FromMilliseconds(10), limit); return false; };
        swap.WaitLimit = TimeSpan.FromMilliseconds(10);
        if (failure == "result-write") swap.Checkpoint = point => { if (point == "validated") throw new IOException("injected unchanged failure"); };
        using var locked = failure == "locked" ? new FileStream(Path.Combine(target, "payload.txt"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        UpdateResult result = swap.Run(target, incoming.Staging, failure == "wait" ? Environment.ProcessId : 0, true, []);
        locked?.Dispose();
        TestRunner.Assert(!result.Success); TestRunner.Equal(1, launches); Same(before, target);
        if (failure != "result-write")
        {
            var written = JsonFiles.Read<UpdateResult>(store.ResultPath);
            TestRunner.Equal(failure == "eligibility" ? "target" : failure, ResultStage(written));
        }
        else TestRunner.Assert(result.Reason!.Contains("Result recording failed", StringComparison.Ordinal));
    }
    private static string? ResultStage(UpdateResult result) =>
        typeof(UpdateResult).GetProperty("Stage")?.GetValue(result) as string;
    private static void WaitIdentity()
    {
        string root = Root(), target = Install(root); var store = new UpdateStore(Path.Combine(root, "state.json"));
        var stage = UpdatePackage.Stage(Package("0.1.1"), store);
        var method = typeof(UpdateSwap).GetMethods().SingleOrDefault(m => m.Name == "Run" && m.GetParameters().Length == 6);
        TestRunner.Assert(method is not null, "Swap must accept the original process start time.");
        using var process = Process.GetCurrentProcess();
        var result = (UpdateResult)method!.Invoke(new UpdateSwap(store), [target, stage.Staging, process.Id, false, Array.Empty<string>(), process.StartTime.ToUniversalTime().AddSeconds(-1)])!;
        TestRunner.Assert(result.Success, result.Reason ?? "");
    }
    private static void LateSwapFailure(string point)
    {
        string root = Root(), target = Install(root); var before = Snapshot(target);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var stage = UpdatePackage.Stage(Package("0.1.1"), store);
        bool reached = false;
        var swap = new UpdateSwap(store) { Checkpoint = p => { if (p == point) { reached = true; throw new IOException("injected " + point); } } };
        var result = swap.Run(target, stage.Staging, 0, false, []);
        TestRunner.Assert(reached && !result.Success && result.RolledBack); Same(before, target);
        TestRunner.Assert(!Directory.Exists(result.BackupPath!)); TestRunner.Equal("swap", ResultStage(result));
    }
    private static string Backup(string target, int day)
    {
        string path = Path.Combine(target, "backup", "0.1.0-202601" + day.ToString("00") + "T0102030000000Z");
        Directory.CreateDirectory(path);
        foreach (string file in UpdatePackage.ReadManifest(target).Files.Select(f => f.Name).Append("release.json"))
            File.Copy(Path.Combine(target, file), Path.Combine(path, file));
        return path;
    }
    private static void RetentionSlots()
    {
        string root = Root(), target = Install(root); var store = new UpdateStore(Path.Combine(root, "state.json"));
        string[] complete = Enumerable.Range(1, 3).Select(day => Backup(target, day)).ToArray();
        string broken = Path.Combine(target, "backup", "0.1.0-20260105T0102030000000Z"); Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "release.json"), "{", new UTF8Encoding(false));
        string incomplete = Path.Combine(target, "backup", "0.1.0-20260106T0102030000000Z"); Directory.CreateDirectory(incomplete);
        File.Copy(Path.Combine(target, "release.json"), Path.Combine(incomplete, "release.json"));
        string empty = Path.Combine(target, "backup", "0.1.0-20260107T0102030000000Z"); Directory.CreateDirectory(empty);
        UpdateCleanup.Run(store, target);
        TestRunner.Assert(!Directory.Exists(complete[0]) && Directory.Exists(complete[1]) && Directory.Exists(complete[2]));
        TestRunner.Assert(Directory.Exists(broken) && Directory.Exists(incomplete) && !Directory.Exists(empty));
    }
    private static void IncompleteCleanup()
    {
        string root = Root(), target = Install(root); var store = new UpdateStore(Path.Combine(root, "state.json"));
        string recent = UpdatePackage.Stage(Package("0.1.1"), store).Staging;
        string old = UpdatePackage.Stage(Package("0.1.1"), store).Staging;
        foreach (string path in Directory.EnumerateFileSystemEntries(old).Append(old + ".json").Append(old))
            if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            else File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        string bad = Path.Combine(target, "backup", "0.1.0-20260109T0102030000000Z"); Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "release.json"), "{}", new UTF8Encoding(false));
        string completed = UpdatePackage.Stage(Package("0.1.1"), store).Staging; store.CompleteStage(completed);
        UpdateCleanup.Run(store, target);
        TestRunner.Assert(Directory.Exists(recent) && !Directory.Exists(old) && !Directory.Exists(completed) && Directory.Exists(bad));
    }
    private static void CleanupLink()
    {
        string root = Root(), target = Install(root); var store = new UpdateStore(Path.Combine(root, "state.json"));
        string destination = Path.Combine(root, "link-destination"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep", new UTF8Encoding(false));
        string linked = UpdatePackage.Stage(Package("0.1.1"), store).Staging; store.CompleteStage(linked);
        string link = Path.Combine(linked, "junction");
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Arguments = "/d /c mklink /J \"" + link + "\" \"" + destination + "\"";
        using var process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); _ = output.GetAwaiter().GetResult(); string diagnostic = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        { Console.WriteLine("SKIP R7 link creation unavailable: exit=" + process.ExitCode + "; junction unavailable"); return; }
        string completed = UpdatePackage.Stage(Package("0.1.1"), store).Staging; store.CompleteStage(completed);
        UpdateCleanup.Run(store, target);
        TestRunner.Assert(Directory.Exists(linked) && !Directory.Exists(completed));
        TestRunner.Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Console.WriteLine("R7 junction created and preserved inside own run: " + root);
    }
    private static void VerifiedManifest()
    {
        string root = Root(), target = Install(root); var before = Snapshot(target);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var stage = UpdatePackage.Stage(Package("0.1.1"), store);
        var swap = new UpdateSwap(store) { Checkpoint = point =>
        {
            if (point != "verify") return;
            byte[] changed = Encoding.UTF8.GetBytes("replaced-content");
            File.WriteAllBytes(Path.Combine(target, "payload.txt"), changed);
            var manifest = UpdatePackage.ReadManifest(target);
            JsonFiles.Write(Path.Combine(target, "release.json"), manifest with { Files = manifest.Files.Select(f =>
                f.Name == "payload.txt" ? f with { Size = changed.Length, Sha256 = UpdatePackage.Hash(changed) } : f).ToArray() });
        } };
        var result = swap.Run(target, stage.Staging, 0, false, []);
        TestRunner.Assert(!result.Success && result.RolledBack); Same(before, target);
    }
    private static byte[] ExtraPackage()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var bytes = new Dictionary<string, byte[]> { ["DbxLight.exe"] = [1], ["dbxl.exe"] = [2], ["new.txt"] = [3] };
            bytes["release.json"] = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest(1, "0.1.1",
                bytes.Select(p => new ReleaseFile(p.Key, p.Value.Length, UpdatePackage.Hash(p.Value))).ToArray()), JsonFiles.Options);
            foreach (var pair in bytes) { using var output = zip.CreateEntry(pair.Key).Open(); output.Write(pair.Value); }
        }
        return memory.ToArray();
    }
    private static void RacedFile()
    {
        string root = Root(), target = Install(root); var before = Snapshot(target);
        var store = new UpdateStore(Path.Combine(root, "state.json")); var stage = UpdatePackage.Stage(ExtraPackage(), store);
        bool created = false;
        var swap = new UpdateSwap(store) { Checkpoint = point =>
        {
            if (point != "before-create:new.txt") return;
            File.WriteAllText(Path.Combine(target, "new.txt"), "foreign", new UTF8Encoding(false)); created = true;
        } };
        var result = swap.Run(target, stage.Staging, 0, false, []);
        TestRunner.Assert(created && !result.Success && result.RolledBack); TestRunner.Equal("foreign", File.ReadAllText(Path.Combine(target, "new.txt")));
        var after = Snapshot(target); foreach (var pair in before) TestRunner.Equal(pair.Value, after[pair.Key]);
        TestRunner.Equal(before.Count + 1, after.Count);
    }
    private static void ResultMessages()
    {
        using var fixture = ResidentFixture();
        ResidentTests.Run(fixture, async resident =>
        {
            var store = new UpdateStore(fixture.State.PathName);
            var notices = new List<(string Title, string Body)>();
            resident.UpdateNoticeOverride = (title, body, _) => notices.Add((title, body));
            foreach (var pair in new Dictionary<string, string> {
                ["verify"] = "ダウンロードした更新を確認できませんでした。", ["wait"] = "前のバージョンの終了を確認できませんでした。",
                ["locked"] = "ほかのプログラムが DbxLight のファイルを使用中でした。", ["swap"] = "ファイルの入れ替えに失敗したため、元に戻しました。",
                ["rollback"] = "前のバージョンのファイルは次の場所にあります: backup-location" })
            {
                var result = new UpdateResult(false, "0.1.1", "PRIVATE RAW EXCEPTION", pair.Key == "swap", "backup-location", DateTimeOffset.UtcNow);
                typeof(UpdateResult).GetProperty("Stage")?.SetValue(result, pair.Key);
                store.WriteResult(result); notices.Clear();
                await resident.ReadUpdateResultAsync();
                var notice = notices.Single();
                TestRunner.Equal(pair.Key == "rollback" ? "更新に失敗し、元に戻せませんでした" : "更新できませんでした", notice.Title);
                TestRunner.Equal((pair.Key == "rollback" ? "" : "前のバージョンのまま起動しました。") + pair.Value, notice.Body);
                TestRunner.Assert(!notice.Body.Contains("PRIVATE", StringComparison.Ordinal));
            }
        });
    }
    private static void ProductionUrls()
    {
        var source = new UpdateSource("invalid-feed"); TestRunner.Assert(source.TestFeed is null);
        string prefix = UpdateSource.ReleasesUrl[..^"/releases".Length];
        source.RequireAsset(new(prefix + "/releases/download/v0.1.1/file.zip"));
        string mixed = prefix[..19] + prefix[19..].ToUpperInvariant();
        source.RequireAsset(new(mixed + "/releases/download/v0.1.1/file.zip"));
        foreach (string bad in new[] { prefix + "-other/releases/download/v0.1.1/file.zip",
            "https://elsewhere.test/releases/download/file.zip", prefix.Replace("https:", "http:", StringComparison.Ordinal) + "/releases/download/file.zip",
            prefix + "/releases/download/../../../outside/file.zip", prefix.Replace("https://", "https://user@", StringComparison.Ordinal) + "/releases/download/file.zip",
            prefix + "/Releases/download/file.zip" })
            Fails<InvalidDataException>(() => source.RequireAsset(new(bad)), bad.Contains("user@", StringComparison.Ordinal) || bad.StartsWith("http:", StringComparison.Ordinal)
                ? "unsupported host or transport" : "outside the release source");
    }
}
