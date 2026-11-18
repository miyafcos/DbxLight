using System.Security.Cryptography;
using System.Text.Json;

namespace DbxLight.Core;

// An older runtime writer can drop the launch fields from the state and both journals.
internal sealed record MountProof(string MountKey, string Device, string? Binding, string? Cursor)
{
    internal static RuntimeState Retain(RuntimeState state, IEnumerable<RuntimeState>? candidates = null)
    {
        if (state.MountDevice is not null && state.MountDropboxBinding is not null && state.MountDropboxCursor is not null)
            return state;
        if (Key(state) is not { } key) return state;

        var proof = Read(state.OwnerStatePath);
        state = Apply(state, proof, key);
        foreach (RuntimeState candidate in candidates ?? [])
        {
            if (state.MountDevice is not null && state.MountDropboxBinding is not null && state.MountDropboxCursor is not null)
                break;
            if (Capture(candidate) is { } remembered) state = Apply(state, remembered, key);
        }
        return state;
    }

    internal static void Remember(RuntimeState state)
    {
        if (Capture(state) is not { } proof) return;
        string path = FileName(state.OwnerStatePath);
        try
        {
            var saved = PrivateJsonFile.Read<MountProof>(path);
            // An unrelated or unreadable file at the optional proof path must be preserved.
            if (saved is not null && (saved.MountKey is not { Length: 64 } || string.IsNullOrEmpty(saved.Device))) return;
            if (saved?.MountKey == proof.MountKey &&
                (saved == proof || saved.Device != proof.Device || saved.Binding is not null && saved.Binding != proof.Binding ||
                 saved.Cursor is not null && saved.Cursor != proof.Cursor)) return;
            PrivateJsonFile.Write(path, proof);
        }
        catch (Exception e) when (Unavailable(e)) { }
    }

    private static RuntimeState Apply(RuntimeState state, MountProof? proof, string key)
    {
        if (proof?.MountKey != key || string.IsNullOrEmpty(proof.Device) ||
            state.MountDevice is not null && state.MountDevice != proof.Device ||
            state.MountDropboxBinding is not null && state.MountDropboxBinding != proof.Binding ||
            state.MountDropboxCursor is not null && state.MountDropboxCursor != proof.Cursor)
            return state;
        if (state.MountDevice == proof.Device && state.MountDropboxBinding == proof.Binding && state.MountDropboxCursor == proof.Cursor)
            return state;
        return state with { MountDevice = proof.Device, MountDropboxBinding = proof.Binding, MountDropboxCursor = proof.Cursor };
    }

    private static MountProof? Capture(RuntimeState state) =>
        state.MountDevice is { Length: > 0 } device && Key(state) is { } key
            ? new(key, device, state.MountDropboxBinding, state.MountDropboxCursor) : null;

    private static string FileName(string statePath) => statePath + ".mount-proof.json";

    private static MountProof? Read(string statePath)
    {
        try { return PrivateJsonFile.Read<MountProof>(FileName(statePath)); }
        catch (Exception e) when (Unavailable(e)) { return null; }
    }

    private static bool Unavailable(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException;

    private static string? Key(RuntimeState state)
    {
        if (state.IdentityVersion != 1 || state.GenerationNumber <= 0 || !Guid.TryParseExact(state.GenerationId, "N", out _) ||
            string.IsNullOrEmpty(state.ConfigPath) || !Path.IsPathFullyQualified(state.OwnerStatePath) ||
            !ProcessIdentity.CompleteRecord(state.RclonePid, state.ProcessStartedUtc, state.ExecutablePath) ||
            state.StopDisposition != StopDisposition.Running || state.LaunchPending || state.StopInProgress ||
            state.StopRequested || state.StoppedUtc is not null)
            return null;

        DbxConfig config = state.ActiveConfig;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            state.GenerationId, state.GenerationNumber, state.RclonePid, state.ProcessStartedUtc, state.RcPort,
            ExecutablePath = state.ExecutablePath.ToUpperInvariant(),
            ConfigPath = state.ConfigPath.ToUpperInvariant(),
            OwnerStatePath = state.OwnerStatePath.ToUpperInvariant(),
            Connection = DropboxCopyPaths.ConnectionKey(config),
            MountPoint = MountPaths.CanonicalMount(config.MountPoint).ToUpperInvariant(),
            CacheDir = Path.GetFullPath(config.CacheDir).ToUpperInvariant(),
            config.NetworkMode,
            HiddenPaths = config.HiddenPaths.Select(path => path.ToUpperInvariant()),
            config.ExcludePatterns
        })));
    }
}
