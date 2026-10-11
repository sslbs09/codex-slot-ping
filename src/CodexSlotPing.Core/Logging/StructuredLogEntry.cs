using CodexSlotPing.Core.Codex;
using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.Logging;

public enum StructuredLogKind
{
    FORMAL_SLOT,
    TEST_NOW
}

public sealed record StructuredLogEntry(
    DateTimeOffset TimestampUtc,
    int LocalOffsetMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SlotId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TestId,
    StructuredLogKind Kind,
    string Model,
    string ReasoningEffort,
    CodexResultClassification Result,
    long ElapsedMilliseconds,
    int? ExitCode,
    string? DiagnosticCategory,
    CodexTokenUsage? Usage);
