using CodexSlotPing.Update;
using CodexSlotPing.Updater;

namespace CodexSlotPing.Updater.Tests;

public sealed partial class UpdateTransactionServiceTests
{
    [Fact]
    public async Task RecoverAsync_PreparedWithNormalDiskCleansNewSiblingAndIsIdempotent()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "prepared-new");
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.Prepared)));
        var recovery = CreateRecovery(fixture);

        var first = await recovery.RecoverAsync(fixture.Invocation);
        var second = await recovery.RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, first.Code);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.NewSiblingPath));
        Assert.Equal(UpdateTransactionPhase.RolledBack, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
    }

    [Fact]
    public async Task RecoverAsync_PreparedButInstallAlreadyMovedToBackupRestoresBackup()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "prepared-new");
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.Prepared)));

        var result = await CreateRecovery(fixture).RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.False(Directory.Exists(fixture.Siblings.NewSiblingPath));
    }

    [Fact]
    public async Task RecoverAsync_BackupCreatedWithInstallMissingRestoresBackup()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.BackupCreated)));

        var result = await CreateRecovery(fixture).RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
    }

    [Theory]
    [InlineData(UpdateTransactionPhase.BackupCreated)]
    [InlineData(UpdateTransactionPhase.ReplacementInstalled)]
    [InlineData(UpdateTransactionPhase.RollbackStarted)]
    public async Task RecoverAsync_ReplacementWithoutAckUsesDiskRealityToRestoreOldInstall(UpdateTransactionPhase phase)
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "new-sibling");
        Directory.Move(fixture.Siblings.NewSiblingPath, fixture.InstallRoot);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(phase)));

        var result = await CreateRecovery(fixture).RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.False(Directory.Exists(fixture.Siblings.FailedSiblingPath));
    }

    [Fact]
    public async Task RecoverAsync_NewAppLaunchedWithMatchingAckCompletesAndCleansBackup()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "new-sibling");
        Directory.Move(fixture.Siblings.NewSiblingPath, fixture.InstallRoot);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.NewAppLaunched, fixture.Runtime.NewAppIdentity)));
        var acknowledgements = new AppStartupAcknowledgementStore(fixture.ControlPaths);
        Assert.Equal(AppStartupAcknowledgementWriteStatus.Created,
            acknowledgements.TryPublish(fixture.CreateAcknowledgement(fixture.Runtime.NewAppIdentity)));

        var result = await CreateRecovery(fixture).RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, result.Code);
        Assert.Equal("0.3.1", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.Equal(UpdateTransactionPhase.Completed, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task RecoverAsync_CompletedWithBackupCleansTerminalArtifactsIdempotently()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "new-sibling");
        Directory.Move(fixture.Siblings.NewSiblingPath, fixture.InstallRoot);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.Completed, fixture.Runtime.NewAppIdentity)));
        var recovery = CreateRecovery(fixture);

        var first = await recovery.RecoverAsync(fixture.Invocation);
        var second = await recovery.RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, first.Code);
        Assert.Equal(UpdaterResultCode.Success, second.Code);
        Assert.Equal("0.3.1", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.BackupSiblingPath));
    }

    [Fact]
    public async Task RecoverAsync_CompletedWithPartiallyDeletedBackupStaysSuccessfulAndIdempotent()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "new-sibling");
        Directory.Move(fixture.Siblings.NewSiblingPath, fixture.InstallRoot);
        File.Delete(Path.Combine(fixture.Siblings.BackupSiblingPath, PackageContract.ManifestFileName));
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.Completed, fixture.Runtime.NewAppIdentity)));
        var recovery = CreateRecovery(fixture);

        var first = await recovery.RecoverAsync(fixture.Invocation);
        var second = await recovery.RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, first.Code);
        Assert.Equal(UpdaterResultCode.Success, second.Code);
        Assert.Equal("0.3.1", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(UpdateTransactionPhase.Completed, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
        Assert.True(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.False(File.Exists(Path.Combine(fixture.Siblings.BackupSiblingPath, PackageContract.ManifestFileName)));
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task RecoverAsync_RereadsJournalAndIntentAfterWaitingForExclusiveLease()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        TransactionFixture.WriteExternalPackage(fixture.InstallRoot, "0.3.1", "new-current");
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.ReplacementInstalled)));
        fixture.Runtime.OnRunnerDrain = () =>
        {
            Assert.Equal(UpdateTransactionJournalWriteStatus.Updated,
                fixture.JournalStore.TryUpdate(fixture.CreateJournal(UpdateTransactionPhase.Completed, fixture.Runtime.NewAppIdentity)));
            Assert.True(fixture.IntentStore.TryDelete(fixture.OperationId));
        };

        var result = await CreateRecovery(fixture).RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, result.Code);
        Assert.Equal("0.3.1", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(UpdateTransactionPhase.Completed, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
        Assert.Equal(UpdateIntentReadStatus.Missing, fixture.IntentStore.Read().Status);
    }

    [Fact]
    public async Task RecoverAsync_RolledBackTerminalStateIsIdempotent()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        TransactionFixture.WriteExternalPackage(fixture.Siblings.FailedSiblingPath, "0.3.1", "failed-new");
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.RolledBack)));
        var recovery = CreateRecovery(fixture);

        var first = await recovery.RecoverAsync(fixture.Invocation);
        var second = await recovery.RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, first.Code);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.False(Directory.Exists(fixture.Siblings.FailedSiblingPath));
    }

    [Fact]
    public async Task RecoverAsync_RecoveryRequiredPreservesAllEvidenceAndIntent()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "new-sibling");
        Directory.Move(fixture.Siblings.NewSiblingPath, fixture.InstallRoot);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.RecoveryRequired)));

        var result = await CreateRecovery(fixture).RecoverAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.RecoveryRequired, result.Code);
        Assert.True(Directory.Exists(fixture.InstallRoot));
        Assert.True(Directory.Exists(fixture.Siblings.BackupSiblingPath));
        Assert.Equal(UpdateIntentReadStatus.Active, fixture.IntentStore.Read().Status);
        Assert.Equal(UpdateTransactionPhase.RecoveryRequired, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
        Assert.Equal(0, fixture.Runtime.AppWaitCalls);
        Assert.Equal(0, fixture.Runtime.RunnerDrainCalls);
        Assert.Equal(0, fixture.FileSystem.ParentProbeCalls);
    }

    private static UpdateTransactionRecoveryService CreateRecovery(TransactionFixture fixture) => new(
        fixture.ControlPaths,
        fixture.IntentStore,
        fixture.JournalStore,
        new AppStartupAcknowledgementStore(fixture.ControlPaths),
        fixture.Runtime,
        fixture.FileSystem);

}
