namespace DbxLight.Core;

// Shared by the test harness and the tray's isolated smoke run.
internal static class TestWorkPaths
{
    internal static string WorkRoot => ResolveWorkRoot(Environment.GetEnvironmentVariable("DBXL_TEST_ROOT"),
        Directory.Exists("D:/"), FindRepositoryRoot(AppContext.BaseDirectory), Path.GetTempPath());

    internal static string ResolveWorkRoot(string? configured, bool hasDriveD, string? repository, string temporary,
        Func<string, (DriveType Type, string Format)>? inspectDrive = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return RequireLocalWorkRoot(configured, inspectDrive);
        const string driveD = @"D:\dbxl-test";
        if (hasDriveD && IsLocalFixedPath(driveD, out _, inspectDrive)) return driveD;
        if (repository is not null)
        {
            string candidate = Path.Combine(repository, "_test-work");
            if (IsLocalFixedPath(candidate, out _, inspectDrive)) return candidate;
        }
        return RequireLocalWorkRoot(Path.Combine(temporary, "dbxl-test"), inspectDrive);
    }

    internal static bool IsLocalFixedPath(string path, out string detail,
        Func<string, (DriveType Type, string Format)>? inspectDrive = null)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path))!;
            var facts = (inspectDrive ?? ReadDrive)(root);
            detail = $"path='{path}', drive='{root}', DriveType={facts.Type}, DriveFormat={facts.Format}";
            return IsLocalFixedDrive(facts.Type, facts.Format);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            detail = $"path='{path}', drive information unavailable: {error.Message}";
            return false;
        }
    }

    internal static bool IsLocalFixedDrive(DriveType type, string format) =>
        type == DriveType.Fixed && (format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("ReFS", StringComparison.OrdinalIgnoreCase));

    private static string RequireLocalWorkRoot(string path, Func<string, (DriveType Type, string Format)>? inspectDrive)
    {
        if (!IsLocalFixedPath(path, out string detail, inspectDrive))
            throw new IOException("Test work root requires a local fixed NTFS or ReFS disk: " + detail);
        return Path.GetFullPath(path);
    }

    private static (DriveType Type, string Format) ReadDrive(string root)
    {
        var drive = new DriveInfo(root);
        return (drive.DriveType, drive.DriveFormat);
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DbxLight.sln"))) return directory.FullName;
        return null;
    }
}
