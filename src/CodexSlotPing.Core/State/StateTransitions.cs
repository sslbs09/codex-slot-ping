using CodexSlotPing.Core.Slots;

namespace CodexSlotPing.Core.State;

public static class StateTransitions
{
    public static bool CanTransition(FormalSlotStatus? currentStatus, FormalSlotStatus nextStatus)
    {
        return currentStatus switch
        {
            null or FormalSlotStatus.PENDING => nextStatus is FormalSlotStatus.CLAIMED or FormalSlotStatus.MISSED,
            FormalSlotStatus.CLAIMED => nextStatus is
                FormalSlotStatus.CONFIRMED_SUCCESS or
                FormalSlotStatus.ATTEMPTED_UNCONFIRMED or
                FormalSlotStatus.PRELAUNCH_FAILED,
            _ => false
        };
    }
}
