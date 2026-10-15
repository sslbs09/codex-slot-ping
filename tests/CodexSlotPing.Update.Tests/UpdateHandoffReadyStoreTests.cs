using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdateHandoffReadyStoreTests
{
    [Fact]
    public void Publish_IsOperationScopedAndDoesNotOverwriteExistingMarker()
    {
        using var temp = new TemporaryDirectory();
        var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, Path.Combine(temp.Path, "install"));
        var paths = UpdateControlPaths.ForInstall(Path.Combine(temp.Path, "local-app-data"), identity);
        var store = new UpdateHandoffReadyStore(paths);
        var operationId = Guid.NewGuid();
        var marker = CreateMarker(identity, operationId);

        Assert.Equal(UpdateHandoffReadyWriteStatus.Created, store.TryPublish(marker));
        Assert.Equal(UpdateHandoffReadyWriteStatus.AlreadyExists, store.TryPublish(marker with { UpdaterPid = 9 }));
        Assert.Equal(UpdateHandoffReadyReadStatus.Active, store.Read(operationId).Status);
        Assert.Equal(marker, store.Read(operationId).HandoffReady);
        Assert.Equal(UpdateHandoffReadyReadStatus.Missing, store.Read(Guid.NewGuid()).Status);
    }

    [Fact]
    public void Validator_RequiresOperationInstallAndLaunchedProcessIdentity()
    {
        using var temp = new TemporaryDirectory();
        var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, Path.Combine(temp.Path, "install"));
        var operationId = Guid.NewGuid();
        var launched = new ProcessIdentity(3210, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));
        var marker = CreateMarker(identity, operationId) with { UpdaterPid = launched.ProcessId, UpdaterStartedUtc = launched.StartedUtc };

        Assert.True(UpdateHandoffReadyValidator.Matches(marker, operationId, identity.Key, launched));
        Assert.False(UpdateHandoffReadyValidator.Matches(marker, Guid.NewGuid(), identity.Key, launched));
        Assert.False(UpdateHandoffReadyValidator.Matches(marker with { InstallIdentity = "other" }, operationId, identity.Key, launched));
        Assert.False(UpdateHandoffReadyValidator.Matches(marker with { UpdaterPid = launched.ProcessId + 1 }, operationId, identity.Key, launched));
        Assert.False(UpdateHandoffReadyValidator.Matches(marker with { UpdaterStartedUtc = launched.StartedUtc.AddSeconds(-1) }, operationId, identity.Key, launched));
    }

    private static UpdateHandoffReady CreateMarker(InstallIdentity identity, Guid operationId) => new(
        1, InstallIdentity.ProductIdValue, identity.Key, operationId, 3210,
        new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 15, 4, 6, 7, TimeSpan.Zero));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
