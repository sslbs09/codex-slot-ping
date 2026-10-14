using CodexSlotPing.Update;
using CodexSlotPing.App.Updates;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdateEvaluatorTests
{
    [Theory]
    [InlineData("1.2.3", UpdateEvaluationStatus.UpToDate)]
    [InlineData("1.2.4", UpdateEvaluationStatus.UpToDate)]
    [InlineData("1.2.2", UpdateEvaluationStatus.UpdateAvailable)]
    public void Evaluate_DoesNotOfferDowngrade(string current, UpdateEvaluationStatus expected)
    {
        var result = UpdateEvaluator.Evaluate(current, [Release("v1.2.3")]);

        Assert.Equal(expected, result.Status);
        Assert.Equal("1.2.3", result.TargetVersion);
    }

    [Fact]
    public void Evaluate_FailsClosedWhenHighestStableContractIsBroken()
    {
        var result = UpdateEvaluator.Evaluate("1.0.0", [Release("v2.0.0", []), Release("v1.5.0", ValidAssets("v1.5.0"))]);

        Assert.Equal(UpdateEvaluationStatus.Failed, result.Status);
        Assert.Equal(UpdateErrorCategory.InvalidAssetContract, result.ErrorCategory);
        Assert.Equal("2.0.0", result.TargetVersion);
    }

    [Fact]
    public void Evaluate_ReportsFailureForInvalidCurrentVersion()
    {
        var result = UpdateEvaluator.Evaluate("not-a-version", [Release("v1.2.3", ValidAssets("v1.2.3"))]);

        Assert.Equal(UpdateEvaluationStatus.Failed, result.Status);
        Assert.Equal(UpdateErrorCategory.InvalidCurrentVersion, result.ErrorCategory);
    }

    [Fact]
    public void Evaluate_ProvidesReleaseDetailsAndAssetMetadata()
    {
        var release = Release("v2.0.0", ValidAssets("v2.0.0"));

        var result = UpdateEvaluator.Evaluate("1.0.0", [release]);

        Assert.Equal("Release v2.0.0", result.ReleaseTitle);
        Assert.Equal("release notes", result.ReleaseNotes);
        Assert.Equal(release.HtmlUrl, result.ReleaseUrl);
        Assert.Equal("CodexSlotPing-v2.0.0-win-x64.zip", result.ZipAsset!.Name);
        Assert.Equal("CodexSlotPing-v2.0.0-win-x64.zip.sha256", result.ChecksumAsset!.Name);
    }

    private static GitHubRelease Release(string tag, IReadOnlyList<GitHubReleaseAsset>? assets = null) =>
        new(tag, "Release " + tag, "release notes", new Uri("https://github.com/sslbs09/codex-slot-ping/releases/tag/" + tag),
            false, false, assets ?? ValidAssets(tag));

    private static IReadOnlyList<GitHubReleaseAsset> ValidAssets(string tag)
    {
        var prefix = "CodexSlotPing-" + tag + "-win-x64.zip";
        return [
            new(prefix, new Uri("https://github.com/sslbs09/codex-slot-ping/releases/download/" + tag + "/" + prefix), 100),
            new(prefix + ".sha256", new Uri("https://github.com/sslbs09/codex-slot-ping/releases/download/" + tag + "/" + prefix + ".sha256"), 64)
        ];
    }
}
