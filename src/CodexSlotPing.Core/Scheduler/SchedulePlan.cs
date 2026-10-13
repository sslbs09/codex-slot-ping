using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.Core.Scheduler;

public sealed record SchedulePlan(DateTimeOffset? OneShotTriggerAtUtc)
{
    public static SchedulePlan None { get; } = new((DateTimeOffset?)null);

    public static SchedulePlan At(DateTimeOffset triggerAtUtc)
    {
        var utc = triggerAtUtc.ToUniversalTime();
        var remainder = utc.Ticks % TimeSpan.TicksPerSecond;
        return new SchedulePlan(remainder == 0
            ? utc
            : new DateTimeOffset(utc.Ticks + TimeSpan.TicksPerSecond - remainder, TimeSpan.Zero));
    }

    public static SchedulePlan FromDecision(EligibilityDecision decision, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.SchemaVersion != EligibilityDecision.CurrentSchemaVersion ||
            decision.Intent != EligibilityIntent.Scheduled ||
            !Enum.IsDefined(decision.Status))
        {
            return None;
        }

        var now = nowUtc.ToUniversalTime();
        return decision.Status switch
        {
            EligibilityStatus.Eligible => At(now),
            EligibilityStatus.NotYetEligible when decision.NextEligibleAtUtc is { } next =>
                At(next.ToUniversalTime() < now ? now : next),
            _ => None
        };
    }
}
