namespace DbxLight.Core;

/// <summary>Keep the mounted, independent and RC metadata views on the same exclusion policy.</summary>
internal static class RcloneFilters
{
    internal static IEnumerable<string> Arguments(DbxConfig config, string? filterFile = null)
    {
        yield return "--ignore-case";
        if (filterFile is not null)
        {
            yield return "--filter-from";
            yield return filterFile;
        }
        else
            foreach (string pattern in config.ExcludePatterns)
            {
                yield return "--exclude";
                yield return pattern;
            }
    }

    internal static object RemoteOptions(DbxConfig config) => new
    {
        FilterRule = Array.Empty<string>(), FilterFrom = Array.Empty<string>(),
        ExcludeRule = config.ExcludePatterns, ExcludeFrom = Array.Empty<string>(), IgnoreCase = true
    };
}
