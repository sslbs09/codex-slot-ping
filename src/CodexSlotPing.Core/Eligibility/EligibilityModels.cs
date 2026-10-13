using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.Eligibility;

public enum EligibilityIntent
{
    Scheduled,
    TestNow
}

public enum TestNowConfirmation
{
    NotConfirmed,
    Confirmed
}

public enum EligibilityStatus
{
    Eligible,
    NotYetEligible,
    Blocked,
    Unknown,
    ConfirmationRequired
}

public enum EligibilityReason
{
    Ready,
    ExternalManualUsageUnknown,
    UsageLimitWithoutTrustedReset,
    WaitingForTrustedReset,
    TestNowConfirmationRequired,
    TestNowExplicitlyConfirmed,
    ClockRollback,
    UnsupportedStateSchema,
    InvalidRequest,
    InvalidState
}

public enum ObservationSource
{
    AppScheduled,
    AppTestNow,
    LegacyState,
    Unknown
}

public enum Confidence
{
    Unknown,
    Low,
    Medium,
    High
}

public enum EligibilityObservationKind
{
    AttemptStarted,
    ConfirmedSuccess,
    UsageLimitReached,
    TestNowConfirmationGranted
}

public sealed record EligibilityObservation(
    [property: JsonRequired] EligibilityObservationKind Kind,
    [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] ObservationSource Source,
    [property: JsonRequired] Confidence Confidence,
    DateTimeOffset? TrustedResetAtUtc = null);

public sealed record EligibilityState(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] DateTimeOffset? HighestObservedUtc,
    [property: JsonRequired] DateTimeOffset? LastAttemptAtUtc,
    [property: JsonRequired] DateTimeOffset? LastConfirmedSuccessAtUtc,
    [property: JsonRequired] bool ExternalManualUsageUnknown,
    [property: JsonRequired] IReadOnlyList<EligibilityObservation> Observations)
{
    public const int CurrentSchemaVersion = 1;

    public static EligibilityState Empty() =>
        new(
            CurrentSchemaVersion,
            null,
            null,
            null,
            ExternalManualUsageUnknown: true,
            Array.Empty<EligibilityObservation>());
}

public sealed record EligibilityRequest(
    EligibilityIntent Intent,
    DateTimeOffset NowUtc,
    TestNowConfirmation TestNowConfirmation)
{
    public static EligibilityRequest Scheduled(DateTimeOffset nowUtc) =>
        new(EligibilityIntent.Scheduled, nowUtc, TestNowConfirmation.NotConfirmed);

    public static EligibilityRequest ConfirmedScheduled(DateTimeOffset nowUtc) =>
        new(EligibilityIntent.Scheduled, nowUtc, TestNowConfirmation.Confirmed);

    public static EligibilityRequest TestNow(DateTimeOffset nowUtc) =>
        new(EligibilityIntent.TestNow, nowUtc, TestNowConfirmation.NotConfirmed);

    public static EligibilityRequest ConfirmedTestNow(DateTimeOffset nowUtc) =>
        new(EligibilityIntent.TestNow, nowUtc, TestNowConfirmation.Confirmed);
}

public sealed record EligibilityDecision(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] EligibilityIntent Intent,
    [property: JsonRequired] EligibilityStatus Status,
    [property: JsonRequired] EligibilityReason Reason,
    DateTimeOffset? NextEligibleAtUtc,
    [property: JsonRequired] Confidence Confidence,
    [property: JsonRequired] ObservationSource ObservationSource,
    [property: JsonRequired] bool RequiresConfirmation)
{
    public const int CurrentSchemaVersion = 1;
}
