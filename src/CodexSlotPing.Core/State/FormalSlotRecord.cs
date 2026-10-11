using System.Text.Json.Serialization;
using CodexSlotPing.Core.Slots;

namespace CodexSlotPing.Core.State;

public sealed record FormalSlotRecord(
    [property: JsonRequired] SlotId SlotId,
    [property: JsonRequired] int Position,
    [property: JsonRequired] FormalSlotStatus Status,
    [property: JsonRequired] DateTimeOffset UpdatedAtUtc,
    string? DiagnosticCategory);
