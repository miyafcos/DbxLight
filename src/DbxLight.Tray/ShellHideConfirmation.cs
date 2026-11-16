using DbxLight.Core;

namespace DbxLight.Tray;

public sealed class StaleHideConfirmationException() : InvalidOperationException(
    "\u8a2d\u5b9a\u304c\u5909\u308f\u308a\u307e\u3057\u305f\u3002\u5bfe\u8c61\u3092\u78ba\u8a8d\u3057\u76f4\u3057\u3066\u304f\u3060\u3055\u3044\u3002");

public sealed record ShellHideConfirmation(string Remote, string MountPoint, string? RcloneConfigPath, string AbsolutePath, string RelativePath)
{
    public static ShellHideConfirmation Create(DbxConfig config, string path) =>
        new(config.Remote, MountPaths.CanonicalMount(config.MountPoint), Normalize(config.RcloneConfigPath),
            Path.GetFullPath(path), ShellPaths.ToRelativeAbsolute(path, config.MountPoint));

    private static string? Normalize(string? path) => path is null ? null : Path.GetFullPath(path);

    public DbxConfig Apply(DbxConfig current)
    {
        if (!Remote.Equals(current.Remote, StringComparison.Ordinal) ||
            !MountPoint.Equals(MountPaths.CanonicalMount(current.MountPoint), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(RcloneConfigPath, Normalize(current.RcloneConfigPath), StringComparison.OrdinalIgnoreCase))
            throw new StaleHideConfirmationException();
        return current with { HiddenPaths = [.. current.HiddenPaths, RelativePath] };
    }

    public string Message => "remote: " + Remote + "\n\u7d76\u5bfe\u30d1\u30b9: " + AbsolutePath + "\n" +
        "\u3053\u306e PC \u306e\u898b\u3048\u65b9\u3060\u3051\u3092\u5909\u3048\u307e\u3059\u3002Dropbox \u4e0a\u306e\u30d5\u30a1\u30a4\u30eb\u3068\u3001\u307b\u304b\u306e\u4eba\u306e\u898b\u3048\u65b9\u306f\u5909\u308f\u308a\u307e\u305b\u3093\u3002\n" +
        "\u8868\u793a\u3057\u306a\u3044\u5834\u6240\u3068\u540c\u3058\u540d\u524d\u306e\u30d5\u30a9\u30eb\u30c0\u3092\u3001\u3042\u3068\u304b\u3089\u4f5c\u3089\u306a\u3044\u3067\u304f\u3060\u3055\u3044\u3002\n" +
        "\u3053\u306e\u30d5\u30a9\u30eb\u30c0\u3092\u8868\u793a\u3057\u306a\u3044\u8a2d\u5b9a\u306b\u3057\u307e\u3059\u304b\u3002";
}
