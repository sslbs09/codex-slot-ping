namespace CodexSlotPing.Core.Scheduler;

public sealed record SchedulerTaskDefinition(
    string TaskPath,
    string RunnerPath,
    string ConfiguredTimeZoneId,
    SchedulePlan SchedulePlan,
    bool WakeToRun,
    bool Enabled = true);
