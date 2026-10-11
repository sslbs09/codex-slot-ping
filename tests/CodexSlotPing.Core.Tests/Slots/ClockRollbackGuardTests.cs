using CodexSlotPing.Core.Slots;

namespace CodexSlotPing.Core.Tests.Slots;

public sealed class ClockRollbackGuardTests
{
    [Fact]
    public void FirstObservation_StoresUtcHighWaterMark()
    {
        var nowUtc = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var result = ClockRollbackGuard.Evaluate(nowUtc, null);

        Assert.False(result.IsBlocked);
        Assert.Equal(nowUtc, result.NewHighestObservedUtc);
    }

    [Fact]
    public void BackwardMove_IsBlockedUntilHighWaterMarkIsReachedAgain()
    {
        var highestObservedUtc = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var nowUtc = highestObservedUtc.AddMinutes(-1);

        var result = ClockRollbackGuard.Evaluate(nowUtc, highestObservedUtc);

        Assert.True(result.IsBlocked);
        Assert.Equal(highestObservedUtc, result.NewHighestObservedUtc);
    }

    [Fact]
    public void MonotonicTime_IsNotBlockedAndAdvancesHighWaterMark()
    {
        var highestObservedUtc = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var nowUtc = highestObservedUtc.AddMinutes(1);

        var result = ClockRollbackGuard.Evaluate(nowUtc, highestObservedUtc);

        Assert.False(result.IsBlocked);
        Assert.Equal(nowUtc, result.NewHighestObservedUtc);
    }
}
