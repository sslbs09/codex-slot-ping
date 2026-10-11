namespace CodexSlotPing.Core.Scheduler;

public sealed record SchedulerTriggerReadback(
    int TriggerType,
    bool IsDaily,
    bool Enabled,
    int DaysInterval,
    string StartBoundary);

public sealed record SchedulerTaskReadback(
    IReadOnlyList<SchedulerTriggerReadback> Triggers,
    bool StartWhenAvailable,
    string MultipleInstancesPolicy,
    string ExecutionTimeLimit,
    int RestartCount,
    string? RestartInterval,
    bool WakeToRun,
    string? PrincipalUserId,
    int LogonType,
    int RunLevel,
    int ActionType,
    string? ActionPath,
    string? ActionArguments,
    string? WorkingDirectory,
    int ActionCount = 1);

public sealed record SchedulerStatus(
    string TaskPath,
    SchedulerState State,
    bool Exists,
    bool IsOwned,
    bool IsEnabled,
    string? Description,
    SchedulerTaskReadback? Readback,
    IReadOnlyList<string> ContractMismatches,
    string? DiagnosticCategory)
{
    public string? RegisteredRunnerPath => Readback?.ActionPath;

    public string? ExpectedRunnerPath { get; init; }

    public string? ConfiguredTimeZoneId { get; init; }

    public string? CurrentWindowsTimeZoneId { get; init; }
}
