using System.Globalization;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Slots;

public sealed class SlotAdmissionEvaluator
{
    private static readonly TimeSpan AdmissionWindow = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan MinimumSlotGap = TimeSpan.FromMinutes(6);
    private readonly ITimeZoneProvider _timeZoneProvider;

    public SlotAdmissionEvaluator(ITimeZoneProvider timeZoneProvider)
    {
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
    }

    public SlotAdmissionResult Evaluate(AppConfig config, DateTimeOffset nowUtc, string currentWindowsTimeZoneId)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(currentWindowsTimeZoneId))
        {
            return Blocked(AdmissionBlockReason.TimeZoneUnavailable);
        }

        var isSystemTimeZone = string.Equals(config.TimeZone, "SYSTEM", StringComparison.OrdinalIgnoreCase);
        if (!isSystemTimeZone &&
            !string.Equals(config.TimeZone, currentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(AdmissionBlockReason.TimeZoneMismatch);
        }

        TimeZoneInfo timeZone;
        try
        {
            timeZone = _timeZoneProvider.Resolve(currentWindowsTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return Blocked(AdmissionBlockReason.TimeZoneUnavailable);
        }
        catch (InvalidTimeZoneException)
        {
            return Blocked(AdmissionBlockReason.TimeZoneUnavailable);
        }
        catch (ArgumentException)
        {
            return Blocked(AdmissionBlockReason.TimeZoneUnavailable);
        }

        if (!TryGetDefinitions(config.LegacyFixedSlots, out var definitions))
        {
            return Blocked(AdmissionBlockReason.InvalidConfiguration);
        }

        var now = nowUtc.ToUniversalTime();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
        var localDate = DateOnly.FromDateTime(localNow);
        var occurrences = BuildOccurrences(definitions, currentWindowsTimeZoneId, timeZone, localDate);

        var eligible = occurrences
            .Where(occurrence => occurrence.StartUtc is { } start && now >= start && now < start + AdmissionWindow)
            .OrderBy(occurrence => occurrence.StartUtc)
            .FirstOrDefault();
        if (eligible is not null)
        {
            return new SlotAdmissionResult(
                SlotAdmissionKind.Eligible,
                FormalSlotStatus.PENDING,
                eligible.SlotId,
                eligible.Definition.Position,
                null);
        }

        var nearest = occurrences
            .Select(occurrence => new
            {
                Occurrence = occurrence,
                Delta = occurrence.StartUtc is { } start
                    ? start - now
                    : occurrence.LocalDate.ToDateTime(occurrence.Definition.LocalTime) - localNow
            })
            .OrderBy(item => item.Delta.Duration())
            .FirstOrDefault();

        if (nearest is null)
        {
            return Blocked(AdmissionBlockReason.InvalidConfiguration);
        }

        var kind = nearest.Delta > TimeSpan.Zero ? SlotAdmissionKind.Pending : SlotAdmissionKind.Missed;
        var status = kind == SlotAdmissionKind.Pending ? FormalSlotStatus.PENDING : FormalSlotStatus.MISSED;
        return new SlotAdmissionResult(kind, status, nearest.Occurrence.SlotId, nearest.Occurrence.Definition.Position, null);
    }

    private static SlotAdmissionResult Blocked(AdmissionBlockReason reason) =>
        new(SlotAdmissionKind.Blocked, null, null, null, reason);

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

    private static IReadOnlyList<SlotOccurrence> BuildOccurrences(
        IReadOnlyList<SlotDefinition> definitions,
        string windowsTimeZoneId,
        TimeZoneInfo timeZone,
        DateOnly currentLocalDate)
    {
        var occurrences = new List<SlotOccurrence>(definitions.Count * 3);
        foreach (var date in new[] { currentLocalDate.AddDays(-1), currentLocalDate, currentLocalDate.AddDays(1) })
        {
            foreach (var definition in definitions)
            {
                var localDateTime = DateTime.SpecifyKind(date.ToDateTime(definition.LocalTime), DateTimeKind.Unspecified);
                DateTimeOffset? startUtc = null;

                if (!timeZone.IsInvalidTime(localDateTime))
                {
                    startUtc = timeZone.IsAmbiguousTime(localDateTime)
                        ? GetFirstUtcOccurrence(localDateTime, timeZone)
                        : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone), TimeSpan.Zero);
                }

                occurrences.Add(new SlotOccurrence(
                    definition,
                    date,
                    new SlotId(windowsTimeZoneId, date, definition.LocalTime),
                    startUtc));
            }
        }

        return occurrences;
    }

    private static DateTimeOffset GetFirstUtcOccurrence(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        return timeZone.GetAmbiguousTimeOffsets(localDateTime)
            .Select(offset => new DateTimeOffset(localDateTime, offset).ToUniversalTime())
            .OrderBy(candidate => candidate)
            .First();
    }

    private sealed record SlotOccurrence(
        SlotDefinition Definition,
        DateOnly LocalDate,
        SlotId SlotId,
        DateTimeOffset? StartUtc);
}
