using System.Globalization;
using CodexSlotPing.App;

namespace CodexSlotPing.App.ReadModels;

public enum TraySummaryStatus
{
    SchedulerError,
    Paused,
    PauseNotPersisted,
    Disabled,
    Blocked,
    Unknown,
    ConfirmationRequired,
    Waiting,
    Ready
}

public sealed record TrayMenuState(
    string StatusSummary,
    string AutomationControlText,
    string PauseResumeText,
    bool PauseResumeEnabled,
    string SchedulerText,
    bool WakeToRunChecked);

public sealed record TraySummaryView(
    TraySummaryStatus Status,
    string StatusSummary,
    string TooltipText,
    TrayMenuState Menu);

public static class TraySummaryProjection
{
    private const int MaxTooltipLength = 63;

    public static TraySummaryView Create(AppScreenState state, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var selectedCulture = UiText.ResolveCulture(culture ?? CultureInfo.CurrentUICulture);

        var (status, summary) = ProjectStatus(state, selectedCulture);
        var tooltipTemplate = UiText.Get("Tray.Tooltip", selectedCulture);
        var tooltip = string.Format(selectedCulture, tooltipTemplate, summary);
        if (tooltip.Length > MaxTooltipLength)
        {
            tooltip = tooltip[..(MaxTooltipLength - 3)] + "...";
        }

        return new TraySummaryView(
            status,
            summary,
            tooltip,
            new TrayMenuState(
                summary,
                string.Format(
                    selectedCulture,
                    UiText.Get("Tray.AutomationControl", selectedCulture),
                    UiText.FormatAutomationControlState(
                        state.Automation.Enabled,
                        state.Automation.IsPaused,
                        state.Automation.IsPauseProtectionActive,
                        selectedCulture)),
                UiText.Get(state.Automation.IsPauseProtectionActive ? "Automation.Resume" : "Automation.Pause", selectedCulture),
                state.Automation.Enabled || state.Automation.IsPauseProtectionActive,
                UiText.FormatTraySchedulerState(state.Scheduler.State, selectedCulture),
                state.Scheduler.EffectiveWakeToRun));
    }

    private static (TraySummaryStatus Status, string Summary) ProjectStatus(
        AppScreenState state,
        CultureInfo culture)
    {
        if (HasSchedulerError(state))
        {
            return (TraySummaryStatus.SchedulerError, UiText.Get("Tray.Status.SchedulerError", culture));
        }

        if (state.Automation.IsPauseProtectionActive)
        {
            return state.Automation.IsPaused
                ? (TraySummaryStatus.Paused, UiText.Get("Tray.Status.Paused", culture))
                : (TraySummaryStatus.PauseNotPersisted, UiText.Get("Tray.Status.PauseNotPersisted", culture));
        }

        if (!state.Automation.Enabled)
        {
            return (TraySummaryStatus.Disabled, UiText.Get("Tray.Status.Disabled", culture));
        }

        return state.Eligibility.Status switch
        {
            EligibilityViewStatus.Blocked => (
                TraySummaryStatus.Blocked,
                string.Format(
                    culture,
                    UiText.Get("Tray.Status.Blocked", culture),
                    state.Eligibility.ReasonText)),
            EligibilityViewStatus.Unknown => (
                TraySummaryStatus.Unknown,
                state.Eligibility.UserFacingStatus),
            EligibilityViewStatus.ConfirmationRequired => (
                TraySummaryStatus.ConfirmationRequired,
                state.Eligibility.UserFacingStatus),
            EligibilityViewStatus.NotYetEligible => (
                TraySummaryStatus.Waiting,
                string.Format(
                    culture,
                    UiText.Get("Tray.Status.Waiting", culture),
                    FormatPlannedAttempt(state, culture))),
            _ => (TraySummaryStatus.Ready, UiText.Get("Tray.Status.Ready", culture))
        };
    }

    private static bool HasSchedulerError(AppScreenState state)
    {
        if (state.Scheduler.State is SchedulerViewState.NeedsRepair
            or SchedulerViewState.TimezoneChanged
            or SchedulerViewState.Conflict
            or SchedulerViewState.Error)
        {
            return true;
        }

        if (state.Automation.IsPauseProtectionActive)
        {
            return state.Scheduler.State is not SchedulerViewState.Disabled
                and not SchedulerViewState.NotInstalled || state.Scheduler.IsEnabled;
        }

        return state.Automation.Enabled
            && (state.Scheduler.State != SchedulerViewState.Enabled || !state.Scheduler.IsEnabled);
    }

    private static string FormatPlannedAttempt(AppScreenState state, CultureInfo culture) =>
        state.Schedule.NextAttemptAtUtc is { } nextAttemptAtUtc
            ? nextAttemptAtUtc.ToLocalTime().ToString("HH:mm", culture)
            : UiText.Get("Eligibility.None", culture);
}
