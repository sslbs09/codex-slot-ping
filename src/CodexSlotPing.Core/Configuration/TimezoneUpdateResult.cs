using CodexSlotPing.Core.Scheduler;

namespace CodexSlotPing.Core.Configuration;

public enum TimezoneUpdateState
{
    UNCHANGED,
    TIMEZONE_CHANGED,
    UPDATED,
    ERROR
}

public sealed record TimezoneUpdateResult(
    TimezoneUpdateState State,
    AppConfig Config,
    SchedulerStatus SchedulerStatus,
    string? DiagnosticCategory,
    bool ConfigSaved)
{
    public bool Succeeded => State is TimezoneUpdateState.UNCHANGED or TimezoneUpdateState.UPDATED;
}
