using System.Text.RegularExpressions;

namespace DbxLight.Core;

public sealed class UpdateSource
{
    private const string Repository = "miyafcos/DbxLight";
    public static string ReleasesUrl => "https://github.com/" + Repository + "/releases";
    public Uri Latest { get; }
    public Uri? TestFeed { get; }
    public UpdateSource(string? feed = null)
    {
        feed ??= Environment.GetEnvironmentVariable("DBXL_UPDATE_FEED");
        if (feed is not null && Regex.IsMatch(feed, @"^http://(127\.0\.0\.1|localhost):[0-9]+/", RegexOptions.CultureInvariant)
            && Uri.TryCreate(feed, UriKind.Absolute, out Uri? uri) && uri.Port is > 0 and <= 65535
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            TestFeed = uri;
        Latest = TestFeed is null ? new("https://api.github.com/repos/" + Repository + "/releases/latest")
            : new(TestFeed, "releases/latest");
    }
    public void RequireAsset(Uri uri)
    {
        RequireTransport(uri);
        if (TestFeed is null)
        {
            const string origin = "https://github.com/";
            string url = uri.AbsoluteUri;
            bool allowed = uri.OriginalString.All(char.IsAscii) && Uri.UnescapeDataString(uri.AbsolutePath).All(char.IsAscii)
                && url.StartsWith(origin, StringComparison.Ordinal) && url.Length >= origin.Length + Repository.Length
                && url.AsSpan(origin.Length, Repository.Length).Equals(Repository.AsSpan(), StringComparison.OrdinalIgnoreCase)
                && url[(origin.Length + Repository.Length)..].StartsWith("/releases/download/", StringComparison.Ordinal);
            if (!allowed) throw new InvalidDataException("The release asset URL is outside the release source.");
        }
    }
    public void RequireTransport(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || (TestFeed is null ? uri.Scheme != "https" :
            uri.Scheme != "http" || !uri.Host.Equals(TestFeed.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != TestFeed.Port))
            throw new InvalidDataException("The update URL uses an unsupported host or transport.");
    }
}
