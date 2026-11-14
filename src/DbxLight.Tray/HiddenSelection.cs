using DbxLight.Core;

namespace DbxLight.Tray;

public sealed record HiddenDiff(IReadOnlyList<string> ToHide, IReadOnlyList<string> ToShow);
public sealed class HiddenSelection
{
    private string[] baseline;
    private string[] current;
    public IReadOnlyList<string> Paths => current;
    public IReadOnlyList<string> Baseline => baseline;
    public HiddenSelection(IEnumerable<string> hidden) { baseline = HiddenFolders.Normalize(hidden).ToArray(); current = [.. baseline]; }
    public bool IsShown(string path) => !HiddenFolders.IsHidden(HiddenFolders.NormalizePath(path), current);
    public void SetShown(string path, bool shown)
    {
        path = HiddenFolders.NormalizePath(path);
        string? parent = current.FirstOrDefault(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase) && HiddenFolders.IsHidden(path, [p]));
        if (shown && parent is not null) throw new ArgumentException("先に親フォルダ「" + parent + "」のチェックを付けてください。");
        if (shown) current = current.Where(p => !MountPathsForSelection.Below(p, path)).ToArray();
        else current = HiddenFolders.Normalize(current.Append(path)).ToArray();
    }
    // Counts the selected folder roots whose effective visibility changes, not every descendant.
    public HiddenDiff Diff() => new(
        current.Where(p => !HiddenFolders.IsHidden(p, baseline)).ToArray(),
        baseline.Where(p => !HiddenFolders.IsHidden(p, current)).ToArray());
    public void Accept() => baseline = [.. current];
    public bool Matches(IEnumerable<string> hidden) =>
        baseline.SequenceEqual(HiddenFolders.Normalize(hidden), StringComparer.OrdinalIgnoreCase);
}
internal static class MountPathsForSelection
{
    public static bool Below(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}
