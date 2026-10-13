using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Tests.Slots;

public sealed class SlotAdmissionEvaluatorTests
{
    private const string EasternTimeZoneId = "Eastern Standard Time";
    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById(EasternTimeZoneId);
    private readonly SlotAdmissionEvaluator _evaluator = new(new WindowsTimeZoneProvider());

    [Fact]
    public void BeforeStart_IsPendingAndCannotDispatchEarly()
    {
        var nowUtc = AtLocal(EasternTimeZone, new DateOnly(2026, 1, 15), new TimeOnly(6, 59));

        var result = Evaluate(nowUtc);

        Assert.Equal(SlotAdmissionKind.Pending, result.Kind);
        Assert.Equal(FormalSlotStatus.PENDING, result.FormalStatus);
        Assert.Equal(new SlotId(EasternTimeZoneId, new DateOnly(2026, 1, 15), new TimeOnly(7, 0)), result.SlotId);
        Assert.Equal(1, result.Position);
    }

    [Fact]
    public void AdmissionWindow_IsStartInclusiveAndFourMinutesExclusive()
    {
        var slotDate = new DateOnly(2026, 1, 15);
        var start = AtLocal(EasternTimeZone, slotDate, new TimeOnly(7, 0));

        var atStart = Evaluate(start);
        var lastInstantInWindow = Evaluate(start.AddMinutes(3).AddMilliseconds(999));
        var atExclusiveEnd = Evaluate(start.AddMinutes(4));

        Assert.Equal(SlotAdmissionKind.Eligible, atStart.Kind);
        Assert.Equal(SlotAdmissionKind.Eligible, lastInstantInWindow.Kind);
        Assert.Equal(FormalSlotStatus.PENDING, lastInstantInWindow.FormalStatus);
        Assert.Equal(SlotAdmissionKind.Missed, atExclusiveEnd.Kind);
        Assert.Equal(FormalSlotStatus.MISSED, atExclusiveEnd.FormalStatus);
        Assert.Equal(new SlotId(EasternTimeZoneId, slotDate, new TimeOnly(7, 0)), atExclusiveEnd.SlotId);
    }

    [Fact]
    public void ExpiredSlot_IsMissedAndNeverReopenedForCatchUp()
    {
        var start = AtLocal(EasternTimeZone, new DateOnly(2026, 1, 15), new TimeOnly(12, 1));

        var result = Evaluate(start.AddMinutes(4));

        Assert.Equal(SlotAdmissionKind.Missed, result.Kind);
        Assert.Equal(FormalSlotStatus.MISSED, result.FormalStatus);
        Assert.Equal(new TimeOnly(12, 1), result.SlotId!.LocalTime);
    }

    [Fact]
    public void SlotIdentityAndPosition_AreStableWhenTriggerOrderChanges()
    {
        var reordered = AppConfigDefaults.Create() with
        {
            LegacyFixedSlots = ["17:01", "07:00", "22:01", "12:01"],
            TimeZone = EasternTimeZoneId
        };
        var nowUtc = AtLocal(EasternTimeZone, new DateOnly(2026, 1, 15), new TimeOnly(12, 2));

        var result = _evaluator.Evaluate(reordered, nowUtc, EasternTimeZoneId);

        Assert.Equal(SlotAdmissionKind.Eligible, result.Kind);
        Assert.Equal(new SlotId(EasternTimeZoneId, new DateOnly(2026, 1, 15), new TimeOnly(12, 1)), result.SlotId);
        Assert.Equal(2, result.Position);
        Assert.Equal("2026-01-15/12:01@Eastern Standard Time", result.SlotId!.ToString());
    }

    [Fact]
    public void AfterMidnight_LateSlotKeepsPreviousLocalDateIdentity()
    {
        var nowUtc = AtLocal(EasternTimeZone, new DateOnly(2026, 1, 16), new TimeOnly(0, 2));

        var result = Evaluate(nowUtc);

        Assert.Equal(SlotAdmissionKind.Missed, result.Kind);
        Assert.Equal(new SlotId(EasternTimeZoneId, new DateOnly(2026, 1, 15), new TimeOnly(22, 1)), result.SlotId);
        Assert.Equal(4, result.Position);
    }

    [Fact]
    public void SystemTimezone_UsesCurrentWindowsTimezoneIdInSlotIdentity()
    {
        var currentZoneId = TimeZoneInfo.Local.Id;
        var currentZone = TimeZoneInfo.Local;
        var nowUtc = AtLocal(currentZone, new DateOnly(2026, 1, 15), new TimeOnly(7, 0));
        var config = AppConfigDefaults.Create();

        var result = _evaluator.Evaluate(config, nowUtc, currentZoneId);

        Assert.Equal(SlotAdmissionKind.Eligible, result.Kind);
        Assert.Equal(currentZoneId, result.SlotId!.WindowsTimeZoneId);
    }

    [Fact]
    public void ExplicitTimezoneMismatch_BlocksAdmission()
    {
        var config = AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId };

        var result = _evaluator.Evaluate(config, DateTimeOffset.Parse("2026-01-15T12:01:00Z"), "Taipei Standard Time");

        Assert.Equal(SlotAdmissionKind.Blocked, result.Kind);
        Assert.Null(result.FormalStatus);
        Assert.Equal(AdmissionBlockReason.TimeZoneMismatch, result.BlockReason);
    }

    [Fact]
    public void SpringForward_NonexistentLocalSlotIsMissed()
    {
        var config = ConfigWithTimes("02:30", "08:30", "14:30", "20:30");
        var nowUtc = DateTimeOffset.Parse("2026-03-08T07:00:00Z"); // 03:00 after the local clock jumps over 02:30.

        var result = _evaluator.Evaluate(config, nowUtc, EasternTimeZoneId);

        Assert.Equal(SlotAdmissionKind.Missed, result.Kind);
        Assert.Equal(FormalSlotStatus.MISSED, result.FormalStatus);
        Assert.Equal(new SlotId(EasternTimeZoneId, new DateOnly(2026, 3, 8), new TimeOnly(2, 30)), result.SlotId);
    }

    [Fact]
    public void FallBack_AmbiguousLocalSlotAdmitsOnlyItsFirstUtcOccurrence()
    {
        var config = ConfigWithTimes("01:30", "07:30", "13:30", "19:30");
        var firstOccurrenceUtc = DateTimeOffset.Parse("2026-11-01T05:30:00Z");
        var repeatedOccurrenceUtc = DateTimeOffset.Parse("2026-11-01T06:30:00Z");

        var firstOccurrence = _evaluator.Evaluate(config, firstOccurrenceUtc, EasternTimeZoneId);
        var repeatedOccurrence = _evaluator.Evaluate(config, repeatedOccurrenceUtc, EasternTimeZoneId);

        Assert.Equal(SlotAdmissionKind.Eligible, firstOccurrence.Kind);
        Assert.Equal(SlotAdmissionKind.Missed, repeatedOccurrence.Kind);
        Assert.Equal(firstOccurrence.SlotId, repeatedOccurrence.SlotId);
    }

    private SlotAdmissionResult Evaluate(DateTimeOffset nowUtc)
    {
        var config = AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId };
        return _evaluator.Evaluate(config, nowUtc, EasternTimeZoneId);
    }

    private static AppConfig ConfigWithTimes(params string[] slots)
    {
        return AppConfigDefaults.Create() with { LegacyFixedSlots = slots, TimeZone = EasternTimeZoneId };
    }

    private static DateTimeOffset AtLocal(TimeZoneInfo timeZone, DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
