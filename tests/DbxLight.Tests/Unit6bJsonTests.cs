using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    private static void JsonShellBytes()
    {
        using var f = new Unit5Fixture("u6b-json");
        const string marker = "見本フォルダ（見本）！";
        f.Store.Save(f.Config with { HiddenPaths = [marker, "a<&>.txt"] });
        RuntimeSnapshots.Save(f.State, new RuntimeState { ConfigPath = f.Store.PathName, ActiveConfig = f.Config,
            RecoveryRequired = true, StopDisposition = StopDisposition.Forced, StoppedUtc = DateTime.UtcNow,
            CacheInspection = RecoveryText.CacheUnknown(marker + " <>&", f.Config.CacheDir) });
        string cli = Path.ChangeExtension(typeof(DbxLight.Cli.Program).Assembly.Location, ".exe");
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        foreach (string[] command in new[] { new[] { "config", "show" }, ["config", "show", "--json"], ["hidden", "--json"], ["status", "--json"] })
        {
            string[] arguments = new[] { "--config", f.Store.PathName, "--state", f.State.PathName }.Concat(command).ToArray();
            string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
            string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var psInfo = RcloneArguments.ProcessInfo(ps, ["-NoProfile", "-NonInteractive", "-Command",
                "& " + PsQuote(cli) + " " + string.Join(' ', arguments.Select(PsQuote)) + "; exit $LASTEXITCODE"]);
            var bashInfo = RcloneArguments.ProcessInfo(bash, ["--noprofile", "--norc", "-c",
                BashQuote(cli.Replace('\\', '/')) + " " + string.Join(' ', arguments.Select(BashQuote))]);
            byte[]? first = null;
            foreach (var info in new[] { psInfo, bashInfo })
            {
                using Process child = Process.Start(info) ?? throw new IOException("Cannot start test shell.");
                using var output = new MemoryStream();
                Task bytes = child.StandardOutput.BaseStream.CopyToAsync(output);
                Task<string> error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(20000)) { child.Kill(entireProcessTree: true); child.WaitForExit(); throw new TimeoutException("Own shell timed out."); }
                bytes.GetAwaiter().GetResult();
                TestRunner.Equal(0, child.ExitCode);
                TestRunner.Equal("", error.GetAwaiter().GetResult());
                byte[] raw = output.ToArray();
                TestRunner.Assert(!raw.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
                string text = new UTF8Encoding(false, true).GetString(raw);
                using var doc = JsonDocument.Parse(text);
                TestRunner.Assert(text.Contains(marker, StringComparison.Ordinal));
                TestRunner.Assert(!text.Contains("\\u4E8B", StringComparison.OrdinalIgnoreCase));
                TestRunner.Assert(text.Contains("\\u003C") && text.Contains("\\u003E") && text.Contains("\\u0026"));
                if (first is not null) TestRunner.Assert(first.AsSpan().SequenceEqual(raw), "Shell output bytes differ.");
                first = raw;
                Console.WriteLine(Path.GetFileName(info.FileName) + " " + string.Join(' ', command) + ": UTF8(no BOM), literal Japanese/fullwidth, HTML escaped, valid JSON; bytes=" + raw.Length);
            }
        }
    }
}
