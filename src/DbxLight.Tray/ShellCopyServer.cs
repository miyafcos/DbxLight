using System.Runtime.InteropServices;
using DbxLight.Core;

namespace DbxLight.Tray;

// DelegateExecute receives a single IShellItemArray even for a mixed selection.
// The local server keeps managed code out of Explorer and runs clipboard calls on its STA.
public static class ShellCopyServer
{
    internal const string ClassId = "{81BF6953-636B-4048-8EA7-84C39FC83F10}";
    internal const int MaxItems = 1024;
    internal const int MaxPathCharacters = 262144;

    internal static int Run(AppOptions options)
    {
        Application.OleRequired();
        using var context = new ApplicationContext();
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        using var idle = new System.Windows.Forms.Timer { Interval = 45000 };
        idle.Tick += (_, _) => context.ExitThread();
        var factory = new CopyFactory(paths => dispatcher.BeginInvoke(async () =>
        {
            idle.Stop();
            try
            {
                string text = await Task.Run(() => CopyTextAsync(options, paths, DropboxFolderClient.Open));
                Clipboard.SetText(text);
            }
            catch (FolderOperationException e) { MessageBox.Show(e.Message, "パスをコピー", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (Exception e) when (e is not OutOfMemoryException)
            { MessageBox.Show("パスをコピーできませんでした。選択した項目とクリップボードの状態を確認してください。", "パスをコピー", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { context.ExitThread(); }
        }), failure => dispatcher.BeginInvoke(() =>
        {
            idle.Stop();
            MessageBox.Show(failure.Message, "パスをコピー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            context.ExitThread();
        }));
        Guid clsid = new(ClassId);
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(in clsid, factory, 4 /* CLSCTX_LOCAL_SERVER */,
            0 /* REGCLS_SINGLEUSE */, out uint cookie));
        try { idle.Start(); Application.Run(context); }
        finally { CoRevokeClassObject(cookie); GC.KeepAlive(factory); }
        return 0;
    }

    internal static async Task<string> CopyTextAsync(AppOptions options, string[] paths, Func<DbxConfig, DropboxFolderClient> open,
        Func<RuntimeState, FolderMountObservation?>? inspect = null)
    {
        var config = DropboxFolderClient.ReadConfiguration(options.ConfigPath);
        string[] lines = await DropboxCopyPaths.CopyAsync(config, paths, open,
            mount: new FolderShareMount(options.ConfigPath, options.StatePath, inspect)).ConfigureAwait(false);
        return string.Join(Environment.NewLine, lines);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoRegisterClassObject(in Guid clsid,
        [MarshalAs(UnmanagedType.Interface)] IClassFactory factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoRevokeClassObject(uint cookie);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class CopyFactory(Action<string[]> copy, Action<FolderOperationException>? selectionError = null) : IClassFactory
    {
        public int CreateInstance(IntPtr outer, in Guid iid, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            IntPtr unknown = Marshal.GetIUnknownForObject(new CopySelection(copy, selectionError));
            try
            {
                Guid requested = iid;
                return Marshal.QueryInterface(unknown, ref requested, out result);
            }
            finally { Marshal.Release(unknown); }
        }

        public int LockServer(bool locked) => 0;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class CopySelection(Action<string[]> copy, Action<FolderOperationException>? selectionError = null)
        : IExecuteCommand, IObjectWithSelection
    {
        private IShellItemArray? selection;
        public void SetSelection(IShellItemArray items) => selection = items;
        public void GetSelection(in Guid iid, out IntPtr items)
        {
            if (selection is null) throw new COMException(null, unchecked((int)0x80004005));
            IntPtr unknown = Marshal.GetIUnknownForObject(selection);
            try
            {
                Guid requested = iid;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref requested, out items));
            }
            finally { Marshal.Release(unknown); }
        }

        public void Execute()
        {
            if (selection is null) throw new COMException(null, unchecked((int)0x80004005));
            selection.GetCount(out uint count);
            if (count == 0) return;
            if (count > MaxItems) { RejectLimit(); return; }
            var paths = new string[count];
            int remaining = MaxPathCharacters;
            for (uint i = 0; i < count; i++)
            {
                selection.GetItemAt(i, out IShellItem item);
                item.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out string path);
                if (path.Length > remaining) { RejectLimit(); return; }
                remaining -= path.Length;
                paths[i] = path;
            }
            copy(paths);
        }

        private void RejectLimit()
        {
            var error = FolderOperationException.For("selection_limit");
            if (selectionError is null) throw error;
            selectionError(error);
        }

        public void SetKeyState(uint state) { }
        public void SetParameters(string? parameters) { }
        public void SetPosition(Point position) { }
        public void SetShowWindow(int show) { }
        public void SetNoShowUI(bool noUI) { }
        public void SetDirectory(string? directory) { }
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, in Guid iid, out IntPtr result);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    [ComImport, Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExecuteCommand
    {
        void SetKeyState(uint state);
        void SetParameters([MarshalAs(UnmanagedType.LPWStr)] string? parameters);
        void SetPosition(Point position);
        void SetShowWindow(int show);
        void SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noUI);
        void SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string? directory);
        void Execute();
    }

    [ComImport, Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectWithSelection
    {
        void SetSelection(IShellItemArray items);
        void GetSelection(in Guid iid, out IntPtr items);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        // Only GetCount and GetItemAt are used. The first four slots occupy their native positions.
        void BindToHandler();
        void GetPropertyStore();
        void GetPropertyDescriptionList();
        void GetAttributes();
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(uint displayNameType, [MarshalAs(UnmanagedType.LPWStr)] out string path);
    }
}
