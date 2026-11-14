namespace DbxLight.Core;

public sealed record DoctorCheck(string Name, bool Passed, string Detail);

public static class Doctor
{
    public static IReadOnlyList<DoctorCheck> Check(DbxConfig config)
    {
        config = config.Validate();
        string? executable = FindExecutable(config.RclonePath);
        string[] winfspCandidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        }.Select(p => Path.Combine(p, "WinFsp", "bin", "winfsp-x64.dll")).ToArray();
        string? winfsp = winfspCandidates.FirstOrDefault(File.Exists);
        bool available = MountPaths.IsAvailable(config.MountPoint);
        var checks = new List<DoctorCheck>
        {
            new("rclone", executable is not null, executable ?? "rclone executable not found: " + config.RclonePath),
            new("WinFsp", winfsp is not null, winfsp ?? "WinFsp x64 runtime DLL was not found in standard installation folders."),
            new("mountPoint", available, config.MountPoint + (available ? " is available." : " is in use."))
        };
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(config.CacheDir))!;
            var drive = new DriveInfo(root);
            checks.Add(new("cache NTFS", drive.IsReady && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase), config.CacheDir + " -> " + drive.DriveFormat));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            checks.Add(new("cache NTFS", false, e.Message));
        }
        return checks;
    }

    private static string? FindExecutable(string path)
    {
        if (Path.IsPathRooted(path)) return File.Exists(path) ? Path.GetFullPath(path) : null;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory.Trim('"'), path);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
