using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DbxLight.Core;

internal static class DetachedProcess
{
    // Process.Start on Windows can inherit the caller's redirected pipe handles,
    // keeping `dbxl start` output open for the whole lifetime of rclone.
    // No inherited handles, no console and a private environment avoid that coupling.
    internal sealed class NoChildCreatedException(int error) : Win32Exception(error);

    public static SuspendedChild CreateSuspended(ProcessStartInfo info)
    {
        string command = string.Join(' ', new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote));
        string environment = string.Join('\0', info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        IntPtr environmentPointer = Marshal.StringToHGlobalUni(environment);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var job = new StartupProcessJob();
        try
        {
            ProcessInformation created;
            // No inherited standard handles and no console: stdout/stderr are discarded.
            bool success = CreateProcess(null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                0x08000404, environmentPointer, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out created);
            if (!success) throw new NoChildCreatedException(Marshal.GetLastWin32Error());
            try
            {
                var process = Process.GetProcessById(created.ProcessId);
                _ = process.SafeHandle; // Acquire our own handle before releasing the creation handle.
                job.Assign(process);
                return new SuspendedChild(process, created.Thread, job);
            }
            catch
            {
                // Use the creation handle, never a PID lookup, when acquisition fails.
                TerminateProcess(created.Process, 1);
                CloseHandle(created.Thread);
                job.Dispose();
                throw;
            }
            finally { CloseHandle(created.Process); }
        }
        catch { job.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(environmentPointer); }
    }

    internal sealed class SuspendedChild(Process process, IntPtr thread, StartupProcessJob job) : IDisposable
    {
        public Process Process { get; } = process;
        private bool resumed;
        public void ResumeRecorded(Action? beforeResume = null, Action? afterResume = null)
        {
            // Only the lifecycle owner calls this after both durable ownership records exist.
            job.AllowSurvival();
            beforeResume?.Invoke();
            if (ResumeThread(thread) == uint.MaxValue) throw StartupFailure.RetryableChild(new Win32Exception(Marshal.GetLastWin32Error()));
            resumed = true;
            afterResume?.Invoke();
        }
        public void AbortIfSuspended()
        {
            if (!resumed && !Process.HasExited)
            {
                Process.Kill();
                if (!Process.WaitForExit(10000)) throw new TimeoutException("The owned suspended child did not exit; retain cache without moving it.");
            }
        }
        public void Dispose()
        {
            try { AbortIfSuspended(); }
            finally { CloseHandle(thread); job.Dispose(); Process.Dispose(); }
        }
    }

    internal sealed class StartupProcessJob : IDisposable
    {
        private readonly SafeFileHandle handle = CreateJobObject(IntPtr.Zero, null);
        internal StartupProcessJob()
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetKillOnClose(true);
        }
        private void SetKillOnClose(bool kill)
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = kill ? 0x2000u : 0u } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal void AllowSurvival() => SetKillOnClose(false);
        public void Dispose() => handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal UIntPtr Affinity;
        internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, int creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
