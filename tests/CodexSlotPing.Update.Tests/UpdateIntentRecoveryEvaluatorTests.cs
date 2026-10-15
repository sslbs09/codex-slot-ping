using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class UpdateIntentRecoveryEvaluatorTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 15, 4, 5, 6, TimeSpan.Zero);
    private static readonly UpdateIntent Intent = new(
        1, "CodexSlotPing", new string('A', 64), Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
        4321, StartedAt, StartedAt.AddMinutes(1));

    [Fact]
    public void Evaluate_KeepActiveWhenPidAndStartTimeMatch()
    {
        var action = UpdateIntentRecoveryEvaluator.Evaluate(
            UpdateIntentReadResult.Active(Intent),
            ProcessIdentityMatcher.Matches(Intent, new ProcessIdentity(Intent.UpdaterPid, StartedAt))
                ? UpdaterProcessMatch.Matching
                : UpdaterProcessMatch.NotMatching,
            TransactionJournalStatus.Incomplete);

        Assert.Equal(UpdateIntentRecoveryAction.KeepActive, action);
    }

    [Fact]
    public void Evaluate_TreatsSamePidWithDifferentStartTimeAsPidReuse()
    {
        var action = UpdateIntentRecoveryEvaluator.Evaluate(
            UpdateIntentReadResult.Active(Intent),
            ProcessIdentityMatcher.Matches(Intent, new ProcessIdentity(Intent.UpdaterPid, StartedAt.AddSeconds(1)))
                ? UpdaterProcessMatch.Matching
                : UpdaterProcessMatch.NotMatching,
            TransactionJournalStatus.Missing);

        Assert.Equal(UpdateIntentRecoveryAction.ClearStaleIntent, action);
    }

    [Theory]
    [InlineData(TransactionJournalStatus.Missing, UpdateIntentRecoveryAction.ClearStaleIntent)]
    [InlineData(TransactionJournalStatus.Incomplete, UpdateIntentRecoveryAction.RecoverIncompleteTransaction)]
    [InlineData(TransactionJournalStatus.Completed, UpdateIntentRecoveryAction.CleanupTerminalTransaction)]
    [InlineData(TransactionJournalStatus.RolledBack, UpdateIntentRecoveryAction.CleanupTerminalTransaction)]
    [InlineData(TransactionJournalStatus.Invalid, UpdateIntentRecoveryAction.InvalidFailClosed)]
    public void Evaluate_ClassifiesDeadUpdaterByJournalState(
        TransactionJournalStatus journalStatus,
        UpdateIntentRecoveryAction expected)
    {
        var action = UpdateIntentRecoveryEvaluator.Evaluate(
            UpdateIntentReadResult.Active(Intent),
            updaterProcess: UpdaterProcessMatch.NotMatching,
            journalStatus);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void Evaluate_FailsClosedForMalformedIntent()
    {
        var action = UpdateIntentRecoveryEvaluator.Evaluate(
            UpdateIntentReadResult.Invalid("bad schema"),
            updaterProcess: UpdaterProcessMatch.NotMatching,
            TransactionJournalStatus.Missing);

        Assert.Equal(UpdateIntentRecoveryAction.InvalidFailClosed, action);
    }

    [Fact]
    public void Evaluate_IncompleteDeadUpdaterClassifiesRecoveryWithoutDeletingIntent()
    {
        using var temp = new TemporaryDirectory();
        var identity = InstallIdentity.Create("CodexSlotPing", Path.Combine(temp.Path, "portable-install"));
        var paths = UpdateControlPaths.ForInstall(Path.Combine(temp.Path, "local-app-data"), identity);
        var store = new UpdateIntentStore(paths);
        var intent = Intent with { InstallIdentity = identity.Key };
        Assert.Equal(UpdateIntentCreateStatus.Created, store.Create(intent));

        var action = UpdateIntentRecoveryEvaluator.Evaluate(
            store.Read(),
            UpdaterProcessMatch.NotMatching,
            TransactionJournalStatus.Incomplete);

        Assert.Equal(UpdateIntentRecoveryAction.RecoverIncompleteTransaction, action);
        Assert.Equal(UpdateIntentReadStatus.Active, store.Read().Status);
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
