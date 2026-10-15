using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class InstallIdentityTests
{
    [Fact]
    public void Create_NormalizesWindowsPathCaseAndTrailingSeparator()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "PortableInstall");
        Directory.CreateDirectory(root);

        var first = InstallIdentity.Create("CodexSlotPing", root + Path.DirectorySeparatorChar);
        var second = InstallIdentity.Create("CodexSlotPing", root.ToUpperInvariant());

        Assert.Equal(first.Key, second.Key);
        Assert.Matches("^[A-F0-9]{64}$", first.Key);
    }

    [Fact]
    public void Create_SeparatesDifferentPortableInstallRoots()
    {
        using var temp = new TemporaryDirectory();
        var firstRoot = Path.Combine(temp.Path, "install-a");
        var secondRoot = Path.Combine(temp.Path, "install-b");

        var first = InstallIdentity.Create("CodexSlotPing", firstRoot);
        var second = InstallIdentity.Create("CodexSlotPing", secondRoot);

        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void Create_RejectsRelativeRootInsteadOfUsingCurrentDirectory()
    {
        Assert.Throws<ArgumentException>(() => InstallIdentity.Create("CodexSlotPing", "portable-install"));
    }

    [Fact]
    public void UpdateControlPaths_AreScopedUnderLocalAppDataAndIdentity()
    {
        using var temp = new TemporaryDirectory();
        var localAppData = Path.Combine(temp.Path, "local-app-data");
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(temp.Path, "install"));

        var paths = UpdateControlPaths.ForInstall(localAppData, identity);

        Assert.Equal(
            Path.Combine(localAppData, "CodexSlotPing", "update-control", identity.Key),
            paths.DirectoryPath);
    }

    [Fact]
    public void IntentMutationMutex_IsScopedToThePerUserControlDirectory()
    {
        using var temp = new TemporaryDirectory();
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(temp.Path, "shared-install"));
        var firstUser = UpdateControlPaths.ForInstall(Path.Combine(temp.Path, "user-a"), identity);
        var secondUser = UpdateControlPaths.ForInstall(Path.Combine(temp.Path, "user-b"), identity);

        Assert.NotEqual(firstUser.IntentMutationMutexName, secondUser.IntentMutationMutexName);
        Assert.Equal(firstUser.IntentMutationMutexName,
            UpdateControlPaths.ForInstall(Path.Combine(temp.Path, "user-a"), identity).IntentMutationMutexName);
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
