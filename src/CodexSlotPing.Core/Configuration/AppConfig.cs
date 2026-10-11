namespace CodexSlotPing.Core.Configuration;

public sealed record AppConfig
{
    public int SchemaVersion { get; init; } = 1;

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> Slots { get; init; } = Array.Empty<string>();

    public string TimeZone { get; init; } = "SYSTEM";

    public string Model { get; init; } = "gpt-5.6-luna";

    public string ReasoningEffort { get; init; } = "low";

    public string Prompt { get; init; } = "Hi.";

    public bool WakeToRun { get; init; }

    public int StrictSlotWindowMinutes { get; init; } = 3;

    public bool CaptureRawLogs { get; init; }
}
