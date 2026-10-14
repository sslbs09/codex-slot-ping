using CodexSlotPing.Update;

namespace CodexSlotPing.App.Updates;

public static class ReleaseAssetContract
{
    public static AssetContractResult Evaluate(GitHubRelease release, SemVersion version)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(version);

        var zipName = $"CodexSlotPing-v{version}-win-x64.zip";
        var checksumName = zipName + ".sha256";
        var errors = new List<string>();
        var zipMatches = release.Assets.Where(asset => string.Equals(asset.Name, zipName, StringComparison.Ordinal)).ToArray();
        var checksumMatches = release.Assets.Where(asset => string.Equals(asset.Name, checksumName, StringComparison.Ordinal)).ToArray();

        if (release.Assets.Count != 2)
        {
            errors.Add("unexpected-asset-count");
        }
        foreach (var asset in release.Assets)
        {
            if (!string.Equals(asset.Name, zipName, StringComparison.Ordinal) &&
                !string.Equals(asset.Name, checksumName, StringComparison.Ordinal))
            {
                errors.Add("wrong-asset-filename");
            }
        }

        ValidateAsset(zipMatches, zipName, "zip", errors);
        ValidateAsset(checksumMatches, checksumName, "checksum", errors);

        if (zipMatches.Length == 0 && release.Assets.Any(asset => asset.Name.StartsWith("CodexSlotPing-", StringComparison.Ordinal) && asset.Name.EndsWith(".zip", StringComparison.Ordinal)))
        {
            errors.Add("wrong-zip-filename");
        }
        if (checksumMatches.Length == 0 && release.Assets.Any(asset => asset.Name.StartsWith("CodexSlotPing-", StringComparison.Ordinal) && asset.Name.EndsWith(".sha256", StringComparison.Ordinal)))
        {
            errors.Add("wrong-checksum-filename");
        }

        return new AssetContractResult(
            errors.Count == 0,
            zipMatches.Length == 1 ? zipMatches[0] : null,
            checksumMatches.Length == 1 ? checksumMatches[0] : null,
            errors);
    }

    private static void ValidateAsset(
        IReadOnlyList<GitHubReleaseAsset> matches,
        string expectedName,
        string kind,
        ICollection<string> errors)
    {
        if (matches.Count == 0)
        {
            errors.Add($"missing-{kind}");
            return;
        }
        if (matches.Count > 1)
        {
            errors.Add($"duplicate-{kind}");
            return;
        }

        var asset = matches[0];
        if (asset.Size <= 0)
        {
            errors.Add($"empty-{kind}");
        }
        if (string.IsNullOrWhiteSpace(asset.Name) || !string.Equals(asset.Name, expectedName, StringComparison.Ordinal))
        {
            errors.Add($"wrong-{kind}-filename");
        }
        if (asset.DownloadUrl is null || !asset.DownloadUrl.IsAbsoluteUri || asset.DownloadUrl.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"invalid-{kind}-url");
        }
    }
}
