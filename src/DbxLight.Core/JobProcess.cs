using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DbxLight.Core;

/// <summary>Redirected helpers never execute before joining their kill-on-close job.</summary>
internal sealed class JobProcess(ProcessStartInfo info) : IDisposable
{
    internal static Action<string, Process>? Checkpoint { get; set; }
    private readonly LoginProcessJob job = new();
    private Process? process;
    private StreamReader? output, error;
    internal StreamReader StandardOutput => output ?? throw new InvalidOperationException("Helper has not started.");
    internal StreamReader StandardError => error ?? throw new InvalidOperationException("Helper has not started.");
    private Process Owned => process ?? throw new InvalidOperationException("Helper has not started.");
    internal bool HasExited => Owned.HasExited;
    internal int ExitCode => Owned.ExitCode;

    internal static JobProcess Start(ProcessStartInfo info)
    {
        var child = new JobProcess(info);
        try { child.Start(); return child; }
        catch { child.Dispose(); throw; }
    }

    internal bool Start()
    {
        if (process is not null) throw new InvalidOperationException("Helper has already started.");
        var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        if (!CreatePipe(out SafeFileHandle readOutput, out SafeFileHandle writeOutput, ref security, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (writeOutput)
        using (readOutput)
        {
            if (!CreatePipe(out SafeFileHandle readError, out SafeFileHandle writeError, ref security, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (writeError)
            using (readError)
            using (SafeFileHandle input = OpenNul("NUL", 0x80000000, 7, ref security, 3, 0, IntPtr.Zero))
            {
                if (input.IsInvalid || !SetHandleInformation(readOutput, 1, 0) || !SetHandleInformation(readError, 1, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr size = IntPtr.Zero;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr attributes = Marshal.AllocHGlobal(size), handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                string environment = string.Join('\0', info.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => p.Key + "=" + p.Value)) + "\0\0";
                IntPtr env = Marshal.StringToHGlobalUni(environment);
                bool initialized = false;
                try
                {
                    if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    initialized = true;
                    IntPtr[] inherited = [input.DangerousGetHandle(), writeOutput.DangerousGetHandle(), writeError.DangerousGetHandle()];
                    Marshal.Copy(inherited, 0, handles, inherited.Length);
                    if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var startup = new StartupInfoEx
                    {
                        Base = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                            StandardInput = inherited[0], StandardOutput = inherited[1], StandardError = inherited[2] },
                        Attributes = attributes
                    };
                    string command = string.Join(' ', new[] { info.FileName }.Concat(info.ArgumentList).Select(DetachedProcess.Quote));
                    if (!CreateProcess(null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08080404, env,
                        string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out ProcessInformation created))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        process = Process.GetProcessById(created.ProcessId);
                        _ = process.SafeHandle;
                        Checkpoint?.Invoke("created", process);
                        Checkpoint?.Invoke("before-job", process);
                        job.Assign(process);
                        Checkpoint?.Invoke("assigned", process);
                        // Duplicate only the parent's read ends. No other parent handles were inherited.
                        output = Reader(readOutput, info.StandardOutputEncoding);
                        error = Reader(readError, info.StandardErrorEncoding);
                        if (ResumeThread(created.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                        return true;
                    }
                    catch
                    {
                        _ = TerminateProcess(created.Process, 1);
                        if (process is not null) _ = process.WaitForExit(10000);
                        throw;
                    }
                    finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
                }
                finally
                {
                    if (initialized) DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(env);
                }
            }
        }
    }

    private static StreamReader Reader(SafeFileHandle source, Encoding? encoding)
    {
        if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out SafeFileHandle copy, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return new StreamReader(new FileStream(copy, FileAccess.Read, 4096, isAsync: false), encoding ?? new UTF8Encoding(false)); }
        catch { copy.Dispose(); throw; }
    }

    internal void StopJob() => job.Dispose();
    internal bool WaitForExit(int milliseconds)
    {
        bool exited = Owned.WaitForExit(milliseconds);
        if (exited) job.Dispose();
        return exited;
    }
    internal void WaitForExit()
    {
        if (!WaitForExit(10000)) throw new IOException("Owned helper did not stop.");
    }
    internal async Task WaitForExitAsync(CancellationToken cancellation = default)
    {
        await Owned.WaitForExitAsync(cancellation).ConfigureAwait(false);
        job.Dispose();
    }
    internal void Kill(bool entireProcessTree = false)
    {
        job.Dispose();
        if (!Owned.HasExited) Owned.Kill();
    }
    public void Dispose()
    {
        job.Dispose();
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill();
                if (!process.WaitForExit(10000)) throw new IOException("Owned helper did not stop.");
            }
        }
        finally { output?.Dispose(); error?.Dispose(); process?.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize; public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Base; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenNul(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, int flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation created);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr fromProcess, SafeFileHandle from, IntPtr toProcess, out SafeFileHandle to, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
}
