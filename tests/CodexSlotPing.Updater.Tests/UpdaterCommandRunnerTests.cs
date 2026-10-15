using CodexSlotPing.Update;
using CodexSlotPing.Updater;

namespace CodexSlotPing.Updater.Tests;

public sealed partial class UpdateTransactionServiceTests
{
    [Fact]
    public async Task UpdaterCommandRunner_ResumesActiveJournalThroughRecovery()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        Directory.Move(fixture.InstallRoot, fixture.Siblings.BackupSiblingPath);
        TransactionFixture.WriteExternalPackage(fixture.Siblings.NewSiblingPath, "0.3.1", "replacement");
        Directory.Move(fixture.Siblings.NewSiblingPath, fixture.InstallRoot);
        Assert.Equal(UpdateTransactionJournalWriteStatus.Created,
            fixture.JournalStore.TryCreate(fixture.CreateJournal(UpdateTransactionPhase.ReplacementInstalled)));

        var result = await CreateCommandRunner(fixture).RunAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.StartupFailedRolledBack, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(UpdateTransactionPhase.RolledBack, fixture.JournalStore.Read(fixture.OperationId).Journal!.Phase);
    }

    [Fact]
    public async Task UpdaterCommandRunner_StartsNewTransactionWhenJournalIsMissing()
    {
        using var fixture = new TransactionFixture();
        fixture.Acknowledgement = StartupAcknowledgementWaitResult.Received(
            fixture.CreateAcknowledgement(fixture.Runtime.NewAppIdentity));

        var result = await CreateCommandRunner(fixture).RunAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.Success, result.Code);
        Assert.Equal("0.3.1", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
    }

    [Fact]
    public async Task UpdaterCommandRunner_FailsClosedWhenJournalIsInvalid()
    {
        using var fixture = new TransactionFixture();
        fixture.PublishIntent();
        File.WriteAllText(fixture.JournalStore.GetJournalFilePath(fixture.OperationId), "not-json");

        var result = await CreateCommandRunner(fixture).RunAsync(fixture.Invocation);

        Assert.Equal(UpdaterResultCode.JournalFailure, result.Code);
        Assert.Equal("0.3.0", PackageDirectoryValidator.ValidateDirectory(fixture.InstallRoot).Version!.ToString());
        Assert.Equal(0, fixture.Runtime.AppWaitCalls);
    }

    private static UpdaterCommandRunner CreateCommandRunner(TransactionFixture fixture) => new(
        fixture.JournalStore,
        fixture.CreateService(),
        new UpdateTransactionRecoveryService(
            fixture.ControlPaths,
            fixture.IntentStore,
            fixture.JournalStore,
            new AppStartupAcknowledgementStore(fixture.ControlPaths),
            fixture.Runtime,
            fixture.FileSystem));
}
