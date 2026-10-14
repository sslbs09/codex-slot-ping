using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.Configuration;

public sealed record AppConfig
{
    public required int SchemaVersion { get; init; }

    public required bool Enabled { get; init; }

    // User intent to suspend automatic scheduled claims without disabling automation.
    // Missing values in existing schema v2 files deserialize as false.
    public bool IsPaused { get; init; }

    // Read-only compatibility for schema v1 input. JsonConfigStore migrates it
    // into LegacyFixedSlots and omits this property when writing schema v2.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Slots { get; init; }

    public IReadOnlyList<string> LegacyFixedSlots { get; init; } = Array.Empty<string>();

    public required string TimeZone { get; init; }

    public required string Model { get; init; }

    public required string ReasoningEffort { get; init; }

    public required string Prompt { get; init; }

    public required bool WakeToRun { get; init; }

    public required int StrictSlotWindowMinutes { get; init; }

    public required bool CaptureRawLogs { get; init; }
}
