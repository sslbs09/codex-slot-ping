using System.Security.Cryptography;
using System.Text;

namespace CodexSlotPing.Update;

public sealed record UpdateControlPaths
{
    private UpdateControlPaths(string directoryPath, string installIdentity, string intentMutationMutexName)
    {
        DirectoryPath = directoryPath;
        InstallIdentity = installIdentity;
        IntentMutationMutexName = intentMutationMutexName;
    }

    public string DirectoryPath { get; }
    public string InstallIdentity { get; }
    public string IntentFilePath => Path.Combine(DirectoryPath, "intent.json");
    public string IntentMutationMutexName { get; }
    public string UpdaterExecutionLockFilePath => Path.Combine(DirectoryPath, "updater.lock");
    public string LeaseFilePath => Path.Combine(DirectoryPath, "lease");
    public string JournalFilePath(Guid operationId) => Path.Combine(DirectoryPath, $"{operationId:N}.journal.json");
    public string HandoffReadyFilePath(Guid operationId) => Path.Combine(DirectoryPath, $"{operationId:N}.handoff-ready");
    public string StartupAcknowledgementFilePath(Guid operationId) => Path.Combine(DirectoryPath, $"{operationId:N}.ack");

    public static UpdateControlPaths ForInstall(string localAppDataRoot, InstallIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(localAppDataRoot) || !Path.IsPathFullyQualified(localAppDataRoot))
        {
            throw new ArgumentException("The LocalAppData root must be an absolute path.", nameof(localAppDataRoot));
        }

        var localAppData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppDataRoot));
        var directory = Path.Combine(localAppData, "CodexSlotPing", "update-control", identity.Key);
        var mutexKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        return new UpdateControlPaths(directory, identity.Key, $"Global\\CodexSlotPing-UpdateIntent-{mutexKey}");
    }

    public static UpdateControlPaths ForCurrentUser(InstallIdentity identity)
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new IOException("LocalAppData could not be resolved.");
        }

        return ForInstall(localAppData, identity);
    }
}
