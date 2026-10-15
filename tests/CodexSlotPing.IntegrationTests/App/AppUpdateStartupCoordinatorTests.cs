using System.Diagnostics;
using System.Text.Json;
using CodexSlotPing.App;
using CodexSlotPing.Update;

namespace CodexSlotPing.IntegrationTests.App;

public sealed class AppUpdateStartupCoordinatorTests
{
    [Fact]
    public async Task NoIntent_AllowsNormalStartup()
    {
        using var fixture = new Fixture();

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.Normal, result.Mode);
    }

    [Fact]
    public async Task ActiveIntentWithoutUpdateArgument_BlocksNormalStartupWhileUpdaterLives()
    {
        using var fixture = new Fixture();
        fixture.WriteIntent(fixture.OperationId);
        fixture.Probe.MatchResult = UpdaterProcessMatch.Matching;

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.UpdateInProgress, result.Mode);
        Assert.Equal(0, fixture.Launcher.StartCount);
    }

    [Fact]
    public async Task MatchingUpdateOperation_AllowsStartupButDoesNotAckBeforeUsableBoundary()
    {
        using var fixture = new Fixture();
        Fixture.WritePackage(fixture.InstallRoot, "0.3.1");
        fixture.WriteIntent(fixture.OperationId);
        fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.NewAppLaunched);
        fixture.Probe.MatchResult = UpdaterProcessMatch.Matching;
        var acknowledgements = new AppStartupAcknowledgementStore(fixture.Paths);

        var result = await fixture.Coordinator.ResolveAsync(["--update-operation", fixture.OperationId.ToString("D")]);

        Assert.Equal(AppUpdateStartupMode.UpdateOperation, result.Mode);
        Assert.Equal(AppStartupAcknowledgementReadStatus.Missing, acknowledgements.Read(fixture.OperationId).Status);
        Assert.Equal(AppStartupAcknowledgementWriteStatus.Invalid,
            fixture.Coordinator.TryPublishStartupAcknowledgement(result, isPrimaryInstance: false, acknowledgements));
        Assert.Equal(AppStartupAcknowledgementReadStatus.Missing, acknowledgements.Read(fixture.OperationId).Status);
        Assert.Equal(AppStartupAcknowledgementWriteStatus.Created,
            fixture.Coordinator.TryPublishStartupAcknowledgement(result, isPrimaryInstance: true, acknowledgements));
        Assert.Equal(AppStartupAcknowledgementReadStatus.Active, acknowledgements.Read(fixture.OperationId).Status);
    }

    [Fact]
    public async Task UpdateOperationWithWrongOperationOrTargetVersion_FailsClosed()
    {
        using var fixture = new Fixture();
        Fixture.WritePackage(fixture.InstallRoot, "0.3.1");
        fixture.WriteIntent(fixture.OperationId);
        fixture.WriteJournal(fixture.OperationId, "0.3.2", UpdateTransactionPhase.NewAppLaunched);
        fixture.Probe.MatchResult = UpdaterProcessMatch.Matching;

        var wrongOperation = await fixture.Coordinator.ResolveAsync(["--update-operation", Guid.NewGuid().ToString("D")]);
        var wrongVersion = await fixture.Coordinator.ResolveAsync(["--update-operation", fixture.OperationId.ToString("D")]);

        Assert.Equal(AppUpdateStartupMode.RecoveryRequired, wrongOperation.Mode);
        Assert.Equal(AppUpdateStartupMode.RecoveryRequired, wrongVersion.Mode);
    }

    [Fact]
    public async Task StaleIntentWithoutJournal_IsClearedThenNormalStartupContinues()
    {
        using var fixture = new Fixture();
        fixture.WriteIntent(fixture.OperationId);
        fixture.Probe.MatchResult = UpdaterProcessMatch.NotMatching;

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.Normal, result.Mode);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task StaleIntentCleanup_DoesNotDeleteIntentWhoseOwnerChangedAfterClassification()
    {
        using var fixture = new Fixture();
        fixture.WriteIntent(fixture.OperationId);
        fixture.Probe.MatchResult = UpdaterProcessMatch.NotMatching;
        var newOwner = new ProcessIdentity(7788, new DateTimeOffset(2026, 10, 15, 4, 8, 9, TimeSpan.Zero));
        fixture.Probe.OnMatch = intent => Assert.True(fixture.IntentStore.TryUpdateOwner(intent.OperationId, newOwner));

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.RecoveryRequired, result.Mode);
        var remaining = fixture.IntentStore.Read();
        Assert.Equal(UpdateIntentReadStatus.Active, remaining.Status);
        Assert.Equal(newOwner.ProcessId, remaining.Intent!.UpdaterPid);
        Assert.Equal(newOwner.StartedUtc, remaining.Intent.UpdaterStartedUtc);
    }

    [Fact]
    public async Task CompletedInstallWithPartialBackupDebris_AllowsNormalStartupAndPreservesDebris()
    {
        using var fixture = new Fixture();
        Fixture.WritePackage(fixture.InstallRoot, "0.3.1");
        fixture.WriteIntent(fixture.OperationId);
        var journal = fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.Completed);
        Directory.CreateDirectory(journal.BackupSiblingPath);
        var debris = Path.Combine(journal.BackupSiblingPath, "partial.txt");
        File.WriteAllText(debris, "unowned partial backup");
        fixture.Probe.MatchResult = UpdaterProcessMatch.NotMatching;

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.Normal, result.Mode);
        Assert.True(result.HasCleanupWarning);
        Assert.True(File.Exists(debris));
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task RolledBackInstall_CleansIntentAndAllowsOldAppStartup()
    {
        using var fixture = new Fixture();
        Fixture.WritePackage(fixture.InstallRoot, "0.3.0");
        fixture.WriteIntent(fixture.OperationId);
        fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.RolledBack);
        fixture.Probe.MatchResult = UpdaterProcessMatch.NotMatching;

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.Normal, result.Mode);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task RollbackOperationArgument_ShowsOneShotNoticeOnlyForVerifiedRolledBackInstall()
    {
        using var fixture = new Fixture();
        Fixture.WritePackage(fixture.InstallRoot, "0.3.0");
        fixture.WriteIntent(fixture.OperationId);
        fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.RolledBack);

        var result = await fixture.Coordinator.ResolveAsync(["--update-rollback", fixture.OperationId.ToString("D")]);

        Assert.Equal(AppUpdateStartupMode.RollbackNotice, result.Mode);
        Assert.Equal("0.3.0", result.TargetVersion);
    }

    [Fact]
    public async Task RollbackOperationArgument_AllowsUpdaterToClearIntentDuringStartup()
    {
        using var fixture = new Fixture();
        Fixture.WritePackage(fixture.InstallRoot, "0.3.0");
        fixture.WriteIntent(fixture.OperationId);
        fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.RolledBack);
        var intentStore = new DeleteIntentOnSecondReadStore(fixture.IntentStore, fixture.OperationId);
        var coordinator = new AppUpdateStartupCoordinator(fixture.Identity, fixture.Paths, intentStore,
            fixture.JournalStore, fixture.Probe, fixture.Launcher, fixture.AppIdentity, fixture.UpdatesRoot);

        var result = await coordinator.ResolveAsync(["--update-rollback", fixture.OperationId.ToString("D")]);

        Assert.Equal(AppUpdateStartupMode.RollbackNotice, result.Mode);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task IncompleteTransaction_LaunchesUpdaterOnlyFromMatchingVerifiedStaging()
    {
        using var fixture = new Fixture();
        fixture.WriteIntent(fixture.OperationId);
        fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.BackupCreated);
        fixture.Probe.MatchResult = UpdaterProcessMatch.NotMatching;

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.RecoveryUpdaterLaunched, result.Mode);
        Assert.Equal(1, fixture.Launcher.StartCount);
        Assert.Equal("--operation-id", fixture.Launcher.LastStartInfo!.ArgumentList[0]);
        Assert.Equal(fixture.OperationId.ToString("D"), fixture.Launcher.LastStartInfo.ArgumentList[1]);
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task IncompleteWithoutTrustedRecoveryExecutable_RequiresRecoveryAndKeepsIntent()
    {
        using var fixture = new Fixture();
        fixture.WriteIntent(fixture.OperationId);
        var journal = fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.BackupCreated);
        Directory.Delete(journal.VerifiedExtractRoot, recursive: true);
        fixture.Probe.MatchResult = UpdaterProcessMatch.NotMatching;

        var result = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.RecoveryRequired, result.Mode);
        Assert.Equal(0, fixture.Launcher.StartCount);
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task InvalidIntentOrJournalMismatch_FailsClosed()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Paths.DirectoryPath);
        File.WriteAllText(fixture.Paths.IntentFilePath, "not-json");

        var invalidIntent = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.RecoveryRequired, invalidIntent.Mode);
        File.Delete(fixture.Paths.IntentFilePath);

        var wrongOwner = new UpdateIntent(1, InstallIdentity.ProductIdValue, new string('A', 64), fixture.OperationId,
            4455, new DateTimeOffset(2026, 10, 15, 4, 5, 7, TimeSpan.Zero), DateTimeOffset.UtcNow);
        File.WriteAllText(fixture.Paths.IntentFilePath, JsonSerializer.Serialize(wrongOwner,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(AppUpdateStartupMode.RecoveryRequired, (await fixture.Coordinator.ResolveAsync([])).Mode);
        File.Delete(fixture.Paths.IntentFilePath);

        fixture.WriteIntent(fixture.OperationId);
        fixture.WriteJournal(fixture.OperationId, "0.3.1", UpdateTransactionPhase.BackupCreated);
        var journalPath = fixture.JournalStore.GetJournalFilePath(fixture.OperationId);
        var journalText = File.ReadAllText(journalPath).Replace(fixture.OperationId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        File.WriteAllText(journalPath, journalText);
        fixture.Probe.MatchResult = UpdaterProcessMatch.NotMatching;
        var invalidJournal = await fixture.Coordinator.ResolveAsync([]);

        Assert.Equal(AppUpdateStartupMode.RecoveryRequired, invalidJournal.Mode);
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexSlotPing.App.Startup.Tests", Guid.NewGuid().ToString("N"));
        private readonly string _updatesRoot;
        public Fixture()
        {
            InstallRoot = Path.Combine(_root, "install");
            _updatesRoot = Path.Combine(_root, "local-app-data", "CodexSlotPing", "updates");
            Directory.CreateDirectory(InstallRoot);
            WritePackage(InstallRoot, "0.3.0");
            Identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, InstallRoot);
            Paths = UpdateControlPaths.ForInstall(Path.Combine(_root, "local-app-data"), Identity);
            IntentStore = new UpdateIntentStore(Paths);
            JournalStore = new UpdateTransactionJournalStore(Paths);
            Probe = new FakeProbe();
            Launcher = new FakeLauncher();
            Coordinator = new AppUpdateStartupCoordinator(Identity, Paths, IntentStore, JournalStore, Probe,
                Launcher, AppIdentity, _updatesRoot);
        }

        public Guid OperationId { get; } = Guid.NewGuid();
        public string InstallRoot { get; }
        public string UpdatesRoot => _updatesRoot;
        public InstallIdentity Identity { get; }
        public UpdateControlPaths Paths { get; }
        public UpdateIntentStore IntentStore { get; }
        public UpdateTransactionJournalStore JournalStore { get; }
        public FakeProbe Probe { get; }
        public FakeLauncher Launcher { get; }
        public AppUpdateStartupCoordinator Coordinator { get; }
        public ProcessIdentity AppIdentity { get; } = new(7890, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));
        public void WriteIntent(Guid operationId) => Assert.Equal(UpdateIntentCreateStatus.Created,
            IntentStore.Create(new UpdateIntent(1, InstallIdentity.ProductIdValue, Identity.Key, operationId, 4455,
                new DateTimeOffset(2026, 10, 15, 4, 5, 7, TimeSpan.Zero), DateTimeOffset.UtcNow)));

        public UpdateTransactionJournal WriteJournal(Guid operationId, string targetVersion, UpdateTransactionPhase phase)
        {
            var stage = Path.Combine(_updatesRoot, targetVersion, operationId.ToString("N"), "extract");
            Directory.CreateDirectory(stage);
            WritePackage(stage, targetVersion);
            var siblings = UpdateTransactionPaths.ForInstall(InstallRoot, operationId);
            var now = DateTimeOffset.UtcNow;
            var journal = new UpdateTransactionJournal(1, InstallIdentity.ProductIdValue, Identity.Key,
                operationId, targetVersion, InstallRoot, stage, siblings.NewSiblingPath, siblings.BackupSiblingPath,
                phase, now, now, phase is UpdateTransactionPhase.NewAppLaunched ? AppIdentity : null);
            Assert.Equal(UpdateTransactionJournalWriteStatus.Created, JournalStore.TryCreate(journal));
            return journal;
        }

        public static void WritePackage(string root, string version)
        {
            Directory.CreateDirectory(root);
            foreach (var file in PackageContract.RequiredFiles) File.WriteAllText(Path.Combine(root, file), $"{file}:{version}");
            var files = PackageContract.RequiredFiles.Append(PackageContract.ManifestFileName).ToArray();
            File.WriteAllText(Path.Combine(root, PackageContract.ManifestFileName), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, productId = InstallIdentity.ProductIdValue, version, files
            }));
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }

    private sealed class FakeProbe : IProcessIdentityProbe
    {
        public UpdaterProcessMatch MatchResult { get; set; } = UpdaterProcessMatch.NotMatching;
        public Action<UpdateIntent>? OnMatch { get; set; }
        public UpdaterProcessMatch Match(UpdateIntent intent)
        {
            OnMatch?.Invoke(intent);
            return MatchResult;
        }
    }

    private sealed class DeleteIntentOnSecondReadStore(IUpdateIntentLifecycle inner, Guid operationId) : IUpdateIntentLifecycle
    {
        private int _reads;

        public UpdateIntentReadResult Read()
        {
            if (Interlocked.Increment(ref _reads) == 2) inner.TryDelete(operationId);
            return inner.Read();
        }

        public UpdateIntentCreateStatus Create(UpdateIntent intent) => inner.Create(intent);
        public bool TryDelete(Guid id) => inner.TryDelete(id);
        public bool TryDelete(Guid id, ProcessIdentity expectedUpdater) => inner.TryDelete(id, expectedUpdater);
        public bool TryUpdateOwner(Guid id, ProcessIdentity updater) => inner.TryUpdateOwner(id, updater);
    }

    private sealed class FakeLauncher : IUpdaterHandoffLauncher
    {
        public int StartCount { get; private set; }
        public ProcessStartInfo? LastStartInfo { get; private set; }
        public IUpdaterHandoffProcess? Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            LastStartInfo = startInfo;
            return new FakeProcess();
        }
    }

    private sealed class FakeProcess : IUpdaterHandoffProcess
    {
        public ProcessIdentity Identity { get; } = new(9001, new DateTimeOffset(2026, 10, 15, 4, 5, 9, TimeSpan.Zero));
        public bool IsSameProcessAlive() => true;
        public Task TerminateIfSameProcessAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
