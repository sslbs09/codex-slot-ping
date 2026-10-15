using System.ComponentModel;
using System.Diagnostics;

namespace CodexSlotPing.Update;

public sealed record ProcessIdentity(int ProcessId, DateTimeOffset StartedUtc);

public enum UpdaterProcessMatch
{
    Matching,
    NotMatching,
    Unknown
}

public interface IProcessIdentityProbe
{
    UpdaterProcessMatch Match(UpdateIntent intent);
}

public sealed class SystemProcessIdentityProbe : IProcessIdentityProbe
{
    public UpdaterProcessMatch Match(UpdateIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        try
        {
            using var process = Process.GetProcessById(intent.UpdaterPid);
            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime());
            return ProcessIdentityMatcher.Matches(intent, identity)
                ? UpdaterProcessMatch.Matching
                : UpdaterProcessMatch.NotMatching;
        }
        catch (ArgumentException)
        {
            return UpdaterProcessMatch.NotMatching;
        }
        catch (InvalidOperationException)
        {
            return UpdaterProcessMatch.NotMatching;
        }
        catch (Win32Exception)
        {
            return UpdaterProcessMatch.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return UpdaterProcessMatch.Unknown;
        }
    }
}

public static class ProcessIdentityMatcher
{
    public static bool Matches(UpdateIntent intent, ProcessIdentity? processIdentity) =>
        processIdentity is not null &&
        processIdentity.ProcessId == intent.UpdaterPid &&
        processIdentity.StartedUtc == intent.UpdaterStartedUtc;
}

public enum TransactionJournalStatus
{
    Missing,
    Incomplete,
    Completed,
    RolledBack,
    Invalid
}

public enum UpdateIntentRecoveryAction
{
    NoIntent,
    KeepActive,
    ClearStaleIntent,
    RecoverIncompleteTransaction,
    CleanupTerminalTransaction,
    InvalidFailClosed
}

public static class UpdateIntentRecoveryEvaluator
{
    public static UpdateIntentRecoveryAction Evaluate(
        UpdateIntentReadResult intentRead,
        UpdaterProcessMatch updaterProcess,
        TransactionJournalStatus journalStatus)
    {
        ArgumentNullException.ThrowIfNull(intentRead);
        if (intentRead.Status == UpdateIntentReadStatus.Missing)
        {
            return UpdateIntentRecoveryAction.NoIntent;
        }
        if (intentRead.Status != UpdateIntentReadStatus.Active || intentRead.Intent is null)
        {
            return UpdateIntentRecoveryAction.InvalidFailClosed;
        }
        if (updaterProcess == UpdaterProcessMatch.Matching)
        {
            return UpdateIntentRecoveryAction.KeepActive;
        }
        if (updaterProcess == UpdaterProcessMatch.Unknown || journalStatus == TransactionJournalStatus.Invalid)
        {
            return UpdateIntentRecoveryAction.InvalidFailClosed;
        }

        return journalStatus switch
        {
            TransactionJournalStatus.Missing => UpdateIntentRecoveryAction.ClearStaleIntent,
            TransactionJournalStatus.Incomplete => UpdateIntentRecoveryAction.RecoverIncompleteTransaction,
            TransactionJournalStatus.Completed or TransactionJournalStatus.RolledBack =>
                UpdateIntentRecoveryAction.CleanupTerminalTransaction,
            _ => UpdateIntentRecoveryAction.InvalidFailClosed
        };
    }
}
