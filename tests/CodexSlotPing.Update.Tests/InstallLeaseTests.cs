using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class InstallLeaseTests
{
    [Fact]
    public void SharedRunnerLeases_CoexistAndDrainBeforeExclusiveLeaseSucceeds()
    {
        using var temp = new TemporaryDirectory();
        var paths = CreatePaths(temp.Path, "install-a");

        Assert.Equal(InstallLeaseAcquireStatus.Acquired, RunnerUpdateLease.TryAcquire(paths, out var first));
        using (first)
        {
            Assert.Equal(InstallLeaseAcquireStatus.Acquired, RunnerUpdateLease.TryAcquire(paths, out var second));
            using (second)
            {
                Assert.Equal(InstallLeaseAcquireStatus.Busy, ExclusiveInstallLease.TryAcquire(paths, out var whileBothRun));
                Assert.Null(whileBothRun);
            }

            Assert.Equal(InstallLeaseAcquireStatus.Busy, ExclusiveInstallLease.TryAcquire(paths, out var whileFirstRuns));
            Assert.Null(whileFirstRuns);
        }

        Assert.Equal(InstallLeaseAcquireStatus.Acquired, ExclusiveInstallLease.TryAcquire(paths, out var exclusive));
        using (exclusive)
        {
            Assert.Equal(InstallLeaseAcquireStatus.Busy, RunnerUpdateLease.TryAcquire(paths, out var whileInstalling));
            Assert.Null(whileInstalling);
        }

        Assert.Equal(InstallLeaseAcquireStatus.Acquired, RunnerUpdateLease.TryAcquire(paths, out var afterInstall));
        afterInstall!.Dispose();
    }

    [Fact]
    public void ExclusiveLeases_AreIsolatedByPortableInstallIdentity()
    {
        using var temp = new TemporaryDirectory();
        var firstPaths = CreatePaths(temp.Path, "install-a");
        var secondPaths = CreatePaths(temp.Path, "install-b");

        Assert.Equal(InstallLeaseAcquireStatus.Acquired, ExclusiveInstallLease.TryAcquire(firstPaths, out var first));
        using (first)
        {
            Assert.Equal(InstallLeaseAcquireStatus.Acquired, ExclusiveInstallLease.TryAcquire(secondPaths, out var second));
            second!.Dispose();
        }
    }

    private static UpdateControlPaths CreatePaths(string root, string installName)
    {
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(root, installName));
        return UpdateControlPaths.ForInstall(Path.Combine(root, "local-app-data"), identity);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
