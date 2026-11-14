using System.Text.Json;
using DbxLight.Core;
using DbxLight.Tray;

namespace DbxLight.Tests;

internal static partial class Unit6gTests
{
    private static void RunFailures(Action<string, string, Action> test)
    {
        foreach (bool json in new[] { false, true }) test("J7", "inherited state ACL reports class location and next action; json=" + json, () => UnsafeStateMessage(json));
        test("J7", "unmarked external exception text is never public", ExternalFailureMessage);
        test("J7", "unknown cache ownership and incomplete scan retain a classified reason", CacheFailureMessages);
    }
    private static void UnsafeStateMessage(bool json)
    {
        using var f = new Unit5Fixture("u6g-start-failure");
        string folder = Path.Combine(f.Root, "inherited-state"); Directory.CreateDirectory(folder);
        var args = new List<string> { "--config", f.Store.PathName, "--state", Path.Combine(folder, "state.json"), "start" };
        if (json) args.Add("--json");
        var reply = Unit3cTests.Capture(() => DbxLight.Cli.Program.Main(args.ToArray()));
        TestRunner.Equal(1, reply.Exit);
        if (json)
        {
            using var doc = JsonDocument.Parse(reply.Output);
            TestRunner.Equal("private_storage_required", doc.RootElement.GetProperty("failureClass").GetString());
            TestRunner.Equal(folder, doc.RootElement.GetProperty("location").GetString());
            TestRunner.Assert(doc.RootElement.GetProperty("nextAction").GetString()!.Contains("\u307e\u3060\u5b58\u5728\u3057\u306a\u3044", StringComparison.Ordinal));
        }
        else
        {
            TestRunner.Assert(reply.Error.Contains("private_storage_required", StringComparison.Ordinal) && reply.Error.Contains(folder, StringComparison.Ordinal));
            TestRunner.Assert(reply.Error.Contains("\u307e\u3060\u5b58\u5728\u3057\u306a\u3044", StringComparison.Ordinal) && reply.Error.Contains("\u672c\u4eba\u3060\u3051", StringComparison.Ordinal));
        }
        Console.WriteLine("J7 start " + (json ? "JSON" : "text") + ": private_storage_required; location matches; next action selects a new nonexistent directory.");
    }
    private static void ExternalFailureMessage()
    {
        using var f = new Unit5Fixture("u6g-untrusted-failure");
        foreach (Exception error in new Exception[] { new IOException(Unit4Tests.Marker), new UnauthorizedAccessException(Unit4Tests.Marker),
            new System.ComponentModel.Win32Exception(5, Unit4Tests.Marker), new InvalidOperationException(Unit4Tests.Marker) })
        {
            StartupFailure.Annotate(error, f.Config, f.Store.PathName);
            TestRunner.Assert(!StartupFailure.PublicMessage(error).Contains(Unit4Tests.Marker, StringComparison.Ordinal));
            TestRunner.Assert(!JsonSerializer.Serialize(StartupFailure.Get(error), JsonFiles.Options).Contains(Unit4Tests.Marker, StringComparison.Ordinal));
            TestRunner.Assert(!Ui.Failure(error).Contains(Unit4Tests.Marker, StringComparison.Ordinal));
        }
        var unmarked = new IOException(Unit4Tests.Marker); TestRunner.Assert(!Ui.Failure(unmarked).Contains(Unit4Tests.Marker, StringComparison.Ordinal));
        try { PrivateStateFiles.EnsureParent(Path.Combine(f.Root, "state.json")); throw new InvalidOperationException("Unsafe fixture was accepted."); }
        catch (UnauthorizedAccessException own)
        {
            StartupFailure.Annotate(own, f.Config, f.Store.PathName);
            FormTests.Sta(() =>
            {
                using var form = new StatusForm(); form.UpdateSnapshot(new(new(), [], [], false, Ui.Failure(own)), null, f.Config);
                string text = FormTests.Field<TextBox>(form, "errors").Text;
                TestRunner.Assert(text.Contains("private_storage_required", StringComparison.Ordinal) && text.Contains(f.Root, StringComparison.Ordinal) && text.Contains("\u307e\u3060\u5b58\u5728\u3057\u306a\u3044", StringComparison.Ordinal));
            });
        }
    }
    private static void CacheFailureMessages()
    {
        using var f = new Unit5Fixture("u6g-owner-failure");
        Directory.CreateDirectory(f.Config.CacheDir);
        File.WriteAllText(Path.Combine(f.Config.CacheDir, "dbxl-owner.json"), "{ " + Unit4Tests.Marker);
        try { f.Controller.Start(); throw new InvalidOperationException("Malformed owner accepted."); }
        catch (PendingUploadsException error)
        {
            TestRunner.Equal("cache_owner_unknown", StartupFailure.Get(error)!.Classification);
            TestRunner.Assert(!StartupFailure.PublicMessage(error).Contains(Unit4Tests.Marker, StringComparison.Ordinal));
        }
        using var other = new Unit5Fixture("u6g-scan-failure");
        Directory.CreateDirectory(other.Config.CacheDir);
        string target = Path.Combine(other.Root, "junction-target"); Directory.CreateDirectory(target);
        string link = Path.Combine(other.Config.CacheDir, "vfs");
        using (var helper = System.Diagnostics.Process.Start(RcloneArguments.ProcessInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target]))!)
            TestRunner.Assert(helper.WaitForExit(10000) && helper.ExitCode == 0);
        try { other.Controller.Start(); throw new InvalidOperationException("Invalid cache accepted."); }
        catch (Exception error) when (error is IOException or PendingUploadsException)
        {
            TestRunner.Equal("cache_scan_incomplete", StartupFailure.Get(error)!.Classification);
            TestRunner.Equal(other.Config.CacheDir, StartupFailure.Get(error)!.Location);
            TestRunner.Assert(!StartupFailure.PublicMessage(error).Contains(Unit4Tests.Marker, StringComparison.Ordinal));
        }
        TestRunner.Assert((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }
}
