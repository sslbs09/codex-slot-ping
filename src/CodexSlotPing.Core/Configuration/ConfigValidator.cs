using System.Buffers;
using System.Globalization;
using System.Text;

namespace CodexSlotPing.Core.Configuration;

public static class ConfigValidator
{
    private const int RequiredSlotCount = 4;
    private const int MinimumSlotGapMinutes = 6;
    private const int FixedSlotWindowMinutes = 3;
    private const int MaximumPromptScalars = 200;
    private const int MinutesPerDay = 24 * 60;

    public static ConfigValidationResult Validate(
        AppConfig candidate,
        string currentWindowsTimeZoneId,
        IReadOnlySet<string> supportedReasoningEfforts)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentWindowsTimeZoneId);
        ArgumentNullException.ThrowIfNull(supportedReasoningEfforts);

        var errors = new List<string>();

        if (candidate.SchemaVersion != 1)
        {
            errors.Add("schemaVersion must be 1.");
        }

        ValidateSlots(candidate.Slots, errors);
        ValidateTimeZone(candidate.TimeZone, currentWindowsTimeZoneId, errors);

        if (string.IsNullOrWhiteSpace(candidate.Model))
        {
            errors.Add("model must be a non-empty selection request.");
        }

        if (string.IsNullOrWhiteSpace(candidate.ReasoningEffort) ||
            !supportedReasoningEfforts.Contains(candidate.ReasoningEffort))
        {
            errors.Add("reasoningEffort is not supported by the detected CLI compatibility set.");
        }

        var normalizedPrompt = candidate.Prompt?.Trim();
        if (string.IsNullOrEmpty(normalizedPrompt))
        {
            errors.Add("prompt must be non-empty after trimming.");
        }
        else
        {
            ValidatePromptScalars(normalizedPrompt, errors);
        }

        if (candidate.StrictSlotWindowMinutes != FixedSlotWindowMinutes)
        {
            errors.Add("strictSlotWindowMinutes is fixed at 3 in v0.1.");
        }

        if (errors.Count > 0)
        {
            return new ConfigValidationResult(false, errors, null);
        }

        return new ConfigValidationResult(
            true,
            Array.Empty<string>(),
            candidate with { Prompt = normalizedPrompt! });
    }

    private static void ValidateSlots(IReadOnlyList<string>? slots, List<string> errors)
    {
        if (slots is null || slots.Count != RequiredSlotCount)
        {
            errors.Add("exactly four slot times are required.");
            return;
        }

        var minuteOfDayValues = new List<int>(RequiredSlotCount);
        for (var index = 0; index < slots.Count; index++)
        {
            var value = slots[index];
            if (value is null || !TimeOnly.TryParseExact(
                    value,
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                errors.Add($"slot {index + 1} must use 24-hour HH:mm format.");
                continue;
            }

            minuteOfDayValues.Add(parsed.Hour * 60 + parsed.Minute);
        }

        if (minuteOfDayValues.Count != RequiredSlotCount)
        {
            return;
        }

        minuteOfDayValues.Sort();
        if (minuteOfDayValues.Distinct().Count() != RequiredSlotCount)
        {
            errors.Add("slot times must be unique.");
        }

        for (var index = 0; index < minuteOfDayValues.Count; index++)
        {
            var nextIndex = (index + 1) % minuteOfDayValues.Count;
            var gap = nextIndex == 0
                ? minuteOfDayValues[0] + MinutesPerDay - minuteOfDayValues[index]
                : minuteOfDayValues[nextIndex] - minuteOfDayValues[index];

            if (gap < MinimumSlotGapMinutes)
            {
                errors.Add("slot times must be at least six minutes apart, including across midnight.");
                return;
            }
        }
    }

    private static void ValidateTimeZone(string? configuredTimeZoneId, string currentWindowsTimeZoneId, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configuredTimeZoneId))
        {
            errors.Add("timeZone must be SYSTEM or a Windows timezone ID.");
            return;
        }

        if (!configuredTimeZoneId.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) &&
            !configuredTimeZoneId.Equals(currentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("the configured timezone lock does not match the current Windows timezone ID.");
        }
    }

    private static bool ValidatePromptScalars(string prompt, List<string> errors)
    {
        var remaining = prompt.AsSpan();
        var scalarCount = 0;

        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                errors.Add("prompt must contain valid Unicode scalar values.");
                return false;
            }

            scalarCount++;
            if (scalarCount > MaximumPromptScalars)
            {
                errors.Add("prompt must not exceed 200 Unicode scalar values.");
                return false;
            }

            remaining = remaining[charsConsumed..];
        }

        return true;
    }
}
