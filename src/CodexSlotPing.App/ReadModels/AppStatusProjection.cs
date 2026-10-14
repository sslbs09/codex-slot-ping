using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
using CodexSlotPing.App;

namespace CodexSlotPing.App.ReadModels;

public static class AppStatusProjection
{
    internal static AppScreenState Create(
        AppConfig config,
        StateDocument state,
        EligibilityDecision eligibilityDecision,
        SchedulePlan schedulePlan,
        SchedulerStatus schedulerStatus,
        EnvironmentStatus codexEnvironment,
        SlotScheduleEntry? nextSlot,
        IReadOnlyList<TodaySlotState> todaySlots,
        DateTimeOffset nowUtc,
        string currentWindowsTimeZoneId,
        string logsDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(eligibilityDecision);
        ArgumentNullException.ThrowIfNull(schedulePlan);
        ArgumentNullException.ThrowIfNull(schedulerStatus);
        ArgumentNullException.ThrowIfNull(codexEnvironment);
        ArgumentNullException.ThrowIfNull(todaySlots);

        var scheduler = ProjectScheduler(schedulerStatus, config.WakeToRun);
        if (config.IsPaused && scheduler.IsEnabled)
        {
            scheduler = scheduler with
            {
                State = SchedulerViewState.Error,
                DiagnosticCategory = "scheduler-enabled-while-paused"
            };
        }

        return new AppScreenState(
            ProjectAutomation(config),
            EligibilityDecisionProjection.Create(
                eligibilityDecision,
                nextPlannedAttemptAtUtc: schedulePlan.OneShotTriggerAtUtc),
            new ScheduleView(schedulePlan.OneShotTriggerAtUtc),
            scheduler,
            ProjectCodexEnvironment(codexEnvironment),
            nextSlot is null ? null : new NextSlotView(nextSlot.Position, nextSlot.StartUtc),
            todaySlots.Select(ProjectTodaySlot).ToArray(),
            ProjectLatestKnownResult(state.LastTest),
            nowUtc,
            currentWindowsTimeZoneId,
            logsDirectory);
    }

    public static AutomationView ProjectAutomation(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new AutomationView(
            config.Enabled,
            config.LegacyFixedSlots.ToArray(),
            config.TimeZone,
            config.Model,
            config.ReasoningEffort,
            config.Prompt,
            config.WakeToRun,
            config.StrictSlotWindowMinutes,
            config.CaptureRawLogs)
        {
            IsPaused = config.IsPaused
        };
    }

    public static AppConfig ToConfig(AutomationView automation, AppConfig currentConfig)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(currentConfig);
        return currentConfig with
        {
            Enabled = automation.Enabled,
            LegacyFixedSlots = automation.LegacyFixedSlots.ToArray(),
            TimeZone = automation.TimeZone,
            Model = automation.Model,
            ReasoningEffort = automation.ReasoningEffort,
            Prompt = automation.Prompt,
            WakeToRun = automation.WakeToRun,
            StrictSlotWindowMinutes = automation.StrictSlotWindowMinutes,
            CaptureRawLogs = automation.CaptureRawLogs
            // IsPaused is changed only through the mutex-protected control operation.
        };
    }

    public static SchedulerView ProjectScheduler(SchedulerStatus status, bool configuredWakeToRun)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new SchedulerView(
            status.State switch
            {
                SchedulerState.ON or SchedulerState.OWNED_ENABLED => SchedulerViewState.Enabled,
                SchedulerState.OFF or SchedulerState.OWNED_DISABLED => SchedulerViewState.Disabled,
                SchedulerState.NEEDS_REPAIR or SchedulerState.DRIFTED => SchedulerViewState.NeedsRepair,
                SchedulerState.TIMEZONE_CHANGED => SchedulerViewState.TimezoneChanged,
                SchedulerState.NOT_INSTALLED => SchedulerViewState.NotInstalled,
                SchedulerState.CONFLICT => SchedulerViewState.Conflict,
                SchedulerState.ERROR => SchedulerViewState.Error,
                _ => SchedulerViewState.Unknown
            },
            status.Exists,
            status.IsOwned,
            status.IsEnabled,
            status.Exists && status.IsOwned && status.Readback is not null
                ? status.Readback.WakeToRun
                : configuredWakeToRun,
            status.DiagnosticCategory,
            status.ContractMismatches.ToArray());
    }

    public static CodexEnvironmentView ProjectCodexEnvironment(EnvironmentStatus environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var supportedReasoningEfforts = environment.SupportedReasoningEfforts.Count > 0
            ? environment.SupportedReasoningEfforts
            : CodexCliCapabilities.DocumentedReasoningEfforts;
        return new CodexEnvironmentView(
            environment.IsReady,
            environment.ExecutablePath,
            environment.CliVersion,
            environment.ChatGptLoginConfirmed,
            supportedReasoningEfforts.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            environment.DiagnosticCodes.ToArray());
    }

    public static LatestKnownResultView? ProjectLatestKnownResult(TestRunRecord? lastTest) =>
        lastTest is null
            ? null
            : new LatestKnownResultView(
                LatestKnownResultSource.TestNow,
                lastTest.CompletedAtUtc,
                lastTest.Model,
                lastTest.ReasoningEffort,
                lastTest.ResultCategory,
                lastTest.ElapsedMilliseconds,
                lastTest.ExitCode);

    private static TodaySlotView ProjectTodaySlot(TodaySlotState slot) => new(
        slot.Position,
        slot.LocalTime,
        slot.Status switch
        {
            FormalSlotStatus.MISSED => SlotViewStatus.Missed,
            FormalSlotStatus.CLAIMED => SlotViewStatus.Claimed,
            FormalSlotStatus.CONFIRMED_SUCCESS => SlotViewStatus.ConfirmedSuccess,
            FormalSlotStatus.ATTEMPTED_UNCONFIRMED => SlotViewStatus.AttemptedUnconfirmed,
            FormalSlotStatus.PRELAUNCH_FAILED => SlotViewStatus.PrelaunchFailed,
            _ => SlotViewStatus.Pending
        });

    public static TestRunResultView ProjectTestRunResult(TestRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new TestRunResultView(
            result.Invocation.Classification switch
            {
                CodexResultClassification.CONFIRMED_SUCCESS => TestResultViewStatus.ConfirmedSuccess,
                CodexResultClassification.ATTEMPTED_UNCONFIRMED => TestResultViewStatus.AttemptedUnconfirmed,
                _ => TestResultViewStatus.PrelaunchFailed
            },
            result.Invocation.ElapsedMilliseconds,
            result.Invocation.DiagnosticCategory);
    }
}
