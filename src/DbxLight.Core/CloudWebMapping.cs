using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

internal sealed record CloudWebMapping(string Root)
{
    internal string Url(string relative) => CloudWebPaths.WebUrl(Root.Length == 0 ? relative : relative.Length == 0 ? Root : Root + "/" + relative);
    internal string Relative(string cloudPath)
    {
        cloudPath = HiddenFolders.NormalizePath(cloudPath, allowRoot: true);
        if (Root.Length == 0) return cloudPath;
        if (cloudPath.Equals(Root, StringComparison.OrdinalIgnoreCase)) return "";
        if (cloudPath.StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase)) return cloudPath[(Root.Length + 1)..];
        throw new ArgumentException("The URL is outside the configured remote subfolder.");
    }

    internal static CloudWebMapping? Read(DbxConfig config)
    {
        if (Path.IsPathFullyQualified(config.Remote) || config.RcloneConfigPath is null) return null;
        int colon = config.Remote.IndexOf(':');
        if (colon <= 0) return null;
        string name = config.Remote[..(colon + 1)], root;
        try { root = HiddenFolders.NormalizePath(config.Remote[(colon + 1)..], allowRoot: true); }
        catch (ArgumentException) { return null; }
        try
        {
            using var process = JobProcess.Start(RcloneArguments.ProcessInfo(config.RclonePath,
                ["listremotes", "--long", "--config", config.RcloneConfigPath, "--ask-password=false"]));
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            if (!process.WaitForExit(5000))
            {
                process.Kill(); process.WaitForExit(10000);
                return null;
            }
            string text = output.GetAwaiter().GetResult();
            error.GetAwaiter().GetResult(); // Provider diagnostics are discarded without retaining their text.
            if (process.ExitCode != 0) return null;
            string[] types = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line =>
                Regex.Match(line, @"^(?<name>.+:)\s+(?<type>\S+)\s*$", RegexOptions.CultureInvariant))
                .Where(match => match.Success && match.Groups["name"].Value.Equals(name, StringComparison.Ordinal))
                .Select(match => match.Groups["type"].Value).ToArray();
            return types.Length == 1 && types[0] == "dropbox" ? new(root) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return null; }
    }
}
