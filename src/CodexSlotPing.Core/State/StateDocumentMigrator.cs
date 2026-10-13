using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Slots;

namespace CodexSlotPing.Core.State;

public static class StateDocumentMigrator
{
    public static StateDocument MigrateV1ToV2(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SchemaVersion == StateDocument.CurrentSchemaVersion)
        {
            return state;
        }

        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported state schema version: {state.SchemaVersion}.");
        }

        if (state.FormalSlots is null)
        {
            throw new InvalidDataException("The v1 state document is missing formalSlots.");
        }

        var requestOutcomes = state.FormalSlots
            .Where(record => record.Status is FormalSlotStatus.CONFIRMED_SUCCESS or FormalSlotStatus.ATTEMPTED_UNCONFIRMED)
            .ToArray();
        var observations = new List<EligibilityObservation>(requestOutcomes.Length * 2);

        foreach (var record in requestOutcomes)
        {
            var observedAtUtc = record.UpdatedAtUtc.ToUniversalTime();
            observations.Add(new EligibilityObservation(
                EligibilityObservationKind.AttemptStarted,
                observedAtUtc,
                ObservationSource.LegacyState,
                Confidence.High));

            if (record.Status == FormalSlotStatus.CONFIRMED_SUCCESS)
            {
                observations.Add(new EligibilityObservation(
                    EligibilityObservationKind.ConfirmedSuccess,
                    observedAtUtc,
                    ObservationSource.LegacyState,
                    Confidence.High));
            }
            else if (string.Equals(record.DiagnosticCategory, "usage-limit", StringComparison.OrdinalIgnoreCase))
            {
                observations.Add(new EligibilityObservation(
                    EligibilityObservationKind.UsageLimitReached,
                    observedAtUtc,
                    ObservationSource.LegacyState,
                    Confidence.High));
            }
        }

        var eligibility = new EligibilityState(
            EligibilityState.CurrentSchemaVersion,
            state.HighestObservedUtc?.ToUniversalTime(),
            GetLatestTimestamp(requestOutcomes),
            GetLatestTimestamp(state.FormalSlots.Where(record => record.Status == FormalSlotStatus.CONFIRMED_SUCCESS)),
            ExternalManualUsageUnknown: true,
            observations
                .OrderBy(observation => observation.ObservedAtUtc)
                .ThenBy(observation => observation.Kind)
                .ToArray(),
            HasUnresolvedClaim: state.FormalSlots.Any(record => record.Status == FormalSlotStatus.CLAIMED));

        return state with
        {
            SchemaVersion = StateDocument.CurrentSchemaVersion,
            Eligibility = eligibility
        };
    }

    private static DateTimeOffset? GetLatestTimestamp(IEnumerable<FormalSlotRecord> records)
    {
        var timestamps = records.Select(record => record.UpdatedAtUtc.ToUniversalTime()).ToArray();
        return timestamps.Length == 0 ? null : timestamps.Max();
    }
}
