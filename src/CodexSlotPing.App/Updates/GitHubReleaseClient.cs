using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexSlotPing.App.Updates;

public sealed class GitHubReleaseClient
{
    public const string ReleasesApiEndpoint = "https://api.github.com/repos/sslbs09/codex-slot-ping/releases?per_page=100&page=1";
    private static readonly Uri ApiOrigin = new("https://api.github.com/");
    private static readonly Regex NextLinkPattern = new(
        "<(?<url>[^>]+)>[^,]*\\brel\\s*=\\s*\"?next\"?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public GitHubReleaseClient(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetAllReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            var releases = new List<GitHubRelease>();
            var visitedPages = new HashSet<string>(StringComparer.Ordinal);
            Uri? page = new(ReleasesApiEndpoint);

            while (page is not null)
            {
                if (!visitedPages.Add(page.AbsoluteUri))
                {
                    throw Malformed("The releases API pagination link repeated a page.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, page);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexSlotPing", "0.3"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var isRateLimited = response.StatusCode == (HttpStatusCode)429 ||
                        (response.StatusCode == HttpStatusCode.Forbidden &&
                         response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
                         remaining.Any(value => value == "0"));
                    throw new ReleaseDiscoveryException(
                        isRateLimited ? UpdateErrorCategory.RateLimited : UpdateErrorCategory.ApiError,
                        $"GitHub Releases API returned HTTP {(int)response.StatusCode}.");
                }

                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var pageReleases = DeserializePage(json);
                releases.AddRange(pageReleases);
                page = GetNextPage(response, page);
            }

            return releases;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("GitHub Releases discovery was canceled.", exception, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new ReleaseDiscoveryException(UpdateErrorCategory.Timeout, "GitHub Releases discovery timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ReleaseDiscoveryException(UpdateErrorCategory.Network, "GitHub Releases could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new ReleaseDiscoveryException(UpdateErrorCategory.MalformedResponse, "GitHub Releases returned malformed JSON.", exception);
        }
    }

    private static IReadOnlyList<GitHubRelease> DeserializePage(string json)
    {
        var payloads = JsonSerializer.Deserialize<List<ReleasePayload?>>(json, JsonOptions);
        if (payloads is null)
        {
            throw Malformed("The releases response was not a JSON array.");
        }

        var releases = new List<GitHubRelease>(payloads.Count);
        foreach (var payload in payloads)
        {
            if (payload is null || string.IsNullOrWhiteSpace(payload.TagName) || payload.Draft is null ||
                payload.Prerelease is null || !TryHttpsUri(payload.HtmlUrl, out var releaseUrl) || payload.Assets is null)
            {
                throw Malformed("A release in the response did not match the GitHub release shape.");
            }

            var assets = new List<GitHubReleaseAsset>(payload.Assets.Count);
            foreach (var asset in payload.Assets)
            {
                if (asset is null || string.IsNullOrWhiteSpace(asset.Name) || asset.Size is null || asset.Size < 0 ||
                    !TryHttpsUri(asset.BrowserDownloadUrl, out var downloadUrl))
                {
                    throw Malformed("An asset in the response did not match the GitHub asset shape.");
                }

                assets.Add(new GitHubReleaseAsset(asset.Name, downloadUrl, asset.Size.Value));
            }

            releases.Add(new GitHubRelease(
                payload.TagName,
                payload.Name,
                payload.Body,
                releaseUrl,
                payload.Draft.Value,
                payload.Prerelease.Value,
                assets));
        }

        return releases;
    }

    private static Uri? GetNextPage(HttpResponseMessage response, Uri currentPage)
    {
        if (!response.Headers.TryGetValues("Link", out var headerValues))
        {
            return null;
        }

        foreach (var header in headerValues)
        {
            var match = NextLinkPattern.Match(header);
            if (match.Success)
            {
                if (!Uri.TryCreate(currentPage, match.Groups["url"].Value, out var nextPage) ||
                    nextPage.Scheme != Uri.UriSchemeHttps || !string.Equals(nextPage.Host, ApiOrigin.Host, StringComparison.OrdinalIgnoreCase))
                {
                    throw Malformed("The releases API returned an invalid pagination link.");
                }

                return nextPage;
            }

            if (header.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("rel=next", StringComparison.OrdinalIgnoreCase))
            {
                throw Malformed("The releases API returned an invalid pagination link.");
            }
        }

        return null;
    }

    private static bool TryHttpsUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static ReleaseDiscoveryException Malformed(string message) =>
        new(UpdateErrorCategory.MalformedResponse, message);

    private sealed class ReleasePayload
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool? Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool? Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<AssetPayload?>? Assets { get; init; }
    }

    private sealed class AssetPayload
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }
    }
}
