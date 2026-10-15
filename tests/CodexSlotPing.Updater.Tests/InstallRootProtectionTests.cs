using CodexSlotPing.Update;
using CodexSlotPing.Updater;

namespace CodexSlotPing.Updater.Tests;

public sealed class InstallRootProtectionTests
{
    [Fact]
    public void IsProtected_RejectsVolumeAndKnownUserRoots()
    {
        var volumeRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(InstallRootProtection.IsProtected(volumeRoot));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.True(InstallRootProtection.IsProtected(profile));
        Assert.True(InstallRootProtection.IsProtected(desktop));
        Assert.True(InstallRootProtection.IsProtected(InstallRootProtection.DownloadsPath));
    }

    [Fact]
    public void ExtendedDeviceAliasOfDownloadsIsRejectedBeforeIdentityCanDiverge()
    {
        Assert.True(OperatingSystem.IsWindows());
        var downloads = InstallRootProtection.DownloadsPath;
        Assert.False(string.IsNullOrWhiteSpace(downloads));
        var deviceAlias = @"\\?\" + downloads;

        Assert.True(InstallRootProtection.IsProtected(deviceAlias));
        Assert.Throws<ArgumentException>(() => InstallIdentity.Create(InstallIdentity.ProductIdValue, deviceAlias));
    }
}
