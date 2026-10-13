using System.Text.Json;
using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.Core.Tests.Eligibility;

public sealed class EligibilityEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T04:00:00Z");
    private readonly EligibilityEvaluator _evaluator = new();

    [Fact]
    public void UnknownExternalUsage_DoesNotPermanentlyBlockScheduledRequest()
    {
        var decision = _evaluator.Evaluate(
            EligibilityState.Empty(),
            EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityDecision.CurrentSchemaVersion, decision.SchemaVersion);
        Assert.Equal(EligibilityStatus.Eligible, decision.Status);
        Assert.Equal(EligibilityReason.Ready, decision.Reason);
        Assert.Null(decision.NextEligibleAtUtc);
        Assert.Equal(Confidence.Medium, decision.Confidence);
        Assert.Equal(ObservationSource.Unknown, decision.ObservationSource);
        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, decision.ExplanationReason);
    }

    [Fact]
    public void ExternalUsageUnknown_UsesLastSuccessAndLocalSpacingForNextAttempt()
    {
        var lastSuccessAt = Now.AddHours(-2);
        var state = EligibilityState.Empty() with { LastConfirmedSuccessAtUtc = lastSuccessAt };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.NotYetEligible, decision.Status);
        Assert.Equal(EligibilityReason.LocalAttemptSpacing, decision.Reason);
        Assert.Equal(Now.AddHours(3), decision.NextEligibleAtUtc);
        Assert.Equal(Confidence.Medium, decision.Confidence);
        Assert.Equal(ObservationSource.Unknown, decision.ObservationSource);
        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, decision.ExplanationReason);
    }

    [Fact]
    public void ExternalUsageUnknown_UsesMostRecentLocalAttemptOrSuccessForSpacing()
    {
        var state = EligibilityState.Empty() with
        {
            LastAttemptAtUtc = Now.AddHours(-2),
            LastConfirmedSuccessAtUtc = Now.AddHours(-8)
        };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.NotYetEligible, decision.Status);
        Assert.Equal(EligibilityReason.LocalAttemptSpacing, decision.Reason);
        Assert.Equal(Now.AddHours(3), decision.NextEligibleAtUtc);
    }

    [Fact]
    public void LocalAttemptSpacingBoundary_AllowsAttemptAtExactlyFiveHours()
    {
        var state = EligibilityState.Empty() with
        {
            LastConfirmedSuccessAtUtc = Now.AddHours(-5)
        };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Eligible, decision.Status);
        Assert.Equal(EligibilityReason.Ready, decision.Reason);
        Assert.Null(decision.NextEligibleAtUtc);
        Assert.Equal(Confidence.Medium, decision.Confidence);
        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, decision.ExplanationReason);
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
    public void UsageLimitWithoutTrustedReset_BlocksWhenExternalUsageIsAlsoUnknown()
    {
        var state = EligibilityState.Empty() with
        {
            Observations = [Observation(EligibilityObservationKind.UsageLimitReached, Now.AddMinutes(-1))]
        };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.UsageLimitWithoutTrustedReset, decision.Reason);
        Assert.Null(decision.NextEligibleAtUtc);
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
    public void TestNowWithoutUsageLimit_StillRequiresOneExplicitConfirmation()
    {
        var decision = _evaluator.Evaluate(
            KnownExternalUsageState(),
            EligibilityRequest.TestNow(Now));

        Assert.Equal(EligibilityStatus.ConfirmationRequired, decision.Status);
        Assert.Equal(EligibilityReason.TestNowConfirmationRequired, decision.Reason);
        Assert.True(decision.RequiresConfirmation);
    }

    [Fact]
    public void ConfirmedTestNowWithoutUsageLimit_IsEligible()
    {
        var decision = _evaluator.Evaluate(
            KnownExternalUsageState(),
            EligibilityRequest.ConfirmedTestNow(Now));

        Assert.Equal(EligibilityStatus.Eligible, decision.Status);
        Assert.Equal(EligibilityReason.TestNowExplicitlyConfirmed, decision.Reason);
        Assert.False(decision.RequiresConfirmation);
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
    public void ConfirmedTestNow_CanMakeOneExplicitAttemptWhileExternalUsageRemainsUnknown()
    {
        var state = EligibilityState.Empty();
        var decision = _evaluator.Evaluate(
            state,
            EligibilityRequest.ConfirmedTestNow(Now));

        Assert.Equal(EligibilityStatus.Eligible, decision.Status);
        Assert.Equal(EligibilityReason.TestNowExplicitlyConfirmed, decision.Reason);
        Assert.False(decision.RequiresConfirmation);
        Assert.True(state.ExternalManualUsageUnknown);
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
        var state = EligibilityState.Empty() with { HighestObservedUtc = highWater };

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
        var state = EligibilityState.Empty() with { SchemaVersion = EligibilityState.CurrentSchemaVersion + 1 };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.UnsupportedStateSchema, decision.Reason);
    }

    [Fact]
    public void UnresolvedClaim_FailsClosedForScheduledRequest()
    {
        var state = EligibilityState.Empty() with { HasUnresolvedClaim = true };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.UnresolvedClaim, decision.Reason);
    }

    [Fact]
    public void InvalidObservation_FailsClosedWhenExternalUsageIsUnknown()
    {
        var state = EligibilityState.Empty() with
        {
            Observations = [new EligibilityObservation(
                (EligibilityObservationKind)int.MaxValue,
                Now,
                ObservationSource.Unknown,
                Confidence.Unknown)]
        };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.Blocked, decision.Status);
        Assert.Equal(EligibilityReason.InvalidState, decision.Reason);
    }

    [Fact]
    public void AttemptAndSuccessTimestamps_UseLocalSpacingWithoutQuotaOrResetPredictions()
    {
        var state = KnownExternalUsageState() with
        {
            LastAttemptAtUtc = Now.AddSeconds(-10),
            LastConfirmedSuccessAtUtc = Now.AddMinutes(-1)
        };

        var decision = _evaluator.Evaluate(state, EligibilityRequest.Scheduled(Now));

        Assert.Equal(EligibilityStatus.NotYetEligible, decision.Status);
        Assert.Equal(EligibilityReason.LocalAttemptSpacing, decision.Reason);
        Assert.Equal(Now.AddHours(5).AddSeconds(-10), decision.NextEligibleAtUtc);
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
