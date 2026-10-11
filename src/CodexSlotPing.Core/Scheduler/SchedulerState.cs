namespace CodexSlotPing.Core.Scheduler;

public enum SchedulerState
{
    OFF,
    ON,
    NEEDS_REPAIR,
    TIMEZONE_CHANGED,
    NOT_INSTALLED,
    OWNED_ENABLED,
    OWNED_DISABLED,
    CONFLICT,
    DRIFTED,
    ERROR
}
