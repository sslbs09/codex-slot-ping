using System.Diagnostics;
using System.IO;
using CodexSlotPing.Update;

namespace CodexSlotPing.App;

public enum AppUpdateStartupMode
{
    Normal,
    UpdateOperation,
    RollbackNotice,
    UpdateInProgress,
    RecoveryUpdaterLaunched,
    RecoveryRequired
}

public sealed record AppUpdateStartupResult(
    AppUpdateStartupMode Mode,
    Guid? OperationId = null,
    string? TargetVersion = null,
    bool HasCleanupWarning = false,
    string? DiagnosticCategory = null);

public sealed class AppUpdateStartupCoordinator
{
    private readonly InstallIdentity _identity;
    private readonly UpdateControlPaths _paths;
    private readonly IUpdateIntentLifecycle _intentStore;
    private readonly IUpdateTransactionJournalStore _journalStore;
    private readonly IProcessIdentityProbe _processProbe;
    private readonly IUpdaterHandoffLauncher _updaterLauncher;
    private readonly ProcessIdentity _currentAppIdentity;
    private readonly string _updatesRoot;

    public AppUpdateStartupCoordinator(
        InstallIdentity identity,
        UpdateControlPaths paths,
        IUpdateIntentLifecycle intentStore,
        IUpdateTransactionJournalStore journalStore,
        IProcessIdentityProbe processProbe,
        IUpdaterHandoffLauncher updaterLauncher,
        ProcessIdentity currentAppIdentity,
        string? updatesRoot = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _intentStore = intentStore ?? throw new ArgumentNullException(nameof(intentStore));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _processProbe = processProbe ?? throw new ArgumentNullException(nameof(processProbe));
        _updaterLauncher = updaterLauncher ?? throw new ArgumentNullException(nameof(updaterLauncher));
        _currentAppIdentity = currentAppIdentity ?? throw new ArgumentNullException(nameof(currentAppIdentity));
        _updatesRoot = Path.GetFullPath(updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSlotPing", "updates"));
    }

    public async Task<AppUpdateStartupResult> ResolveAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var parsed = AppUpdateStartupArguments.Parse(arguments);
        if (!parsed.IsValid) return Required("startup-arguments-invalid");

        UpdateIntentReadResult intentRead;
        try { intentRead = _intentStore.Read(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Required("intent-read-failed"); }

        if (intentRead.Status == UpdateIntentReadStatus.Invalid || intentRead.Status == UpdateIntentReadStatus.IoError)
        {
            return Required("intent-invalid");
        }

        if (intentRead.Status == UpdateIntentReadStatus.Missing)
        {
            if (parsed.OperationId is { } operationId && parsed.IsRollback)
            {
                return ValidateRollback(operationId, intentMayBeMissing: true);
            }
            return parsed.OperationId is null && !parsed.IsRollback
                ? new(AppUpdateStartupMode.Normal)
                : Required("update-operation-without-intent");
        }

        var intent = intentRead.Intent!;
        var journalRead = _journalStore.Read(intent.OperationId);
        if (journalRead.Status is UpdateTransactionJournalReadStatus.Invalid or UpdateTransactionJournalReadStatus.IoError)
        {
            return Required("journal-invalid");
        }

        if (parsed.OperationId is { } requestedOperation)
        {
            if (requestedOperation != intent.OperationId) return Required("update-operation-mismatch");
            if (parsed.IsRollback) return ValidateRollback(requestedOperation, intentMayBeMissing: false, journalRead.Journal);
            if (_processProbe.Match(intent) != UpdaterProcessMatch.Matching) return Required("updater-identity-mismatch");
            return ValidateUpdateStartup(requestedOperation, journalRead);
        }

        var journalStatus = Classify(journalRead);
        var action = UpdateIntentRecoveryEvaluator.Evaluate(intentRead, _processProbe.Match(intent), journalStatus);
        switch (action)
        {
            case UpdateIntentRecoveryAction.NoIntent:
                return new(AppUpdateStartupMode.Normal);
            case UpdateIntentRecoveryAction.KeepActive:
                return new(AppUpdateStartupMode.UpdateInProgress, intent.OperationId);
            case UpdateIntentRecoveryAction.ClearStaleIntent:
                return _intentStore.TryDelete(intent.OperationId,
                    new ProcessIdentity(intent.UpdaterPid, intent.UpdaterStartedUtc))
                    ? new(AppUpdateStartupMode.Normal)
                    : Required("stale-intent-cleanup-failed");
            case UpdateIntentRecoveryAction.CleanupTerminalTransaction:
                return ResolveTerminal(intent, journalRead.Journal!);
            case UpdateIntentRecoveryAction.RecoverIncompleteTransaction:
                return await LaunchRecoveryAsync(intent, journalRead.Journal!, cancellationToken).ConfigureAwait(false);
            default:
                return Required("update-state-ambiguous");
        }
    }

    public AppStartupAcknowledgementWriteStatus TryPublishStartupAcknowledgement(
        AppUpdateStartupResult startup,
        bool isPrimaryInstance,
        IAppStartupAcknowledgementStore acknowledgementStore)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(acknowledgementStore);
        if (!isPrimaryInstance || startup.Mode != AppUpdateStartupMode.UpdateOperation ||
            startup.OperationId is not { } operationId || startup.TargetVersion is not { } targetText ||
            !SemVersion.TryParse(targetText, out var target))
        {
            return AppStartupAcknowledgementWriteStatus.Invalid;
        }

        var intentRead = _intentStore.Read();
        if (intentRead.Status != UpdateIntentReadStatus.Active || intentRead.Intent?.OperationId != operationId ||
            _processProbe.Match(intentRead.Intent) != UpdaterProcessMatch.Matching)
        {
            return AppStartupAcknowledgementWriteStatus.Invalid;
        }
        var currentJournal = _journalStore.Read(operationId);
        var currentValidation = ValidateUpdateStartup(operationId, currentJournal);
        if (currentValidation.Mode != AppUpdateStartupMode.UpdateOperation ||
            !string.Equals(currentValidation.TargetVersion, target!.ToString(), StringComparison.Ordinal))
        {
            return AppStartupAcknowledgementWriteStatus.Invalid;
        }

        return acknowledgementStore.TryPublish(new AppStartupAcknowledgement(
            1, InstallIdentity.ProductIdValue, _identity.Key, operationId, target.ToString(),
            _currentAppIdentity.ProcessId, _currentAppIdentity.StartedUtc));
    }

    private AppUpdateStartupResult ValidateUpdateStartup(Guid operationId, UpdateTransactionJournalReadResult journalRead)
    {
        var journal = journalRead.Journal;
        if (journalRead.Status != UpdateTransactionJournalReadStatus.Active || journal is null ||
            journal.OperationId != operationId || journal.Phase is not (UpdateTransactionPhase.ReplacementInstalled or UpdateTransactionPhase.NewAppLaunched) ||
            !JournalMatchesInstall(journal) || !SemVersion.TryParse(journal.TargetVersion, out var targetVersion))
        {
            return Required("update-startup-journal-mismatch");
        }

        var installed = PackageDirectoryValidator.ValidateDirectory(_identity.InstallRoot, targetVersion);
        return installed.IsValid
            ? new(AppUpdateStartupMode.UpdateOperation, operationId, targetVersion!.ToString())
            : Required("update-startup-package-mismatch");
    }

    private AppUpdateStartupResult ValidateRollback(Guid operationId, bool intentMayBeMissing, UpdateTransactionJournal? knownJournal = null)
    {
        var journalRead = knownJournal is null ? _journalStore.Read(operationId) : UpdateTransactionJournalReadResult.Active(knownJournal);
        var journal = journalRead.Journal;
        var intentRead = _intentStore.Read();
        var matchingIntent = intentRead.Status == UpdateIntentReadStatus.Active && intentRead.Intent?.OperationId == operationId &&
            string.Equals(intentRead.Intent.InstallIdentity, _identity.Key, StringComparison.Ordinal);
        if (journalRead.Status != UpdateTransactionJournalReadStatus.Active || journal is null ||
            journal.Phase != UpdateTransactionPhase.RolledBack || !JournalMatchesInstall(journal) ||
            (!matchingIntent && !(intentMayBeMissing && intentRead.Status == UpdateIntentReadStatus.Missing)) ||
            !SemVersion.TryParse(journal.TargetVersion, out var target))
        {
            return Required("rollback-startup-unverified");
        }

        var installed = PackageDirectoryValidator.ValidateDirectory(_identity.InstallRoot);
        if (!installed.IsValid || installed.Version is null || !installed.Version.IsStable || installed.Version.CompareTo(target) >= 0)
        {
            return Required("rollback-install-unverified");
        }
        return new(AppUpdateStartupMode.RollbackNotice, operationId, installed.Version.ToString());
    }

    private AppUpdateStartupResult ResolveTerminal(UpdateIntent intent, UpdateTransactionJournal journal)
    {
        if (!JournalMatchesInstall(journal) || !SemVersion.TryParse(journal.TargetVersion, out var target))
        {
            return Required("terminal-journal-mismatch");
        }

        var installed = PackageDirectoryValidator.ValidateDirectory(_identity.InstallRoot);
        var validTerminalInstall = journal.Phase switch
        {
            UpdateTransactionPhase.Completed => installed.IsValid && installed.Version is not null &&
                string.Equals(installed.Version.ToString(), target!.ToString(), StringComparison.Ordinal),
            UpdateTransactionPhase.RolledBack => installed.IsValid && installed.Version is not null && installed.Version.IsStable && installed.Version.CompareTo(target!) < 0,
            _ => false
        };
        if (!validTerminalInstall) return Required("terminal-install-unverified");

        var warning = false;
        var siblings = UpdateTransactionPaths.ForInstall(_identity.InstallRoot, journal.OperationId);
        if (Directory.Exists(siblings.BackupSiblingPath))
        {
            var backup = PackageDirectoryValidator.ValidateDirectory(siblings.BackupSiblingPath);
            if (backup.IsValid)
            {
                try { Directory.Delete(siblings.BackupSiblingPath, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { warning = true; }
            }
            else
            {
                // Partial or unowned debris has no manifest ownership proof; leave it intact.
                warning = true;
            }
        }

        if (!_intentStore.TryDelete(intent.OperationId,
                new ProcessIdentity(intent.UpdaterPid, intent.UpdaterStartedUtc)))
        {
            return Required("terminal-intent-cleanup-failed");
        }
        return new(AppUpdateStartupMode.Normal, intent.OperationId, target!.ToString(), warning,
            warning ? "terminal-cleanup-warning" : null);
    }

    private async Task<AppUpdateStartupResult> LaunchRecoveryAsync(
        UpdateIntent intent,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (!JournalMatchesInstall(journal) || journal.Phase == UpdateTransactionPhase.RecoveryRequired ||
            !SemVersion.TryParse(journal.TargetVersion, out var target) || !IsTrustedStageRoot(journal, target!))
        {
            return Required("recovery-updater-untrusted");
        }

        var updaterPath = Path.Combine(journal.VerifiedExtractRoot, "CodexSlotPing.Updater.exe");
        var invocation = new UpdaterInvocation(journal.OperationId, target!, _identity.InstallRoot,
            journal.VerifiedExtractRoot, _currentAppIdentity);
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = journal.VerifiedExtractRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        invocation.AppendArgumentsTo(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        IUpdaterHandoffProcess? process;
        try { process = _updaterLauncher.Start(startInfo); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Required("recovery-updater-launch-failed");
        }

        if (process is null) return Required("recovery-updater-launch-failed");
        using (process)
        {
            return process.IsSameProcessAlive()
                ? new(AppUpdateStartupMode.RecoveryUpdaterLaunched, journal.OperationId, target!.ToString())
                : Required("recovery-updater-exited");
        }
    }

    private bool IsTrustedStageRoot(UpdateTransactionJournal journal, SemVersion target)
    {
        if (!Path.IsPathFullyQualified(journal.VerifiedExtractRoot)) return false;
        var stage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(journal.VerifiedExtractRoot));
        var expectedStage = Path.Combine(_updatesRoot, target.ToString(), journal.OperationId.ToString("N"), "extract");
        if (!PathsEqual(stage, expectedStage)) return false;

        var package = PackageDirectoryValidator.ValidateDirectory(stage, target);
        return package.IsValid && File.Exists(Path.Combine(stage, "CodexSlotPing.Updater.exe"));
    }

    private bool JournalMatchesInstall(UpdateTransactionJournal journal) =>
        journal.OperationId != Guid.Empty &&
        string.Equals(journal.InstallIdentity, _paths.InstallIdentity, StringComparison.Ordinal) &&
        string.Equals(journal.InstallIdentity, _identity.Key, StringComparison.Ordinal) &&
        PathsEqual(journal.InstallRoot, _identity.InstallRoot);

    private static TransactionJournalStatus Classify(UpdateTransactionJournalReadResult read)
    {
        if (read.Status == UpdateTransactionJournalReadStatus.Missing) return TransactionJournalStatus.Missing;
        if (read.Status != UpdateTransactionJournalReadStatus.Active || read.Journal is null) return TransactionJournalStatus.Invalid;
        return read.Journal.Phase switch
        {
            UpdateTransactionPhase.Completed => TransactionJournalStatus.Completed,
            UpdateTransactionPhase.RolledBack => TransactionJournalStatus.RolledBack,
            _ => TransactionJournalStatus.Incomplete
        };
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static AppUpdateStartupResult Required(string category) =>
        new(AppUpdateStartupMode.RecoveryRequired, DiagnosticCategory: category);
}

public sealed record AppUpdateStartupArguments(bool IsValid, Guid? OperationId, bool IsRollback)
{
    public static AppUpdateStartupArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0) return new(true, null, false);
        if (arguments.Count != 2 || arguments[1] is null ||
            arguments[0] is not ("--update-operation" or "--update-rollback") ||
            !Guid.TryParseExact(arguments[1], "D", out var operationId) || operationId == Guid.Empty)
        {
            return new(false, null, false);
        }
        return new(true, operationId, arguments[0] == "--update-rollback");
    }
}
