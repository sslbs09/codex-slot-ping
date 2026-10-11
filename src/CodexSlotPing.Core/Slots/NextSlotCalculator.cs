using System.Globalization;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Slots;

public sealed record SlotScheduleEntry(SlotId SlotId, int Position, DateTimeOffset StartUtc);

public sealed class NextSlotCalculator
{
    private static readonly TimeSpan AdmissionWindow = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan MinimumSlotGap = TimeSpan.FromMinutes(6);
    private readonly ITimeZoneProvider _timeZoneProvider;

    public NextSlotCalculator(ITimeZoneProvider timeZoneProvider)
    {
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
    }

    public SlotScheduleEntry? GetNext(
        AppConfig config,
        StateDocument state,
        DateTimeOffset nowUtc,
        string currentWindowsTimeZoneId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);

        if (string.IsNullOrWhiteSpace(currentWindowsTimeZoneId) || state.FormalSlots is null)
        {
            return null;
        }

        var isSystemTimeZone = string.Equals(config.TimeZone, "SYSTEM", StringComparison.OrdinalIgnoreCase);
        if (!isSystemTimeZone &&
            !string.Equals(config.TimeZone, currentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        TimeZoneInfo timeZone;
        try
        {
            timeZone = _timeZoneProvider.Resolve(currentWindowsTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!TryGetDefinitions(config.Slots, out var definitions))
        {
            return null;
        }

        var now = nowUtc.ToUniversalTime();
        var currentLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var eligibleOrFuture = new List<SlotScheduleEntry>();

        foreach (var date in new[] { currentLocalDate, currentLocalDate.AddDays(1) })
        {
            foreach (var definition in definitions)
            {
                var localDateTime = DateTime.SpecifyKind(date.ToDateTime(definition.LocalTime), DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(localDateTime))
                {
                    continue;
                }

                var startUtc = GetStartUtc(localDateTime, timeZone);
                var slotId = new SlotId(currentWindowsTimeZoneId, date, definition.LocalTime);
                if (HasExistingIdentity(state, slotId) || HasClaimedPosition(state, date, definition.Position))
                {
                    continue;
                }

                if (now < startUtc + AdmissionWindow)
                {
                    eligibleOrFuture.Add(new SlotScheduleEntry(slotId, definition.Position, startUtc));
                }
            }
        }

        return eligibleOrFuture.OrderBy(entry => entry.StartUtc).FirstOrDefault();
    }

    private static bool HasExistingIdentity(StateDocument state, SlotId slotId) =>
        state.FormalSlots.Any(record => record.SlotId == slotId);

    private static bool HasClaimedPosition(StateDocument state, DateOnly date, int position) =>
        state.FormalSlots.Any(record =>
            record.SlotId.LocalDate == date &&
            record.Position == position &&
            record.Status != FormalSlotStatus.MISSED);

    private static bool TryGetDefinitions(IReadOnlyList<string>? configuredSlots, out IReadOnlyList<SlotDefinition> definitions)
    {
        definitions = Array.Empty<SlotDefinition>();
        if (configuredSlots is null || configuredSlots.Count != 4)
        {
            return false;
        }

        var times = new List<TimeOnly>(4);
        foreach (var value in configuredSlots)
        {
            if (value is null || !TimeOnly.TryParseExact(
                    value,
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time))
            {
                return false;
            }

            times.Add(time);
        }

        times.Sort();
        if (times.Distinct().Count() != 4)
        {
            return false;
        }

        for (var index = 0; index < times.Count; index++)
        {
            var nextIndex = (index + 1) % times.Count;
            var gap = nextIndex == 0
                ? times[0].ToTimeSpan() + TimeSpan.FromDays(1) - times[index].ToTimeSpan()
                : times[nextIndex].ToTimeSpan() - times[index].ToTimeSpan();
            if (gap < MinimumSlotGap)
            {
                return false;
            }
        }

        definitions = times.Select((time, index) => new SlotDefinition(index + 1, time)).ToArray();
        return true;
    }

    private static DateTimeOffset GetStartUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        if (timeZone.IsAmbiguousTime(localDateTime))
        {
            return timeZone.GetAmbiguousTimeOffsets(localDateTime)
                .Select(offset => new DateTimeOffset(localDateTime, offset).ToUniversalTime())
                .OrderBy(candidate => candidate)
                .First();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone), TimeSpan.Zero);
    }
}
