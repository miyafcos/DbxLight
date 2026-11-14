using System.Diagnostics;
using System.Text.Json;

namespace DbxLight.Core;

public sealed record RemoteFolder(string Name, string Path, bool Hidden);

public static class FolderBrowser
{
    public static IReadOnlyList<RemoteFolder> List(DbxConfig config, string path = "", CancellationToken cancellation = default)
    {
        config = config.Validate(forMount: true);
        string relative = MountPaths.ToRelative(path, config.MountPoint, allowRoot: true);
        string target = JoinRemote(config.Remote, relative);
        var args = new List<string> { "lsjson", target, "--dirs-only", "--ask-password=false", "--ftp-ask-password=false", "--sftp-ask-password=false" };
        if (config.RcloneConfigPath is not null) { args.Add("--config"); args.Add(Path.GetFullPath(config.RcloneConfigPath)); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        using var process = new JobProcess(RcloneArguments.ProcessInfo(config.RclonePath, args));
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try { process.WaitForExitAsync(stop.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                process.StopJob();
                process.WaitForExit();
                Task.WhenAll(output, errors).GetAwaiter().GetResult();
                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException("フォルダの確認が時間切れになりました。もう一度お試しください。");
            }
            process.StopJob();
            string json = output.GetAwaiter().GetResult();
            errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new IOException();
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray().Where(item => item.GetProperty("IsDir").GetBoolean()).Select(item =>
            {
                string name = item.GetProperty("Name").GetString() ?? throw new InvalidDataException();
                string child = HiddenFolders.NormalizePath(relative.Length == 0 ? name : relative + "/" + name);
                return new RemoteFolder(name, child, HiddenFolders.IsHidden(child, config.HiddenPaths));
            }).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { throw; }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            process.StopJob();
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
            catch (InvalidOperationException) { }
            throw new IOException("フォルダを取得できませんでした。接続と認可を確認して、もう一度お試しください。");
        }
    }

    public static string JoinRemote(string remote, string relative)
    {
        if (relative.Length == 0) return remote;
        if (Path.IsPathFullyQualified(remote)) return Path.Combine(remote, relative.Replace('/', Path.DirectorySeparatorChar));
        return remote.EndsWith(':') ? remote + relative : remote.TrimEnd('/') + "/" + relative;
    }
}
