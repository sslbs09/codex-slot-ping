using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using CodexSlotPing.Update;

namespace CodexSlotPing.App.Updates;

public enum UpdatePackageAssetKind
{
    Archive,
    Sha256Sidecar
}

public enum PackageDownloadFailure
{
    InvalidAsset,
    RedirectRejected,
    HttpFailure,
    NetworkFailure,
    Timeout,
    SizeLimitExceeded,
    SizeMismatch,
    IoFailure
}

public sealed record PackageDownloadProgress(long BytesReceived, long? TotalBytes, int? PercentComplete);

public sealed record PackageDownloadResult(
    bool Succeeded,
    long BytesReceived,
    long? TotalBytes,
    PackageDownloadFailure? Failure);

public sealed class UpdatePackageDownloader
{
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    public const int MaximumSidecarBytes = Sha256SidecarParser.MaximumBytes;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public UpdatePackageDownloader(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<PackageDownloadResult> DownloadAssetAsync(
        GitHubReleaseAsset asset,
        SemVersion targetVersion,
        UpdatePackageAssetKind kind,
        string destinationPartPath,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(targetVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPartPath);

        var expectedName = kind switch
        {
            UpdatePackageAssetKind.Archive => $"CodexSlotPing-v{targetVersion}-win-x64.zip",
            UpdatePackageAssetKind.Sha256Sidecar => $"CodexSlotPing-v{targetVersion}-win-x64.zip.sha256",
            _ => null
        };
        var maximumBytes = kind == UpdatePackageAssetKind.Archive ? MaximumArchiveBytes : MaximumSidecarBytes;
        if (expectedName is null || !string.Equals(asset.Name, expectedName, StringComparison.Ordinal) ||
            !TryGetExpectedAssetUri(asset.DownloadUrl, targetVersion, expectedName, out var initialUri) ||
            asset.Size <= 0 || asset.Size > maximumBytes)
        {
            return Failed(PackageDownloadFailure.InvalidAsset);
        }

        var createdPart = false;
        long bytesReceived = 0;
        long? totalBytes = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var response = await SendFollowingGitHubRedirectsAsync(initialUri, timeout.Token).ConfigureAwait(false);
            if (response is null)
            {
                return Failed(PackageDownloadFailure.RedirectRejected);
            }
            if (!response.IsSuccessStatusCode)
            {
                return Failed(PackageDownloadFailure.HttpFailure);
            }

            totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is < 0 || totalBytes > maximumBytes)
            {
                return Failed(PackageDownloadFailure.SizeLimitExceeded);
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPartPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            createdPart = true;

            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (bytesReceived > maximumBytes - read)
                {
                    return Failed(PackageDownloadFailure.SizeLimitExceeded, bytesReceived, totalBytes);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                bytesReceived += read;
                int? percent = totalBytes is > 0 and <= MaximumArchiveBytes
                    ? (int)Math.Clamp(bytesReceived * 100L / totalBytes.Value, 0L, 100L)
                    : null;
                progress?.Report(new PackageDownloadProgress(bytesReceived, totalBytes, percent));
            }

            await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            if (bytesReceived != asset.Size || (totalBytes is not null && bytesReceived != totalBytes.Value))
            {
                return Failed(PackageDownloadFailure.SizeMismatch, bytesReceived, totalBytes);
            }

            createdPart = false;
            return new PackageDownloadResult(true, bytesReceived, totalBytes, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(PackageDownloadFailure.Timeout, bytesReceived, totalBytes);
        }
        catch (HttpRequestException)
        {
            return Failed(PackageDownloadFailure.NetworkFailure, bytesReceived, totalBytes);
        }
        catch (IOException)
        {
            return Failed(PackageDownloadFailure.NetworkFailure, bytesReceived, totalBytes);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(PackageDownloadFailure.IoFailure, bytesReceived, totalBytes);
        }
        finally
        {
            if (createdPart)
            {
                TryDelete(destinationPartPath);
            }
        }
    }

    private async Task<HttpResponseMessage?> SendFollowingGitHubRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexSlotPing", "0.3"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            if (redirectCount >= MaximumRedirects || response.Headers.Location is null ||
                !Uri.TryCreate(currentUri, response.Headers.Location, out var redirectedUri) ||
                !IsAllowedGitHubAssetRedirect(redirectedUri))
            {
                response.Dispose();
                return null;
            }

            response.Dispose();
            currentUri = redirectedUri;
        }
    }

    private static bool TryGetExpectedAssetUri(Uri? candidate, SemVersion version, string fileName, out Uri expected)
    {
        expected = new Uri($"https://github.com/sslbs09/codex-slot-ping/releases/download/v{version}/{Uri.EscapeDataString(fileName)}");
        return candidate is not null && candidate.IsAbsoluteUri &&
            candidate.Scheme == Uri.UriSchemeHttps && string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            candidate.Port == 443 && string.IsNullOrEmpty(candidate.UserInfo) &&
            string.IsNullOrEmpty(candidate.Query) && string.IsNullOrEmpty(candidate.Fragment) &&
            string.Equals(candidate.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal);
    }

    private static bool IsAllowedGitHubAssetRedirect(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) &&
        (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static PackageDownloadResult Failed(
        PackageDownloadFailure failure,
        long bytesReceived = 0,
        long? totalBytes = null) => new(false, bytesReceived, totalBytes, failure);

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
