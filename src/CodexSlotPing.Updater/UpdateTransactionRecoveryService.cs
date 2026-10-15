using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal sealed class UpdateTransactionRecoveryService(
    UpdateControlPaths controlPaths,
    IUpdateIntentLifecycle intentStore,
    IUpdateTransactionJournalStore journalStore,
    IAppStartupAcknowledgementStore acknowledgementStore,
    IUpdateTransactionRuntime runtime,
    IUpdateTransactionFileSystem fileSystem)
{
    private static readonly SemVersion MinimumAutomaticVersion = SemVersion.Parse("0.3.0");
    private static readonly TimeSpan AppExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunnerDrainTimeout = TimeSpan.FromSeconds(120);

    public async Task<UpdateTransactionResult> RecoverAsync(
        UpdaterInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (invocation is null || invocation.OperationId == Guid.Empty || invocation.TargetVersion is null ||
            !invocation.TargetVersion.IsStable || !IsAbsolute(invocation.InstallRoot) ||
            !IsAbsolute(invocation.VerifiedExtractRoot) || invocation.AppIdentity.ProcessId <= 0 ||
            invocation.AppIdentity.StartedUtc.Offset != TimeSpan.Zero)
        {
            return Result(UpdaterResultCode.InvalidArguments, "recovery-invocation-invalid");
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
            return Result(UpdaterResultCode.InvalidArguments, "recovery-install-root-invalid");
        }
        if (!string.Equals(identity.Key, controlPaths.InstallIdentity, StringComparison.Ordinal))
        {
            return Result(UpdaterResultCode.IntentMismatch, "recovery-control-owner-mismatch");
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

        var snapshotFailure = ReadRecoverySnapshot(invocation, identity, out _);
        if (snapshotFailure is not null) return snapshotFailure;

        if (InstallRootProtection.IsProtected(identity.InstallRoot) || !NoReparsePointsInDirectoryChain(siblings.ParentDirectory) ||
            !fileSystem.ProbeParentRename(siblings.ParentDirectory))
        {
            return Result(UpdaterResultCode.RecoveryRequired, "recovery-parent-preflight-failed");
        }

        var appWait = await runtime.WaitForAppExitAsync(invocation.AppIdentity, AppExitTimeout, cancellationToken).ConfigureAwait(false);
        if (appWait != AppProcessWaitStatus.Exited)
        {
            return Result(UpdaterResultCode.AppExitTimeout, appWait == AppProcessWaitStatus.Unknown ? "recovery-app-identity-unavailable" : "recovery-app-exit-timeout");
        }

        var drain = await runtime.WaitForRunnerDrainAsync(controlPaths, RunnerDrainTimeout, cancellationToken).ConfigureAwait(false);
        if (drain.Status != RunnerDrainWaitStatus.Acquired || drain.Lease is null)
        {
            return Result(
                drain.Status == RunnerDrainWaitStatus.TimedOut ? UpdaterResultCode.RunnerDrainTimeout : UpdaterResultCode.RecoveryRequired,
                drain.Status == RunnerDrainWaitStatus.TimedOut ? "recovery-runner-drain-timeout" : "recovery-lease-failed");
        }

        using (drain.Lease)
        {
            var latestSnapshotFailure = ReadRecoverySnapshot(invocation, identity, out var latestJournal);
            if (latestSnapshotFailure is not null) return latestSnapshotFailure;
            return await RecoverWithLeaseAsync(invocation, identity, siblings, latestJournal!, cancellationToken).ConfigureAwait(false);
        }
    }

    private UpdateTransactionResult? ReadRecoverySnapshot(
        UpdaterInvocation invocation,
        InstallIdentity identity,
        out UpdateTransactionJournal? journal)
    {
        journal = null;
        var journalRead = journalStore.Read(invocation.OperationId);
        if (journalRead.Status != UpdateTransactionJournalReadStatus.Active || journalRead.Journal is null)
        {
            return Result(UpdaterResultCode.JournalFailure, "recovery-journal-unavailable");
        }
        journal = journalRead.Journal;
        if (journal.OperationId != invocation.OperationId ||
            !string.Equals(journal.InstallIdentity, identity.Key, StringComparison.Ordinal) ||
            !PathsEqual(journal.InstallRoot, identity.InstallRoot) ||
            !PathsEqual(journal.VerifiedExtractRoot, invocation.VerifiedExtractRoot) ||
            !string.Equals(journal.TargetVersion, invocation.TargetVersion.ToString(), StringComparison.Ordinal))
        {
            return Result(UpdaterResultCode.JournalFailure, "recovery-journal-invocation-mismatch");
        }

        var intentRead = intentStore.Read();
        var terminal = journal.Phase is UpdateTransactionPhase.Completed or UpdateTransactionPhase.RolledBack;
        if (intentRead.Status == UpdateIntentReadStatus.Active)
        {
            if (intentRead.Intent?.OperationId != invocation.OperationId ||
                !string.Equals(intentRead.Intent.InstallIdentity, identity.Key, StringComparison.Ordinal))
            {
                return Result(UpdaterResultCode.IntentMismatch, "recovery-intent-operation-mismatch");
            }
        }
        else if (!(terminal && intentRead.Status == UpdateIntentReadStatus.Missing))
        {
            return Result(UpdaterResultCode.IntentMismatch, "recovery-intent-invalid");
        }

        if (journal.Phase == UpdateTransactionPhase.RecoveryRequired)
        {
            return Result(UpdaterResultCode.RecoveryRequired, "journal-requires-manual-recovery");
        }
        return null;
    }

    private async Task<UpdateTransactionResult> RecoverWithLeaseAsync(
        UpdaterInvocation invocation,
        InstallIdentity identity,
        UpdateTransactionPathSet siblings,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var target = invocation.TargetVersion;
        var current = Inspect(identity.InstallRoot, target);
        var backup = Inspect(siblings.BackupSiblingPath, target);
        var newSibling = Inspect(siblings.NewSiblingPath, target);
        var failedSibling = Inspect(siblings.FailedSiblingPath, target);

        if (journal.Phase == UpdateTransactionPhase.Completed)
        {
            if (current.State != PackageState.Target)
            {
                return await MarkRecoveryRequiredAsync(journal, "recovery-completed-install-invalid").ConfigureAwait(false);
            }
            return await CompleteRecoveredAsync(journal, identity, siblings, backup.Version, target, cancellationToken).ConfigureAwait(false);
        }

        if (current.State == PackageState.Invalid || backup.State is PackageState.Target or PackageState.Invalid ||
            newSibling.State is PackageState.Old or PackageState.Invalid || failedSibling.State is PackageState.Old or PackageState.Invalid)
        {
            return await MarkRecoveryRequiredAsync(journal, "recovery-package-ownership-invalid").ConfigureAwait(false);
        }

        var oldVersion = backup.State == PackageState.Old ? backup.Version :
            current.State == PackageState.Old ? current.Version : null;
        if (oldVersion is not null && oldVersion.CompareTo(MinimumAutomaticVersion) < 0)
        {
            return await MarkRecoveryRequiredAsync(journal, "recovery-legacy-install-not-supported").ConfigureAwait(false);
        }

        switch (journal.Phase)
        {
            case UpdateTransactionPhase.Prepared:
                if (current.State == PackageState.Old && backup.State == PackageState.Missing)
                {
                    return await FinishNoMutationRollbackAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                if ((current.State == PackageState.Missing && backup.State == PackageState.Old) ||
                    (current.State == PackageState.Target && backup.State == PackageState.Old))
                {
                    return await RollbackToOldAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                break;

            case UpdateTransactionPhase.BackupCreated:
                if (current.State == PackageState.Old && backup.State == PackageState.Missing)
                {
                    return await FinishNoMutationRollbackAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                if ((current.State == PackageState.Missing || current.State == PackageState.Target) && backup.State == PackageState.Old)
                {
                    return await RollbackToOldAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                break;

            case UpdateTransactionPhase.ReplacementInstalled:
                if (current.State == PackageState.Target && backup.State == PackageState.Old && HasMatchingAcknowledgement(journal, target))
                {
                    return await CompleteRecoveredAsync(journal, identity, siblings, oldVersion, target, cancellationToken).ConfigureAwait(false);
                }
                if ((current.State == PackageState.Target || current.State == PackageState.Missing) && backup.State == PackageState.Old)
                {
                    return await RollbackToOldAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                if (current.State == PackageState.Old && backup.State == PackageState.Missing)
                {
                    return await FinishNoMutationRollbackAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                break;

            case UpdateTransactionPhase.NewAppLaunched:
                if (current.State == PackageState.Target && backup.State == PackageState.Old && HasMatchingAcknowledgement(journal, target))
                {
                    return await CompleteRecoveredAsync(journal, identity, siblings, oldVersion, target, cancellationToken).ConfigureAwait(false);
                }
                if ((current.State == PackageState.Target || current.State == PackageState.Missing) && backup.State == PackageState.Old)
                {
                    return await RollbackToOldAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                if (current.State == PackageState.Old && backup.State == PackageState.Missing)
                {
                    return await FinishNoMutationRollbackAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                break;

            case UpdateTransactionPhase.RollbackStarted:
                if (current.State == PackageState.Old && backup.State == PackageState.Missing)
                {
                    return await FinishNoMutationRollbackAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                if ((current.State == PackageState.Target || current.State == PackageState.Missing) && backup.State == PackageState.Old)
                {
                    return await RollbackToOldAsync(journal, identity, siblings, oldVersion!, target, cancellationToken).ConfigureAwait(false);
                }
                break;

            case UpdateTransactionPhase.RolledBack:
                if (current.State == PackageState.Old && backup.State == PackageState.Missing)
                {
                    CleanupTargetArtifacts(siblings, newSibling, failedSibling, target);
                    return ClearIntentOrRecovery(journal.OperationId, UpdaterResultCode.StartupFailedRolledBack, "recovery-rollback-already-complete");
                }
                break;
        }

        return await MarkRecoveryRequiredAsync(journal, "recovery-filesystem-state-ambiguous").ConfigureAwait(false);
    }

    private async Task<UpdateTransactionResult> FinishNoMutationRollbackAsync(
        UpdateTransactionJournal journal,
        InstallIdentity identity,
        UpdateTransactionPathSet siblings,
        SemVersion oldVersion,
        SemVersion target,
        CancellationToken cancellationToken)
    {
        if (journal.Phase != UpdateTransactionPhase.RollbackStarted)
        {
            var rollbackStarted = journal with { Phase = UpdateTransactionPhase.RollbackStarted, UpdatedUtc = UtcAfter(journal.UpdatedUtc) };
            if (!TryUpdate(rollbackStarted)) return Result(UpdaterResultCode.RecoveryRequired, "recovery-rollback-journal-failed");
            journal = rollbackStarted;
        }

        return await RollbackToOldAsync(journal, identity, siblings, oldVersion, target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateTransactionResult> RollbackToOldAsync(
        UpdateTransactionJournal journal,
        InstallIdentity identity,
        UpdateTransactionPathSet siblings,
        SemVersion oldVersion,
        SemVersion target,
        CancellationToken cancellationToken)
    {
        if (journal.Phase != UpdateTransactionPhase.RollbackStarted)
        {
            var rollbackStarted = journal with { Phase = UpdateTransactionPhase.RollbackStarted, UpdatedUtc = UtcAfter(journal.UpdatedUtc) };
            if (!TryUpdate(rollbackStarted)) return Result(UpdaterResultCode.RecoveryRequired, "recovery-rollback-journal-failed");
            journal = rollbackStarted;
        }

        if (journal.LaunchedAppIdentity is not null)
        {
            await runtime.TerminateIfSameProcessAsync(journal.LaunchedAppIdentity, cancellationToken).ConfigureAwait(false);
        }

        var current = Inspect(identity.InstallRoot, target);
        var backup = Inspect(siblings.BackupSiblingPath, target);
        if (current.State == PackageState.Target)
        {
            if (backup.State != PackageState.Old || fileSystem.DirectoryExists(siblings.FailedSiblingPath))
            {
                return await MarkRecoveryRequiredAsync(journal, "recovery-rollback-source-invalid").ConfigureAwait(false);
            }
            try
            {
                fileSystem.MoveDirectory(identity.InstallRoot, siblings.FailedSiblingPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return await MarkRecoveryRequiredAsync(journal, "recovery-new-install-move-failed").ConfigureAwait(false);
            }
        }
        else if (current.State != PackageState.Missing && current.State != PackageState.Old)
        {
            return await MarkRecoveryRequiredAsync(journal, "recovery-current-install-invalid").ConfigureAwait(false);
        }

        backup = Inspect(siblings.BackupSiblingPath, target);
        if (current.State == PackageState.Old)
        {
            if (backup.State != PackageState.Missing)
            {
                return await MarkRecoveryRequiredAsync(journal, "recovery-duplicate-old-install").ConfigureAwait(false);
            }
        }
        else
        {
            if (backup.State != PackageState.Old)
            {
                return await MarkRecoveryRequiredAsync(journal, "recovery-backup-invalid").ConfigureAwait(false);
            }
            try
            {
                fileSystem.MoveDirectory(siblings.BackupSiblingPath, identity.InstallRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return await MarkRecoveryRequiredAsync(journal, "recovery-backup-restore-failed").ConfigureAwait(false);
            }
        }

        if (!PackageDirectoryValidator.ValidateDirectory(identity.InstallRoot, oldVersion).IsValid)
        {
            return await MarkRecoveryRequiredAsync(journal, "recovery-restored-install-invalid").ConfigureAwait(false);
        }
        var rolledBack = journal with { Phase = UpdateTransactionPhase.RolledBack, UpdatedUtc = UtcAfter(journal.UpdatedUtc) };
        if (!TryUpdate(rolledBack)) return Result(UpdaterResultCode.RecoveryRequired, "recovery-rolled-back-journal-failed");

        await runtime.LaunchAppAsync(Path.Combine(identity.InstallRoot, "CodexSlotPing.App.exe"), identity.InstallRoot, null, cancellationToken).ConfigureAwait(false);
        var (newSibling, failedSibling) = (Inspect(siblings.NewSiblingPath, target), Inspect(siblings.FailedSiblingPath, target));
        CleanupTargetArtifacts(siblings, newSibling, failedSibling, target);
        return ClearIntentOrRecovery(journal.OperationId, UpdaterResultCode.StartupFailedRolledBack, "recovery-restored-old-install");
    }

    private async Task<UpdateTransactionResult> CompleteRecoveredAsync(
        UpdateTransactionJournal journal,
        InstallIdentity identity,
        UpdateTransactionPathSet siblings,
        SemVersion? oldVersion,
        SemVersion target,
        CancellationToken cancellationToken)
    {
        if (journal.Phase != UpdateTransactionPhase.Completed)
        {
            var completed = journal with { Phase = UpdateTransactionPhase.Completed, UpdatedUtc = UtcAfter(journal.UpdatedUtc) };
            if (!TryUpdate(completed)) return Result(UpdaterResultCode.RecoveryRequired, "recovery-completed-journal-failed");
            journal = completed;
        }

        var backup = Inspect(siblings.BackupSiblingPath, target);
        if (backup.State == PackageState.Old && oldVersion is not null && backup.Version?.ToString() == oldVersion.ToString())
        {
            TryDeletePackage(siblings.BackupSiblingPath, oldVersion);
        }
        else if (backup.State is PackageState.Target or PackageState.Invalid)
        {
            var remainingNewSibling = Inspect(siblings.NewSiblingPath, target);
            var remainingFailedSibling = Inspect(siblings.FailedSiblingPath, target);
            CleanupTargetArtifacts(siblings, remainingNewSibling, remainingFailedSibling, target);
            return ClearIntentOrRecovery(journal.OperationId, UpdaterResultCode.Success, "recovery-completed-install-backup-cleanup-deferred");
        }

        var newSibling = Inspect(siblings.NewSiblingPath, target);
        var failedSibling = Inspect(siblings.FailedSiblingPath, target);
        CleanupTargetArtifacts(siblings, newSibling, failedSibling, target);
        return ClearIntentOrRecovery(journal.OperationId, UpdaterResultCode.Success, "recovery-completed-install");
    }

    private bool HasMatchingAcknowledgement(UpdateTransactionJournal journal, SemVersion target)
    {
        if (journal.LaunchedAppIdentity is null) return false;
        var read = acknowledgementStore.Read(journal.OperationId);
        return read.Status == AppStartupAcknowledgementReadStatus.Active &&
            AppStartupAcknowledgementValidator.Matches(
                read.Acknowledgement, journal.OperationId, journal.InstallIdentity, target, journal.LaunchedAppIdentity);
    }

    private (PackageState State, SemVersion? Version) Inspect(string path, SemVersion target)
    {
        if (!fileSystem.DirectoryExists(path))
        {
            return fileSystem.FileExists(path) ? (PackageState.Invalid, null) : (PackageState.Missing, null);
        }
        var package = PackageDirectoryValidator.ValidateDirectory(path);
        if (!package.IsValid || package.Version is null)
        {
            return (PackageState.Invalid, null);
        }
        if (string.Equals(package.Version.ToString(), target.ToString(), StringComparison.Ordinal))
        {
            return (PackageState.Target, package.Version);
        }
        if (package.Version.IsStable && package.Version.CompareTo(MinimumAutomaticVersion) >= 0 && package.Version.CompareTo(target) < 0)
        {
            return (PackageState.Old, package.Version);
        }
        return (PackageState.Invalid, package.Version);
    }

    private void CleanupTargetArtifacts(
        UpdateTransactionPathSet siblings,
        (PackageState State, SemVersion? Version) newSibling,
        (PackageState State, SemVersion? Version) failedSibling,
        SemVersion target)
    {
        if (newSibling.State == PackageState.Target) TryDeletePackage(siblings.NewSiblingPath, target);
        if (failedSibling.State == PackageState.Target) TryDeletePackage(siblings.FailedSiblingPath, target);
    }

    private bool TryDeletePackage(string path, SemVersion version)
    {
        try
        {
            if (!PackageDirectoryValidator.ValidateDirectory(path, version).IsValid) return false;
            fileSystem.DeleteDirectory(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private UpdateTransactionResult ClearIntentOrRecovery(Guid operationId, UpdaterResultCode successCode, string detail)
    {
        var current = intentStore.Read();
        if (current.Status == UpdateIntentReadStatus.Missing) return Result(successCode, detail);
        return current.Status == UpdateIntentReadStatus.Active && current.Intent?.OperationId == operationId && intentStore.TryDelete(operationId)
            ? Result(successCode, detail)
            : Result(UpdaterResultCode.RecoveryRequired, "recovery-intent-cleanup-failed");
    }

    private async Task<UpdateTransactionResult> MarkRecoveryRequiredAsync(UpdateTransactionJournal journal, string detail)
    {
        if (journal.Phase != UpdateTransactionPhase.RecoveryRequired)
        {
            TryUpdate(journal with { Phase = UpdateTransactionPhase.RecoveryRequired, UpdatedUtc = UtcAfter(journal.UpdatedUtc) });
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return Result(UpdaterResultCode.RecoveryRequired, detail);
    }

    private bool TryUpdate(UpdateTransactionJournal journal) =>
        journalStore.TryUpdate(journal) == UpdateTransactionJournalWriteStatus.Updated;

    private static bool NoReparsePointsInDirectoryChain(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || PathsEqual(parent, current)) break;
            current = parent;
        }
        return true;
    }

    private static bool IsAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset UtcAfter(DateTimeOffset value)
    {
        var now = DateTimeOffset.UtcNow;
        return now > value ? now : value.AddTicks(1);
    }

    private static UpdateTransactionResult Result(UpdaterResultCode code, string detail) => new(code, detail);

    private enum PackageState { Missing, Old, Target, Invalid }
}
