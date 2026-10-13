namespace CodexSlotPing.Core.Eligibility;

public sealed class EligibilityEvaluator : IEligibilityEvaluator
{
    private static readonly TimeSpan LocalAttemptSpacing = TimeSpan.FromHours(5);

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
                    confidence: state.ExternalManualUsageUnknown ? Confidence.Medium : Confidence.Unknown,
                    observationSource: ObservationSource.AppTestNow,
                    explanationReason: state.ExternalManualUsageUnknown
                        ? EligibilityReason.ExternalManualUsageUnknown
                        : null)
                : Decision(
                    request.Intent,
                    EligibilityStatus.ConfirmationRequired,
                    EligibilityReason.TestNowConfirmationRequired,
                    confidence: state.ExternalManualUsageUnknown ? Confidence.Medium : Confidence.Unknown,
                    requiresConfirmation: true,
                    explanationReason: state.ExternalManualUsageUnknown
                        ? EligibilityReason.ExternalManualUsageUnknown
                        : null);
        }

        var latestLocalUseAtUtc = Later(
            state.LastAttemptAtUtc?.ToUniversalTime(),
            state.LastConfirmedSuccessAtUtc?.ToUniversalTime());
        if (latestLocalUseAtUtc is { } localUseAtUtc)
        {
            var nextLocalAttemptAtUtc = localUseAtUtc.Add(LocalAttemptSpacing);
            if (nowUtc < nextLocalAttemptAtUtc)
            {
                return Decision(
                    request.Intent,
                    EligibilityStatus.NotYetEligible,
                    EligibilityReason.LocalAttemptSpacing,
                    nextEligibleAtUtc: nextLocalAttemptAtUtc,
                    confidence: state.ExternalManualUsageUnknown ? Confidence.Medium : Confidence.High,
                    observationSource: state.ExternalManualUsageUnknown
                        ? ObservationSource.Unknown
                        : FindLocalObservationSource(state, localUseAtUtc),
                    explanationReason: state.ExternalManualUsageUnknown
                        ? EligibilityReason.ExternalManualUsageUnknown
                        : null);
            }
        }

        if (state.ExternalManualUsageUnknown)
        {
            return Decision(
                request.Intent,
                EligibilityStatus.Eligible,
                EligibilityReason.Ready,
                confidence: Confidence.Medium,
                observationSource: ObservationSource.Unknown,
                explanationReason: EligibilityReason.ExternalManualUsageUnknown);
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

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first.Value >= second.Value ? first : second;

    private static ObservationSource FindLocalObservationSource(EligibilityState state, DateTimeOffset observedAtUtc) =>
        state.Observations
            .Where(observation =>
                observation.Kind is EligibilityObservationKind.AttemptStarted or EligibilityObservationKind.ConfirmedSuccess &&
                observation.ObservedAtUtc.ToUniversalTime() == observedAtUtc)
            .OrderByDescending(observation => observation.ObservedAtUtc)
            .Select(observation => observation.Source)
            .DefaultIfEmpty(ObservationSource.Unknown)
            .First();

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
        bool requiresConfirmation = false,
        EligibilityReason? explanationReason = null) =>
        new(
            EligibilityDecision.CurrentSchemaVersion,
            intent,
            status,
            reason,
            nextEligibleAtUtc?.ToUniversalTime(),
            confidence,
            observationSource,
            requiresConfirmation,
            explanationReason);
}
