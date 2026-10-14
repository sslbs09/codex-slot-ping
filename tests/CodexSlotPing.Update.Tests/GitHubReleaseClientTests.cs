using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task GetAllReleasesAsync_CombinesPagesAndUsesHttpsUserAgent()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new DelegateHandler((request, _) =>
        {
            requests.Add(request);
            var page = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? "v0.10.0" : "v0.9.0";
            var response = JsonResponse($"[{{\"tag_name\":\"{page}\",\"name\":\"{page}\",\"body\":\"notes\",\"html_url\":\"https://github.com/sslbs09/codex-slot-ping/releases/tag/{page}\",\"draft\":false,\"prerelease\":false,\"assets\":[]}}]");
            if (requests.Count == 1)
            {
                response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/sslbs09/codex-slot-ping/releases?per_page=100&page=2>; rel=\"next\"");
            }
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new GitHubReleaseClient(http);

        var releases = await client.GetAllReleasesAsync(CancellationToken.None);

        Assert.Equal(2, releases.Count);
        Assert.Equal("v0.9.0", releases[0].TagName);
        Assert.Equal("v0.10.0", releases[1].TagName);
        Assert.All(requests, request => Assert.Equal("https", request.RequestUri!.Scheme));
        Assert.All(requests, request => Assert.Contains(request.Headers.UserAgent, part => part.Product?.Name == "CodexSlotPing"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateErrorCategory.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateErrorCategory.ApiError)]
    public async Task GetAllReleasesAsync_ClassifiesApiFailures(HttpStatusCode status, UpdateErrorCategory expected)
    {
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var client = new GitHubReleaseClient(http);

        var error = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => client.GetAllReleasesAsync());

        Assert.Equal(expected, error.ErrorCategory);
    }

    [Fact]
    public async Task GetAllReleasesAsync_RejectsMalformedJson()
    {
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(JsonResponse("{"))));
        var client = new GitHubReleaseClient(http);

        var error = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => client.GetAllReleasesAsync());

        Assert.Equal(UpdateErrorCategory.MalformedResponse, error.ErrorCategory);
    }

    [Fact]
    public async Task GetAllReleasesAsync_HonorsCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var client = new GitHubReleaseClient(http);
        using var cancellation = new CancellationTokenSource();
        var operation = client.GetAllReleasesAsync(cancellation.Token);
        await started.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task GetAllReleasesAsync_EnforcesTimeout()
    {
        using var http = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var client = new GitHubReleaseClient(http, TimeSpan.FromMilliseconds(40));

        var error = await Assert.ThrowsAsync<ReleaseDiscoveryException>(() => client.GetAllReleasesAsync());

        Assert.Equal(UpdateErrorCategory.Timeout, error.ErrorCategory);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
