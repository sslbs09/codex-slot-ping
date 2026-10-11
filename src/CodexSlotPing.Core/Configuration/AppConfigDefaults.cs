namespace CodexSlotPing.Core.Configuration;

public static class AppConfigDefaults
{
    public static AppConfig Create() => new()
    {
        SchemaVersion = 1,
        Enabled = true,
        Slots = ["07:00", "12:01", "17:01", "22:01"],
        TimeZone = "SYSTEM",
        Model = "gpt-5.6-luna",
        ReasoningEffort = "low",
        Prompt = "Hi.",
        WakeToRun = false,
        StrictSlotWindowMinutes = 3,
        CaptureRawLogs = false
    };
}
