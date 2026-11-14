using System.Collections.Concurrent;

namespace DbxLight.Core;

internal sealed record MetadataItem(string Name, bool IsDir, long Size, DateTimeOffset? ModifiedUtc);

/// <summary>Use only the attributes populated by directory enumeration, never a per-file stat.</summary>
internal sealed class MountMetadata(string mountPoint)
{
    private readonly ConcurrentDictionary<string, string> directories = new(StringComparer.OrdinalIgnoreCase);
    internal static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    internal IReadOnlyList<MetadataItem> Read(string relative)
    {
        string directory = Resolve(relative);
        ValidateAncestors(directory);
        var result = new List<MetadataItem>();
        foreach (FileSystemInfo info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*",
            new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            // These properties are already populated by EnumerateFileSystemInfos on Windows.
            bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
            result.Add(new(info.Name, isDir, isDir ? -1 : ((FileInfo)info).Length, info.LastWriteTimeUtc));
            if (isDir) directories[Key(relative.Length == 0 ? info.Name : relative + "/" + info.Name)] = info.FullName;
        }
        return result;
    }

    private string Resolve(string relative)
    {
        if (relative.Length == 0) return Extended(MountPaths.CanonicalMount(mountPoint));
        if (directories.TryGetValue(Key(relative), out string? path)) return path;
        int slash = relative.LastIndexOf('/');
        // Resolve user-supplied ancestors through their parents' listings too. A reparse
        // point is never admitted to directories, including an explicitly requested path.
        _ = Read(slash < 0 ? "" : relative[..slash]);
        return directories.TryGetValue(Key(relative), out path) ? path :
            throw new DirectoryNotFoundException("The mounted directory is absent or is a reparse point.");
    }

    private static void ValidateAncestors(string directory)
    {
        try
        {
            for (DirectoryInfo? ancestor = new(directory); ancestor is not null; ancestor = ancestor.Parent)
            {
                FileAttributes attributes = File.GetAttributes(ancestor.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("The enumeration origin or an ancestor is no longer an ordinary directory.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AgentCommandException(3, "mount_changed", "The enumeration origin changed or cannot be verified; discard these results.");
        }
    }

    private static string Key(string path) => path.Normalize();
}
