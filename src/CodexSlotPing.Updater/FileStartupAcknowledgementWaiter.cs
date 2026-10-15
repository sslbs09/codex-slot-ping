using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal sealed class FileStartupAcknowledgementWaiter(IAppStartupAcknowledgementStore store) : IStartupAcknowledgementWaiter
{
    public async Task<StartupAcknowledgementWaitResult> WaitAsync(
        Guid operationId,
        string installIdentity,
        SemVersion targetVersion,
        ProcessIdentity launchedAppIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var read = store.Read(operationId);
            if (read.Status == AppStartupAcknowledgementReadStatus.Active && read.Acknowledgement is not null)
            {
                return AppStartupAcknowledgementValidator.Matches(
                    read.Acknowledgement, operationId, installIdentity, targetVersion, launchedAppIdentity)
                    ? StartupAcknowledgementWaitResult.Received(read.Acknowledgement)
                    : StartupAcknowledgementWaitResult.Rejected();
            }
            if (read.Status == AppStartupAcknowledgementReadStatus.Invalid)
            {
                return StartupAcknowledgementWaitResult.Rejected();
            }
            if (read.Status == AppStartupAcknowledgementReadStatus.IoError)
            {
                return StartupAcknowledgementWaitResult.StorageFailure();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        return StartupAcknowledgementWaitResult.TimedOut();
    }
}
