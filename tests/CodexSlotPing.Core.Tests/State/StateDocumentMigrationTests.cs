using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;

namespace CodexSlotPing.Core.Tests.State;

public sealed class StateDocumentMigrationTests
{
    [Fact]
    public void V1Migration_PreservesHighWaterAndMapsOnlyObservedRequestOutcomes()
    {
        var highWater = At(12, 5);
        var successAt = At(12, 1);
        var attemptAt = At(12, 2);
        var prelaunchAt = At(12, 3);
        var claimAt = At(12, 4);
        var v1 = new StateDocument(
            1,
            highWater,
            [
                Record("07:00", FormalSlotStatus.CONFIRMED_SUCCESS, successAt),
                Record("12:01", FormalSlotStatus.ATTEMPTED_UNCONFIRMED, attemptAt, "stream-error"),
                Record("17:01", FormalSlotStatus.PRELAUNCH_FAILED, prelaunchAt, "process-start-failed"),
                Record("22:01", FormalSlotStatus.CLAIMED, claimAt)
            ],
            new TestRunRecord("test-id", successAt, "gpt-5.6-luna", "low", "CONFIRMED_SUCCESS", 1, 0));

        var migrated = StateDocumentMigrator.MigrateV1ToV2(v1);
        var migratedAgain = StateDocumentMigrator.MigrateV1ToV2(migrated);

        Assert.Equal(StateDocument.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(highWater, migrated.HighestObservedUtc);
        Assert.Equal(v1.FormalSlots, migrated.FormalSlots);
        Assert.Equal(v1.LastTest, migrated.LastTest);
        Assert.Equal(migrated, migratedAgain);
        Assert.NotNull(migrated.Eligibility);
        Assert.Equal(EligibilityState.CurrentSchemaVersion, migrated.Eligibility.SchemaVersion);
        Assert.Equal(highWater, migrated.Eligibility.HighestObservedUtc);
        Assert.Equal(attemptAt, migrated.Eligibility.LastAttemptAtUtc);
        Assert.Equal(successAt, migrated.Eligibility.LastConfirmedSuccessAtUtc);
        Assert.True(migrated.Eligibility.ExternalManualUsageUnknown);
        Assert.True(migrated.Eligibility.HasUnresolvedClaim);
        Assert.Contains(migrated.Eligibility.Observations,
            observation => observation.Kind == EligibilityObservationKind.AttemptStarted && observation.ObservedAtUtc == successAt);
        Assert.Contains(migrated.Eligibility.Observations,
            observation => observation.Kind == EligibilityObservationKind.AttemptStarted && observation.ObservedAtUtc == attemptAt);
        Assert.Contains(migrated.Eligibility.Observations,
            observation => observation.Kind == EligibilityObservationKind.ConfirmedSuccess && observation.ObservedAtUtc == successAt);
        Assert.DoesNotContain(migrated.Eligibility.Observations,
            observation => observation.ObservedAtUtc == prelaunchAt);
        Assert.DoesNotContain(migrated.Eligibility.Observations,
            observation => observation.ObservedAtUtc == claimAt);
    }

    [Fact]
    public void V1UsageLimitDiagnostic_MigratesAsBlockingObservationWithoutResetEstimate()
    {
        var attemptedAt = At(12, 1);
        var v1 = new StateDocument(
            1,
            attemptedAt,
            [Record("07:00", FormalSlotStatus.ATTEMPTED_UNCONFIRMED, attemptedAt, "usage-limit")],
            null);

        var migrated = StateDocumentMigrator.MigrateV1ToV2(v1);
        var usageLimit = Assert.Single(migrated.Eligibility!.Observations,
            observation => observation.Kind == EligibilityObservationKind.UsageLimitReached);

        Assert.Equal(attemptedAt, usageLimit.ObservedAtUtc);
        Assert.Equal(ObservationSource.LegacyState, usageLimit.Source);
        Assert.Equal(Confidence.High, usageLimit.Confidence);
        Assert.Null(usageLimit.TrustedResetAtUtc);
    }

    private static FormalSlotRecord Record(
        string time,
        FormalSlotStatus status,
        DateTimeOffset updatedAtUtc,
        string? diagnosticCategory = null) =>
        new(
            new SlotId("Eastern Standard Time", new DateOnly(2026, 1, 15), TimeOnly.Parse(time)),
            1,
            status,
            updatedAtUtc,
            diagnosticCategory);

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);
}
