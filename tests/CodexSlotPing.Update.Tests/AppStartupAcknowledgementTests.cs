using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class AppStartupAcknowledgementTests
{
    [Fact]
    public void Store_CreatesOneOperationScopedAcknowledgementAndDoesNotOverwrite()
    {
        using var temp = new TemporaryDirectory();
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(temp.Path, "install"));
        var paths = UpdateControlPaths.ForInstall(Path.Combine(temp.Path, "local-app-data"), identity);
        var operationId = Guid.NewGuid();
        var ack = CreateAck(identity, operationId);
        var store = new AppStartupAcknowledgementStore(paths);

        Assert.Equal(AppStartupAcknowledgementWriteStatus.Created, store.TryPublish(ack));
        Assert.Equal(AppStartupAcknowledgementWriteStatus.AlreadyExists, store.TryPublish(ack with { RunningVersion = "0.3.2" }));
        var read = store.Read(operationId);
        Assert.Equal(AppStartupAcknowledgementReadStatus.Active, read.Status);
        Assert.Equal(ack, read.Acknowledgement);
    }

    [Fact]
    public void Validator_RequiresOperationIdentityVersionAndLaunchedProcessIdentity()
    {
        using var temp = new TemporaryDirectory();
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(temp.Path, "install"));
        var operationId = Guid.NewGuid();
        var launched = new ProcessIdentity(3210, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));
        var ack = CreateAck(identity, operationId) with { AppPid = launched.ProcessId, AppStartedUtc = launched.StartedUtc };

        Assert.True(AppStartupAcknowledgementValidator.Matches(ack, operationId, identity.Key, SemVersion.Parse("0.3.1"), launched));
        Assert.False(AppStartupAcknowledgementValidator.Matches(ack, Guid.NewGuid(), identity.Key, SemVersion.Parse("0.3.1"), launched));
        Assert.False(AppStartupAcknowledgementValidator.Matches(ack with { RunningVersion = "0.3.2" }, operationId, identity.Key, SemVersion.Parse("0.3.1"), launched));
        Assert.False(AppStartupAcknowledgementValidator.Matches(ack with { RunningVersion = "0.3.1+other-build" }, operationId, identity.Key, SemVersion.Parse("0.3.1"), launched));
        Assert.False(AppStartupAcknowledgementValidator.Matches(ack with { AppPid = launched.ProcessId + 1 }, operationId, identity.Key, SemVersion.Parse("0.3.1"), launched));
    }

    private static AppStartupAcknowledgement CreateAck(InstallIdentity identity, Guid operationId) => new(
        1, "CodexSlotPing", identity.Key, operationId, "0.3.1", 3210,
        new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
