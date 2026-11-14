namespace DbxLight.Core;

public sealed partial class MountController
{
    // Called only by the stopped-cache preflight. Queue absence after mount is not
    // evidence of stranded data: an open writer is legitimately Dirty but unqueued.
    private static bool NeedsPreflightNameRescue(RuntimeState view, CacheEntry entry, string cloud)
    {
        string encoded = Path.GetRelativePath(view.CacheDataRoot!, entry.DataPath).Replace('\\', '/');
        return !encoded.Equals(cloud, StringComparison.Ordinal);
    }
}
