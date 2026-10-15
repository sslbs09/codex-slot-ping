using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CodexSlotPing.App;
using CodexSlotPing.App.ReadModels;
using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.Updater.Tests;

public sealed class ReleaseArtifactE2ETests
{
    [Fact]
    [Trait("Category", "ReleaseArtifactE2E")]
    public async Task RealPackages_FlowThroughDiscoveryStagingHandoffTransactionAndAcknowledgement()
    {
        var currentArchivePath = RequiredPath("CSP_RELEASE_E2E_CURRENT_ARCHIVE");
        var targetArchivePath = RequiredPath("CSP_RELEASE_E2E_TARGET_ARCHIVE");
        var targetVersion = SemVersion.Parse("0.3.1");
        var currentVersion = SemVersion.Parse("0.3.0");
        var targetBytes = await File.ReadAllBytesAsync(targetArchivePath);
        var targetSidecar = await File.ReadAllBytesAsync(targetArchivePath + ".sha256");
        var targetZipName = "CodexSlotPing-v0.3.1-win-x64.zip";
        var targetSidecarName = targetZipName + ".sha256";
        var targetZipUrl = new Uri($"https://github.com/sslbs09/codex-slot-ping/releases/download/v{targetVersion}/{targetZipName}");
        var targetSidecarUrl = new Uri($"https://github.com/sslbs09/codex-slot-ping/releases/download/v{targetVersion}/{targetSidecarName}");
        var releaseHandler = new ReleaseArtifactHandler(
            targetVersion, targetZipName, targetSidecarName,
            targetZipUrl, targetSidecarUrl, targetBytes, targetSidecar);
        using var http = new HttpClient(releaseHandler);
        using var temp = new TemporaryDirectory();

        var installRoot = Path.Combine(temp.Path, "install");
        var packageExtractor = new PackageArchiveExtractor(new WindowsPackageFileVersionInspector());
        var currentValidation = await packageExtractor.ExtractAndValidateAsync(
            currentArchivePath, installRoot, currentVersion);
        Assert.True(currentValidation.IsValid, string.Join(",", currentValidation.Errors));

        var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, installRoot);
        var paths = UpdateControlPaths.ForInstall(Path.Combine(temp.Path, "local-app-data"), identity);
        var intentStore = new UpdateIntentStore(paths);
        var handoffStore = new UpdateHandoffReadyStore(paths);
        var launcher = new TestHandoffLauncher(handoffStore);
        var handoff = new UpdateHandoffCoordinator(
            paths, intentStore, handoffStore, launcher,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(2));
        var updatesRoot = Path.Combine(temp.Path, "updates");
        var staging = new PackageStagingCoordinator(
            new UpdatePackageDownloader(http), packageExtractor, updatesRoot);
        var controller = new AppUpdateController(
            new UpdateCheckCoordinator(new GitHubReleaseClient(http)),
            currentVersion.ToString(), staging, handoff);

        var checkedState = await controller.CheckForUpdatesAsync(manualRefresh: true);
        Assert.Equal(UpdateViewStatus.UpdateAvailable, checkedState.Status);
        Assert.Equal(targetVersion.ToString(), checkedState.TargetVersion);
        Assert.Equal(1, releaseHandler.MetadataRequests);

        var staged = await controller.DownloadUpdateAsync();
        Assert.True(staged.IsReady, string.Join(",", staged.Errors));
        Assert.Equal(UpdateViewStatus.ReadyToInstall, controller.CurrentState.Status);
        Assert.Equal(targetVersion.ToString(), staged.Descriptor!.Version);
        Assert.Equal(targetBytes.LongLength, new FileInfo(staged.Descriptor.VerifiedZipPath).Length);
        Assert.Equal(targetBytes.LongLength, releaseHandler.DownloadedArchiveBytes);
        AssertPackagedExecutableStartup(Path.Combine(staged.Descriptor.VerifiedExtractPath, "CodexSlotPing.Runner.exe"), 64);
        AssertPackagedExecutableStartup(Path.Combine(staged.Descriptor.VerifiedExtractPath, "CodexSlotPing.Updater.exe"), 2);
        Assert.NotNull(controller.ReadyToInstallPackage);
        Assert.True(Guid.TryParseExact(staged.Descriptor.OperationId, "D", out _));
        Assert.True(Path.IsPathFullyQualified(installRoot));
        Assert.True(Path.IsPathFullyQualified(staged.Descriptor.VerifiedExtractPath));
        Assert.Equal(identity.Key, paths.InstallIdentity);
        var handoffStage = PackageDirectoryValidator.ValidateDirectory(staged.Descriptor.VerifiedExtractPath, targetVersion);
        Assert.True(handoffStage.IsValid, string.Join(",", handoffStage.Errors));

        var appIdentity = CurrentProcessIdentity();
        Assert.True(appIdentity.ProcessId > 0);
        Assert.Equal(TimeSpan.Zero, appIdentity.StartedUtc.Offset);
        var handoffResult = await controller.InstallAndRestartAsync(installRoot, appIdentity, () => false);
        Assert.Equal(UpdateHandoffStatus.Ready, handoffResult.Status);
        Assert.NotNull(launcher.Invocation);
        Assert.True(handoffStore.TryDelete(launcher.Invocation!.OperationId));

        var runtime = new TestTransactionRuntime();
        var acknowledgementStore = new AppStartupAcknowledgementStore(paths);
        var service = new UpdateTransactionService(
            staged.Descriptor.VerifiedExtractPath,
            paths,
            intentStore,
            new UpdateTransactionJournalStore(paths),
            runtime,
            new SystemUpdateTransactionFileSystem(),
            new PublishingAcknowledgementWaiter(acknowledgementStore),
            handoffStore,
            () => runtime.UpdaterIdentity);

        var transaction = await service.ExecuteAsync(launcher.Invocation);

        Assert.Equal(UpdaterResultCode.Success, transaction.Code);
        Assert.Equal(1, runtime.AppExitWaits);
        Assert.Equal(1, runtime.RunnerDrainWaits);
        Assert.Equal(1, runtime.AppLaunches);
        var installed = PackageDirectoryValidator.ValidateDirectory(installRoot, targetVersion);
        Assert.True(installed.IsValid, string.Join(",", installed.Errors));
        Assert.Equal(targetVersion.ToString(), installed.Version!.ToString());
        Assert.False(Directory.Exists(UpdateTransactionPaths.ForInstall(installRoot, launcher.Invocation.OperationId).BackupSiblingPath));
        Assert.Equal(UpdateIntentReadStatus.Missing, intentStore.Read().Status);
        var acknowledgement = acknowledgementStore.Read(launcher.Invocation.OperationId);
        Assert.Equal(AppStartupAcknowledgementReadStatus.Active, acknowledgement.Status);
        Assert.Equal(targetVersion.ToString(), acknowledgement.Acknowledgement!.RunningVersion);
        Assert.Equal(launcher.Invocation.OperationId, acknowledgement.Acknowledgement.OperationId);
    }

    private static string RequiredPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Required E2E artifact environment variable is missing: {name}");
        Assert.True(File.Exists(value), $"E2E artifact does not exist: {value}");
        return Path.GetFullPath(value!);
    }

    private static ProcessIdentity CurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime());
    }

    private static void AssertPackagedExecutableStartup(string executablePath, int expectedExitCode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(TimeSpan.FromSeconds(30)), $"Executable did not exit promptly: {executablePath}");
        Assert.Equal(expectedExitCode, process.ExitCode);
    }

    private sealed class ReleaseArtifactHandler(
        SemVersion targetVersion,
        string zipName,
        string sidecarName,
        Uri zipUrl,
        Uri sidecarUrl,
        byte[] targetArchive,
        byte[] targetSidecar) : HttpMessageHandler
    {
        public int MetadataRequests { get; private set; }
        public long DownloadedArchiveBytes { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsoluteUri == GitHubReleaseClient.ReleasesApiEndpoint)
            {
                MetadataRequests++;
                var payload = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        tag_name = $"v{targetVersion}",
                        name = "Codex Slot Ping v0.3.1",
                        body = "Isolated release E2E fixture.",
                        html_url = $"https://github.com/sslbs09/codex-slot-ping/releases/tag/v{targetVersion}",
                        draft = false,
                        prerelease = false,
                        assets = new[]
                        {
                            new { name = zipName, size = targetArchive.LongLength, browser_download_url = zipUrl.AbsoluteUri },
                            new { name = sidecarName, size = targetSidecar.LongLength, browser_download_url = sidecarUrl.AbsoluteUri }
                        }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            if (uri == zipUrl)
            {
                DownloadedArchiveBytes = targetArchive.LongLength;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(targetArchive) });
            }
            if (uri == sidecarUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(targetSidecar) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TestHandoffLauncher(UpdateHandoffReadyStore handoffStore) : IUpdaterHandoffLauncher
    {
        private readonly ProcessIdentity _identity = new(90001, DateTimeOffset.UtcNow);
        public UpdaterInvocation? Invocation { get; private set; }

        public IUpdaterHandoffProcess? Start(ProcessStartInfo startInfo)
        {
            if (!UpdaterInvocationParser.TryParse(startInfo.ArgumentList, out var invocation) || invocation is null)
            {
                return null;
            }
            Invocation = invocation;
            handoffStore.TryPublish(new UpdateHandoffReady(
                1, InstallIdentity.ProductIdValue,
                InstallIdentity.Create(InstallIdentity.ProductIdValue, invocation.InstallRoot).Key,
                invocation.OperationId, _identity.ProcessId, _identity.StartedUtc, DateTimeOffset.UtcNow));
            return new TestHandoffProcess(_identity);
        }
    }

    private sealed class TestHandoffProcess(ProcessIdentity identity) : IUpdaterHandoffProcess
    {
        public ProcessIdentity Identity { get; } = identity;
        public bool IsSameProcessAlive() => true;
        public Task TerminateIfSameProcessAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class TestTransactionRuntime : IUpdateTransactionRuntime
    {
        public ProcessIdentity UpdaterIdentity { get; } = new(90002, DateTimeOffset.UtcNow.AddMilliseconds(1));
        public int AppExitWaits { get; private set; }
        public int RunnerDrainWaits { get; private set; }
        public int AppLaunches { get; private set; }

        public Task<AppProcessWaitStatus> WaitForAppExitAsync(ProcessIdentity app, TimeSpan timeout, CancellationToken cancellationToken)
        {
            AppExitWaits++;
            return Task.FromResult(AppProcessWaitStatus.Exited);
        }

        public Task<RunnerDrainWaitResult> WaitForRunnerDrainAsync(UpdateControlPaths paths, TimeSpan timeout, CancellationToken cancellationToken)
        {
            RunnerDrainWaits++;
            return Task.FromResult(RunnerDrainWaitResult.Acquired(new TestLease()));
        }

        public Task<UpdateAppLaunchResult> LaunchAppAsync(string executablePath, string workingDirectory, Guid? operationId, bool rollback, CancellationToken cancellationToken)
        {
            AppLaunches++;
            Assert.True(File.Exists(executablePath));
            Assert.NotNull(operationId);
            Assert.False(rollback);
            return Task.FromResult(UpdateAppLaunchResult.Started(new ProcessIdentity(90003, DateTimeOffset.UtcNow.AddMilliseconds(2))));
        }

        public Task TerminateIfSameProcessAsync(ProcessIdentity process, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PublishingAcknowledgementWaiter(AppStartupAcknowledgementStore store) : IStartupAcknowledgementWaiter
    {
        public Task<StartupAcknowledgementWaitResult> WaitAsync(
            Guid operationId, string installIdentity, SemVersion targetVersion,
            ProcessIdentity launchedAppIdentity, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var acknowledgement = new AppStartupAcknowledgement(
                1, InstallIdentity.ProductIdValue, installIdentity, operationId,
                targetVersion.ToString(), launchedAppIdentity.ProcessId, launchedAppIdentity.StartedUtc);
            if (store.TryPublish(acknowledgement) != AppStartupAcknowledgementWriteStatus.Created)
            {
                return Task.FromResult(StartupAcknowledgementWaitResult.StorageFailure());
            }
            var stored = store.Read(operationId);
            return Task.FromResult(stored.Status == AppStartupAcknowledgementReadStatus.Active
                ? StartupAcknowledgementWaitResult.Received(stored.Acknowledgement!)
                : StartupAcknowledgementWaitResult.Rejected());
        }
    }

    private sealed class TestLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "CodexSlotPing.ReleaseE2E", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
