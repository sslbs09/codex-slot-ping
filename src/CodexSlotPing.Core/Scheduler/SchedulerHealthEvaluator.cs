using System.Globalization;

namespace CodexSlotPing.Core.Scheduler;

public sealed class SchedulerHealthEvaluator
{
    public SchedulerStatus Evaluate(
        SchedulerStatus status,
        string currentRunnerPath,
        string configuredTimeZoneId,
        string currentWindowsTimeZoneId,
        IReadOnlyList<string>? expectedSlotTimes = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunnerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredTimeZoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentWindowsTimeZoneId);

        string expectedRunnerPath;
        try
        {
            expectedRunnerPath = Path.GetFullPath(currentRunnerPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return status with
            {
                State = SchedulerState.ERROR,
                ExpectedRunnerPath = currentRunnerPath,
                ConfiguredTimeZoneId = configuredTimeZoneId,
                CurrentWindowsTimeZoneId = currentWindowsTimeZoneId,
                DiagnosticCategory = "invalid-runner-path"
            };
        }

        var mismatches = status.ContractMismatches
            .Where(code => code is not "runner-action-mismatch" and not "timezone-mismatch")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (status.Readback is { ActionCount: not 1 } &&
            !mismatches.Contains("action-count-mismatch", StringComparer.Ordinal))
        {
            mismatches.Add("action-count-mismatch");
        }
        if (expectedSlotTimes is not null && status.Readback is { } readback &&
            readback.Triggers.Count == 4 && !TriggerTimesMatch(readback.Triggers, expectedSlotTimes) &&
            !mismatches.Contains("trigger-times-mismatch", StringComparer.Ordinal))
        {
            mismatches.Add("trigger-times-mismatch");
        }
        var timezoneChanged = !string.Equals(configuredTimeZoneId, "SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(configuredTimeZoneId, currentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase);
        var pathChanged = status.Exists && status.IsOwned &&
                          (status.Readback is null || !PathsEqual(status.Readback.ActionPath, expectedRunnerPath));

        if (timezoneChanged)
        {
            mismatches.Add("timezone-mismatch");
        }

        if (pathChanged)
        {
            mismatches.Add("runner-action-mismatch");
        }

        var state = ResolveState(status, timezoneChanged, pathChanged, mismatches.Count > 0);
        var diagnostic = state switch
        {
            SchedulerState.TIMEZONE_CHANGED => "timezone-mismatch",
            SchedulerState.NEEDS_REPAIR => pathChanged ? "runner-action-mismatch" : mismatches.FirstOrDefault() ?? "scheduler-drift",
            SchedulerState.CONFLICT => "task-ownership-conflict",
            SchedulerState.OFF or SchedulerState.ON => null,
            _ => status.DiagnosticCategory
        };

        return status with
        {
            State = state,
            ContractMismatches = mismatches,
            DiagnosticCategory = diagnostic,
            ExpectedRunnerPath = expectedRunnerPath,
            ConfiguredTimeZoneId = configuredTimeZoneId,
            CurrentWindowsTimeZoneId = currentWindowsTimeZoneId
        };
    }

    private static bool TriggerTimesMatch(
        IReadOnlyList<SchedulerTriggerReadback> triggers,
        IReadOnlyList<string> expectedSlotTimes)
    {
        if (expectedSlotTimes.Count != 4 || triggers.Count != 4)
        {
            return false;
        }

        var expected = expectedSlotTimes.OrderBy(time => time, StringComparer.Ordinal).ToArray();
        var actual = new string[4];
        for (var index = 0; index < triggers.Count; index++)
        {
            if (!DateTime.TryParseExact(
                    triggers[index].StartBoundary,
                    "yyyy-MM-dd'T'HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var startBoundary))
            {
                return false;
            }

            actual[index] = startBoundary.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        Array.Sort(actual, StringComparer.Ordinal);
        return expected.SequenceEqual(actual, StringComparer.Ordinal);
    }

    private static SchedulerState ResolveState(
        SchedulerStatus status,
        bool timezoneChanged,
        bool pathChanged,
        bool hasContractMismatch)
    {
        if (status.State == SchedulerState.ERROR)
        {
            return SchedulerState.ERROR;
        }

        if (status.State == SchedulerState.CONFLICT || (status.Exists && !status.IsOwned))
        {
            return SchedulerState.CONFLICT;
        }

        if (timezoneChanged)
        {
            return SchedulerState.TIMEZONE_CHANGED;
        }

        if (!status.Exists)
        {
            return SchedulerState.OFF;
        }

        if (status.IsOwned && (pathChanged || hasContractMismatch || status.State == SchedulerState.DRIFTED))
        {
            return SchedulerState.NEEDS_REPAIR;
        }

        if (status.IsOwned)
        {
            return status.IsEnabled ? SchedulerState.ON : SchedulerState.OFF;
        }

        return SchedulerState.OFF;
    }

    private static bool PathsEqual(string? actualPath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
