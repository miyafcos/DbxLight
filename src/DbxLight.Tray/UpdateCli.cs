using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DbxLight.Core;

namespace DbxLight.Tray;

internal sealed class UpdateCli(string executable, AppOptions options)
{
    internal async Task<JsonElement> RunAsync(string command, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string word in new[] { "--config", options.ConfigPath, "--state", options.StatePath, "update", command, "--json" })
            info.ArgumentList.Add(word);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start the update command.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(command == "download" ? TimeSpan.FromMinutes(11) : TimeSpan.FromMinutes(3));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            using var doc = JsonDocument.Parse(await output);
            _ = await errors;
            JsonElement root = doc.RootElement;
            if (process.ExitCode != 0 || !root.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(root.TryGetProperty("message", out var message) ? message.GetString() : "Update command failed.");
            return root.GetProperty("result").Clone();
        }
        catch
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); }
            throw;
        }
    }
}
