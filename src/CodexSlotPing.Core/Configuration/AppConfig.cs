namespace CodexSlotPing.Core.Configuration;

public sealed record AppConfig
{
    public required int SchemaVersion { get; init; }

    public required bool Enabled { get; init; }

    public required IReadOnlyList<string> Slots { get; init; }

    public required string TimeZone { get; init; }

    public required string Model { get; init; }

    public required string ReasoningEffort { get; init; }

    public required string Prompt { get; init; }

    public required bool WakeToRun { get; init; }

    public required int StrictSlotWindowMinutes { get; init; }

    public required bool CaptureRawLogs { get; init; }
}
