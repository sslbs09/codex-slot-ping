using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Tests.Time;

public sealed class WindowsTimeZoneProviderTests
{
    [Fact]
    public void CurrentWindowsTimeZoneId_ResolvesToLocalWindowsZone()
    {
        var provider = new WindowsTimeZoneProvider();

        var resolved = provider.Resolve(provider.CurrentWindowsTimeZoneId);

        Assert.Equal(TimeZoneInfo.Local.Id, provider.CurrentWindowsTimeZoneId);
        Assert.Equal(TimeZoneInfo.Local.Id, resolved.Id);
    }
}
