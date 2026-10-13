using System.Text.Json.Serialization;
using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.Core.State;

public sealed record StateDocument(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] DateTimeOffset? HighestObservedUtc,
    [property: JsonRequired] IReadOnlyList<FormalSlotRecord> FormalSlots,
    [property: JsonRequired] TestRunRecord? LastTest,
    EligibilityState? Eligibility = null)
{
    public const int CurrentSchemaVersion = 2;

    public static StateDocument Empty() =>
        new(CurrentSchemaVersion, null, Array.Empty<FormalSlotRecord>(), null, EligibilityState.Empty());
}
