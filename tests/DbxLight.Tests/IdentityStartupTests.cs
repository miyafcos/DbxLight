using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal static class IdentityStartupTests
{
    public static void SuspendedImage()
    {
        string root = Path.Combine(UnitTests.RepositoryRoot(), "_test-work", "u3b-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string exe = Environment.ProcessPath!;
        var command = new StringBuilder("\"" + exe + "\" --unit3a-child \"" + Path.Combine(root, "never-resumed.txt") + "\"");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, root, ref startup, out var native))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using Process child = Process.GetProcessById(native.ProcessId);
            try
            {
                MethodInfo method = typeof(MountController).GetMethod("WaitForImage", BindingFlags.NonPublic | BindingFlags.Static)!;
                string image = (string)method.Invoke(null, [child])!;
                Console.WriteLine($"B3 suspended own PID={child.Id}: recorded image={image}");
                TestRunner.Assert(image.Equals(exe, StringComparison.OrdinalIgnoreCase), "A loader DLL was accepted as the executable image: " + image);
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                TestRunner.Assert(child.WaitForExit(10000), "Owned suspended child did not exit.");
                Console.WriteLine($"B3 suspended own PID={child.Id}: exited; never resumed.");
            }
        }
        finally { CloseHandle(native.Thread); CloseHandle(native.Process); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
