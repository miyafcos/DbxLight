using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DbxLight.Core;

// Authentication values have no printable representation and never leave this assembly.
internal sealed class SnapshotToken(string accessToken, DateTimeOffset expiresUtc)
{
    internal string AccessToken { get; } = accessToken;
    internal DateTimeOffset ExpiresUtc { get; } = expiresUtc;
    internal bool SameValue(SnapshotToken? other) =>
        other is not null && AccessToken == other.AccessToken && ExpiresUtc == other.ExpiresUtc;
    public override string ToString() => "[redacted]";
}

internal sealed record SnapshotRemote(string Name, string Prefix, bool TeamRoot, string? NamespaceId, bool UsesCustomApp = false);
internal sealed record SnapshotCredentialSource(string Path, string Key);
internal interface ISnapshotTokenBroker
{
    SnapshotRemote Remote { get; }
    Task<SnapshotToken> ReadAsync(CancellationToken token);
    Task RenewAsync(CancellationToken token);
    Task RenewRejectedAsync(SnapshotToken rejected, CancellationToken token) => RenewAsync(token);
    Task<SnapshotCredentialSource?> CopySourceAsync(byte[] nonce, CancellationToken token) => Task.FromResult<SnapshotCredentialSource?>(null);
}

internal sealed class SnapshotTokenBroker : ISnapshotTokenBroker
{
    private readonly DbxConfig config;
    private string? configPath;
    private (byte[] Nonce, SnapshotCredentialSource Source, SnapshotToken Value)? renewed;
    public SnapshotRemote Remote { get; private set; }

    internal SnapshotTokenBroker(DbxConfig config)
    {
        this.config = config;
        int colon = config.Remote.IndexOf(':');
        if (colon <= 0 || Path.IsPathFullyQualified(config.Remote) || config.Remote[..colon].Any(c => c is '/' or '\\' || char.IsControl(c)))
            throw new SnapshotException(2, "unsupported_remote");
        string suffix = config.Remote[(colon + 1)..];
        string prefix = suffix.Trim('/');
        SnapshotJson.Relative(prefix);
        Remote = new(config.Remote[..colon], prefix, suffix.StartsWith('/'), null);
    }

    public async Task<SnapshotToken> ReadAsync(CancellationToken token)
    {
        try
        {
            var saved = ReadConfig(await ResolveConfigPathAsync(token).ConfigureAwait(false));
            if (renewed is { } refreshed && refreshed.Source == await CopySourceAsync(refreshed.Nonce, token).ConfigureAwait(false))
                return refreshed.Value;
            renewed = null;
            return saved;
        }
        catch (SnapshotException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new SnapshotException(3, "auth_unavailable"); }
    }

    // Resolve once, before authentication, so the caller can protect its output roots.
    internal async Task<string> ResolveConfigPathAsync(CancellationToken token)
    {
        try
        {
            return configPath ??= config.RcloneConfigPath is { } selected ? Path.GetFullPath(selected) : await DefaultPath(token);
        }
        catch (OperationCanceledException) { throw; }
        catch (SnapshotException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new SnapshotException(3, "auth_unavailable"); }
    }

    private async Task<string> DefaultPath(CancellationToken token)
    {
        // This prints a path, never configuration or credentials. Use rclone's own default selection.
        string output = await Run(["config", "file"], capturePath: true, token);
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string path = lines.LastOrDefault()?.Trim() ?? "";
        if (!Path.IsPathFullyQualified(path)) throw new SnapshotException(3, "auth_unavailable");
        return Path.GetFullPath(path);
    }

    private SnapshotToken ReadConfig(string path, Stream? renewalConfig = null)
    {
        renewalConfig?.Write(Encoding.UTF8.GetBytes("[" + Remote.Name + "]\ntype = dropbox\n"));
        byte[] line = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        SnapshotToken? selectedToken = null;
        string? type = null, ns = null;
        bool inSection = false, found = false, customApp = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            int used = 0, sections = 0;
            void ParseLine()
            {
                ReadOnlySpan<byte> bytes = line.AsSpan(0, used).Trim((byte)'\r');
                if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
                int start = 0;
                while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t') start++;
                bytes = bytes[start..];
                if (bytes.IsEmpty || bytes[0] is (byte)'#' or (byte)';') return;
                if (bytes.StartsWith("RCLONE_ENCRYPT"u8)) throw new SnapshotException(3, "encrypted_config");
                if (bytes[0] == '[')
                {
                    string section = Encoding.UTF8.GetString(bytes).Trim();
                    inSection = section == "[" + Remote.Name + "]";
                    if (inSection && ++sections > 1) throw new SnapshotException(3, "unsupported_config");
                    found |= inSection; return;
                }
                if (!inSection) return;
                int equals = bytes.IndexOf((byte)'=');
                if (equals < 1) throw new SnapshotException(3, "unsupported_config");
                string key = Encoding.UTF8.GetString(bytes[..equals]).Trim();
                ReadOnlySpan<byte> value = bytes[(equals + 1)..];
                while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t') value = value[1..];
                if (key == "token")
                {
                    if (selectedToken is not null && renewalConfig is not null)
                        throw new SnapshotException(3, "unsupported_config");
                    selectedToken = ReadToken(value);
                    if (renewalConfig is not null) WriteExpiredToken(renewalConfig, value);
                    return;
                }
                if (renewalConfig is not null && key is ("client_id" or "client_secret" or "client_credentials"))
                {
                    renewalConfig.Write(bytes);
                    renewalConfig.WriteByte((byte)'\n');
                }
                // Only non-secret options needed to reproduce the namespace are decoded.
                if (key is "client_id" or "client_secret" && !value.IsEmpty) customApp = true;
                if (key == "type") type = Encoding.UTF8.GetString(value).Trim();
                if (key == "root_namespace") ns = Encoding.UTF8.GetString(value).Trim();
                if (key is "impersonate" or "impersonate_admin" or "shared_files" or "shared_folders" or "encoding")
                {
                    string setting = Encoding.UTF8.GetString(value).Trim();
                    if (setting.Length > 0 && !(key is "shared_files" or "shared_folders" &&
                        setting.Equals("false", StringComparison.OrdinalIgnoreCase)))
                        throw new SnapshotException(2, "unsupported_namespace");
                }
            }
            // Do not create strings for other remotes, refresh credentials or application secrets.
            int next;
            while ((next = stream.ReadByte()) >= 0)
            {
                if (next == '\n')
                {
                    ParseLine(); CryptographicOperations.ZeroMemory(line.AsSpan(0, used)); used = 0;
                }
                else
                {
                    if (used == line.Length) throw new SnapshotException(3, "unsupported_config");
                    line[used++] = (byte)next;
                }
            }
            if (used > 0) ParseLine();
            if (!found || type != "dropbox") throw new SnapshotException(2, "unsupported_remote");
            Remote = Remote with { NamespaceId = string.IsNullOrEmpty(ns) ? null : ns, UsesCustomApp = customApp };
            return selectedToken ?? throw new SnapshotException(3, "auth_unavailable");
        }
        finally { CryptographicOperations.ZeroMemory(line); ArrayPool<byte>.Shared.Return(line); }
    }

    // Only an opaque fingerprint enters private copy state. The same fingerprint is
    // also used with an ephemeral nonce to detect changes across mount startup.
    public async Task<SnapshotCredentialSource?> CopySourceAsync(byte[] nonce, CancellationToken token)
    {
        try
        {
            string file = await ResolveConfigPathAsync(token).ConfigureAwait(false);
            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 8 * 1024 * 1024 ||
                !GetFileInformationByHandleEx(stream.SafeFileHandle, 0, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()) ||
                info.ChangeTime <= 0)
                return null;
            var mac = await HMACSHA256.HashDataAsync(nonce, stream, token).ConfigureAwait(false);
            string key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { ChangeTime = info.ChangeTime, CreationTime = info.CreationTime,
                    File = Path.GetFullPath(file).ToUpperInvariant(), Content = Convert.ToHexString(mac) })));
            return new(file, key);
        }
        catch (Exception e) when (e is SnapshotException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal uint FileAttributes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        out FileBasicInfo info, uint size);

    private static SnapshotToken ReadToken(ReadOnlySpan<byte> value)
    {
        string? access = null;
        DateTimeOffset expiry = DateTimeOffset.MaxValue;
        var reader = new Utf8JsonReader(value);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new SnapshotException(3, "auth_unavailable");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new SnapshotException(3, "auth_unavailable");
            bool isAccess = reader.ValueTextEquals("access_token"u8), isExpiry = reader.ValueTextEquals("expiry"u8);
            if (!reader.Read()) throw new SnapshotException(3, "auth_unavailable");
            if (isAccess && reader.TokenType == JsonTokenType.String) access = reader.GetString();
            else if (isExpiry)
            {
                if (reader.TokenType != JsonTokenType.String || !DateTimeOffset.TryParse(reader.GetString(),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiry))
                    throw new SnapshotException(3, "auth_unavailable");
                // oauth2 treats the zero expiry (and an absent expiry) as non-expiring.
                if (expiry == DateTimeOffset.MinValue) expiry = DateTimeOffset.MaxValue;
            }
            else reader.Skip();
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read() ||
            string.IsNullOrEmpty(access) || access.Any(char.IsControl))
            throw new SnapshotException(3, "auth_unavailable");
        return new(access, expiry);
    }

    // About always calls the authenticated Dropbox quota API, including at an empty root.
    // Keep the argument builder shared with the real-rclone local integration test.
    internal static IReadOnlyList<string> RenewalArguments(DbxConfig config)
    {
        var args = new List<string> { "about", config.Remote, "--json",
            "--contimeout", "5s", "--timeout", "10s", "--retries", "1", "--low-level-retries", "1" };
        if (config.RcloneConfigPath is not null) { args.Add("--config"); args.Add(config.RcloneConfigPath); }
        return args;
    }

    public async Task RenewAsync(CancellationToken token) =>
        _ = await Run(RenewalArguments(config), capturePath: false, token);

    private static void WriteExpiredToken(Stream config, ReadOnlySpan<byte> value)
    {
        var reader = new Utf8JsonReader(value);
        using var document = JsonDocument.ParseValue(ref reader);
        config.Write("token = "u8);
        using (var json = new Utf8JsonWriter(config))
        {
            json.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                if (!property.NameEquals("expiry"u8)) property.WriteTo(json);
            json.WriteString("expiry"u8, "2000-01-01T00:00:00Z");
            json.WriteEndObject();
        }
        config.WriteByte((byte)'\n');
    }

    public async Task RenewRejectedAsync(SnapshotToken rejected, CancellationToken token)
    {
        string path = await ResolveConfigPathAsync(token).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var source = await CopySourceAsync(nonce, token).ConfigureAwait(false)
            ?? throw new SnapshotException(3, "auth_unavailable");
        using var input = new MemoryStream();
        try
        {
            if (ReadConfig(path, input).AccessToken != rejected.AccessToken ||
                source != await CopySourceAsync(nonce, token).ConfigureAwait(false))
                return; // The caller re-reads any competing process's update.

            var updated = await RenewWithPrivateConfigAsync(input, token).ConfigureAwait(false);
            if (source == await CopySourceAsync(nonce, token).ConfigureAwait(false) &&
                updated.AccessToken != rejected.AccessToken && updated.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(10))
                renewed = (nonce, source, updated);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Prefer a competing refresh even if this attempt failed.
            if (source == await CopySourceAsync(nonce, token).ConfigureAwait(false))
                throw new SnapshotException(3, "auth_unavailable");
        }
        finally { CryptographicOperations.ZeroMemory(input.GetBuffer()); }
    }

    private async Task<SnapshotToken> RenewWithPrivateConfigAsync(MemoryStream input, CancellationToken token)
    {
        // An expired copy forces rclone to exchange the token with the same OAuth app.
        // Its refresh writes only to this private scratch directory, never the live file.
        using var scratch = new SnapshotRenewalFile();
        using (var stream = new FileInfo(scratch.ConfigPath).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
            4096, FileOptions.None, PrivateStateFiles.FileSecurity()))
        {
            input.WriteTo(stream);
        }

        var renewal = config with { Remote = Remote.Name + ":", RcloneConfigPath = scratch.ConfigPath };
        await Run(RenewalArguments(renewal), capturePath: false, token).ConfigureAwait(false);
        return await new SnapshotTokenBroker(renewal).ReadAsync(token).ConfigureAwait(false);
    }

    private async Task<string> Run(IEnumerable<string> arguments, bool capturePath, CancellationToken token)
    {
        try
        {
            string[] flags = ["--ask-password=false", "--ftp-ask-password=false", "--sftp-ask-password=false"];
            using var process = JobProcess.Start(RcloneArguments.ProcessInfo(config.RclonePath, arguments.Concat(flags)));
            using var cancel = token.Register(() => { try { process.Kill(); } catch { } });
            Task<string>? output = capturePath ? process.StandardOutput.ReadToEndAsync(token) : null;
            Task? discard = capturePath ? null : process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
            Task errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            var watch = Stopwatch.StartNew();
            while (!process.WaitForExit(50))
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed.TotalSeconds > 30) { process.Kill(); throw new SnapshotException(3, "auth_unavailable"); }
                await Task.Yield();
            }
            await errors;
            if (discard is not null) await discard;
            string text = output is null ? "" : await output;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || text.Length > 32768) throw new SnapshotException(3, "auth_unavailable");
            return text;
        }
        catch (SnapshotException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new SnapshotException(3, "auth_unavailable"); }
    }
}
