using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DbxLight.Core;

internal static class PrivateStateFiles
{
    internal static SecurityIdentifier CurrentSid => WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Cannot find current user SID.");

    internal static FileSecurity FileSecurity()
    {
        SecurityIdentifier sid = CurrentSid;
        var acl = new FileSecurity();
        acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }

    internal static void EnsureParent(string file)
    {
        string parent = Path.GetDirectoryName(file)!;
        CreatePrivate(new DirectoryInfo(parent));
        ValidateDirectory(parent);
    }

    private static void CreatePrivate(DirectoryInfo directory)
    {
        if (directory.Exists) return;
        if (directory.Parent is { } parent && !parent.Exists) CreatePrivate(parent);
        SecurityIdentifier sid = CurrentSid;
        var acl = new DirectorySecurity(); acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(acl);
    }

    internal static SafeFileHandle LockParent(string file)
    {
        string parent = Path.GetDirectoryName(file)!;
        // Hold the directory without FILE_SHARE_DELETE while validating, creating and replacing the child.
        SafeFileHandle handle = OpenDirectory(parent, 0x00020000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new UnauthorizedAccessException("Cannot lock the private state directory: " + new Win32Exception(error).Message);
        }
        try { ValidateDirectory(parent); return handle; }
        catch { handle.Dispose(); throw; }
    }

    // Legacy application parents may retain inherited ACLs. Only existing private
    // files can move out of them; never create credentials under this handle.
    internal static SafeFileHandle LockExistingParent(string file)
    {
        string parent = Path.GetDirectoryName(file)!;
        RescuePaths.Ancestors(parent);
        SafeFileHandle handle = OpenDirectory(parent, 0x00020000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try { RescuePaths.Ancestors(parent); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw StartupFailure.PrivateStorage(directory, "本人専用の保存先に再解析ポイントは使えません。");
        Validate(new DirectoryInfo(directory).GetAccessControl(), directory, requireProtected: true);
    }

    internal static void ValidateExisting(string file)
    {
        try { _ = File.GetAttributes(file); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw StartupFailure.PrivateStorage(file, "本人専用の普通のファイルとして使えません。");
        Validate(new FileInfo(file).GetAccessControl(), file, requireProtected: false);
    }

    private static void Validate(FileSystemSecurity acl, string path, bool requireProtected)
    {
        SecurityIdentifier sid = CurrentSid;
        var allows = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
        if ((requireProtected && !acl.AreAccessRulesProtected) || !sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))) || allows.Length == 0 ||
            allows.Any(rule => !sid.Equals(rule.IdentityReference)) ||
            !allows.Any(rule => (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl))
            throw StartupFailure.PrivateStorage(path, "保存先が本人だけの所有・権限ではないため使えません。");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
