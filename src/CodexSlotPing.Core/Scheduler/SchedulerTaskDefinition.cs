namespace CodexSlotPing.Core.Scheduler;

public sealed record SchedulerTaskDefinition(
    string TaskPath,
    string RunnerPath,
    string ConfiguredTimeZoneId,
    IReadOnlyList<TimeOnly> SlotTimes,
    bool WakeToRun,
    DateOnly? StartBoundaryDate = null);
