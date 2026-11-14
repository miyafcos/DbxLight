using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

public enum LoginOutcome { Succeeded, Cancelled, TimedOut, Failed }
public sealed record LoginResult(LoginOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == LoginOutcome.Succeeded;
}

/// <summary>Authentication output is drained to nowhere, including on failure and cancellation.</summary>
public sealed class RcloneLogin
{
    public const string RemoteName = "dbxlight";
    public static string DefaultConfigPath => CredentialSwitch.KeyPath(DbxConfig.DefaultConfigPath);
    public string ConfigPath { get; }
    private readonly string executable;
    internal string Executable => executable;
    private readonly TimeSpan timeout;
    internal Action<string>? FileCreated { get; init; }

    public RcloneLogin(string executable, string? configPath = null) : this(executable, configPath, TimeSpan.FromMinutes(5)) { }
    internal RcloneLogin(string executable, string? configPath, TimeSpan timeout)
    {
        this.executable = executable;
        ConfigPath = configPath ?? DefaultConfigPath;
        if (!Path.IsPathFullyQualified(ConfigPath)) throw new ArgumentException("Login config must be fully qualified.");
        this.timeout = timeout;
    }

    public async Task<IReadOnlyList<string>> ListRemotesAsync(bool dedicated, CancellationToken cancellation = default)
    {
        if (dedicated && !File.Exists(ConfigPath)) return [];
        var args = new List<string> { "listremotes" };
        if (dedicated) args.AddRange(["--config", ConfigPath]);
        var result = await RunAsync(args, capture: true, cancellation);
        if (result.Outcome != LoginOutcome.Succeeded) throw new IOException("接続先の一覧を取得できませんでした。rclone を確認して、もう一度お試しください。");
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => Regex.IsMatch(s, @"^[\p{L}\p{N}_ .-]+:$", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal RcloneLogin AtPath(string path) => new(executable, path, timeout) { FileCreated = FileCreated };

    internal PrivateLoginFile BeginFileChange(ConfigStore config) => new(config, ConfigPath, FileCreated);

    internal async Task<LoginResult> AuthorizeAsync(string workingPath, CancellationToken cancellation = default)
    {
        var result = await RunAsync(["config", "create", RemoteName, "dropbox", "--config", workingPath, "--no-output"], false, cancellation);
        return Explain(result.Outcome, result.ExitCode);
    }

    internal async Task DeleteAsync(string workingPath, CancellationToken cancellation = default)
    {
        var result = await RunAsync(["config", "delete", RemoteName, "--config", workingPath], false, cancellation);
        if (result.Outcome != LoginOutcome.Succeeded) throw new IOException("ログアウトできませんでした。接続先の設定は保持しています。もう一度お試しください。");
    }

    private static LoginResult Explain(LoginOutcome outcome, int? exitCode) => new(outcome, outcome switch
    {
        LoginOutcome.Succeeded => "Dropbox へのログインが完了しました。",
        LoginOutcome.Cancelled => "ログインを取り消しました。もう一度始められます。",
        LoginOutcome.TimedOut => "5 分以内に認可が完了しませんでした。もう一度ログインしてください。",
        _ => exitCode is { } code
            ? $"rclone がエラーで終了しました（終了値 {code}）。ブラウザの認可と接続を確認して、もう一度お試しください。"
            : "rclone のログイン処理を開始または完了できませんでした。実行ファイルと接続を確認して、もう一度お試しください。"
    });

    private async Task<(LoginOutcome Outcome, string Output, int? ExitCode)> RunAsync(IEnumerable<string> args, bool capture, CancellationToken cancellation)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        using var process = new JobProcess(RcloneArguments.ProcessInfo(executable, args));
        Task<string>? output = null;
        Task? discarded = null, errors = null;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!process.Start()) return (LoginOutcome.Failed, "", null);
            // Standard input is NUL (EOF); config create takes documented defaults.
            errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            if (capture) output = ReadBoundedAsync(process.StandardOutput);
            else discarded = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            process.StopJob();
            if (errors is not null) await errors.ConfigureAwait(false);
            if (discarded is not null) await discarded.ConfigureAwait(false);
            string names = output is null ? "" : await output.ConfigureAwait(false);
            return (process.ExitCode == 0 ? LoginOutcome.Succeeded : LoginOutcome.Failed, names, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            process.StopJob();
            await StopOwnedAsync(process).ConfigureAwait(false);
            await DrainAsync(output, discarded, errors).ConfigureAwait(false);
            return (cancellation.IsCancellationRequested ? LoginOutcome.Cancelled : LoginOutcome.TimedOut, "", null);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            process.StopJob();
            await StopOwnedAsync(process).ConfigureAwait(false);
            await DrainAsync(output, discarded, errors).ConfigureAwait(false);
            // No inner exception or process text may carry OAuth tokens to a caller.
            return (LoginOutcome.Failed, "", null);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] buffer = new char[1024];
        int count;
        bool overflow = false;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count <= 65536) text.Append(buffer, 0, count);
            else overflow = true;
        }
        return overflow ? "" : text.ToString();
    }

    private static async Task StopOwnedAsync(JobProcess process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException) { /* Start failed: no owned process exists. */ }
    }

    private static async Task DrainAsync(params Task?[] tasks)
    {
        foreach (Task? task in tasks)
            if (task is not null)
                try { await task.ConfigureAwait(false); } catch (IOException) { }
    }
}
