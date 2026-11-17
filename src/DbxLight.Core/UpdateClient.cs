using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DbxLight.Core;

public sealed record UpdateCheckResult(string CurrentVersion, string? LatestVersion, bool UpdateAvailable, bool CanApply, string ReleasesUrl, bool Unchanged);
public sealed record UpdateDownloadResult(string Version, string Staging, string ReleasesUrl);

public sealed class UpdateClient : IDisposable
{
    public const long MaximumZipBytes = 50L * 1024 * 1024;
    private readonly HttpClient http;
    private readonly UpdateSource source;
    private readonly UpdateStore store;
    private readonly string current;
    internal Func<TimeSpan, TimeSpan>? TimeoutOverride { get; set; }
    public static TimeSpan DownloadTimeout(long size) => TimeSpan.FromSeconds(30 + size * 10d / (1024 * 1024));
    private CancellationTokenSource Budget(CancellationToken token, TimeSpan value)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(token); source.CancelAfter(TimeoutOverride?.Invoke(value) ?? value); return source; }
    public UpdateClient(UpdateStore store, UpdateSource? source = null, HttpMessageHandler? handler = null, string? currentVersion = null)
    {
        this.store = store; this.source = source ?? new(); current = currentVersion ?? UpdateVersion.Current;
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
        http.Timeout = Timeout.InfiniteTimeSpan;
    }
    public async Task<UpdateCheckResult> CheckAsync(string installDirectory, CancellationToken token = default)
    {
        UpdateCheckState old = store.LoadCheck();
        // Record attempts too: an unavailable feed must not create an automatic retry loop.
        store.ChangeCheck(s => s with { CheckedUtc = DateTimeOffset.UtcNow });
        using var timeout = Budget(token, TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await SendAsync(source.Latest, old.Source == source.Latest.AbsoluteUri ? old.ETag : null, timeout.Token);
        bool unchanged = response.StatusCode == HttpStatusCode.NotModified;
        UpdateRelease? release;
        string? latest;
        if (unchanged)
        {
            if (old.Source != source.Latest.AbsoluteUri || old.ETag is null) throw new InvalidDataException("No cached release for this response.");
            release = old.Release; latest = old.LatestVersion;
            if (release is not null) { source.RequireAsset(new(release.Zip.Url)); source.RequireAsset(new(release.Checksum.Url)); }
        }
        else
        {
            response.EnsureSuccessStatusCode();
            byte[] data = await ReadLimitedAsync(response, 1024 * 1024, timeout.Token);
            using var doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()
                || !UpdateVersion.TryTag(root.GetProperty("tag_name").GetString(), out var version))
            { release = null; latest = null; }
            else
            {
                latest = version.ToString();
                string name = "DbxLight-" + latest + "-win-x64.zip";
                UpdateAsset Find(string assetName)
                {
                    var found = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == assetName).ToArray();
                    if (found.Length != 1) throw new InvalidDataException("Required release asset is missing or duplicated.");
                    var a = found[0];
                    string url = a.GetProperty("browser_download_url").GetString() ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new InvalidDataException("Invalid release asset URL.");
                    source.RequireAsset(uri);
                    long size = a.GetProperty("size").GetInt64();
                    if (size is < 1 || size > (assetName.EndsWith(".sha256", StringComparison.Ordinal) ? 4096 : MaximumZipBytes))
                        throw new InvalidDataException("Invalid release asset size.");
                    string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() : null;
                    return new(assetName, url, size, digest);
                }
                release = new(latest, Find(name), Find(name + ".sha256"));
            }
            store.ChangeCheck(s => s with { Source = source.Latest.AbsoluteUri, ETag = response.Headers.ETag?.ToString(),
                LatestVersion = latest, Release = release });
        }
        bool available = UpdateVersion.TryParse(latest, out var newest) && UpdateVersion.TryParse(current, out var installed) && newest.CompareTo(installed) > 0;
        return new(current, latest, available, UpdatePackage.CanApply(installDirectory), UpdateSource.ReleasesUrl, unchanged);
    }
    public async Task<UpdateDownloadResult> DownloadAsync(string installDirectory, CancellationToken token = default)
    {
        var check = await CheckAsync(installDirectory, token);
        if (!check.UpdateAvailable) throw new InvalidDataException("No newer release is available.");
        var release = store.LoadCheck().Release ?? throw new InvalidDataException("No downloadable release.");
        using var checksumTimeout = Budget(token, TimeSpan.FromSeconds(30));
        using var checksumResponse = await SendAsync(new(release.Checksum.Url), null, checksumTimeout.Token, asset: true);
        checksumResponse.EnsureSuccessStatusCode();
        byte[] checksumData = await ReadLimitedAsync(checksumResponse, 4096, checksumTimeout.Token);
        RequireSizeAndDigest(checksumData, release.Checksum);
        string line = Encoding.UTF8.GetString(checksumData).TrimEnd('\r', '\n');
        if (line.Length != 66 + release.Zip.Name.Length || line[64..66] != "  " || line[66..] != release.Zip.Name || !UpdatePackage.IsHash(line[..64]))
            throw new InvalidDataException("Invalid SHA-256 release asset.");
        using var zipTimeout = Budget(token, DownloadTimeout(release.Zip.Size));
        using var zipResponse = await SendAsync(new(release.Zip.Url), null, zipTimeout.Token, asset: true);
        zipResponse.EnsureSuccessStatusCode();
        byte[] zip = await ReadLimitedAsync(zipResponse, MaximumZipBytes, zipTimeout.Token);
        RequireSizeAndDigest(zip, release.Zip);
        if (!UpdatePackage.Hash(zip).Equals(line[..64], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release ZIP SHA-256 does not match.");
        return UpdatePackage.Stage(zip, store, release.Version, current, requireNewer: true);
    }
    private static void RequireSizeAndDigest(byte[] data, UpdateAsset asset)
    {
        if (data.LongLength != asset.Size) throw new InvalidDataException("Release asset size does not match.");
        if (asset.Digest is { } digest && (!digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !UpdatePackage.IsHash(digest[7..]) || !UpdatePackage.Hash(data).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Release asset digest does not match.");
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? etag, CancellationToken token, bool asset = false)
    {
        if (asset) source.RequireAsset(uri);
        for (int redirects = 0; ; redirects++)
        {
            source.RequireTransport(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("DbxLight/" + current);
            if (etag is not null) request.Headers.IfNoneMatch.ParseAdd(etag);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (redirects >= 5 || location is null) throw new HttpRequestException("Too many update redirects or missing location.");
                uri = new(uri, location);
                continue;
            }
            return response;
        }
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Release response exceeds the size limit.");
        using Stream input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Release response exceeds the size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public void Dispose() => http.Dispose();
}
