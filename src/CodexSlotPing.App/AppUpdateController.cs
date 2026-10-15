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
    private readonly UpdateHandoffCoordinator? _handoffCoordinator;
    private readonly string _currentVersion;
    private UpdateEvaluation? _lastEvaluation;
    private int _installInProgress;

    public AppUpdateController(
        UpdateCheckCoordinator coordinator,
        string currentVersion,
        PackageStagingCoordinator? packageStagingCoordinator = null,
        UpdateHandoffCoordinator? handoffCoordinator = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _packageStagingCoordinator = packageStagingCoordinator;
        _handoffCoordinator = handoffCoordinator;
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
        var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, AppContext.BaseDirectory);
        var updatePaths = UpdateControlPaths.ForCurrentUser(identity);
        var handoff = new UpdateHandoffCoordinator(
            updatePaths,
            new UpdateIntentStore(updatePaths),
            new UpdateHandoffReadyStore(updatePaths),
            new SystemUpdaterHandoffLauncher());
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 2, 0, 0);
        var package = PackageDirectoryValidator.ValidateDirectory(AppContext.BaseDirectory);
        var currentVersion = package.IsValid && package.Version is not null
            ? package.Version.ToString()
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        return new AppUpdateController(coordinator, currentVersion, packageStaging, handoff);
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
        catch (Exception)
        {
            var failed = UpdateReadModel.Failed(_currentVersion, null, UpdateFailureCategory.Network);
            SetState(failed);
            return failed;
        }
    }

    public async Task<UpdateHandoffResult> InstallAndRestartAsync(
        string installRoot,
        ProcessIdentity appIdentity,
        Func<bool> testNowIsRunning,
        CancellationToken cancellationToken = default)
    {
        var descriptor = ReadyToInstallPackage;
        if (descriptor is null || CurrentState.Status is not UpdateViewStatus.ReadyToInstall and not UpdateViewStatus.Failed ||
            _handoffCoordinator is null)
        {
            return new(UpdateHandoffStatus.InvalidStagedPackage);
        }
        if (Interlocked.CompareExchange(ref _installInProgress, 1, 0) != 0)
        {
            return new(UpdateHandoffStatus.AlreadyInstalling);
        }

        var previous = CurrentState;
        UpdateHandoffResult result;
        try
        {
            result = await _handoffCoordinator.BeginAsync(
                descriptor,
                installRoot,
                appIdentity,
                testNowIsRunning,
                () => SetState(UpdateReadModel.Installing(previous)),
                cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            Volatile.Write(ref _installInProgress, 0);
            SetState(previous with { Status = UpdateViewStatus.Failed, ErrorCategory = UpdateFailureCategory.UpdaterBootstrapFailure });
            throw;
        }

        if (result.Status is UpdateHandoffStatus.Ready)
        {
            return result;
        }

        if (result.Status is UpdateHandoffStatus.TestNowRunning)
        {
            Volatile.Write(ref _installInProgress, 0);
            return result;
        }

        Volatile.Write(ref _installInProgress, 0);
        SetState(previous with
        {
            Status = UpdateViewStatus.Failed,
            ErrorCategory = UpdateFailureCategory.UpdaterBootstrapFailure,
            DownloadProgressPercent = null
        });
        return result;
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
