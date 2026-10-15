using System.Diagnostics;
using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal sealed class UpdateTransactionService(
    string updaterBaseDirectory,
    UpdateControlPaths controlPaths,
    IUpdateIntentLifecycle intentStore,
    IUpdateTransactionJournalStore journalStore,
    IUpdateTransactionRuntime runtime,
    IUpdateTransactionFileSystem fileSystem,
    IStartupAcknowledgementWaiter acknowledgementWaiter,
    IUpdateHandoffReadyStore handoffReadyStore,
    Func<ProcessIdentity>? processIdentityProvider = null)
{
    private static readonly SemVersion MinimumAutomaticVersion = SemVersion.Parse("0.3.0");
    private static readonly TimeSpan AppExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunnerDrainTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StartupAcknowledgementTimeout = TimeSpan.FromSeconds(30);

    public async Task<UpdateTransactionResult> ExecuteAsync(
        UpdaterInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (!IsInvocationWellFormed(invocation))
        {
            return Result(UpdaterResultCode.InvalidArguments, "invocation-invalid");
        }

        InstallIdentity identity;
        UpdateTransactionPathSet siblings;
        try
        {
            identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, invocation.InstallRoot);
            siblings = UpdateTransactionPaths.ForInstall(identity.InstallRoot, invocation.OperationId);
        }
        catch (ArgumentException)
        {
            return Result(UpdaterResultCode.InvalidArguments, "install-root-invalid");
        }

        if (!string.Equals(identity.Key, controlPaths.InstallIdentity, StringComparison.Ordinal))
        {
            return Result(UpdaterResultCode.IntentMismatch, "control-path-owner-mismatch");
        }

        var executionLeaseResult = UpdaterExecutionLease.TryAcquire(controlPaths);
        if (executionLeaseResult.Status == UpdaterExecutionLeaseStatus.Busy)
        {
            return Result(UpdaterResultCode.UpdaterAlreadyRunning, "updater-already-running");
        }
        if (executionLeaseResult.Status != UpdaterExecutionLeaseStatus.Acquired || executionLeaseResult.Lease is null)
        {
            return Result(UpdaterResultCode.RecoveryRequired, "updater-execution-lease-failed");
        }
        using var updaterExecutionLease = executionLeaseResult.Lease;

        UpdateIntentReadResult intentRead;
        try
        {
            intentRead = intentStore.Read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(UpdaterResultCode.IntentMismatch, "intent-read-failed");
        }
        if (intentRead.Status != UpdateIntentReadStatus.Missing)
        {
            return Result(UpdaterResultCode.IntentMismatch, "conflicting-update-intent");
        }

        var existingJournal = journalStore.Read(invocation.OperationId);
        if (existingJournal.Status == UpdateTransactionJournalReadStatus.Active)
        {
            return Result(UpdaterResultCode.RecoveryRequired, "transaction-journal-already-exists");
        }
        if (existingJournal.Status != UpdateTransactionJournalReadStatus.Missing)
        {
            return Result(UpdaterResultCode.JournalFailure, "transaction-journal-unavailable");
        }

        var preflight = ValidateBeforeWaiting(invocation, identity, siblings);
        if (!preflight.IsValid)
        {
            return Result(UpdaterResultCode.PreflightFailed, preflight.Error ?? "preflight-failed");
        }

        ProcessIdentity updaterIdentity;
        try
        {
            updaterIdentity = processIdentityProvider?.Invoke() ?? ReadCurrentProcessIdentity();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Result(UpdaterResultCode.PreflightFailed, "updater-identity-unavailable");
        }

        var createdUtc = DateTimeOffset.UtcNow;
        var intent = new UpdateIntent(
            1, InstallIdentity.ProductIdValue, identity.Key, invocation.OperationId,
            updaterIdentity.ProcessId, updaterIdentity.StartedUtc, createdUtc);
        if (intentStore.Create(intent) != UpdateIntentCreateStatus.Created)
        {
            return Result(UpdaterResultCode.IntentMismatch, "update-intent-create-failed");
        }

        var handoffReady = new UpdateHandoffReady(
            1, InstallIdentity.ProductIdValue, identity.Key, invocation.OperationId,
            updaterIdentity.ProcessId, updaterIdentity.StartedUtc, DateTimeOffset.UtcNow);
        if (handoffReadyStore.TryPublish(handoffReady) != UpdateHandoffReadyWriteStatus.Created)
        {
            ClearOwnedIntent(invocation.OperationId);
            return Result(UpdaterResultCode.PreflightFailed, "handoff-ready-publish-failed");
        }

        var appWait = await runtime.WaitForAppExitAsync(invocation.AppIdentity, AppExitTimeout, cancellationToken).ConfigureAwait(false);
        if (appWait != AppProcessWaitStatus.Exited)
        {
            ClearOwnedIntent(invocation.OperationId);
            handoffReadyStore.TryDelete(invocation.OperationId);
            return Result(UpdaterResultCode.AppExitTimeout, appWait == AppProcessWaitStatus.Unknown ? "app-identity-unavailable" : "app-exit-timeout");
        }

        var drain = await runtime.WaitForRunnerDrainAsync(controlPaths, RunnerDrainTimeout, cancellationToken).ConfigureAwait(false);
        if (drain.Status != RunnerDrainWaitStatus.Acquired || drain.Lease is null)
        {
            ClearOwnedIntent(invocation.OperationId);
            handoffReadyStore.TryDelete(invocation.OperationId);
            return Result(
                drain.Status == RunnerDrainWaitStatus.TimedOut ? UpdaterResultCode.RunnerDrainTimeout : UpdaterResultCode.PreflightFailed,
                drain.Status == RunnerDrainWaitStatus.TimedOut ? "runner-drain-timeout" : "runner-lease-failed");
        }

        using (drain.Lease)
        {
            var result = await ExecuteWithLeaseAsync(invocation, identity, siblings, preflight, cancellationToken).ConfigureAwait(false);
            if (result.Code != UpdaterResultCode.RecoveryRequired)
            {
                handoffReadyStore.TryDelete(invocation.OperationId);
            }
            return result;
        }
    }

    private async Task<UpdateTransactionResult> ExecuteWithLeaseAsync(
        UpdaterInvocation invocation,
        InstallIdentity identity,
        UpdateTransactionPathSet siblings,
        TransactionPreflight preflight,
        CancellationToken cancellationToken)
    {
        var secondPreflight = ValidateBeforeWaiting(invocation, identity, siblings);
        if (!secondPreflight.IsValid ||
            !string.Equals(secondPreflight.CurrentVersion?.ToString(), preflight.CurrentVersion?.ToString(), StringComparison.Ordinal))
        {
            ClearOwnedIntent(invocation.OperationId);
            return Result(UpdaterResultCode.PreflightFailed, "package-changed-before-transaction");
        }

        try
        {
            fileSystem.CopyPackage(invocation.VerifiedExtractRoot, siblings.NewSiblingPath, secondPreflight.StagingPackage!.RegularFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClearOwnedIntent(invocation.OperationId);
            return Result(UpdaterResultCode.PreflightFailed, "sibling-copy-failed");
        }

        var newPackage = PackageDirectoryValidator.ValidateDirectory(siblings.NewSiblingPath, invocation.TargetVersion);
        if (!newPackage.IsValid)
        {
            ClearOwnedIntent(invocation.OperationId);
            return Result(UpdaterResultCode.PreflightFailed, "new-sibling-validation-failed");
        }

        var now = DateTimeOffset.UtcNow;
        var journal = new UpdateTransactionJournal(
            1,
            InstallIdentity.ProductIdValue,
            identity.Key,
            invocation.OperationId,
            invocation.TargetVersion.ToString(),
            identity.InstallRoot,
            Path.GetFullPath(invocation.VerifiedExtractRoot),
            siblings.NewSiblingPath,
            siblings.BackupSiblingPath,
            UpdateTransactionPhase.Prepared,
            now,
            now,
            null);
        if (journalStore.TryCreate(journal) != UpdateTransactionJournalWriteStatus.Created)
        {
            TryDeleteValidatedPackage(siblings.NewSiblingPath, invocation.TargetVersion);
            ClearOwnedIntent(invocation.OperationId);
            return Result(UpdaterResultCode.JournalFailure, "prepared-journal-write-failed");
        }

        try
        {
            fileSystem.MoveDirectory(identity.InstallRoot, siblings.BackupSiblingPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (IsValidPackage(identity.InstallRoot, preflight.CurrentVersion) && !fileSystem.DirectoryExists(siblings.BackupSiblingPath))
            {
                var rolledBack = journal with { Phase = UpdateTransactionPhase.RolledBack, UpdatedUtc = UtcAfter(journal.UpdatedUtc) };
                if (!TryUpdate(rolledBack))
                {
                    // Keep the Prepared journal and active Intent so startup recovery can
                    // prove the install is untouched and finish cleanup later.
                    return Result(UpdaterResultCode.RecoveryRequired, "install-backup-rename-journal-failed");
                }
                TryDeleteValidatedPackage(siblings.NewSiblingPath, invocation.TargetVersion);
                ClearOwnedIntent(invocation.OperationId);
                return Result(UpdaterResultCode.ReplacementFailed, "install-backup-rename-failed");
            }
            return await MarkRecoveryRequiredAsync(journal, "install-backup-state-unknown").ConfigureAwait(false);
        }

        var backupCreated = journal with { Phase = UpdateTransactionPhase.BackupCreated, UpdatedUtc = UtcAfter(journal.CreatedUtc) };
        if (!TryUpdate(backupCreated))
        {
            return await RollbackAsync(backupCreated, preflight.CurrentVersion!, invocation.TargetVersion, null,
                UpdaterResultCode.ReplacementFailed, launchOldApp: false, "backup-journal-write-failed", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            fileSystem.MoveDirectory(siblings.NewSiblingPath, identity.InstallRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return await RollbackAsync(backupCreated, preflight.CurrentVersion!, invocation.TargetVersion, null,
                UpdaterResultCode.ReplacementFailed, launchOldApp: false, "replacement-rename-failed", cancellationToken).ConfigureAwait(false);
        }

        var replacementInstalled = backupCreated with { Phase = UpdateTransactionPhase.ReplacementInstalled, UpdatedUtc = UtcAfter(backupCreated.UpdatedUtc) };
        if (!TryUpdate(replacementInstalled))
        {
            return await RollbackAsync(replacementInstalled, preflight.CurrentVersion!, invocation.TargetVersion, null,
                UpdaterResultCode.ReplacementFailed, launchOldApp: false, "replacement-journal-write-failed", cancellationToken).ConfigureAwait(false);
        }

        var appPath = Path.Combine(identity.InstallRoot, "CodexSlotPing.App.exe");
        var launched = await runtime.LaunchAppAsync(appPath, identity.InstallRoot, invocation.OperationId, rollback: false, cancellationToken).ConfigureAwait(false);
        if (!launched.Succeeded || launched.Identity is null)
        {
            return await RollbackAsync(replacementInstalled, preflight.CurrentVersion!, invocation.TargetVersion, null,
                UpdaterResultCode.StartupFailedRolledBack, launchOldApp: true, "new-app-launch-failed", cancellationToken).ConfigureAwait(false);
        }

        var appLaunched = replacementInstalled with
        {
            Phase = UpdateTransactionPhase.NewAppLaunched,
            UpdatedUtc = UtcAfter(replacementInstalled.UpdatedUtc),
            LaunchedAppIdentity = launched.Identity
        };
        if (!TryUpdate(appLaunched))
        {
            return await RollbackAsync(appLaunched, preflight.CurrentVersion!, invocation.TargetVersion, launched.Identity,
                UpdaterResultCode.StartupFailedRolledBack, launchOldApp: true, "launch-journal-write-failed", cancellationToken).ConfigureAwait(false);
        }

        var acknowledgement = await acknowledgementWaiter.WaitAsync(
            invocation.OperationId, identity.Key, invocation.TargetVersion, launched.Identity,
            StartupAcknowledgementTimeout, cancellationToken).ConfigureAwait(false);
        if (acknowledgement.Status != StartupAcknowledgementWaitStatus.Received ||
            !AppStartupAcknowledgementValidator.Matches(
                acknowledgement.Acknowledgement, invocation.OperationId, identity.Key, invocation.TargetVersion, launched.Identity))
        {
            return await RollbackAsync(appLaunched, preflight.CurrentVersion!, invocation.TargetVersion, launched.Identity,
                UpdaterResultCode.StartupFailedRolledBack, launchOldApp: true,
                acknowledgement.Status == StartupAcknowledgementWaitStatus.TimedOut ? "startup-ack-timeout" : "startup-ack-rejected",
                cancellationToken).ConfigureAwait(false);
        }

        var completed = appLaunched with { Phase = UpdateTransactionPhase.Completed, UpdatedUtc = UtcAfter(appLaunched.UpdatedUtc) };
        if (!TryUpdate(completed))
        {
            // The new App is healthy. Keep its process, backup, journal, and Intent so recovery can finish from the matching ack.
            return Result(UpdaterResultCode.RecoveryRequired, "completed-journal-write-failed");
        }

        TryDeleteValidatedPackage(siblings.BackupSiblingPath, preflight.CurrentVersion);
        TryDeleteStagingOperationDirectory(invocation, controlPaths);
        if (!ClearOwnedIntent(invocation.OperationId))
        {
            return Result(UpdaterResultCode.RecoveryRequired, "completed-intent-cleanup-failed");
        }
        return Result(UpdaterResultCode.Success, "update-completed");
    }

    private TransactionPreflight ValidateBeforeWaiting(
        UpdaterInvocation invocation,
        InstallIdentity identity,
        UpdateTransactionPathSet siblings)
    {
        var stageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(invocation.VerifiedExtractRoot));
        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updaterBaseDirectory));
        if (!PathsEqual(stageRoot, baseDirectory) || InstallRootProtection.IsProtected(identity.InstallRoot) ||
            IsSameOrBelow(identity.InstallRoot, stageRoot) || IsSameOrBelow(stageRoot, identity.InstallRoot))
        {
            return TransactionPreflight.Invalid("install-or-staging-root-invalid");
        }

        var current = PackageDirectoryValidator.ValidateDirectory(identity.InstallRoot);
        var staging = PackageDirectoryValidator.ValidateDirectory(stageRoot, invocation.TargetVersion);
        if (!current.IsValid || current.Version is null || !staging.IsValid || staging.Version is null)
        {
            return TransactionPreflight.Invalid("manifest-ownership-invalid");
        }

        if (!current.Version.IsStable || current.Version.CompareTo(MinimumAutomaticVersion) < 0 ||
            invocation.TargetVersion.CompareTo(MinimumAutomaticVersion) < 0 ||
            invocation.TargetVersion.CompareTo(current.Version) <= 0)
        {
            return TransactionPreflight.Invalid("version-transition-not-supported");
        }

        if (!Directory.Exists(siblings.ParentDirectory) ||
            !NoReparsePointsInDirectoryChain(siblings.ParentDirectory) ||
            !fileSystem.ProbeParentRename(siblings.ParentDirectory))
        {
            return TransactionPreflight.Invalid("install-parent-not-writable-or-renamable");
        }

        if (Exists(siblings.NewSiblingPath) || Exists(siblings.BackupSiblingPath) || Exists(siblings.FailedSiblingPath))
        {
            return TransactionPreflight.Invalid("transaction-sibling-collision");
        }

        return new TransactionPreflight(true, null, current.Version, staging);
    }

    private async Task<UpdateTransactionResult> RollbackAsync(
        UpdateTransactionJournal journal,
        SemVersion oldVersion,
        SemVersion targetVersion,
        ProcessIdentity? launchedApp,
        UpdaterResultCode resultAfterRollback,
        bool launchOldApp,
        string detail,
        CancellationToken cancellationToken)
    {
        var siblings = UpdateTransactionPaths.ForInstall(journal.InstallRoot, journal.OperationId);
        var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, journal.InstallRoot);
        if (launchedApp is not null)
        {
            await runtime.TerminateIfSameProcessAsync(launchedApp, cancellationToken).ConfigureAwait(false);
        }

        var rollbackStarted = journal with { Phase = UpdateTransactionPhase.RollbackStarted, UpdatedUtc = UtcAfter(journal.UpdatedUtc) };
        TryUpdate(rollbackStarted);

        var installExists = fileSystem.DirectoryExists(identity.InstallRoot);
        if (installExists)
        {
            if (!IsValidPackage(identity.InstallRoot, targetVersion) || Exists(siblings.FailedSiblingPath))
            {
                return await MarkRecoveryRequiredAsync(rollbackStarted, "new-install-cannot-be-safely-moved").ConfigureAwait(false);
            }
            try
            {
                fileSystem.MoveDirectory(identity.InstallRoot, siblings.FailedSiblingPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return await MarkRecoveryRequiredAsync(rollbackStarted, "new-install-rollback-move-failed").ConfigureAwait(false);
            }
        }

        if (!fileSystem.DirectoryExists(siblings.BackupSiblingPath) || !IsValidPackage(siblings.BackupSiblingPath, oldVersion))
        {
            return await MarkRecoveryRequiredAsync(rollbackStarted, "owned-backup-unavailable").ConfigureAwait(false);
        }
        try
        {
            fileSystem.MoveDirectory(siblings.BackupSiblingPath, identity.InstallRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return await MarkRecoveryRequiredAsync(rollbackStarted, "backup-restore-failed").ConfigureAwait(false);
        }

        if (!IsValidPackage(identity.InstallRoot, oldVersion))
        {
            return await MarkRecoveryRequiredAsync(rollbackStarted, "restored-install-validation-failed").ConfigureAwait(false);
        }

        var rolledBack = rollbackStarted with { Phase = UpdateTransactionPhase.RolledBack, UpdatedUtc = UtcAfter(rollbackStarted.UpdatedUtc) };
        if (!TryUpdate(rolledBack))
        {
            return Result(UpdaterResultCode.RecoveryRequired, "rollback-journal-write-failed");
        }

        if (launchOldApp)
        {
            var oldAppPath = Path.Combine(identity.InstallRoot, "CodexSlotPing.App.exe");
            await runtime.LaunchAppAsync(oldAppPath, identity.InstallRoot, journal.OperationId, rollback: true, cancellationToken).ConfigureAwait(false);
        }

        TryDeleteValidatedPackage(siblings.FailedSiblingPath, targetVersion);
        TryDeleteValidatedPackage(siblings.NewSiblingPath, targetVersion);
        if (!ClearOwnedIntent(journal.OperationId))
        {
            return Result(UpdaterResultCode.RecoveryRequired, "rollback-intent-cleanup-failed");
        }
        return Result(resultAfterRollback, detail);
    }

    private Task<UpdateTransactionResult> MarkRecoveryRequiredAsync(UpdateTransactionJournal journal, string detail)
    {
        TryUpdate(journal with { Phase = UpdateTransactionPhase.RecoveryRequired, UpdatedUtc = UtcAfter(journal.UpdatedUtc) });
        return Task.FromResult(Result(UpdaterResultCode.RecoveryRequired, detail));
    }

    private bool TryUpdate(UpdateTransactionJournal journal) =>
        journalStore.TryUpdate(journal) == UpdateTransactionJournalWriteStatus.Updated;

    private bool ClearOwnedIntent(Guid operationId)
    {
        try { return intentStore.TryDelete(operationId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool IsValidPackage(string root, SemVersion? version)
    {
        if (!fileSystem.DirectoryExists(root) || version is null)
        {
            return false;
        }
        return PackageDirectoryValidator.ValidateDirectory(root, version).IsValid;
    }

    private bool TryDeleteValidatedPackage(string root, SemVersion? version)
    {
        if (!fileSystem.DirectoryExists(root) || !IsValidPackage(root, version))
        {
            return false;
        }
        try
        {
            fileSystem.DeleteDirectory(root);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryDeleteStagingOperationDirectory(UpdaterInvocation invocation, UpdateControlPaths paths)
    {
        try
        {
            var extractRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(invocation.VerifiedExtractRoot));
            var operationDirectory = Path.GetDirectoryName(extractRoot);
            var versionDirectory = operationDirectory is null ? null : Path.GetDirectoryName(operationDirectory);
            var updatesRoot = versionDirectory is null ? null : Path.GetDirectoryName(versionDirectory);
            var productRoot = updatesRoot is null ? null : Path.GetDirectoryName(updatesRoot);
            if (operationDirectory is null || versionDirectory is null || updatesRoot is null || productRoot is null ||
                !string.Equals(Path.GetFileName(extractRoot), "extract", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(operationDirectory), invocation.OperationId.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(versionDirectory), invocation.TargetVersion.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !PathsEqual(productRoot, Path.GetDirectoryName(Path.GetDirectoryName(paths.DirectoryPath))!) ||
                !PathsEqual(updatesRoot, Path.Combine(productRoot, "updates")) ||
                !DirectoryTreeHasNoReparsePoints(operationDirectory))
            {
                return;
            }

            var entries = Directory.EnumerateFileSystemEntries(operationDirectory).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (entries.SetEquals(["extract", "package.zip", "package.zip.sha256"]))
            {
                fileSystem.DeleteDirectory(operationDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The Updater may still have loaded files from this staging directory; later cleanup can remove it.
        }
    }

    private bool Exists(string path) => fileSystem.DirectoryExists(path) || fileSystem.FileExists(path);

    private static bool IsInvocationWellFormed(UpdaterInvocation? invocation) =>
        invocation is not null && invocation.OperationId != Guid.Empty && invocation.TargetVersion is not null &&
        invocation.TargetVersion.IsStable && IsAbsolute(invocation.InstallRoot) && IsAbsolute(invocation.VerifiedExtractRoot) &&
        invocation.AppIdentity is not null && invocation.AppIdentity.ProcessId > 0 && invocation.AppIdentity.StartedUtc.Offset == TimeSpan.Zero;

    private static bool NoReparsePointsInDirectoryChain(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || PathsEqual(parent, current)) break;
            current = parent;
        }
        return true;
    }

    private static bool DirectoryTreeHasNoReparsePoints(string root)
    {
        if (!NoReparsePointsInDirectoryChain(root)) return false;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(pending.Pop()).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return true;
    }

    private static bool IsSameOrBelow(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "." || (!Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static ProcessIdentity ReadCurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }

    private static DateTimeOffset UtcAfter(DateTimeOffset value)
    {
        var now = DateTimeOffset.UtcNow;
        return now > value ? now : value.AddTicks(1);
    }

    private static UpdateTransactionResult Result(UpdaterResultCode code, string detail) => new(code, detail);

    private sealed record TransactionPreflight(
        bool IsValid,
        string? Error,
        SemVersion? CurrentVersion,
        PackageDirectoryValidationResult? StagingPackage)
    {
        public static TransactionPreflight Invalid(string error) => new(false, error, null, null);
    }
}
