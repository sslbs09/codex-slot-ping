namespace CodexSlotPing.Update;

public enum UpdateErrorCategory
{
    Network,
    Timeout,
    ApiError,
    RateLimited,
    MalformedResponse,
    NoStableRelease,
    InvalidAssetContract,
    InvalidCurrentVersion
}

public enum UpdateEvaluationStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed record GitHubReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record GitHubRelease(
    string TagName,
    string? Name,
    string? Body,
    Uri HtmlUrl,
    bool Draft,
    bool Prerelease,
    IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed record StableRelease(GitHubRelease Release, SemVersion Version);

public sealed record AssetContractResult(
    bool IsValid,
    GitHubReleaseAsset? Zip,
    GitHubReleaseAsset? Checksum,
    IReadOnlyList<string> Errors);

public sealed record UpdateEvaluation(
    UpdateEvaluationStatus Status,
    string CurrentVersion,
    string? TargetVersion,
    string? ReleaseTitle,
    string? ReleaseNotes,
    Uri? ReleaseUrl,
    GitHubReleaseAsset? ZipAsset,
    GitHubReleaseAsset? ChecksumAsset,
    UpdateErrorCategory? ErrorCategory);

public sealed record UpdateCheckResult(UpdateEvaluation Evaluation, DateTimeOffset CheckedAtUtc, bool FromCache);

public sealed class ReleaseDiscoveryException(UpdateErrorCategory errorCategory, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UpdateErrorCategory ErrorCategory { get; } = errorCategory;
}
