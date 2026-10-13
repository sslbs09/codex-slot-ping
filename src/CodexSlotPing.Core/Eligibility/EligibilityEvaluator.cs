namespace CodexSlotPing.Core.Eligibility;

public sealed class EligibilityEvaluator : IEligibilityEvaluator
{
    public EligibilityDecision Evaluate(EligibilityState state, EligibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        if (state.SchemaVersion != EligibilityState.CurrentSchemaVersion)
        {
            return Decision(
                request.Intent,
                EligibilityStatus.Blocked,
                EligibilityReason.UnsupportedStateSchema);
        }

        if (state.Observations is null ||
            state.Observations.Any(observation =>
                observation is null ||
                !Enum.IsDefined(observation.Kind) ||
                !Enum.IsDefined(observation.Source) ||
                !Enum.IsDefined(observation.Confidence)))
        {
            return Decision(
                request.Intent,
                EligibilityStatus.Blocked,
                EligibilityReason.InvalidState);
        }

        if (!Enum.IsDefined(request.Intent) ||
            !Enum.IsDefined(request.TestNowConfirmation) ||
            (request.Intent == EligibilityIntent.Scheduled &&
             request.TestNowConfirmation != TestNowConfirmation.NotConfirmed))
        {
            return Decision(
                request.Intent,
                EligibilityStatus.Blocked,
                EligibilityReason.InvalidRequest);
        }

        var nowUtc = request.NowUtc.ToUniversalTime();
        if (state.HighestObservedUtc is { } highestObservedUtc &&
            nowUtc < highestObservedUtc.ToUniversalTime())
        {
            return Decision(
                request.Intent,
                EligibilityStatus.Blocked,
                EligibilityReason.ClockRollback,
                nextEligibleAtUtc: highestObservedUtc.ToUniversalTime(),
                confidence: Confidence.High);
        }

        if (state.HasUnresolvedClaim)
        {
            return Decision(
                request.Intent,
                EligibilityStatus.Blocked,
                EligibilityReason.UnresolvedClaim,
                confidence: Confidence.High,
                observationSource: ObservationSource.AppScheduled);
        }

        var latestUsageLimit = state.Observations
            .Where(observation => observation.Kind == EligibilityObservationKind.UsageLimitReached)
            .OrderByDescending(observation => observation.ObservedAtUtc.ToUniversalTime())
            .ThenBy(observation => observation.TrustedResetAtUtc.HasValue)
            .ThenByDescending(observation => observation.TrustedResetAtUtc?.ToUniversalTime())
            .ThenBy(observation => observation.Source)
            .ThenBy(observation => observation.Confidence)
            .FirstOrDefault();

        if (latestUsageLimit is { } usageLimit)
        {
            var trustedResetAtUtc = GetTrustedResetAtUtc(usageLimit);
            if (trustedResetAtUtc is null || nowUtc < trustedResetAtUtc.Value.ToUniversalTime())
            {
                return EvaluateUsageLimit(request, usageLimit, trustedResetAtUtc);
            }
        }

        if (request.Intent == EligibilityIntent.TestNow)
        {
            return request.TestNowConfirmation == TestNowConfirmation.Confirmed
                ? Decision(
                    request.Intent,
                    EligibilityStatus.Eligible,
                    EligibilityReason.TestNowExplicitlyConfirmed,
                    observationSource: ObservationSource.AppTestNow)
                : Decision(
                    request.Intent,
                    EligibilityStatus.ConfirmationRequired,
                    EligibilityReason.TestNowConfirmationRequired,
                    requiresConfirmation: true);
        }

        if (state.ExternalManualUsageUnknown)
        {
            return Decision(
                request.Intent,
                EligibilityStatus.Unknown,
                EligibilityReason.ExternalManualUsageUnknown);
        }

        return Decision(
            request.Intent,
            EligibilityStatus.Eligible,
            EligibilityReason.Ready);
    }

    private static DateTimeOffset? GetTrustedResetAtUtc(EligibilityObservation observation)
    {
        if (observation.TrustedResetAtUtc is null ||
            observation.Source == ObservationSource.Unknown ||
            observation.Confidence != Confidence.High)
        {
            return null;
        }

        return observation.TrustedResetAtUtc.Value.ToUniversalTime();
    }

    private static EligibilityDecision EvaluateUsageLimit(
        EligibilityRequest request,
        EligibilityObservation usageLimit,
        DateTimeOffset? trustedResetAtUtc)
    {
        if (request.Intent == EligibilityIntent.TestNow)
        {
            if (request.TestNowConfirmation == TestNowConfirmation.Confirmed)
            {
                return Decision(
                    request.Intent,
                    EligibilityStatus.Eligible,
                    EligibilityReason.TestNowExplicitlyConfirmed,
                    confidence: usageLimit.Confidence,
                    observationSource: ObservationSource.AppTestNow);
            }

            return Decision(
                request.Intent,
                EligibilityStatus.ConfirmationRequired,
                EligibilityReason.TestNowConfirmationRequired,
                nextEligibleAtUtc: trustedResetAtUtc?.ToUniversalTime(),
                confidence: usageLimit.Confidence,
                observationSource: usageLimit.Source,
                requiresConfirmation: true);
        }

        if (trustedResetAtUtc is { } resetAtUtc)
        {
            return Decision(
                request.Intent,
                EligibilityStatus.NotYetEligible,
                EligibilityReason.WaitingForTrustedReset,
                nextEligibleAtUtc: resetAtUtc.ToUniversalTime(),
                confidence: usageLimit.Confidence,
                observationSource: usageLimit.Source);
        }

        return Decision(
            request.Intent,
            EligibilityStatus.Blocked,
            EligibilityReason.UsageLimitWithoutTrustedReset,
            confidence: usageLimit.Confidence,
            observationSource: usageLimit.Source);
    }

    private static EligibilityDecision Decision(
        EligibilityIntent intent,
        EligibilityStatus status,
        EligibilityReason reason,
        DateTimeOffset? nextEligibleAtUtc = null,
        Confidence confidence = Confidence.Unknown,
        ObservationSource observationSource = ObservationSource.Unknown,
        bool requiresConfirmation = false) =>
        new(
            EligibilityDecision.CurrentSchemaVersion,
            intent,
            status,
            reason,
            nextEligibleAtUtc?.ToUniversalTime(),
            confidence,
            observationSource,
            requiresConfirmation);
}
