using DbxLight.Core;

namespace DbxLight.Tests;

internal static class TestPaths
{
    internal static string WorkRoot => TestWorkPaths.WorkRoot;
    internal static string ResolveWorkRoot(string? configured, bool hasDriveD, string repository,
        Func<string, (DriveType Type, string Format)> inspectDrive) =>
        TestWorkPaths.ResolveWorkRoot(configured, hasDriveD, repository, Path.GetTempPath(), inspectDrive);
}
