using System.Diagnostics;
using System.Text.Json;
using CodexSlotPing.App;
using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.IntegrationTests.App;

public sealed class UpdateHandoffCoordinatorTests
{
    [Fact]
    public async Task ReadyPackage_UsesArgumentListAndAcceptsOnlyLiveLaunchedUpdaterMarker()
    {
        using var fixture = new Fixture();
        fixture.Launcher.OnStart = (startInfo, process) =>
        {
            Assert.Equal(Path.Combine(fixture.StageRoot, "CodexSlotPing.Updater.exe"), startInfo.FileName);
            Assert.Equal(fixture.StageRoot, startInfo.WorkingDirectory);
            Assert.False(startInfo.UseShellExecute);
            Assert.Contains("--operation-id", startInfo.ArgumentList);
            Assert.DoesNotContain("--operation-id", startInfo.Arguments);
            Assert.Equal("--operation-id", startInfo.ArgumentList[0]);
            Assert.Equal(fixture.OperationId.ToString("D"), startInfo.ArgumentList[1]);
            fixture.HandoffStore.TryPublish(new UpdateHandoffReady(1, InstallIdentity.ProductIdValue,
                fixture.Identity.Key, fixture.OperationId, process.Identity.ProcessId,
                process.Identity.StartedUtc, DateTimeOffset.UtcNow));
        };

        var result = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);

        Assert.Equal(UpdateHandoffStatus.Ready, result.Status);
        Assert.Equal(1, fixture.Launcher.StartCount);
    }

    [Fact]
    public async Task TestNowRunning_BlocksBeforeUpdaterLaunch()
    {
        using var fixture = new Fixture();

        var result = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => true);

        Assert.Equal(UpdateHandoffStatus.TestNowRunning, result.Status);
        Assert.Equal(0, fixture.Launcher.StartCount);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task UpdaterExitsBeforeReady_CleansOnlyItsIntentAndLeavesAppInControl()
    {
        using var fixture = new Fixture();
        fixture.Launcher.OnStart = (_, process) =>
        {
            fixture.IntentStore.Create(new UpdateIntent(1, InstallIdentity.ProductIdValue, fixture.Identity.Key,
                fixture.OperationId, process.Identity.ProcessId, process.Identity.StartedUtc, DateTimeOffset.UtcNow));
            process.Alive = false;
        };

        var result = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);

        Assert.Equal(UpdateHandoffStatus.UpdaterExitedBeforeReady, result.Status);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdateHandoffReadyReadStatus.Missing, fixture.HandoffStore.Read(fixture.OperationId).Status);
    }

    [Fact]
    public async Task WrongUpdaterIdentityMarker_IsIgnoredThenTimeoutCleansOnlyMatchingOperation()
    {
        using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(25));
        fixture.Launcher.OnStart = (_, process) =>
        {
            fixture.IntentStore.Create(new UpdateIntent(1, InstallIdentity.ProductIdValue, fixture.Identity.Key,
                fixture.OperationId, process.Identity.ProcessId, process.Identity.StartedUtc, DateTimeOffset.UtcNow));
            fixture.HandoffStore.TryPublish(new UpdateHandoffReady(1, InstallIdentity.ProductIdValue,
                fixture.Identity.Key, fixture.OperationId, process.Identity.ProcessId + 1,
                process.Identity.StartedUtc, DateTimeOffset.UtcNow));
        };

        var result = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);

        Assert.Equal(UpdateHandoffStatus.TimedOut, result.Status);
        Assert.False(fixture.Launcher.LastProcess!.Alive);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdateHandoffReadyReadStatus.Missing, fixture.HandoffStore.Read(fixture.OperationId).Status);
    }

    [Fact]
    public async Task WrongUpdaterStartTimeMarker_IsIgnored()
    {
        using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(20));
        fixture.Launcher.OnStart = (_, process) => fixture.HandoffStore.TryPublish(new UpdateHandoffReady(1,
            InstallIdentity.ProductIdValue, fixture.Identity.Key, fixture.OperationId,
            process.Identity.ProcessId, process.Identity.StartedUtc.AddSeconds(-1), DateTimeOffset.UtcNow));

        var result = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);

        Assert.Equal(UpdateHandoffStatus.TimedOut, result.Status);
        Assert.False(fixture.Launcher.LastProcess!.Alive);
    }

    [Fact]
    public async Task WrongOperationMarkerAtExpectedPath_IsIgnored()
    {
        using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(20));
        fixture.Launcher.OnStart = (_, _) =>
        {
            var wrongOperation = new UpdateHandoffReady(1, InstallIdentity.ProductIdValue, fixture.Identity.Key,
                Guid.NewGuid(), 4321, new DateTimeOffset(2026, 10, 15, 4, 5, 7, TimeSpan.Zero), DateTimeOffset.UtcNow);
            Directory.CreateDirectory(fixture.Paths.DirectoryPath);
            File.WriteAllText(fixture.Paths.HandoffReadyFilePath(fixture.OperationId), JsonSerializer.Serialize(wrongOperation,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        };

        var result = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);

        Assert.Equal(UpdateHandoffStatus.TimedOut, result.Status);
        Assert.False(fixture.Launcher.LastProcess!.Alive);
    }

    [Fact]
    public async Task Timeout_DoesNotDeleteIntentFromAnotherOperation()
    {
        using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(25));
        var unrelated = Guid.NewGuid();
        fixture.Launcher.OnStart = (_, _) => fixture.IntentStore.Create(new UpdateIntent(1,
            InstallIdentity.ProductIdValue, fixture.Identity.Key, unrelated, 8888,
            new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero), DateTimeOffset.UtcNow));

        var result = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);

        Assert.Equal(UpdateHandoffStatus.TimedOut, result.Status);
        Assert.Equal(unrelated, fixture.IntentStore.Read().Intent?.OperationId);
    }

    [Fact]
    public async Task DuplicateInstallRequest_OnlyLaunchesOneUpdater()
    {
        using var fixture = new Fixture();
        fixture.Launcher.OnStart = (_, process) => fixture.HandoffStore.TryPublish(new UpdateHandoffReady(1,
            InstallIdentity.ProductIdValue, fixture.Identity.Key, fixture.OperationId,
            process.Identity.ProcessId, process.Identity.StartedUtc, DateTimeOffset.UtcNow));

        var first = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);
        var duplicate = await fixture.Coordinator.BeginAsync(fixture.Descriptor, fixture.InstallRoot,
            fixture.AppIdentity, () => false);

        Assert.Equal(UpdateHandoffStatus.Ready, first.Status);
        Assert.Equal(UpdateHandoffStatus.AlreadyInstalling, duplicate.Status);
        Assert.Equal(1, fixture.Launcher.StartCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexSlotPing.App.Tests", Guid.NewGuid().ToString("N"));
        public Fixture(TimeSpan? timeout = null)
        {
            InstallRoot = Path.Combine(_root, "install");
            StageRoot = Path.Combine(_root, "updates", "0.3.1", OperationId.ToString("N"), "extract");
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(StageRoot);
            foreach (var file in PackageContract.RequiredFiles) File.WriteAllText(Path.Combine(StageRoot, file), file);
            var files = PackageContract.RequiredFiles.Append(PackageContract.ManifestFileName).ToArray();
            File.WriteAllText(Path.Combine(StageRoot, PackageContract.ManifestFileName), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, productId = InstallIdentity.ProductIdValue, version = "0.3.1", files
            }));
            Identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, InstallRoot);
            Paths = UpdateControlPaths.ForInstall(Path.Combine(_root, "local-app-data"), Identity);
            IntentStore = new UpdateIntentStore(Paths);
            HandoffStore = new UpdateHandoffReadyStore(Paths);
            Launcher = new FakeLauncher();
            Coordinator = new UpdateHandoffCoordinator(Paths, IntentStore, HandoffStore, Launcher,
                timeout ?? TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        }

        public Guid OperationId { get; } = Guid.NewGuid();
        public string InstallRoot { get; }
        public string StageRoot { get; }
        public InstallIdentity Identity { get; }
        public UpdateControlPaths Paths { get; }
        public UpdateIntentStore IntentStore { get; }
        public UpdateHandoffReadyStore HandoffStore { get; }
        public FakeLauncher Launcher { get; }
        public UpdateHandoffCoordinator Coordinator { get; }
        public ProcessIdentity AppIdentity { get; } = new(7890, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));
        public ReadyToInstallDescriptor Descriptor => new("0.3.1", OperationId.ToString("D"),
            Path.Combine(StageRoot, "package.zip"), Path.Combine(StageRoot, "package.zip.sha256"), StageRoot,
            new PackageManifest(1, InstallIdentity.ProductIdValue, "0.3.1", PackageContract.RequiredFiles.Append(PackageContract.ManifestFileName).ToArray()));

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }

    private sealed class FakeLauncher : IUpdaterHandoffLauncher
    {
        public int StartCount { get; private set; }
        public FakeProcess? LastProcess { get; private set; }
        public Action<ProcessStartInfo, FakeProcess>? OnStart { get; set; }
        public IUpdaterHandoffProcess? Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            LastProcess = new FakeProcess();
            OnStart?.Invoke(startInfo, LastProcess);
            return LastProcess;
        }
    }

    private sealed class FakeProcess : IUpdaterHandoffProcess
    {
        public ProcessIdentity Identity { get; } = new(4321, new DateTimeOffset(2026, 10, 15, 4, 5, 7, TimeSpan.Zero));
        public bool Alive { get; set; } = true;
        public bool IsSameProcessAlive() => Alive;
        public Task TerminateIfSameProcessAsync(CancellationToken cancellationToken) { Alive = false; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
