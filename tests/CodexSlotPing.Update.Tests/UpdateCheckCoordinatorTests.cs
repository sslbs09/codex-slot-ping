using CodexSlotPing.Update;
using CodexSlotPing.App.Updates;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdateCheckCoordinatorTests
{
    [Fact]
    public async Task CheckAsync_CachesSuccessfulChecksForTwentyFourHours()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        var fetchCount = 0;
        var coordinator = new UpdateCheckCoordinator(_ =>
        {
            fetchCount++;
            return Task.FromResult(Releases());
        }, clock);

        await coordinator.CheckAsync("1.0.0");
        clock.Advance(TimeSpan.FromHours(23));
        var cached = await coordinator.CheckAsync("1.0.0");
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        var refreshed = await coordinator.CheckAsync("1.0.0");

        Assert.Equal(2, fetchCount);
        Assert.True(cached.FromCache);
        Assert.False(refreshed.FromCache);
    }

    [Fact]
    public async Task CheckAsync_ManualRefreshBypassesCacheAge()
    {
        var fetchCount = 0;
        var coordinator = new UpdateCheckCoordinator(_ =>
        {
            fetchCount++;
            return Task.FromResult(Releases());
        }, new TestTimeProvider(DateTimeOffset.UtcNow));

        await coordinator.CheckAsync("1.0.0");
        var manual = await coordinator.CheckAsync("1.0.0", manualRefresh: true);

        Assert.Equal(2, fetchCount);
        Assert.False(manual.FromCache);
    }

    [Fact]
    public async Task CheckAsync_CollapsesConcurrentChecksIntoOneFetch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<IReadOnlyList<GitHubRelease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchCount = 0;
        var coordinator = new UpdateCheckCoordinator(_ =>
        {
            Interlocked.Increment(ref fetchCount);
            started.TrySetResult();
            return response.Task;
        }, new TestTimeProvider(DateTimeOffset.UtcNow));

        var first = coordinator.CheckAsync("1.0.0");
        await started.Task;
        var second = coordinator.CheckAsync("1.0.0", manualRefresh: true);
        response.SetResult(Releases());
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, fetchCount);
        Assert.All(results, result => Assert.Equal(UpdateEvaluationStatus.UpdateAvailable, result.Evaluation.Status));
    }

    [Fact]
    public async Task CheckAsync_ReturnsFailedWithoutCachingDiscoveryErrors()
    {
        var fetchCount = 0;
        var coordinator = new UpdateCheckCoordinator(_ =>
        {
            fetchCount++;
            throw new ReleaseDiscoveryException(UpdateErrorCategory.RateLimited, "rate limited");
        }, new TestTimeProvider(DateTimeOffset.UtcNow));

        var first = await coordinator.CheckAsync("1.0.0");
        var second = await coordinator.CheckAsync("1.0.0");

        Assert.Equal(UpdateEvaluationStatus.Failed, first.Evaluation.Status);
        Assert.Equal(UpdateErrorCategory.RateLimited, first.Evaluation.ErrorCategory);
        Assert.False(second.FromCache);
        Assert.Equal(2, fetchCount);
    }

    private static IReadOnlyList<GitHubRelease> Releases()
    {
        const string name = "CodexSlotPing-v2.0.0-win-x64.zip";
        return [new GitHubRelease("v2.0.0", "2.0", "notes",
            new Uri("https://github.com/sslbs09/codex-slot-ping/releases/tag/v2.0.0"), false, false,
            [new GitHubReleaseAsset(name, new Uri("https://github.com/sslbs09/codex-slot-ping/releases/download/v2.0.0/" + name), 100),
             new GitHubReleaseAsset(name + ".sha256", new Uri("https://github.com/sslbs09/codex-slot-ping/releases/download/v2.0.0/" + name + ".sha256"), 64)])];
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
