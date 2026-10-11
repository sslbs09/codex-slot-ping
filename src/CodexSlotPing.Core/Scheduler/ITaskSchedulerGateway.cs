namespace CodexSlotPing.Core.Scheduler;

public interface ITaskSchedulerGateway
{
    Task<SchedulerStatus> ReadStatusAsync(
        string taskPath,
        string currentRunnerPath,
        string configuredTimeZoneId,
        CancellationToken cancellationToken);

    Task<SchedulerOperationResult> InstallOrUpdateAsync(
        SchedulerTaskDefinition definition,
        CancellationToken cancellationToken);

    Task<SchedulerOperationResult> DisableAsync(string taskPath, CancellationToken cancellationToken);

    Task<SchedulerOperationResult> RemoveAsync(string taskPath, CancellationToken cancellationToken);
}
