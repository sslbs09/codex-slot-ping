namespace CodexSlotPing.Core.Time;

public sealed class WindowsTimeZoneProvider : ITimeZoneProvider
{
    public string CurrentWindowsTimeZoneId => TimeZoneInfo.Local.Id;

    public TimeZoneInfo Resolve(string windowsTimeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsTimeZoneId);
        return TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId);
    }
}
