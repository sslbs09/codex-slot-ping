namespace CodexSlotPing.App.ReadModels;

public enum EligibilityViewStatus
{
    Eligible,
    NotYetEligible,
    Blocked,
    Unknown,
    ConfirmationRequired
}

public enum SchedulerViewState
{
    Disabled,
    Enabled,
    NeedsRepair,
    TimezoneChanged,
    NotInstalled,
    Conflict,
    Error,
    Unknown
}

public enum SlotViewStatus
{
    Pending,
    Missed,
    Claimed,
    ConfirmedSuccess,
    AttemptedUnconfirmed,
    PrelaunchFailed
}

public enum TestResultViewStatus
{
    ConfirmedSuccess,
    AttemptedUnconfirmed,
    PrelaunchFailed
}

public enum LatestKnownResultSource
{
    TestNow
}

public enum TimezoneUpdateViewState
{
    Unchanged,
    TimezoneChanged,
    Updated,
    Error
}

public sealed record AutomationView(
    bool Enabled,
    IReadOnlyList<string> LegacyFixedSlots,
    string TimeZone,
    string Model,
    string ReasoningEffort,
    string Prompt,
    bool WakeToRun,
    int StrictSlotWindowMinutes,
    bool CaptureRawLogs)
{
    public bool IsPaused { get; init; }
}

public sealed record EligibilityView(
    EligibilityViewStatus Status,
    string StatusText,
    string Reason,
    string Confidence,
    DateTimeOffset? NextEligibleAtUtc,
    string? NextEligibleAtText,
    string? Explanation,
    bool RequiresConfirmation,
    bool IsEligible,
    string DetailsText,
    string TestNowConfirmationText)
{
    public string UserFacingStatus { get; init; } = string.Empty;
    public string ReasonText => Reason;
    public string ConfidenceText => Confidence;
    public string NextPlannedAttemptText { get; init; } = string.Empty;
    public string Details { get; init; } = DetailsText;
}

public sealed record ScheduleView(DateTimeOffset? NextAttemptAtUtc);

public sealed record SchedulerView(
    SchedulerViewState State,
    bool Exists,
    bool IsOwned,
    bool IsEnabled,
    bool EffectiveWakeToRun,
    string? DiagnosticCategory,
    IReadOnlyList<string> ContractMismatches);

public sealed record CodexEnvironmentView(
    bool IsReady,
    string? ExecutablePath,
    string? CliVersion,
    bool? ChatGptLoginConfirmed,
    IReadOnlyList<string> SupportedReasoningEfforts,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record NextSlotView(int Position, DateTimeOffset StartUtc);

public sealed record TodaySlotView(int Position, TimeOnly LocalTime, SlotViewStatus Status);

public sealed record LatestKnownResultView(
    LatestKnownResultSource Source,
    DateTimeOffset CompletedAtUtc,
    string Model,
    string ReasoningEffort,
    string ResultCategory,
    long ElapsedMilliseconds,
    int? ExitCode);

public sealed record TestRunResultView(
    TestResultViewStatus Classification,
    long ElapsedMilliseconds,
    string? DiagnosticCategory);

public sealed record TestNowRunView(EligibilityView Decision, TestRunResultView? Result);

public sealed record SchedulerOperationView(bool Succeeded, string? DiagnosticCategory);

public sealed record TimezoneUpdateView(
    TimezoneUpdateViewState State,
    bool Succeeded,
    string? DiagnosticCategory,
    bool ConfigSaved);

public sealed record AppScreenState(
    AutomationView Automation,
    EligibilityView Eligibility,
    ScheduleView Schedule,
    SchedulerView Scheduler,
    CodexEnvironmentView Codex,
    NextSlotView? NextSlot,
    IReadOnlyList<TodaySlotView> TodaySlots,
    LatestKnownResultView? LatestKnownResult,
    DateTimeOffset NowUtc,
    string CurrentWindowsTimeZoneId,
    string LogsDirectory);
