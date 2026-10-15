using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal sealed class UpdaterCommandRunner(
    IUpdateTransactionJournalStore journalStore,
    UpdateTransactionService transactionService,
    UpdateTransactionRecoveryService recoveryService)
{
    public Task<UpdateTransactionResult> RunAsync(
        UpdaterInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var journal = journalStore.Read(invocation.OperationId);
        return journal.Status switch
        {
            UpdateTransactionJournalReadStatus.Missing => transactionService.ExecuteAsync(invocation, cancellationToken),
            UpdateTransactionJournalReadStatus.Active => recoveryService.RecoverAsync(invocation, cancellationToken),
            _ => Task.FromResult(new UpdateTransactionResult(UpdaterResultCode.JournalFailure, "transaction-journal-unavailable"))
        };
    }
}
