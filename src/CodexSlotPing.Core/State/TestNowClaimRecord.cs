using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.State;

public sealed record TestNowClaimRecord(
    [property: JsonRequired] string TestId,
    [property: JsonRequired] DateTimeOffset ClaimedAtUtc);
