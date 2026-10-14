using CodexSlotPing.Update;

namespace CodexSlotPing.App.ReadModels;

public enum UpdateViewStatus
{
    Unknown,
    Checking,
    UpToDate,
    UpdateAvailable,
    Failed
}

public enum UpdateFailureCategory
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

public sealed record UpdateAssetView(string FileName, string DownloadUrl, long Size);

public sealed record UpdateReadModel(
    UpdateViewStatus Status,
    string CurrentVersion,
    string? TargetVersion,
    string? ReleaseTitle,
    string? ReleaseNotes,
    string? ReleaseUrl,
    UpdateAssetView? ZipAsset,
    UpdateAssetView? ChecksumAsset,
    UpdateFailureCategory? ErrorCategory,
    DateTimeOffset? CheckedAtUtc)
{
    public static UpdateReadModel Unknown(string currentVersion) =>
        new(UpdateViewStatus.Unknown, currentVersion, null, null, null, null, null, null, null, null);

    public static UpdateReadModel Checking(string currentVersion) =>
        new(UpdateViewStatus.Checking, currentVersion, null, null, null, null, null, null, null, null);
}

public static class UpdateReadModelProjection
{
    public static UpdateReadModel Create(UpdateEvaluation evaluation, DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return new UpdateReadModel(
            evaluation.Status switch
            {
                UpdateEvaluationStatus.UpToDate => UpdateViewStatus.UpToDate,
                UpdateEvaluationStatus.UpdateAvailable => UpdateViewStatus.UpdateAvailable,
                _ => UpdateViewStatus.Failed
            },
            evaluation.CurrentVersion,
            evaluation.TargetVersion,
            evaluation.ReleaseTitle,
            evaluation.ReleaseNotes,
            evaluation.ReleaseUrl?.AbsoluteUri,
            ProjectAsset(evaluation.ZipAsset),
            ProjectAsset(evaluation.ChecksumAsset),
            ProjectError(evaluation.ErrorCategory),
            checkedAtUtc);
    }

    private static UpdateAssetView? ProjectAsset(GitHubReleaseAsset? asset) =>
        asset is null ? null : new UpdateAssetView(asset.Name, asset.DownloadUrl.AbsoluteUri, asset.Size);

    private static UpdateFailureCategory? ProjectError(UpdateErrorCategory? category) => category switch
    {
        UpdateErrorCategory.Network => UpdateFailureCategory.Network,
        UpdateErrorCategory.Timeout => UpdateFailureCategory.Timeout,
        UpdateErrorCategory.ApiError => UpdateFailureCategory.ApiError,
        UpdateErrorCategory.RateLimited => UpdateFailureCategory.RateLimited,
        UpdateErrorCategory.MalformedResponse => UpdateFailureCategory.MalformedResponse,
        UpdateErrorCategory.NoStableRelease => UpdateFailureCategory.NoStableRelease,
        UpdateErrorCategory.InvalidAssetContract => UpdateFailureCategory.InvalidAssetContract,
        UpdateErrorCategory.InvalidCurrentVersion => UpdateFailureCategory.InvalidCurrentVersion,
        _ => null
    };
}
