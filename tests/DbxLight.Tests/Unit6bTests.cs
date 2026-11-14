namespace DbxLight.Tests;

internal static partial class Unit6bTests
{
    public static void Run(TestRunner runner, string? only = null, bool includeMounts = true)
    {
        void Test(string group, string name, Action action, bool mount = false)
        {
            if ((only is null || only == group) && (!mount || includeMounts))
                runner.Test(group + ": " + name, action);
        }
        Test("Q5", "read-only shutdown still guards pending, unknown and recovery states", SessionDecisions);
        Test("Q5", "external settings or mount generations and busy control invalidate tray snapshots", SessionGenerations, true);
        Test("Q1", "wait returns the exact verified metadata version and observation time", UploadReceipt, true);
        Test("Q1", "resaves during verification and after the last queue read cannot return zero", WaitResaveRaces, true);
        Test("Q1", "resaved target is verified again before eventual success", WaitResaveRetry, true);
        Test("Q1", "unreadable, incomplete and body-only cache pairs are unknown for paths and all", WaitCacheUnknown, true);
        Test("Q2", "foreign state is rejected before runtime observation can change it", ForeignAgentState, true);
        Test("Q2", "ls/find/stat reject a replacement mount including partial results", MountReplacement, true);
        Test("Q2", "cached enumeration roots and ancestors reject replacement junctions", EnumerationOrigins);
        Test("Q4", "verified Dropbox subroots round-trip and ambiguous Windows names use extended paths", WebMappings);
        Test("Q3", "case-insensitive exclusions match stopped, mounted and RC views", ExclusionParity, true);
        Test("A3", "status distinguishes active arguments, stopped settings and legacy uncertainty", StatusPermission, true);
        Test("A2", "missing or empty configuration has a consistent structured result", NotConfigured);
        Test("A4", "unknown directory size and time are null in listing and stat", DirectoryMetadata, true);
        Test("A1", "PowerShell 5.1 and Git Bash preserve UTF-8 JSON bytes and safe escaping", JsonShellBytes);
    }
}
