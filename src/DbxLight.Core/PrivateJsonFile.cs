using System.Security.AccessControl;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

internal static class PrivateJsonFile
{
    internal static T? Read<T>(string path) where T : class
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using var directory = PrivateStateFiles.LockParent(path);
        PrivateStateFiles.ValidateExisting(path);
        return JsonFiles.Read<T>(path);
    }

    internal static void Write<T>(string path, T value)
    {
        PrivateStateFiles.EnsureParent(path);
        using var directory = PrivateStateFiles.LockParent(path);
        PrivateStateFiles.ValidateExisting(path);
        string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        using (FileStream stream = new FileInfo(pending).Create(FileMode.CreateNew, FileSystemRights.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough, PrivateStateFiles.FileSecurity()))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(value, JsonFiles.Options) + "\n");
            stream.Write(bytes); stream.Flush(true);
        }
        File.Move(pending, path, overwrite: true);
    }
}
