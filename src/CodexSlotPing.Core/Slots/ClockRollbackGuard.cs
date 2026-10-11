namespace CodexSlotPing.Core.Slots;

public sealed record ClockRollbackResult(bool IsBlocked, DateTimeOffset? NewHighestObservedUtc);

public static class ClockRollbackGuard
{
    public static ClockRollbackResult Evaluate(DateTimeOffset nowUtc, DateTimeOffset? highestObservedUtc)
    {
        var currentUtc = nowUtc.ToUniversalTime();
        if (highestObservedUtc is not { } highest)
        {
            return new ClockRollbackResult(false, currentUtc);
        }

        var highWaterUtc = highest.ToUniversalTime();
        if (currentUtc < highWaterUtc)
        {
            return new ClockRollbackResult(true, highWaterUtc);
        }

        return new ClockRollbackResult(false, currentUtc);
    }
}
