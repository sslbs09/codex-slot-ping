using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;

namespace CodexSlotPing.Core.Tests.State;

public sealed class StateTransitionsTests
{
    [Fact]
    public void PendingAndDerivedPending_AllowOnlyClaimOrMiss()
    {
        Assert.True(StateTransitions.CanTransition(null, FormalSlotStatus.CLAIMED));
        Assert.True(StateTransitions.CanTransition(FormalSlotStatus.PENDING, FormalSlotStatus.CLAIMED));
        Assert.True(StateTransitions.CanTransition(null, FormalSlotStatus.MISSED));
        Assert.True(StateTransitions.CanTransition(FormalSlotStatus.PENDING, FormalSlotStatus.MISSED));

        Assert.False(StateTransitions.CanTransition(null, FormalSlotStatus.CONFIRMED_SUCCESS));
        Assert.False(StateTransitions.CanTransition(FormalSlotStatus.PENDING, FormalSlotStatus.ATTEMPTED_UNCONFIRMED));
        Assert.False(StateTransitions.CanTransition(FormalSlotStatus.PENDING, FormalSlotStatus.PRELAUNCH_FAILED));
    }

    [Theory]
    [InlineData(FormalSlotStatus.CONFIRMED_SUCCESS)]
    [InlineData(FormalSlotStatus.ATTEMPTED_UNCONFIRMED)]
    [InlineData(FormalSlotStatus.PRELAUNCH_FAILED)]
    public void ClaimedSlot_CanBeFinalizedToAnyTerminalAttemptOutcome(FormalSlotStatus finalStatus)
    {
        Assert.True(StateTransitions.CanTransition(FormalSlotStatus.CLAIMED, finalStatus));
    }

    [Fact]
    public void StaleClaim_CanBeReconciledAsUnconfirmed()
    {
        Assert.True(StateTransitions.CanTransition(FormalSlotStatus.CLAIMED, FormalSlotStatus.ATTEMPTED_UNCONFIRMED));
        Assert.False(StateTransitions.CanTransition(FormalSlotStatus.CLAIMED, FormalSlotStatus.PENDING));
    }

    [Theory]
    [InlineData(FormalSlotStatus.MISSED)]
    [InlineData(FormalSlotStatus.CONFIRMED_SUCCESS)]
    [InlineData(FormalSlotStatus.ATTEMPTED_UNCONFIRMED)]
    [InlineData(FormalSlotStatus.PRELAUNCH_FAILED)]
    public void TerminalStatus_CannotReturnToPendingOrAnotherState(FormalSlotStatus terminalStatus)
    {
        Assert.False(StateTransitions.CanTransition(terminalStatus, FormalSlotStatus.PENDING));
        Assert.False(StateTransitions.CanTransition(terminalStatus, FormalSlotStatus.CLAIMED));
    }
}
