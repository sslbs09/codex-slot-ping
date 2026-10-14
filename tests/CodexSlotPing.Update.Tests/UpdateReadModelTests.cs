using CodexSlotPing.App.ReadModels;
using CodexSlotPing.App;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdateReadModelTests
{
    [Fact]
    public async Task AppUpdateController_ProjectsNetworkFailureIntoUpdateStatus()
    {
        var coordinator = new UpdateCheckCoordinator(_ =>
            Task.FromException<IReadOnlyList<GitHubRelease>>(
                new ReleaseDiscoveryException(UpdateErrorCategory.Network, "offline")));
        var controller = new AppUpdateController(coordinator, "0.2.0");

        var state = await controller.CheckForUpdatesAsync();

        Assert.Equal(UpdateViewStatus.Failed, state.Status);
        Assert.Equal(UpdateFailureCategory.Network, state.ErrorCategory);
        Assert.Equal("0.2.0", state.CurrentVersion);
    }

    [Fact]
    public void Projection_IsDeterministicAndIncludesOnlyAppOwnedFacts()
    {
        var evaluation = UpdateEvaluator.Evaluate("1.0.0", [new GitHubRelease(
            "v2.0.0", "Release", "notes", new Uri("https://github.com/sslbs09/codex-slot-ping/releases/tag/v2.0.0"),
            false, false,
            [new GitHubReleaseAsset("CodexSlotPing-v2.0.0-win-x64.zip", new Uri("https://github.com/example/package.zip"), 100),
             new GitHubReleaseAsset("CodexSlotPing-v2.0.0-win-x64.zip.sha256", new Uri("https://github.com/example/package.sha256"), 64)])]);
        var checkedAt = new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.Zero);

        var first = UpdateReadModelProjection.Create(evaluation, checkedAt);
        var second = UpdateReadModelProjection.Create(evaluation, checkedAt);

        Assert.Equal(first, second);
        Assert.Equal(UpdateViewStatus.UpdateAvailable, first.Status);
        Assert.Equal("1.0.0", first.CurrentVersion);
        Assert.Equal("2.0.0", first.TargetVersion);
        Assert.Equal("notes", first.ReleaseNotes);
        Assert.Equal("https://github.com/sslbs09/codex-slot-ping/releases/tag/v2.0.0", first.ReleaseUrl);
        Assert.Equal("CodexSlotPing-v2.0.0-win-x64.zip", first.ZipAsset!.FileName);
        Assert.Equal(100, first.ZipAsset.Size);
        Assert.Equal("CodexSlotPing-v2.0.0-win-x64.zip.sha256", first.ChecksumAsset!.FileName);
        Assert.Equal(checkedAt, first.CheckedAtUtc);

        var propertyTypes = typeof(UpdateReadModel).GetProperties().Select(property => property.PropertyType);
        Assert.DoesNotContain(propertyTypes, type => type == typeof(GitHubRelease) || type == typeof(GitHubReleaseAsset));
    }
}
