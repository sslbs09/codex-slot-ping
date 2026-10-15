using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.App.ReadModels;

public enum UpdateViewStatus
{
    Unknown,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Installing,
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
    InvalidCurrentVersion,
    DownloadFailed,
    ChecksumInvalid,
    PackageInvalid,
    VersionMismatch,
    StagingIoFailure,
    UpdaterBootstrapFailure
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
    DateTimeOffset? CheckedAtUtc,
    int? DownloadProgressPercent = null,
    string? StagedVersion = null,
    string? OperationId = null)
{
    public static UpdateReadModel Unknown(string currentVersion) =>
        new(UpdateViewStatus.Unknown, currentVersion, null, null, null, null, null, null, null, null);

    public static UpdateReadModel Checking(string currentVersion) =>
        new(UpdateViewStatus.Checking, currentVersion, null, null, null, null, null, null, null, null);

    public static UpdateReadModel UpdateAvailable(string currentVersion, string targetVersion) =>
        new(UpdateViewStatus.UpdateAvailable, currentVersion, targetVersion, null, null, null, null, null, null, null);

    public static UpdateReadModel Downloading(string currentVersion, string targetVersion, int? percentComplete = null) =>
        new(UpdateViewStatus.Downloading, currentVersion, targetVersion, null, null, null, null, null, null, null,
            Math.Clamp(percentComplete ?? 0, 0, 100));

    public static UpdateReadModel ReadyToInstall(string currentVersion, string targetVersion, string operationId) =>
        new(UpdateViewStatus.ReadyToInstall, currentVersion, targetVersion, null, null, null, null, null, null, null,
            null, targetVersion, operationId);

    public static UpdateReadModel Installing(UpdateReadModel ready) =>
        ready with { Status = UpdateViewStatus.Installing, DownloadProgressPercent = null, ErrorCategory = null };

    public static UpdateReadModel Failed(string currentVersion, string? targetVersion, UpdateFailureCategory category) =>
        new(UpdateViewStatus.Failed, currentVersion, targetVersion, null, null, null, null, null, category, null);
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
