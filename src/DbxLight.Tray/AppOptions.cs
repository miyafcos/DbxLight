using DbxLight.Core;

namespace DbxLight.Tray;

public sealed record AppOptions(string ConfigPath, string StatePath, string? HidePath = null, string? WebPath = null,
    string? SmokeOutput = null, string? RegistryRoot = null, string? Registration = null,
    string? WebCopyPath = null, string? SharePath = null, bool CopyPathServer = false)
{
    public static AppOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? registration = null;
        bool copyPathServer = false, embedding = false;
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key is "--register" or "--unregister")
            {
                if (registration is not null) throw new ArgumentException("登録の操作は 1 つずつ指定してください。");
                registration = key;
                continue;
            }
            if (key == "--copy-path-server" && !copyPathServer) { copyPathServer = true; continue; }
            if (key.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) && !embedding) { embedding = true; continue; }
            if (key is not ("--config" or "--state" or "--hide" or "--web" or "--hide-shell" or "--web-shell" or
                "--web-copy-shell" or "--share-shell" or "--smoke" or "--registry-root"))
                throw new ArgumentException("使えない引数です: " + key);
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(key, args[i]))
                throw new ArgumentException("引数の値がないか、重複しています: " + key);
        }
        string? Get(string key) => values.GetValueOrDefault(key);
        if (embedding && !copyPathServer || new[] { Get("--hide"), Get("--web"), Get("--hide-shell"), Get("--web-shell"),
            Get("--web-copy-shell"), Get("--share-shell"), Get("--smoke"), registration,
            copyPathServer ? "copy" : null }.Count(v => v is not null) > 1)
            throw new ArgumentException("操作は 1 つずつ指定してください。");
        string? Shell(string key) => Get(key) is { } encoded ? ShellPaths.DecodeShellPath(encoded) : null;
        return new(new ConfigStore(Get("--config")).PathName, new StateStore(Get("--state")).PathName,
            Get("--hide") ?? Shell("--hide-shell"), Get("--web") ?? Shell("--web-shell"), Get("--smoke"), Get("--registry-root"), registration,
            Shell("--web-copy-shell"), Shell("--share-shell"), copyPathServer);
    }
}
