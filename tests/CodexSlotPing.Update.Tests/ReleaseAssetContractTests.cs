using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class ReleaseAssetContractTests
{
    private static readonly SemVersion Version = SemVersion.Parse("1.2.3");

    [Fact]
    public void Evaluate_AcceptsTheExactNonemptyAssetPair()
    {
        var result = ReleaseAssetContract.Evaluate(Release(Assets(Zip(), Checksum())), Version);

        Assert.True(result.IsValid);
        Assert.Equal("CodexSlotPing-v1.2.3-win-x64.zip", result.Zip!.Name);
        Assert.Equal("CodexSlotPing-v1.2.3-win-x64.zip.sha256", result.Checksum!.Name);
    }

    [Fact]
    public void Evaluate_RejectsMissingZip()
    {
        Assert.False(ReleaseAssetContract.Evaluate(Release(Assets(Checksum())), Version).IsValid);
    }

    [Fact]
    public void Evaluate_RejectsMissingChecksum()
    {
        Assert.False(ReleaseAssetContract.Evaluate(Release(Assets(Zip())), Version).IsValid);
    }

    [Fact]
    public void Evaluate_RejectsDuplicateZip()
    {
        Assert.False(ReleaseAssetContract.Evaluate(Release(Assets(Zip(), Zip())), Version).IsValid);
    }

    [Fact]
    public void Evaluate_RejectsDuplicateChecksum()
    {
        Assert.False(ReleaseAssetContract.Evaluate(Release(Assets(Zip(), Checksum(), Checksum())), Version).IsValid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_RejectsAnEmptyAsset(bool zip)
    {
        var assets = zip ? Assets(Zip(size: 0), Checksum()) : Assets(Zip(), Checksum(size: 0));

        Assert.False(ReleaseAssetContract.Evaluate(Release(assets), Version).IsValid);
    }

    [Fact]
    public void Evaluate_RejectsIncorrectFilenames()
    {
        var assets = Assets(
            Asset("CodexSlotPing-v1.2.3-win-arm64.zip"),
            Asset("CodexSlotPing-v1.2.3-win-x64.sha256"));

        Assert.False(ReleaseAssetContract.Evaluate(Release(assets), Version).IsValid);
    }

    [Fact]
    public void Evaluate_RejectsAdditionalAssetsOutsideTheExactPair()
    {
        var assets = Assets(Zip(), Checksum(), Asset("readme.txt"));

        Assert.False(ReleaseAssetContract.Evaluate(Release(assets), Version).IsValid);
    }

    private static GitHubRelease Release(IReadOnlyList<GitHubReleaseAsset> assets) =>
        new("v1.2.3", "1.2.3", "notes", new Uri("https://github.com/sslbs09/codex-slot-ping/releases/tag/v1.2.3"),
            false, false, assets);

    private static IReadOnlyList<GitHubReleaseAsset> Assets(params GitHubReleaseAsset[] assets) => assets;
    private static GitHubReleaseAsset Zip(long size = 20) => Asset("CodexSlotPing-v1.2.3-win-x64.zip", size);
    private static GitHubReleaseAsset Checksum(long size = 64) => Asset("CodexSlotPing-v1.2.3-win-x64.zip.sha256", size);
    private static GitHubReleaseAsset Asset(string name, long size = 20) =>
        new(name, new Uri("https://github.com/sslbs09/codex-slot-ping/releases/download/v1.2.3/" + name), size);
}
