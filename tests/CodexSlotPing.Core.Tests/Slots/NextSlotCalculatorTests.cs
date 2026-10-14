using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Tests.Slots;

public sealed class NextSlotCalculatorTests
{
    private const string EasternTimeZoneId = "Eastern Standard Time";
    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById(EasternTimeZoneId);
    private readonly NextSlotCalculator _calculator = new(new WindowsTimeZoneProvider());

    [Fact]
    public void ReturnsEarliestFutureIdentity()
    {
        var nowUtc = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(6, 0));

        var next = GetNext(AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId }, StateDocument.Empty(), nowUtc);

        Assert.NotNull(next);
        Assert.Equal(new SlotId(EasternTimeZoneId, new DateOnly(2026, 1, 15), new TimeOnly(7, 0)), next.SlotId);
        Assert.Equal(1, next.Position);
        Assert.Equal(AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 0)), next.StartUtc);
    }

    [Fact]
    public void OpenAdmissionWindow_RemainsTheNextSlot()
    {
        var nowUtc = AtLocal(new DateOnly(2026, 1, 15), new TimeOnly(7, 2));

        var next = GetNext(AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId }, StateDocument.Empty(), nowUtc);

        Assert.NotNull(next);
        Assert.Equal(new SlotId(EasternTimeZoneId, new DateOnly(2026, 1, 15), new TimeOnly(7, 0)), next.SlotId);
    }

    [Fact]
    public void TerminalIdentity_IsSkipped()
    {
        var date = new DateOnly(2026, 1, 15);
        var state = WithRecords(Record(date, "07:00", 1, FormalSlotStatus.MISSED));
        var nowUtc = AtLocal(date, new TimeOnly(6, 0));

        var next = GetNext(AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId }, state, nowUtc);

        Assert.NotNull(next);
        Assert.Equal(new SlotId(EasternTimeZoneId, date, new TimeOnly(12, 1)), next.SlotId);
    }

    [Fact]
    public void ClaimedPosition_IsSkippedAfterItsConfiguredTimeChanges()
    {
        var date = new DateOnly(2026, 1, 15);
        var config = AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId, LegacyFixedSlots = ["08:00", "12:01", "17:01", "22:01"] };
        var state = WithRecords(Record(date, "07:00", 1, FormalSlotStatus.CLAIMED));
        var nowUtc = AtLocal(date, new TimeOnly(6, 0));

        var next = GetNext(config, state, nowUtc);

        Assert.NotNull(next);
        Assert.Equal(new SlotId(EasternTimeZoneId, date, new TimeOnly(12, 1)), next.SlotId);
    }

    [Fact]
    public void EditedUnclaimedFutureTime_CreatesNewIdentity()
    {
        var date = new DateOnly(2026, 1, 15);
        var config = AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId, LegacyFixedSlots = ["07:00", "12:10", "17:01", "22:01"] };
        var state = WithRecords(
            Record(date, "07:00", 1, FormalSlotStatus.CONFIRMED_SUCCESS),
            Record(date, "12:01", 2, FormalSlotStatus.MISSED));
        var nowUtc = AtLocal(date, new TimeOnly(7, 5));

        var next = GetNext(config, state, nowUtc);

        Assert.NotNull(next);
        Assert.Equal(new SlotId(EasternTimeZoneId, date, new TimeOnly(12, 10)), next.SlotId);
    }

    [Fact]
    public void WhenTodayHasNoFutureSlots_ReturnsFirstSlotOnNextLocalDay()
    {
        var date = new DateOnly(2026, 1, 15);
        var nowUtc = AtLocal(date, new TimeOnly(22, 5));

        var next = GetNext(AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId }, StateDocument.Empty(), nowUtc);

        Assert.NotNull(next);
        Assert.Equal(new SlotId(EasternTimeZoneId, date.AddDays(1), new TimeOnly(7, 0)), next.SlotId);
        Assert.Equal(AtLocal(date.AddDays(1), new TimeOnly(7, 0)), next.StartUtc);
    }

    [Fact]
    public void TimezoneMismatch_ReturnsNoNextSlot()
    {
        var config = AppConfigDefaults.Create() with { TimeZone = "Pacific Standard Time" };

        var next = GetNext(config, StateDocument.Empty(), DateTimeOffset.Parse("2026-01-15T12:00:00Z"));

        Assert.Null(next);
    }

    private SlotScheduleEntry? GetNext(AppConfig config, StateDocument state, DateTimeOffset nowUtc) =>
        _calculator.GetNext(config, state, nowUtc, EasternTimeZoneId);

    private static StateDocument WithRecords(params FormalSlotRecord[] records) =>
        new(1, null, records, null);

    private static FormalSlotRecord Record(DateOnly date, string time, int position, FormalSlotStatus status) =>
        new(
            new SlotId(EasternTimeZoneId, date, TimeOnly.Parse(time)),
            position,
            status,
            new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            null);

    private static DateTimeOffset AtLocal(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, EasternTimeZone), TimeSpan.Zero);
    }
}
