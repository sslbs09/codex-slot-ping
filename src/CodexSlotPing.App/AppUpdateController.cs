using System.Net.Http;
using System.IO;
using CodexSlotPing.App.ReadModels;
using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.App;

public sealed class AppUpdateController
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly UpdateCheckCoordinator _coordinator;
    private readonly PackageStagingCoordinator? _packageStagingCoordinator;
    private readonly string _currentVersion;
    private UpdateEvaluation? _lastEvaluation;

    public AppUpdateController(
        UpdateCheckCoordinator coordinator,
        string currentVersion,
        PackageStagingCoordinator? packageStagingCoordinator = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _packageStagingCoordinator = packageStagingCoordinator;
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        _currentVersion = currentVersion;
        CurrentState = UpdateReadModel.Unknown(currentVersion);
    }

    public event Action<UpdateReadModel>? StateChanged;

    public UpdateReadModel CurrentState { get; private set; }

    public ReadyToInstallDescriptor? ReadyToInstallPackage { get; private set; }

    public static AppUpdateController CreateDefault()
    {
        var client = new GitHubReleaseClient(SharedHttpClient);
        var coordinator = new UpdateCheckCoordinator(client);
        var downloadHttpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var packageStaging = new PackageStagingCoordinator(
            new UpdatePackageDownloader(downloadHttpClient),
            new PackageArchiveExtractor(new WindowsPackageFileVersionInspector()));
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 2, 0, 0);
        var currentVersion = $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        return new AppUpdateController(coordinator, currentVersion, packageStaging);
    }

    public async Task<UpdateReadModel> CheckForUpdatesAsync(
        bool manualRefresh = true,
        CancellationToken cancellationToken = default)
    {
        SetState(UpdateReadModel.Checking(_currentVersion));
        _lastEvaluation = null;
        try
        {
            var result = await _coordinator.CheckAsync(_currentVersion, manualRefresh, cancellationToken);
            _lastEvaluation = result.Evaluation;
            var projected = UpdateReadModelProjection.Create(result.Evaluation, result.CheckedAtUtc);
            SetState(projected);
            return projected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(UpdateReadModel.Unknown(_currentVersion));
            throw;
        }
    }

    public async Task<PackageStagingResult> DownloadUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_packageStagingCoordinator is null || _lastEvaluation is null || CurrentState.Status != UpdateViewStatus.UpdateAvailable)
        {
            var failure = PackageStagingResult.Failed(PackageStagingFailure.InvalidUpdateMetadata, "no-available-update");
            SetState(UpdateReadModel.Failed(_currentVersion, CurrentState.TargetVersion, UpdateFailureCategory.PackageInvalid));
            return failure;
        }

        var availableState = CurrentState;
        ReadyToInstallPackage = null;
        SetState(availableState with { Status = UpdateViewStatus.Downloading, DownloadProgressPercent = null, OperationId = null, StagedVersion = null });
        var progress = new Progress<PackageStagingProgress>(value =>
        {
            if (CurrentState.Status == UpdateViewStatus.Downloading &&
                string.Equals(CurrentState.TargetVersion, value.Version, StringComparison.Ordinal))
            {
                SetState(CurrentState with { DownloadProgressPercent = value.PercentComplete });
            }
        });

        try
        {
            var result = await _packageStagingCoordinator.StageAsync(_lastEvaluation, progress, cancellationToken);
            if (result.IsReady)
            {
                ReadyToInstallPackage = result.Descriptor;
                SetState(availableState with
                {
                    Status = UpdateViewStatus.ReadyToInstall,
                    DownloadProgressPercent = null,
                    StagedVersion = result.Descriptor!.Version,
                    OperationId = result.Descriptor.OperationId,
                    ErrorCategory = null
                });
            }
            else
            {
                SetState(availableState with
                {
                    Status = UpdateViewStatus.Failed,
                    DownloadProgressPercent = null,
                    ErrorCategory = ProjectStagingFailure(result.Failure)
                });
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(availableState);
            throw;
        }
        catch (IOException)
        {
            SetState(availableState with
            {
                Status = UpdateViewStatus.Failed,
                DownloadProgressPercent = null,
                ErrorCategory = UpdateFailureCategory.StagingIoFailure
            });
            return PackageStagingResult.Failed(PackageStagingFailure.IoFailure, "staging-cleanup-failed");
        }
        catch (UnauthorizedAccessException)
        {
            SetState(availableState with
            {
                Status = UpdateViewStatus.Failed,
                DownloadProgressPercent = null,
                ErrorCategory = UpdateFailureCategory.StagingIoFailure
            });
            return PackageStagingResult.Failed(PackageStagingFailure.IoFailure, "staging-cleanup-failed");
        }
    }

    private static UpdateFailureCategory ProjectStagingFailure(PackageStagingFailure? failure) => failure switch
    {
        PackageStagingFailure.DownloadFailed => UpdateFailureCategory.DownloadFailed,
        PackageStagingFailure.InvalidChecksumSidecar or PackageStagingFailure.ChecksumMismatch => UpdateFailureCategory.ChecksumInvalid,
        PackageStagingFailure.InvalidArchive or PackageStagingFailure.InvalidManifest => UpdateFailureCategory.PackageInvalid,
        PackageStagingFailure.BinaryVersionMismatch => UpdateFailureCategory.VersionMismatch,
        _ => UpdateFailureCategory.StagingIoFailure
    };

    private void SetState(UpdateReadModel state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
    }
}
