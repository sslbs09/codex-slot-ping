using CodexSlotPing.Update;

namespace CodexSlotPing.Runner;

public enum RunnerUpdateGateBlockReason
{
    IntentPresentOrInvalid,
    InstallLeaseBusy,
    InfrastructureFailure
}

public sealed class RunnerUpdateAdmission : IDisposable
{
    private RunnerUpdateLease? _lease;

    private RunnerUpdateAdmission(RunnerUpdateLease? lease, RunnerUpdateGateBlockReason? blockReason)
    {
        _lease = lease;
        BlockReason = blockReason;
    }

    public bool IsAdmitted => _lease is not null;
    public RunnerUpdateGateBlockReason? BlockReason { get; }

    public static RunnerUpdateAdmission Admitted(RunnerUpdateLease lease) =>
        new(lease ?? throw new ArgumentNullException(nameof(lease)), null);

    public static RunnerUpdateAdmission Blocked(RunnerUpdateGateBlockReason reason) => new(null, reason);

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

public static class RunnerUpdateGate
{
    public static RunnerUpdateAdmission TryEnterForCurrentInstall()
    {
        try
        {
            var identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, AppContext.BaseDirectory);
            var paths = UpdateControlPaths.ForCurrentUser(identity);
            return TryEnter(paths, new UpdateIntentStore(paths));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return RunnerUpdateAdmission.Blocked(RunnerUpdateGateBlockReason.InfrastructureFailure);
        }
    }

    public static RunnerUpdateAdmission TryEnter(UpdateControlPaths paths, IUpdateIntentReader intentReader)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(intentReader);

        RunnerUpdateLease? lease = null;
        try
        {
            if (intentReader.Read().Status != UpdateIntentReadStatus.Missing)
            {
                return RunnerUpdateAdmission.Blocked(RunnerUpdateGateBlockReason.IntentPresentOrInvalid);
            }

            var leaseStatus = RunnerUpdateLease.TryAcquire(paths, out var acquiredLease);
            if (leaseStatus != InstallLeaseAcquireStatus.Acquired)
            {
                return RunnerUpdateAdmission.Blocked(RunnerUpdateGateBlockReason.InstallLeaseBusy);
            }
            if (acquiredLease is null)
            {
                return RunnerUpdateAdmission.Blocked(RunnerUpdateGateBlockReason.InfrastructureFailure);
            }
            lease = acquiredLease;

            var secondRead = intentReader.Read();
            if (secondRead.Status != UpdateIntentReadStatus.Missing)
            {
                lease.Dispose();
                lease = null;
                return RunnerUpdateAdmission.Blocked(RunnerUpdateGateBlockReason.IntentPresentOrInvalid);
            }

            var admission = RunnerUpdateAdmission.Admitted(lease);
            lease = null;
            return admission;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            lease?.Dispose();
            return RunnerUpdateAdmission.Blocked(RunnerUpdateGateBlockReason.InfrastructureFailure);
        }
    }
}
