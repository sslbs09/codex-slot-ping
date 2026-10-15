using CodexSlotPing.Update;
using CodexSlotPing.Updater;

namespace CodexSlotPing.Updater.Tests;

public sealed class ProgramResultTests
{
    [Fact]
    public void RecoveryRequiredResultIncludesInstallJournalAndSiblingLocations()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexSlotPing.Updater.Tests", Guid.NewGuid().ToString("N"));
        var installRoot = Path.Combine(root, "programs", "CodexSlotPing");
        Directory.CreateDirectory(installRoot);
        try
        {
            var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, installRoot);
            var paths = UpdateControlPaths.ForInstall(Path.Combine(root, "local-app-data"), identity);
            var invocation = new UpdaterInvocation(Guid.NewGuid(), SemVersion.Parse("0.3.1"), installRoot,
                Path.Combine(root, "extract"), new ProcessIdentity(100, DateTimeOffset.UtcNow));

            var output = Program.FormatResult(
                new UpdateTransactionResult(UpdaterResultCode.RecoveryRequired, "backup-restore-failed"),
                paths,
                invocation);
            var siblings = UpdateTransactionPaths.ForInstall(installRoot, invocation.OperationId);

            Assert.Contains(paths.JournalFilePath(invocation.OperationId), output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(siblings.InstallRoot, output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(siblings.BackupSiblingPath, output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(siblings.NewSiblingPath, output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
