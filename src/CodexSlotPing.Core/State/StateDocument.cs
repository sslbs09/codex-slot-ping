using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.State;

public sealed record StateDocument(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] DateTimeOffset? HighestObservedUtc,
    [property: JsonRequired] IReadOnlyList<FormalSlotRecord> FormalSlots,
    [property: JsonRequired] TestRunRecord? LastTest)
{
    public static StateDocument Empty() => new(1, null, Array.Empty<FormalSlotRecord>(), null);
}
