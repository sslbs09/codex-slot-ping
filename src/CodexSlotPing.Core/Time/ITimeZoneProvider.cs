namespace CodexSlotPing.Core.Time;

public interface ITimeZoneProvider
{
    string CurrentWindowsTimeZoneId { get; }

    TimeZoneInfo Resolve(string windowsTimeZoneId);
}
