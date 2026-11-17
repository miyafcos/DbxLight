using System.Security.Cryptography;
using System.Text;

namespace DbxLight.Core;

internal sealed class SnapshotDestination(DbxConfig config, string? configPath = null, string? statePath = null)
{
    private string? brokerConfigDirectory;
    internal void ProtectBrokerConfig(string path) => brokerConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(path));

    private static bool Under(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal string Check(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) || path[2..].Contains(':'))
                throw new SnapshotException(2, "unsafe_destination");
            string full = Path.GetFullPath(path);
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(full)).TrimEnd(' ', '.').Equals("rclone.conf", StringComparison.OrdinalIgnoreCase))
                throw new SnapshotException(2, "unsafe_destination");
            string mount = Path.GetFullPath(MountPaths.CanonicalMount(config.MountPoint));
            string selectedConfig = new ConfigStore(configPath).PathName;
            string selectedState = Path.GetFullPath(statePath ?? Environment.GetEnvironmentVariable("DBXL_STATE") ?? DbxConfig.DefaultStatePath);
            string[] protectedRoots = new[] { config.CacheDir, config.RescueDir, config.LogDir, DbxConfig.LocalRoot,
                Path.GetDirectoryName(DbxConfig.DefaultConfigPath)!, Path.GetDirectoryName(selectedConfig)!,
                Path.GetDirectoryName(selectedState)!, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rclone"),
                config.RcloneConfigPath is { } keyPath ? Path.GetDirectoryName(Path.GetFullPath(keyPath))! : Path.GetDirectoryName(DbxConfig.DefaultConfigPath)! }
                .Concat(brokerConfigDirectory is { } discovered ? new[] { discovered } : Array.Empty<string>())
                .Select(Path.GetFullPath).SelectMany(p => new[] { p, ResolveRoot(p) }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool Protected(string candidate) => Under(candidate, mount) || protectedRoots.Any(p => Under(candidate, p));
            if (Protected(full)) throw new SnapshotException(2, "unsafe_destination");
            string root = Path.GetPathRoot(full)!;
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new SnapshotException(2, "unsafe_destination");
            string resolved = root;
            foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                resolved = Path.Combine(resolved, part);
                if (Attributes(resolved) is { } attributes)
                {
                    FileSystemInfo item = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(resolved) : new FileInfo(resolved);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // Resolve the actual target before refusing links, including junctions to a mount/cache.
                        FileSystemInfo? target = item.ResolveLinkTarget(returnFinalTarget: true);
                        if (target is null) throw new SnapshotException(2, "unsafe_destination");
                        _ = Path.GetFullPath(target.FullName);
                        throw new SnapshotException(2, "unsafe_destination");
                    }
                }
                if (Protected(resolved))
                    throw new SnapshotException(2, "unsafe_destination");
            }
            return full;
        }
        catch (SnapshotException) { throw; }
        catch { throw new SnapshotException(2, "unsafe_destination"); }
    }

    private static FileAttributes? Attributes(string path)
    {
        // Exists follows directory links and hides dangling junctions. Inspect the link itself.
        try { return File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static string ResolveRoot(string path)
    {
        string root = Path.GetPathRoot(path)!, resolved = root;
        foreach (string part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            if (Attributes(resolved) is not { } attributes) continue;
            FileSystemInfo item = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                resolved = item.ResolveLinkTarget(true)?.FullName ?? throw new SnapshotException(2, "unsafe_destination");
        }
        return Path.GetFullPath(resolved);
    }

    internal string NewFile(string path)
    {
        string full = Check(path);
        if (File.Exists(full) || Directory.Exists(full)) throw new SnapshotException(2, "destination_exists");
        return full;
    }
    internal void Parent(string path)
    {
        Check(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Check(path);
    }
    internal void Promote(string partial, string final)
    {
        Check(partial); NewFile(final);
        File.Move(partial, final);
    }
}

internal sealed class SnapshotStagedFile : IDisposable
{
    internal string Final { get; }
    internal string Partial { get; }
    internal StreamWriter Writer { get; }
    private readonly SnapshotDestination destination;
    internal SnapshotStagedFile(SnapshotDestination destination, string final, SnapshotResult result)
    {
        this.destination = destination;
        Final = destination.NewFile(final);
        destination.Parent(Final);
        Partial = Final + ".partial." + Guid.NewGuid().ToString("N");
        destination.Check(Partial);
        Writer = new StreamWriter(new FileStream(Partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { NewLine = "\n" };
        result.PartialFiles.Add(Partial);
    }
    internal void Commit()
    {
        Writer.Flush();
        ((FileStream)Writer.BaseStream).Flush(true);
        Writer.Dispose();
        destination.Promote(Partial, Final);
    }
    public void Dispose() => Writer.Dispose();
}

internal sealed record SnapshotHashes(long Size, string ContentHash, string Sha256);
internal static class SnapshotHash
{
    internal const int BlockSize = 4 * 1024 * 1024;
    internal static async Task<SnapshotHashes> ComputeAsync(Stream input, Stream? output = null,
        long? expectedSize = null, CancellationToken cancellation = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var block = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long total = 0; int inBlock = 0, n;
        while ((n = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            total = checked(total + n);
            if (expectedSize is { } expected && total > expected) throw new SnapshotException(4, "size_mismatch");
            sha.AppendData(buffer, 0, n);
            int offset = 0;
            while (offset < n)
            {
                int take = Math.Min(n - offset, BlockSize - inBlock);
                block.AppendData(buffer, offset, take); inBlock += take; offset += take;
                if (inBlock == BlockSize) { aggregate.AppendData(block.GetHashAndReset()); inBlock = 0; }
            }
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, n), cancellation);
        }
        if (inBlock != 0) aggregate.AppendData(block.GetHashAndReset());
        if (expectedSize is { } length && total != length) throw new SnapshotException(4, "size_mismatch");
        return new(total, Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }
}
