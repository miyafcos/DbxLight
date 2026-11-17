using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbxLight.Core;

public sealed record ReleaseFile(string Name, long Size, string Sha256);
public sealed record ReleaseManifest(int Schema, string Version, ReleaseFile[] Files);

public static class UpdatePackage
{
    public const long MaximumExpandedBytes = 100L * 1024 * 1024;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    public static bool SafeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name.Contains("..", StringComparison.Ordinal)
            || name.Any(c => char.IsControl(c) || "/\\:<>\"|?*".Contains(c)) || name.EndsWith(' ') || name.EndsWith('.')) return false;
        return !Regex.IsMatch(name.Split('.')[0].TrimEnd(' '), @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    internal static void RequirePlain(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Update paths must not be links.");
    }
    private static ReleaseManifest Manifest(byte[] bytes, string? expectedVersion = null)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Release manifest is too large.");
        var result = JsonSerializer.Deserialize<ReleaseManifest>(bytes, JsonFiles.Options) ?? throw new InvalidDataException("Missing release manifest.");
        if (result.Schema != 1 || !UpdateVersion.TryParse(result.Version, out _) || expectedVersion is not null && result.Version != expectedVersion
            || result.Files is null || result.Files.Length is < 2 or > 63) throw new InvalidDataException("Invalid release manifest schema, version or file count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = bytes.Length;
        foreach (var file in result.Files)
        {
            if (file is null || !SafeName(file.Name) || file.Name.Equals("release.json", StringComparison.OrdinalIgnoreCase)
                || !names.Add(file.Name) || file.Size < 0 || file.Size > MaximumExpandedBytes || !IsHash(file.Sha256))
                throw new InvalidDataException("Invalid release manifest file.");
            total = checked(total + file.Size);
        }
        if (total > MaximumExpandedBytes || !names.Contains("DbxLight.exe") || !names.Contains("dbxl.exe"))
            throw new InvalidDataException("Release is incomplete or exceeds the expanded size limit.");
        return result;
    }
    public static ReleaseManifest ReadManifest(string directory) => ReadManifest(directory, out _);
    internal static ReleaseManifest ReadManifest(string directory, out byte[] bytes)
    {
        RequirePlain(directory);
        string path = Path.Combine(directory, "release.json"); RequirePlain(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Release manifest is too large.");
        bytes = File.ReadAllBytes(path);
        return Manifest(bytes);
    }
    public static bool CanApply(string directory)
    {
        try
        {
            var manifest = ReadManifest(directory);
            foreach (var file in manifest.Files)
            {
                string path = Path.Combine(directory, file.Name);
                if (!File.Exists(path)) return false;
                RequirePlain(path);
            }
            string probe = Path.Combine(directory, ".update-probe-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or OverflowException) { return false; }
    }
    public static ReleaseManifest ValidateDirectory(string directory, bool exact = true)
    {
        var manifest = ReadManifest(directory);
        ValidateDirectory(directory, manifest, exact);
        return manifest;
    }
    public static void ValidateDirectory(string directory, ReleaseManifest manifest, bool exact = false)
    {
        RequirePlain(directory);
        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(directory, file.Name); RequirePlain(path);
            if (new FileInfo(path).Length != file.Size || HashFile(path) != file.Sha256)
                throw new InvalidDataException("Release file size or SHA-256 does not match: " + file.Name);
        }
        if (exact)
        {
            var names = manifest.Files.Select(f => f.Name).Append("release.json").ToHashSet(StringComparer.Ordinal);
            if (!Directory.EnumerateFileSystemEntries(directory).All(path => File.Exists(path) && names.Remove(Path.GetFileName(path))) || names.Count != 0)
                throw new InvalidDataException("Staging has files outside the release manifest.");
        }
    }
    public static UpdateDownloadResult Stage(byte[] zip, UpdateStore store, string? expectedVersion = null,
        string? currentVersion = null, bool requireNewer = false)
    {
        if (zip.LongLength > UpdateClient.MaximumZipBytes) throw new InvalidDataException("Release ZIP exceeds the size limit.");
        using var input = new MemoryStream(zip, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 3 or > 64) throw new InvalidDataException("Invalid ZIP entry count.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!SafeName(entry.FullName) || !entries.TryAdd(entry.FullName, entry)
                || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.Length > MaximumExpandedBytes)
                throw new InvalidDataException("Unsafe ZIP entry.");
            total = checked(total + entry.Length);
        }
        if (total > MaximumExpandedBytes || !entries.TryGetValue("release.json", out var manifestEntry) || manifestEntry.FullName != "release.json")
            throw new InvalidDataException("Missing release manifest or expanded size limit exceeded.");
        byte[] Read(ZipArchiveEntry entry, long limit)
        {
            if (entry.Length > limit) throw new InvalidDataException("ZIP entry exceeds the size limit.");
            using var stream = entry.Open();
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536]; int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("Expanded ZIP entry exceeds the size limit.");
                output.Write(buffer, 0, count);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("ZIP entry size does not match.");
            return output.ToArray();
        }
        byte[] manifestBytes = Read(manifestEntry, 1024 * 1024);
        var manifest = Manifest(manifestBytes, expectedVersion);
        if (requireNewer && (!UpdateVersion.TryParse(currentVersion ?? UpdateVersion.Current, out var current) ||
            !UpdateVersion.TryParse(manifest.Version, out var incoming) || incoming.CompareTo(current) <= 0))
            throw new InvalidDataException("Only a newer release may be downloaded.");
        if (entries.Count != manifest.Files.Length + 1) throw new InvalidDataException("ZIP contains files outside the release manifest.");
        // Validate every name and hash before creating the staging directory.
        foreach (var file in manifest.Files)
        {
            if (!entries.TryGetValue(file.Name, out var entry) || entry.FullName != file.Name || entry.Length != file.Size
                || Hash(Read(entry, file.Size)) != file.Sha256) throw new InvalidDataException("ZIP file does not match the release manifest: " + file.Name);
        }
        string stage = Path.Combine(store.Root, "staging-" + manifest.Version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(store.Root);
        using var lease = new FileStream(stage + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(stage);
        store.RecordStage(stage, false);
        try
        {
            foreach (var file in manifest.Files) File.WriteAllBytes(Path.Combine(stage, file.Name), Read(entries[file.Name], file.Size));
            File.WriteAllBytes(Path.Combine(stage, "release.json"), manifestBytes);
            ValidateDirectory(stage);
            return new(manifest.Version, stage, UpdateSource.ReleasesUrl);
        }
        catch { store.RecordStage(stage, true); throw; }
    }
}
