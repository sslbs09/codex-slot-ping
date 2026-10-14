using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Scheduler;

namespace CodexSlotPing.Core.Tests.Scheduler;

public sealed class SchedulePlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EligibleDecision_ProducesImmediateOneShot()
    {
        var plan = SchedulePlan.FromDecision(Decision(EligibilityStatus.Eligible, EligibilityReason.Ready), Now);

        Assert.Equal(Now, plan.OneShotTriggerAtUtc);
    }

    [Fact]
    public void NotYetEligibleDecision_ProducesFutureOneShot()
    {
        var nextAttempt = Now.AddHours(3);

        var plan = SchedulePlan.FromDecision(
            Decision(EligibilityStatus.NotYetEligible, EligibilityReason.LocalAttemptSpacing, nextAttempt), Now);

        Assert.Equal(nextAttempt, plan.OneShotTriggerAtUtc);
    }

    [Theory]
    [InlineData(EligibilityStatus.Blocked)]
    [InlineData(EligibilityStatus.Unknown)]
    [InlineData(EligibilityStatus.ConfirmationRequired)]
    public void DecisionWithoutTrustedTime_ProducesNoTrigger(EligibilityStatus status)
    {
        var plan = SchedulePlan.FromDecision(Decision(status, EligibilityReason.UsageLimitWithoutTrustedReset), Now);

        Assert.Null(plan.OneShotTriggerAtUtc);
    }

    [Fact]
    public void NotYetEligibleWithoutNextTime_ProducesNoTrigger()
    {
        var plan = SchedulePlan.FromDecision(
            Decision(EligibilityStatus.NotYetEligible, EligibilityReason.LocalAttemptSpacing), Now);

        Assert.Null(plan.OneShotTriggerAtUtc);
    }

    [Fact]
    public void EquivalentEligibilityTimes_ProduceSamePlanWithoutForwardingDecisionDetails()
    {
        var nextAttempt = Now.AddHours(5);
        var certain = Decision(EligibilityStatus.NotYetEligible, EligibilityReason.LocalAttemptSpacing, nextAttempt);
        var externallyUnknown = certain with
        {
            Confidence = Confidence.Medium,
            ObservationSource = ObservationSource.Unknown,
            ExplanationReason = EligibilityReason.ExternalManualUsageUnknown
        };

        Assert.Equal(
            SchedulePlan.FromDecision(certain, Now),
            SchedulePlan.FromDecision(externallyUnknown, Now));
    }

    [Fact]
    public void ExternalUsageUnknownWithLocalSpacing_ProducesLocalNextAttempt()
    {
        var state = EligibilityState.Empty() with { LastConfirmedSuccessAtUtc = Now.AddHours(-2) };
        var decision = new EligibilityEvaluator().Evaluate(state, EligibilityRequest.Scheduled(Now));

        var plan = SchedulePlan.FromDecision(decision, Now);

        Assert.Equal(EligibilityStatus.NotYetEligible, decision.Status);
        Assert.Equal(Confidence.Medium, decision.Confidence);
        Assert.Equal(Now.AddHours(3), plan.OneShotTriggerAtUtc);
    }

    [Fact]
    public void UnsupportedDecisionSchema_FailsClosedWithoutTrigger()
    {
        var decision = Decision(EligibilityStatus.Eligible, EligibilityReason.Ready) with
        {
            SchemaVersion = EligibilityDecision.CurrentSchemaVersion + 1
        };

        Assert.Null(SchedulePlan.FromDecision(decision, Now).OneShotTriggerAtUtc);
    }

    private static EligibilityDecision Decision(
        EligibilityStatus status,
        EligibilityReason reason,
        DateTimeOffset? nextEligibleAtUtc = null) =>
        new(EligibilityDecision.CurrentSchemaVersion, EligibilityIntent.Scheduled, status, reason,
            nextEligibleAtUtc, Confidence.Medium, ObservationSource.Unknown, false);
}
