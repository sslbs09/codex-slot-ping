namespace CodexSlotPing.Core.Scheduler;

public sealed record SchedulerOperationResult(
    bool Succeeded,
    string? DiagnosticCategory,
    SchedulerStatus? Status);
