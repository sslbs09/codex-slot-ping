using System.Text.Json;
using CodexSlotPing.Update;
using CodexSlotPing.Updater;

namespace CodexSlotPing.Updater.Tests;

public sealed partial class UpdateTransactionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_UpdaterCreatesIntentAndPublishesReadyBeforeWaitingOrInstallMutation()
    {
        using var fixture = new TransactionFixture();
        var intentAtWait = UpdateIntentReadStatus.Missing;
        UpdateIntent? ownedIntentAtWait = null;
        var readyAtWait = UpdateHandoffReadyReadStatus.Missing;
        var movesAtWait = -1;
        fixture.Runtime.OnAppWait = () =>
        {
            intentAtWait = fixture.IntentStore.Read().Status;
            ownedIntentAtWait = fixture.IntentStore.Read().Intent;
            readyAtWait = fixture.HandoffStore.Read(fixture.OperationId).Status;
            movesAtWait = fixture.FileSystem.Moves.Count;
        };
        fixture.Acknowledgement = StartupAcknowledgementWaitResult.Received(fixture.CreateAcknowledgement(fixture.Runtime.NewAppIdentity));

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, result.Code);
        Assert.Equal(UpdateIntentReadStatus.Active, intentAtWait);
        Assert.Equal(UpdateHandoffReadyReadStatus.Active, readyAtWait);
        Assert.Equal(0, movesAtWait);
        Assert.Equal(fixture.Runtime.UpdaterIdentity.ProcessId, ownedIntentAtWait?.UpdaterPid);
        Assert.Equal(fixture.Runtime.UpdaterIdentity.StartedUtc, ownedIntentAtWait?.UpdaterStartedUtc);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPathReplacesPackageAndCompletesOnlyAfterMatchingAck()
    {
        using var fixture = new TransactionFixture();
        fixture.Acknowledgement = StartupAcknowledgementWaitResult.Received(fixture.CreateAcknowledgement(fixture.Runtime.NewAppIdentity));

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, result.Code);
        Assert.Equal("0.3.1", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.False(Directory.Exists(fixture.Siblings.NewSiblingPath));
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
        var journal = fixture.JournalStore.Read(fixture.OperationId);
        Assert.Equal(UpdateTransactionJournalReadStatus.Active, journal.Status);
        Assert.Equal(UpdateTransactionPhase.Completed, journal.Journal!.Phase);
        Assert.Equal(fixture.OperationId, fixture.Runtime.Launches.Single().OperationId);
    }

    [Fact]
    public async Task ExecuteAsync_PreparedJournalFailureLeavesCurrentInstallUntouched()
    {
        using var fixture = new TransactionFixture { FailPreparedJournal = true };

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.JournalFailure, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.False(Directory.Exists(fixture.Siblings.NewSiblingPath));
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
        Assert.Empty(fixture.FileSystem.Moves);
    }

    [Fact]
    public async Task ExecuteAsync_ReplacementRenameFailureRestoresOldInstall()
    {
        using var fixture = new TransactionFixture();
        fixture.FileSystem.FailMove = (source, destination) =>
            PathsEqual(source, fixture.Siblings.NewSiblingPath) && PathsEqual(destination, fixture.InstallRoot);

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.ReplacementFailed, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdateTransactionPhase.RolledBack, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
    }

    [Fact]
    public async Task ExecuteAsync_RollbackFailurePreservesBackupJournalAndIntent()
    {
        using var fixture = new TransactionFixture();
        fixture.FileSystem.FailMove = (source, destination) =>
            (PathsEqual(source, fixture.Siblings.NewSiblingPath) && PathsEqual(destination, fixture.InstallRoot)) ||
            (PathsEqual(source, fixture.Siblings.BackupSiblingPath) && PathsEqual(destination, fixture.InstallRoot));

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.RecoveryRequired, result.Code);
        Assert.True(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdateTransactionPhase.RecoveryRequired, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
    }

    [Fact]
    public async Task ExecuteAsync_AppExitTimeoutDoesNotMutateInstallAndClearsIntent()
    {
        using var fixture = new TransactionFixture();
        fixture.Runtime.AppWait = AppProcessWaitStatus.TimedOut;

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.AppExitTimeout, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Empty(fixture.FileSystem.Moves);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task ExecuteAsync_RunnerDrainTimeoutDoesNotMutateInstallAndClearsIntent()
    {
        using var fixture = new TransactionFixture();
        fixture.Runtime.RunnerDrain = RunnerDrainWaitResult.TimedOut();

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.RunnerDrainTimeout, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Empty(fixture.FileSystem.Moves);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Runtime.LastRunnerDrainTimeout);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidCurrentManifestRefusesBeforeWaitingOrMutating()
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(Path.Combine(fixture.InstallRoot, "unowned.txt"), "user-owned");

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.PreflightFailed, result.Code);
        Assert.Equal(0, fixture.Runtime.AppWaitCalls);
        Assert.Empty(fixture.FileSystem.Moves);
        Assert.True(File.Exists(Path.Combine(fixture.InstallRoot, "unowned.txt")));
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task ExecuteAsync_UnwritableParentRefusesBeforeWaitingOrMutating()
    {
        using var fixture = new TransactionFixture();
        fixture.FileSystem.ParentProbeSucceeds = false;

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.PreflightFailed, result.Code);
        Assert.Equal(0, fixture.Runtime.AppWaitCalls);
        Assert.Empty(fixture.FileSystem.Moves);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_PreexistingSiblingCollisionIsPreservedAndRefused()
    {
        using var fixture = new TransactionFixture();
        Directory.CreateDirectory(fixture.Siblings.NewSiblingPath);
        var marker = Path.Combine(fixture.Siblings.NewSiblingPath, "collision.txt");
        File.WriteAllText(marker, "must survive");

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.PreflightFailed, result.Code);
        Assert.Equal("must survive", File.ReadAllText(marker));
        Assert.Empty(fixture.FileSystem.Moves);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_PreexistingBackupCollisionIsPreservedAndRefused()
    {
        using var fixture = new TransactionFixture();
        Directory.CreateDirectory(fixture.Siblings.BackupSiblingPath);
        var marker = Path.Combine(fixture.Siblings.BackupSiblingPath, "collision.txt");
        File.WriteAllText(marker, "must survive");

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.PreflightFailed, result.Code);
        Assert.Equal("must survive", File.ReadAllText(marker));
        Assert.Empty(fixture.FileSystem.Moves);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateInvocationCannotClearIntentOwnedByRunningTransaction()
    {
        using var fixture = new TransactionFixture();
        using var copyStarted = new ManualResetEventSlim(false);
        using var allowCopy = new ManualResetEventSlim(false);
        fixture.FileSystem.CopyStarted = copyStarted;
        fixture.FileSystem.AllowCopy = allowCopy;
        fixture.FileSystem.FailMove = (source, destination) =>
            (PathsEqual(source, fixture.Siblings.NewSiblingPath) && PathsEqual(destination, fixture.InstallRoot)) ||
            (PathsEqual(source, fixture.Siblings.BackupSiblingPath) && PathsEqual(destination, fixture.InstallRoot));

        var firstTask = Task.Run(() => fixture.CreateService().ExecuteAsync(fixture.Invocation));
        Assert.True(copyStarted.Wait(TimeSpan.FromSeconds(10)), "The first updater did not enter sibling preparation.");
        UpdateTransactionResult duplicate;
        try
        {
            duplicate = await fixture.CreateService().ExecuteAsync(fixture.Invocation);
        }
        finally
        {
            allowCopy.Set();
        }

        var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UpdaterResultCode.UpdaterAlreadyRunning, duplicate.Code);
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdaterResultCode.RecoveryRequired, first.Code);
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdateTransactionPhase.RecoveryRequired, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsLegacyInstallWithoutManifestVersion030OrLater()
    {
        using var fixture = new TransactionFixture();
        fixture.SetCurrentVersion("0.2.0");

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.PreflightFailed, result.Code);
        Assert.Equal("0.2.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Empty(fixture.FileSystem.Moves);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsPrereleaseCurrentInstallThatRecoveryCannotClassify()
    {
        using var fixture = new TransactionFixture();
        fixture.SetCurrentVersion("0.3.1-preview.1");

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.PreflightFailed, result.Code);
        Assert.Equal("0.3.1-preview.1", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(0, fixture.Runtime.AppWaitCalls);
        Assert.Empty(fixture.FileSystem.Moves);
    }

    [Fact]
    public async Task ExecuteAsync_FirstRenameFailureLeavesCurrentInstallUntouched()
    {
        using var fixture = new TransactionFixture();
        fixture.FileSystem.FailMove = (source, destination) =>
            PathsEqual(source, fixture.InstallRoot) && PathsEqual(destination, fixture.Siblings.BackupSiblingPath);

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.ReplacementFailed, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task ExecuteAsync_FirstRenameAndTerminalJournalWriteFailuresPreserveRecoveryState()
    {
        using var fixture = new TransactionFixture { FailJournalUpdate = true };
        fixture.FileSystem.FailMove = (source, destination) =>
            PathsEqual(source, fixture.InstallRoot) && PathsEqual(destination, fixture.Siblings.BackupSiblingPath);

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.RecoveryRequired, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdateTransactionPhase.Prepared, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);

        var recovered = await CreateRecovery(fixture).RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, recovered.Code);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task ExecuteAsync_NewSiblingManifestMismatchRefusesBeforeCurrentInstallRename()
    {
        using var fixture = new TransactionFixture();
        fixture.FileSystem.CorruptCopiedManifest = true;

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.PreflightFailed, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.Empty(fixture.FileSystem.Moves);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task ExecuteAsync_NewAppLaunchFailureRollsBackAndRelaunchesOldApp()
    {
        using var fixture = new TransactionFixture();
        fixture.Runtime.FailNextAppLaunch = true;

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(2, fixture.Runtime.Launches.Count);
        Assert.NotNull(fixture.Runtime.Launches[0].OperationId);
        Assert.False(fixture.Runtime.Launches[0].Rollback);
        Assert.Equal(fixture.OperationId, fixture.Runtime.Launches[1].OperationId);
        Assert.True(fixture.Runtime.Launches[1].Rollback);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedStartupAcknowledgementRollsBack()
    {
        using var fixture = new TransactionFixture();
        fixture.Acknowledgement = StartupAcknowledgementWaitResult.Received(
            fixture.CreateAcknowledgement(fixture.Runtime.NewAppIdentity) with { RunningVersion = "0.3.2" });

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(fixture.Runtime.NewAppIdentity, fixture.Runtime.TerminatedProcess);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("process-id")]
    [InlineData("process-start")]
    [InlineData("build-metadata")]
    public async Task ExecuteAsync_RejectsStaleOrWrongProcessAcknowledgements(string mismatch)
    {
        using var fixture = new TransactionFixture();
        var acknowledgement = fixture.CreateAcknowledgement(fixture.Runtime.NewAppIdentity);
        fixture.Acknowledgement = StartupAcknowledgementWaitResult.Received(mismatch switch
        {
            "operation" => acknowledgement with { OperationId = Guid.NewGuid() },
            "process-id" => acknowledgement with { AppPid = acknowledgement.AppPid + 1 },
            "process-start" => acknowledgement with { AppStartedUtc = acknowledgement.AppStartedUtc.AddSeconds(-1) },
            _ => acknowledgement with { RunningVersion = "0.3.1+different-build" }
        });

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(fixture.Runtime.NewAppIdentity, fixture.Runtime.TerminatedProcess);
    }

    [Fact]
    public async Task ExecuteAsync_MissingStartupAcknowledgementTimesOutAndRollsBack()
    {
        using var fixture = new TransactionFixture();
        fixture.Acknowledgement = StartupAcknowledgementWaitResult.TimedOut();

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(fixture.Runtime.NewAppIdentity, fixture.Runtime.TerminatedProcess);
    }

    [Fact]
    public async Task ExecuteAsync_CopiesStageToInstallParentSiblingAndRenamesOnlyInstallSiblings()
    {
        using var fixture = new TransactionFixture();
        fixture.Acknowledgement = StartupAcknowledgementWaitResult.Received(fixture.CreateAcknowledgement(fixture.Runtime.NewAppIdentity));

        var result = await fixture.CreateService().ExecuteAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, result.Code);
        Assert.Equal(new[] { (fixture.StageRoot, fixture.Siblings.NewSiblingPath) }, fixture.FileSystem.Copies);
        Assert.Equal(2, fixture.FileSystem.Moves.Count);
        Assert.Contains(fixture.FileSystem.Moves, move => PathsEqual(move.Source, fixture.InstallRoot) &&
            PathsEqual(move.Destination, fixture.Siblings.BackupSiblingPath));
        Assert.Contains(fixture.FileSystem.Moves, move => PathsEqual(move.Source, fixture.Siblings.NewSiblingPath) &&
            PathsEqual(move.Destination, fixture.InstallRoot));
        Assert.DoesNotContain(fixture.FileSystem.Moves, move => PathsEqual(move.Source, fixture.StageRoot));
        Assert.All(fixture.FileSystem.Moves, move =>
        {
            Assert.True(PathsEqual(fixture.InstallParent, Path.GetDirectoryName(move.Source)!));
            Assert.True(PathsEqual(fixture.InstallParent, Path.GetDirectoryName(move.Destination)!));
        });
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed class TransactionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexSlotPing.Updater.Tests", Guid.NewGuid().ToString("N"));

        public TransactionFixture()
        {
            Directory.CreateDirectory(_root);
            InstallParent = Path.Combine(_root, "programs");
            InstallRoot = Path.Combine(InstallParent, "CodexSlotPing");
            StageRoot = Path.Combine(_root, "local-app-data", "CodexSlotPing", "updates", "0.3.1", OperationId.ToString("N"), "extract");
            Directory.CreateDirectory(InstallParent);
            WritePackage(InstallRoot, "0.3.0", "old");
            WritePackage(StageRoot, "0.3.1", "new");
            Identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, InstallRoot);
            ControlPaths = UpdateControlPaths.ForInstall(Path.Combine(_root, "local-app-data"), Identity);
            IntentStore = new UpdateIntentStore(ControlPaths);
            HandoffStore = new UpdateHandoffReadyStore(ControlPaths);
            JournalStore = new UpdateTransactionJournalStore(ControlPaths);
            Siblings = UpdateTransactionPaths.ForInstall(InstallRoot, OperationId);
            Invocation = new UpdaterInvocation(OperationId, SemVersion.Parse("0.3.1"), InstallRoot, StageRoot, AppIdentity);
            Runtime = new FakeRuntime();
            FileSystem = new FakeFileSystem();
            Acknowledgement = StartupAcknowledgementWaitResult.TimedOut();
        }

        public Guid OperationId { get; } = Guid.NewGuid();
        public string InstallParent { get; }
        public string InstallRoot { get; }
        public string StageRoot { get; }
        public InstallIdentity Identity { get; }
        public UpdateControlPaths ControlPaths { get; }
        public UpdateIntentStore IntentStore { get; }
        public UpdateHandoffReadyStore HandoffStore { get; }
        public UpdateTransactionJournalStore JournalStore { get; }
        public UpdateTransactionPathSet Siblings { get; }
        public UpdaterInvocation Invocation { get; }
        public ProcessIdentity AppIdentity { get; } = new(6001, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));
        public FakeRuntime Runtime { get; }
        public FakeFileSystem FileSystem { get; }
        public StartupAcknowledgementWaitResult Acknowledgement { get; set; }
        public bool FailPreparedJournal { get; init; }
        public bool FailJournalUpdate { get; init; }

        public void PublishIntent()
        {
            var intent = new UpdateIntent(1, InstallIdentity.ProductIdValue, Identity.Key, OperationId,
                6002, new DateTimeOffset(2026, 10, 15, 4, 5, 7, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 15, 4, 5, 8, TimeSpan.Zero));
            Assert.Equal(UpdateIntentCreateStatus.Created, IntentStore.Create(intent));
        }

        public void SetCurrentVersion(string version) => WritePackage(InstallRoot, version, "current");

        public UpdateTransactionJournal CreateJournal(UpdateTransactionPhase phase, ProcessIdentity? launchedAppIdentity = null)
        {
            var now = DateTimeOffset.UtcNow;
            return new UpdateTransactionJournal(
                1, InstallIdentity.ProductIdValue, Identity.Key, OperationId, "0.3.1", InstallRoot, StageRoot,
                Siblings.NewSiblingPath, Siblings.BackupSiblingPath, phase, now, now, launchedAppIdentity);
        }

        public UpdateTransactionService CreateService(string? updaterBaseDirectory = null) => new(
            Path.GetFullPath(updaterBaseDirectory ?? StageRoot), ControlPaths, IntentStore,
            FailPreparedJournal || FailJournalUpdate ? new FailingJournalStore(JournalStore, FailPreparedJournal, FailJournalUpdate) : JournalStore,
            Runtime, FileSystem, new FakeAcknowledgementWaiter(this), HandoffStore, () => Runtime.UpdaterIdentity);

        public static void WriteExternalPackage(string root, string version, string marker) => WritePackage(root, version, marker);

        public AppStartupAcknowledgement CreateAcknowledgement(ProcessIdentity identity) => new(
            1, InstallIdentity.ProductIdValue, Identity.Key, OperationId, "0.3.1", identity.ProcessId, identity.StartedUtc);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static void WritePackage(string root, string version, string marker)
        {
            Directory.CreateDirectory(root);
            foreach (var file in PackageContract.RequiredFiles)
            {
                File.WriteAllText(Path.Combine(root, file), marker + ":" + version);
            }
            var files = PackageContract.RequiredFiles.Append(PackageContract.ManifestFileName).ToArray();
            File.WriteAllText(Path.Combine(root, PackageContract.ManifestFileName), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                productId = "CodexSlotPing",
                version,
                files
            }));
        }
    }

    private sealed class FakeRuntime : IUpdateTransactionRuntime
    {
        public ProcessIdentity UpdaterIdentity { get; } = new(6002, new DateTimeOffset(2026, 10, 15, 4, 5, 7, TimeSpan.Zero));
        public ProcessIdentity NewAppIdentity { get; } = new(7001, new DateTimeOffset(2026, 10, 15, 4, 7, 8, TimeSpan.Zero));
        public List<(string Path, Guid? OperationId, bool Rollback)> Launches { get; } = [];
        public AppProcessWaitStatus AppWait { get; set; } = AppProcessWaitStatus.Exited;
        public RunnerDrainWaitResult RunnerDrain { get; set; } = RunnerDrainWaitResult.Acquired(new DummyLease());
        public bool FailNextAppLaunch { get; set; }
        public int AppWaitCalls { get; private set; }
        public int RunnerDrainCalls { get; private set; }
        public TimeSpan LastRunnerDrainTimeout { get; private set; }
        public ProcessIdentity? TerminatedProcess { get; private set; }
        public Action? OnRunnerDrain { get; set; }
        public Action? OnAppWait { get; set; }
        public Task<AppProcessWaitStatus> WaitForAppExitAsync(ProcessIdentity app, TimeSpan timeout, CancellationToken cancellationToken) =>
            CountAppWait();
        private Task<AppProcessWaitStatus> CountAppWait()
        {
            AppWaitCalls++;
            OnAppWait?.Invoke();
            return Task.FromResult(AppWait);
        }
        public Task<RunnerDrainWaitResult> WaitForRunnerDrainAsync(UpdateControlPaths paths, TimeSpan timeout, CancellationToken cancellationToken)
        {
            RunnerDrainCalls++;
            LastRunnerDrainTimeout = timeout;
            OnRunnerDrain?.Invoke();
            return Task.FromResult(RunnerDrain);
        }
        public Task<UpdateAppLaunchResult> LaunchAppAsync(string executablePath, string workingDirectory, Guid? operationId, bool rollback, CancellationToken cancellationToken)
        {
            Launches.Add((executablePath, operationId, rollback));
            if (FailNextAppLaunch)
            {
                FailNextAppLaunch = false;
                return Task.FromResult(UpdateAppLaunchResult.Failed());
            }
            return Task.FromResult(UpdateAppLaunchResult.Started(NewAppIdentity));
        }
        public Task TerminateIfSameProcessAsync(ProcessIdentity process, CancellationToken cancellationToken)
        {
            TerminatedProcess = process;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileSystem : IUpdateTransactionFileSystem
    {
        public bool ParentProbeSucceeds { get; set; } = true;
        public int ParentProbeCalls { get; private set; }
        public bool CorruptCopiedManifest { get; set; }
        public Func<string, string, bool>? FailMove { get; set; }
        public ManualResetEventSlim? CopyStarted { get; set; }
        public ManualResetEventSlim? AllowCopy { get; set; }
        public List<(string Source, string Destination)> Copies { get; } = [];
        public List<(string Source, string Destination)> Moves { get; } = [];
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);
        public bool ProbeParentRename(string parentDirectory)
        {
            ParentProbeCalls++;
            return ParentProbeSucceeds;
        }
        public void CopyPackage(string sourceRoot, string destinationRoot, IReadOnlyList<string> relativeFiles)
        {
            Copies.Add((sourceRoot, destinationRoot));
            Directory.CreateDirectory(destinationRoot);
            foreach (var relative in relativeFiles)
            {
                var source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            if (CorruptCopiedManifest)
            {
                File.WriteAllText(Path.Combine(destinationRoot, PackageContract.ManifestFileName), "{\"schemaVersion\":1}");
            }
            CopyStarted?.Set();
            if (AllowCopy is not null && !AllowCopy.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new IOException("test copy barrier timed out");
            }
        }
        public void MoveDirectory(string source, string destination)
        {
            Moves.Add((source, destination));
            if (FailMove?.Invoke(source, destination) == true) throw new IOException("simulated directory move failure");
            Directory.Move(source, destination);
        }
        public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
    }

    private sealed class FakeAcknowledgementWaiter(TransactionFixture fixture) : IStartupAcknowledgementWaiter
    {
        public Task<StartupAcknowledgementWaitResult> WaitAsync(
            Guid operationId, string installIdentity, SemVersion targetVersion, ProcessIdentity launchedAppIdentity,
            TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(fixture.Acknowledgement);
    }

    private sealed class FailingJournalStore(UpdateTransactionJournalStore inner, bool failCreate, bool failUpdate) : IUpdateTransactionJournalStore
    {
        private bool _failUpdate = failUpdate;
        public string GetJournalFilePath(Guid operationId) => inner.GetJournalFilePath(operationId);
        public UpdateTransactionJournalReadResult Read(Guid operationId) => inner.Read(operationId);
        public UpdateTransactionJournalWriteStatus TryCreate(UpdateTransactionJournal journal) =>
            failCreate ? UpdateTransactionJournalWriteStatus.StorageFailure : inner.TryCreate(journal);
        public UpdateTransactionJournalWriteStatus TryUpdate(UpdateTransactionJournal journal)
        {
            if (_failUpdate)
            {
                _failUpdate = false;
                return UpdateTransactionJournalWriteStatus.StorageFailure;
            }
            return inner.TryUpdate(journal);
        }
    }

    private sealed class DummyLease : IDisposable { public void Dispose() { } }
}
