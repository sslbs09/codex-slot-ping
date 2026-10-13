using System.Text.Json;
using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.Core.Tests.Eligibility;

public sealed class EligibilityEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T04:00:00Z");
    private readonly EligibilityEvaluator _evaluator = new();

    [Fact]
    public void UnknownExternalUsage_FailsClosedForScheduledRequest()
    {
        var decision = _evaluator.Evaluate(
            EligibilityState.Empty(),
            EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityDecision.CurrentSchemaVersion, decision.SchemaVersion);
        Assert.Equal(EligibilityStatus.Unknown, decision.Status);
        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, decision.Reason);
        Assert.Null(decision.NextEligibleAtUtc);
        Assert.Equal(Confidence.Unknown, decision.Confidence);
        Assert.Equal(ObservationSource.Unknown, decision.ObservationSource);
    }

    [Fact]
    public void UsageLimitWithoutTrustedReset_BlocksScheduledRequest()
    {
        var state = KnownExternalUsageState(
            Observation(EligibilityObservationKind.UsageLimitReached, Now.AddMinutes(-1)));

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.UsageLimitWithoutTrustedReset, decision.Reason);
        Assert.Null(decision.NextEligibleAtUtc);
        Assert.Equal(ObservationSource.AppScheduled, decision.ObservationSource);
        Assert.Equal(Confidence.High, decision.Confidence);
    }

    [Fact]
    public void TrustedReset_IsReportedExactlyAndDoesNotBecomeAnEstimate()
    {
        var resetAt = Now.AddHours(2);
        var state = KnownExternalUsageState(
            Observation(EligibilityObservationKind.UsageLimitReached, Now.AddMinutes(-1), resetAt));

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.NotYetEligible, decision.Status);
        Assert.Equal(EligibilityReason.WaitingForTrustedReset, decision.Reason);
        Assert.Equal(resetAt, decision.NextEligibleAtUtc);
    }

    [Theory]
    [InlineData(Confidence.Low, ObservationSource.AppScheduled)]
    [InlineData(Confidence.High, ObservationSource.Unknown)]
    public void ResetTimestampWithoutTrustedEvidence_DoesNotUnlockScheduledRequest(
        Confidence confidence,
        ObservationSource source)
    {
        var state = KnownExternalUsageState(
            new EligibilityObservation(
                EligibilityObservationKind.UsageLimitReached,
                Now.AddMinutes(-1),
                source,
                confidence,
                Now.AddHours(2)));

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.UsageLimitWithoutTrustedReset, decision.Reason);
        Assert.Null(decision.NextEligibleAtUtc);
    }

    [Fact]
    public void TestNowWithoutConfirmation_RequiresConfirmationForUsageLimit()
    {
        var state = KnownExternalUsageState(
            Observation(EligibilityObservationKind.UsageLimitReached, Now.AddMinutes(-1)));

        var decision = _evaluator.Evaluate(
            state,
            EligibilityRequest.TestNow(Now));

        Assert.Equal(EligibilityStatus.ConfirmationRequired, decision.Status);
        Assert.Equal(EligibilityReason.TestNowConfirmationRequired, decision.Reason);
        Assert.True(decision.RequiresConfirmation);
        Assert.Null(decision.NextEligibleAtUtc);
    }

    [Fact]
    public void ConfirmedTestNow_ReturnsManualDecisionWithoutClearingUsageLimit()
    {
        var usageLimit = Observation(EligibilityObservationKind.UsageLimitReached, Now.AddMinutes(-1));
        var state = KnownExternalUsageState(usageLimit);

        var decision = _evaluator.Evaluate(
            state,
            EligibilityRequest.ConfirmedTestNow(Now));

        Assert.Equal(EligibilityStatus.Eligible, decision.Status);
        Assert.Equal(EligibilityReason.TestNowExplicitlyConfirmed, decision.Reason);
        Assert.False(decision.RequiresConfirmation);
        Assert.Equal(ObservationSource.AppTestNow, decision.ObservationSource);
        Assert.Equal(Confidence.High, decision.Confidence);
        Assert.Null(decision.NextEligibleAtUtc);
        Assert.Contains(usageLimit, state.Observations);
    }

    [Fact]
    public void TestNowConfirmation_DoesNotResolveUnknownExternalUsage()
    {
        var decision = _evaluator.Evaluate(
            EligibilityState.Empty(),
            EligibilityRequest.ConfirmedTestNow(Now));

        Assert.Equal(EligibilityStatus.Unknown, decision.Status);
        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, decision.Reason);
        Assert.False(decision.RequiresConfirmation);
    }

    [Fact]
    public void ScheduledRequest_CannotCarryTestNowConfirmation()
    {
        var decision = _evaluator.Evaluate(
            KnownExternalUsageState(),
            EligibilityRequest.ConfirmedScheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.InvalidRequest, decision.Reason);
    }

    [Fact]
    public void ClockRollback_BlocksUntilThePersistedHighWaterMark()
    {
        var highWater = Now.AddMinutes(5);
        var state = KnownExternalUsageState() with { HighestObservedUtc = highWater };

        var decision = _evaluator.Evaluate(
            state,
            EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.ClockRollback, decision.Reason);
        Assert.Equal(highWater, decision.NextEligibleAtUtc);
    }

    [Fact]
    public void UnsupportedStateSchema_FailsClosed()
    {
        var state = KnownExternalUsageState() with { SchemaVersion = EligibilityState.CurrentSchemaVersion + 1 };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.UnsupportedStateSchema, decision.Reason);
    }

    [Fact]
    public void UnresolvedClaim_FailsClosedForScheduledRequest()
    {
        var state = KnownExternalUsageState() with { HasUnresolvedClaim = true };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.UnresolvedClaim, decision.Reason);
    }

    [Fact]
    public void AttemptAndSuccessTimestamps_DoNotCreateQuotaOrResetPredictions()
    {
        var state = KnownExternalUsageState() with
        {
            LastAttemptAtUtc = Now.AddSeconds(-10),
            LastConfirmedSuccessAtUtc = Now.AddMinutes(-1)
        };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Eligible, decision.Status);
        Assert.Equal(EligibilityReason.Ready, decision.Reason);
        Assert.Null(decision.NextEligibleAtUtc);
    }

    [Fact]
    public void SameInput_ProducesStableDecisionAndSchemaVersion()
    {
        var state = KnownExternalUsageState(
            Observation(EligibilityObservationKind.UsageLimitReached, Now.AddMinutes(-1), Now.AddHours(1)));
        var request = EligibilityRequest.Scheduled(Now);

        var first = _evaluator.Evaluate(state, request);
        var second = _evaluator.Evaluate(state, request);
        var roundTrip = JsonSerializer.Deserialize<EligibilityDecision>(JsonSerializer.Serialize(first));

        Assert.Equal(first, second);
        Assert.Equal(first, roundTrip);
        Assert.Equal(EligibilityState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(EligibilityDecision.CurrentSchemaVersion, first.SchemaVersion);
        Assert.Equal(EligibilityReason.WaitingForTrustedReset, first.Reason);
    }

    [Fact]
    public void ObservationInputOrder_DoesNotChangeDecision()
    {
        var older = Observation(
            EligibilityObservationKind.UsageLimitReached,
            Now.AddHours(-2),
            Now.AddHours(-1));
        var newer = Observation(
            EligibilityObservationKind.UsageLimitReached,
            Now.AddMinutes(-1));
        var stateA = KnownExternalUsageState(older, newer);
        var stateB = KnownExternalUsageState(newer, older);

        var first = _evaluator.Evaluate(stateA, EligibilityRequest.Scheduled(Now));
        var second = _evaluator.Evaluate(stateB, EligibilityRequest.Scheduled(Now));

        Assert.Equal(first, second);
        Assert.Equal(EligibilityReason.UsageLimitWithoutTrustedReset, first.Reason);
    }

    private static EligibilityState KnownExternalUsageState(params EligibilityObservation[] observations) =>
        EligibilityState.Empty() with
        {
            ExternalManualUsageUnknown = false,
            Observations = observations
        };

    private static EligibilityObservation Observation(
        EligibilityObservationKind kind,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? trustedResetAtUtc = null) =>
        new(
            kind,
            observedAtUtc,
            ObservationSource.AppScheduled,
            Confidence.High,
            trustedResetAtUtc);
}
