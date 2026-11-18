using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DbxLight.FakeRclone;

internal static class Program
{
    private const string Marker = "OAUTH_SECRET_UNIT4_NEVER_DISPLAY";
    private static int Main(string[] args)
    {
        string root = AppContext.BaseDirectory;
        string scenario = File.Exists(Path.Combine(root, "scenario.txt")) ? File.ReadAllText(Path.Combine(root, "scenario.txt")).Trim() : "success";
        if (args.Length == 1 && args[0] == "--fake-child") { Thread.Sleep(TimeSpan.FromMinutes(6)); return 0; }
        string records = Path.Combine(root, "calls.jsonl");
        using Process self = Process.GetCurrentProcess();
        File.AppendAllText(records, JsonSerializer.Serialize(new { pid = Environment.ProcessId, startedUtc = self.StartTime.ToUniversalTime(), args }) + "\n", new UTF8Encoding(false));
        int index = Array.IndexOf(args, "--config");
        string? config = index >= 0 ? args[index + 1] : null;
        if (args[0] == "mount" && scenario is "mount" or "mount-poison" or "mount-bad-config-paths" or "mount-long-config-paths")
        {
            if (scenario == "mount-poison")
            {
                Console.WriteLine(Marker); Console.Error.WriteLine("ERROR " + Marker);
                int logIndex = Array.IndexOf(args, "--log-file");
                if (logIndex >= 0 && args[logIndex + 1] != "NUL")
                    File.AppendAllText(args[logIndex + 1], "ERROR " + Marker + "\n", new UTF8Encoding(false));
            }
            return Mount(args, scenario);
        }
        if (scenario.StartsWith("poison", StringComparison.Ordinal) && args[0] is "version" or "rc")
        {
            Console.Error.WriteLine(Marker);
            Console.WriteLine(args[0] == "version" ? "rclone v1.74.3" : "{" + (char)34 + Marker + (char)34 + ":");
            return 0;
        }
        if (scenario.StartsWith("snapshot", StringComparison.Ordinal))
        {
            if (args[0] == "config" && args.Length > 1 && args[1] == "file")
            {
                Console.WriteLine(Path.Combine(root, "snapshot.conf")); return 0;
            }
            if (args[0] == "about")
            {
                var expected = new List<string> { "about", "sample:/", "--json", "--contimeout", "5s", "--timeout", "10s",
                    "--retries", "1", "--low-level-retries", "1" };
                if (config is not null) { expected.Add("--config"); expected.Add(Path.Combine(root, "snapshot.conf")); }
                expected.AddRange(["--ask-password=false", "--ftp-ask-password=false", "--sftp-ask-password=false"]);
                if (!args.SequenceEqual(expected, StringComparer.Ordinal)) return 93;
                Console.WriteLine(Marker); Console.Error.WriteLine(Marker);
                if (scenario == "snapshot-wait") { Thread.Sleep(30000); return 0; }
                string selected = config ?? Path.Combine(root, "snapshot.conf");
                File.WriteAllText(selected, File.ReadAllText(selected).Replace("2000-01-01", "2099-01-01"), new UTF8Encoding(false));
                return 0;
            }
            return 92;
        }
        if (args[0] == "about" && scenario == "folder-refresh-hold" && config is not null)
        {
            File.Copy(config, config + ".staged");
            File.WriteAllText(Path.Combine(root, "renewal-ready"), "");
            Thread.Sleep(TimeSpan.FromMinutes(1));
            return 0;
        }
        if (args[0] == "about" && scenario == "folder-refresh" && config is not null)
        {
            string text = File.ReadAllText(config);
            // Like rclone, a saved token with no expiry or a future expiry is reused.
            if (text.Contains("2000-01-01", StringComparison.Ordinal))
                File.WriteAllText(config, text.Replace("SAMPLE_UNIT9_TOKEN", "SAMPLE_UNIT9_UPDATED").Replace("2000-01-01", "2099-01-01"));
            return 0;
        }
        if (args[0] == "listremotes")
        {
            Console.Error.WriteLine(Marker);
            if (scenario == "folder-web-roundtrip") { Console.WriteLine("sample: dropbox"); return 0; }
            if (scenario.StartsWith("poison", StringComparison.Ordinal)) Console.WriteLine(Marker);
            Console.WriteLine(config is null ? "sample-existing:\nteam-archive:" : File.Exists(config) && new FileInfo(config).Length > 0 ? "dbxlight:" : "");
            return 0;
        }
        if (args[0] == "lsjson")
        {
            Console.Error.WriteLine(Marker);
            if (scenario.StartsWith("poison", StringComparison.Ordinal))
            {
                if (scenario == "poison-failure") { Console.WriteLine(Marker); return 17; }
                if (scenario == "poison-json") { Console.WriteLine("{" + (char)34 + Marker + (char)34 + ":"); return 0; }
                string selected = JsonSerializer.Serialize(new { Name = "sample.txt", IsDir = false, Size = 3,
                    ModTime = "2026-01-01T00:00:00Z", ProviderToken = Marker });
                Console.WriteLine(args.Contains("--stat") ? selected : "[" + selected + "]");
                return 0;
            }
            if (scenario == "folder-web-roundtrip")
            {
                const string item = "{\"Name\":\"Reports\",\"IsDir\":true,\"Size\":-1,\"ModTime\":\"2026-01-01T00:00:00Z\"}";
                Console.WriteLine(args.Contains("--stat") ? item : "[" + item + "]");
                return 0;
            }
            if (scenario == "team-failure" && args[1].EndsWith(":/", StringComparison.Ordinal)) { Console.WriteLine(Marker); return 17; }
            if (scenario == "listing-failure") { Console.WriteLine(Marker); return 17; }
            Console.WriteLine("[{\"Name\":\"01 \\u696d\\u52d9\\u8cc7\\u6599\",\"IsDir\":true},{\"Name\":\"02 \\u5171\\u6709\",\"IsDir\":true}]");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "config")
        {
            Console.WriteLine(Marker); Console.Error.WriteLine(Marker);
            if (config is null) return 91;
            if (args[1] == "delete") { File.WriteAllText(config, "", new UTF8Encoding(false)); return scenario == "delete-failure" ? 17 : 0; }
            File.WriteAllText(config, "[dbxlight]\ntype = dropbox\ntoken = " + Marker + "\n", new UTF8Encoding(false));
            if (scenario is "wait" or "failure")
            {
                if (scenario == "failure") return 17;
                using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--fake-child" } })!;
                File.AppendAllText(records, JsonSerializer.Serialize(new { pid = child.Id, startedUtc = child.StartTime.ToUniversalTime(), args = new[] { "--fake-child" } }) + "\n", new UTF8Encoding(false));
                Thread.Sleep(TimeSpan.FromMinutes(6));
            }
            return 0;
        }
        return 92;
    }

    private static int Mount(string[] args, string scenario)
    {
        string Value(string name) => args[Array.IndexOf(args, name) + 1];
        string drive = args[2].TrimEnd('\\');
        string folder = Path.Combine(AppContext.BaseDirectory, "mounted");
        Directory.CreateDirectory(folder);
        string cache = Value("--cache-dir");
        string data = Path.Combine(cache, "vfs", "sample"), meta = Path.Combine(cache, "vfsMeta", "sample");
        Directory.CreateDirectory(data); Directory.CreateDirectory(meta);
        string address = Value("--rc-addr");
        int port = int.Parse(address[(address.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        string target = @"\??\" + folder;
        bool mapped = false;
        bool badConfigPaths = scenario is "mount-bad-config-paths" or "mount-long-config-paths";
        using Process? realMount = badConfigPaths ? StartLocalMount() : null;
        try
        {
            listener.Start();
            if (realMount is null)
            {
                if (!DefineDosDevice(1, drive, target)) return 93;
                mapped = true;
            }
            bool quit = false;
            while (!quit)
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                string request = reader.ReadLine() ?? "";
                int length = 0; string? header;
                while (!string.IsNullOrEmpty(header = reader.ReadLine()))
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                char[] body = new char[length]; int read = 0;
                while (read < body.Length) { int n = reader.Read(body, read, body.Length - read); if (n == 0) break; read += n; }
                string path = request.Split(' ').ElementAtOrDefault(1) ?? "";
                object response = path switch
                {
                    "/core/pid" => new { pid = Environment.ProcessId },
                    "/vfs/stats" => new { fs = args[1], diskCache = new { uploadsQueued = 0, uploadsInProgress = 0, erroredFiles = 0, files = 0, bytesUsed = 0, path = data, pathMeta = meta } },
                    "/vfs/queue" => new { queue = Array.Empty<object>() },
                    "/core/transferred" => new { transferred = Array.Empty<object>() },
                    "/core/stats" => new { transfers = 0, transferring = Array.Empty<object>() },
                    _ => new { }
                };
                bool optionalCheck = badConfigPaths && path == "/config/paths";
                if (optionalCheck)
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config-paths-observed"), "");
                    response = new { config = @"T:\" + new string('z', 40000) };
                }
                byte[] json = optionalCheck && scenario == "mount-bad-config-paths" ? "{"u8.ToArray() :
                    JsonSerializer.SerializeToUtf8Bytes(response);
                byte[] prefix = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + json.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(prefix); stream.Write(json); stream.Flush();
                quit = path == "/core/quit";
            }
            return 0;
        }
        finally
        {
            listener.Stop();
            if (mapped) _ = DefineDosDevice(7, drive, target);
            if (realMount is { HasExited: false }) { realMount.Kill(); realMount.WaitForExit(); }
        }

        Process StartLocalMount()
        {
            string executable = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "real-rclone.txt")).Trim();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string option in new[] { "mount", folder, drive, "--read-only", "--network-mode",
                "--volname", Value("--volname"), "--config", Value("--config") })
                start.ArgumentList.Add(option);
            return Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated local rclone.");
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string name, string target);
}
