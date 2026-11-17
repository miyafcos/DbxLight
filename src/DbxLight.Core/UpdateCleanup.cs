using System.Text.RegularExpressions;

namespace DbxLight.Core;

public static class UpdateCleanup
{
    public static bool IsStage(string name) => Regex.IsMatch(name, @"^staging-(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    public static bool IsBackup(string name) => Regex.IsMatch(name, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-[0-9]{8}T[0-9]{13}Z\z", RegexOptions.CultureInvariant);
    private static bool Skippable(Exception error) => error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException;
    public static void Run(UpdateStore store, string target, DateTimeOffset? now = null, bool cleanupBackups = true)
    {
        DateTimeOffset clock = now ?? DateTimeOffset.UtcNow;
        if (Directory.Exists(store.Root))
        {
            try
            {
                UpdatePackage.RequirePlain(store.Root);
                foreach (string path in Directory.EnumerateDirectories(store.Root))
                {
                    if (!IsStage(Path.GetFileName(path))) continue;
                    try
                    {
                        UpdatePackage.RequirePlain(path);
                        bool completed = false;
                        if (File.Exists(path + ".json"))
                        {
                            UpdatePackage.RequirePlain(path + ".json");
                            var record = JsonFiles.Read<UpdateStageRecord>(path + ".json");
                            if (record.Name != Path.GetFileName(path)) continue;
                            completed = record.Completed;
                        }
                        if (File.Exists(path + ".lock")) UpdatePackage.RequirePlain(path + ".lock");
                        using (var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        {
                            var entries = PlainEntries(path);
                            DateTime latest = entries.Append(path).Select(p => File.GetLastWriteTimeUtc(p)).Max();
                            if (File.Exists(path + ".json")) latest = new[] { latest, File.GetLastWriteTimeUtc(path + ".json") }.Max();
                            if (!completed && clock - new DateTimeOffset(latest, TimeSpan.Zero) < TimeSpan.FromDays(7)) continue;
                            DeletePlainTree(path, entries);
                        }
                        File.Delete(path + ".json"); File.Delete(path + ".lock");
                    }
                    catch (Exception error) when (Skippable(error)) { }
                }
            }
            catch (Exception error) when (Skippable(error)) { }
        }
        if (!cleanupBackups) return;
        string backups = Path.Combine(target, "backup");
        if (!Directory.Exists(backups)) return;
        try
        {
            UpdatePackage.RequirePlain(backups);
            var complete = new List<string>();
            foreach (string path in Directory.EnumerateDirectories(backups).Where(p => IsBackup(Path.GetFileName(p))))
            {
                try
                {
                    UpdatePackage.RequirePlain(path);
                    if (!Directory.EnumerateFileSystemEntries(path).Any()) { Directory.Delete(path); continue; }
                    var manifest = UpdatePackage.ReadManifest(path);
                    if (manifest.Files.All(file => File.Exists(Path.Combine(path, file.Name)))) { _ = PlainEntries(path); complete.Add(path); }
                }
                catch (Exception error) when (Skippable(error)) { }
            }
            foreach (string path in complete.OrderByDescending(p => Path.GetFileName(p)[(Path.GetFileName(p).IndexOf('-') + 1)..], StringComparer.Ordinal).Skip(2))
                try { DeletePlainTree(path, PlainEntries(path)); } catch (Exception error) when (Skippable(error)) { }
        }
        catch (Exception error) when (Skippable(error)) { }
    }
    private static List<string> PlainEntries(string path)
    {
        UpdatePackage.RequirePlain(path);
        var all = new List<string>(); var pending = new Stack<string>(); pending.Push(path);
        while (pending.TryPop(out var directory))
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            { UpdatePackage.RequirePlain(child); all.Add(child); if (Directory.Exists(child)) pending.Push(child); }
        return all;
    }
    private static void DeletePlainTree(string path, List<string> all)
    {
        foreach (string child in all.Where(File.Exists)) File.Delete(child);
        foreach (string child in all.Where(Directory.Exists).OrderByDescending(p => p.Length)) Directory.Delete(child);
        Directory.Delete(path);
    }
}
