using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.State;

public sealed record TestRunRecord(
    [property: JsonRequired] string TestId,
    [property: JsonRequired] DateTimeOffset CompletedAtUtc,
    [property: JsonRequired] string Model,
    [property: JsonRequired] string ReasoningEffort,
    [property: JsonRequired] string ResultCategory,
    [property: JsonRequired] long ElapsedMilliseconds,
    [property: JsonRequired] int? ExitCode);
