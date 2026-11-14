using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DbxLight.Core;

namespace DbxLight.Tests;

internal sealed class Unit5Fixture : IDisposable
{
    public string Root { get; }
    public ConfigStore Store { get; }
    public StateStore State { get; }
    public MountController Controller { get; }
    public DbxConfig Config { get; }
    public string MountRoot => MountPaths.CanonicalMount(Config.MountPoint);
    public string RemoteRoot => Path.Combine(Root, "remote");
    private readonly Dictionary<int, RuntimeState> owned = [];
    public Unit5Fixture(string label, bool writable = true, string writeBack = "2s", string[]? hidden = null, bool cloudNames = false)
    {
        Root = Path.Combine(@"D:\dbxl-test", "run-u5-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string remote = Path.Combine(Root, "remote"); Directory.CreateDirectory(remote);
        string empty = Path.Combine(Root, "empty.conf"); File.WriteAllText(empty, "", new UTF8Encoding(false));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string source = cloudNames ? ":local,encoding='Slash,Ctl,Del,Dot':" + remote : remote;
        Config = new DbxConfig
        {
            Remote = source, WriteAllowedRemotes = writable ? [source] : [], RcloneConfigPath = empty,
            MountPoint = new[] { "U:", "V:", "W:" }.First(MountPaths.IsAvailable),
            CacheDir = Path.Combine(Root, "cache"), RescueDir = Path.Combine(Root, "rescue"), LogDir = Path.Combine(Root, "logs"), RcPort = port,
            CacheMaxSize = "8M", CacheMinFreeSpace = "16M", PollInterval = "0", DirCacheTime = "1h",
            HiddenPaths = hidden ?? [], TestOverrides = cloudNames ? null : new() { WriteBack = writeBack, CachePollInterval = "1s" }
        };
        Store = new ConfigStore(Path.Combine(Root, "config.json")); Store.Save(Config);
        State = new StateStore(Path.Combine(Root, "runtime", "state.json"));
        Controller = new MountController(Store, State);
        Console.WriteLine("Unit5 run=" + Root);
    }
    public void Start(bool awaitRecovery = true)
    {
        try { Controller.Start(); }
        finally
        {
            RuntimeState? state = State.Load();
            if (state is not null)
            {
                owned[state.RclonePid] = state;
                JsonFiles.Write(Path.Combine(Root, "owned.json"), owned.Values.Select(s => new { pid = s.RclonePid, startedUtc = s.ProcessStartedUtc, executable = s.ExecutablePath }));
            }
        }
        if (awaitRecovery) Wait(() => !Controller.Status().RecoveryRequired, "Initial recovery not verified.");
    }
    public void Seed(string path, string text = "cloud content")
    {
        string target = Path.Combine(RemoteRoot, path.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text, new UTF8Encoding(false));
    }
    public void Write(string path, string text) => File.WriteAllText(Path.Combine(MountRoot, path.Replace('/', '\\')), text, new UTF8Encoding(false));
    public (int Exit, string Output, string Error) Cli(params string[] args)
    {
        string executable = Path.ChangeExtension(typeof(DbxLight.Cli.Program).Assembly.Location, ".exe");
        var info = RcloneArguments.ProcessInfo(executable, new[] { "--config", Store.PathName, "--state", State.PathName }.Concat(args));
        using Process process = Process.Start(info) ?? throw new IOException("Cannot start own CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(45000)) { process.Kill(); process.WaitForExit(10000); throw new TimeoutException("Own CLI timed out."); }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
    public static void Wait(Func<bool> predicate, string message, int seconds = 15)
    {
        var timer = Stopwatch.StartNew();
        do { if (predicate()) return; Thread.Sleep(50); } while (timer.Elapsed.TotalSeconds < seconds);
        throw new TimeoutException(message);
    }
    public void Dispose()
    {
        if (owned.Count > 0)
        {
            try { Controller.Stop(true); }
            finally
            {
                foreach (RuntimeState state in owned.Values)
                {
                    using var process = ProcessIdentity.Open(state);
                    if (process is not null) { process.Kill(); TestRunner.Assert(process.WaitForExit(10000)); }
                    using var remaining = ProcessIdentity.Open(state); TestRunner.Assert(remaining is null);
                }
            }
        }
        long bytes = new DirectoryInfo(MountMetadata.Extended(Root)).EnumerateFileSystemInfos("*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .OfType<FileInfo>().Sum(file => file.Length);
        TestRunner.Assert(bytes < 50L * 1024 * 1024);
        Console.WriteLine($"Owned unit5 PIDs={string.Join(',', owned.Keys)}: exited; run bytes={bytes}; artifacts={Root}");
    }
}
