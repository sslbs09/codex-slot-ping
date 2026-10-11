namespace CodexSlotPing.Core.Scheduler;

public enum SchedulerState
{
    NOT_INSTALLED,
    OWNED_ENABLED,
    OWNED_DISABLED,
    CONFLICT,
    DRIFTED,
    ERROR
}
