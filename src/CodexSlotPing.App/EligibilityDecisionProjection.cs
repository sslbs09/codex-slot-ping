using System.Globalization;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.App.ReadModels;

namespace CodexSlotPing.App;

public static class EligibilityDecisionProjection
{
    public static EligibilityView Create(
        EligibilityDecision decision,
        CultureInfo? culture = null,
        DateTimeOffset? nextPlannedAttemptAtUtc = null,
        bool scheduleDeferredUntilResume = false)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var selectedCulture = UiText.ResolveCulture(culture ?? CultureInfo.CurrentUICulture);
        var view = new EligibilityView(
            MapStatus(decision.Status),
            UiText.Get($"Eligibility.Status.{decision.Status}", selectedCulture),
            UiText.Get($"Eligibility.Reason.{decision.Reason}", selectedCulture),
            UiText.Get($"Eligibility.Confidence.{decision.Confidence}", selectedCulture),
            decision.NextEligibleAtUtc,
            decision.NextEligibleAtUtc?.ToLocalTime().ToString("g", selectedCulture),
            decision.ExplanationReason is { } explanationReason
                ? UiText.Get($"Eligibility.Reason.{explanationReason}", selectedCulture)
                : null,
            decision.RequiresConfirmation,
            decision.Status == EligibilityStatus.Eligible,
            string.Empty,
            string.Empty);
        var projected = view with
        {
            DetailsText = FormatDetails(view, selectedCulture),
            TestNowConfirmationText = FormatTestNowConfirmation(view, selectedCulture),
            UserFacingStatus = FormatUserFacingStatus(view, selectedCulture),
            NextPlannedAttemptText = scheduleDeferredUntilResume
                ? UiText.Get("Main.ScheduleRecalculateAfterResume", selectedCulture)
                : FormatPlannedAttempt(nextPlannedAttemptAtUtc, selectedCulture)
        };
        return projected with { Details = FormatExplainabilityDetails(projected, selectedCulture) };
    }

    public static string FormatTestNowConfirmation(
        EligibilityView view,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var selectedCulture = UiText.ResolveCulture(culture ?? CultureInfo.CurrentUICulture);
        return string.Format(
            selectedCulture,
            UiText.Get("Main.TestNowConfirmWithDecision", selectedCulture),
            UiText.Get("Main.TestNowConfirmBody", selectedCulture),
            Environment.NewLine,
            view.StatusText,
            view.Reason,
            view.Confidence,
            view.NextEligibleAtText ?? UiText.Get("Eligibility.None", selectedCulture),
            FormatExplanation(view.Explanation, selectedCulture));
    }

    public static string FormatDetails(
        EligibilityView view,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var selectedCulture = UiText.ResolveCulture(culture ?? CultureInfo.CurrentUICulture);
        return string.Format(
            selectedCulture,
            UiText.Get("Main.EligibilityDetails", selectedCulture),
            view.StatusText,
            Environment.NewLine,
            view.Reason,
            view.Confidence,
            view.NextEligibleAtText ?? UiText.Get("Eligibility.None", selectedCulture),
            FormatExplanation(view.Explanation, selectedCulture));
    }

    private static EligibilityViewStatus MapStatus(EligibilityStatus status) => status switch
    {
        EligibilityStatus.Eligible => EligibilityViewStatus.Eligible,
        EligibilityStatus.NotYetEligible => EligibilityViewStatus.NotYetEligible,
        EligibilityStatus.Blocked => EligibilityViewStatus.Blocked,
        EligibilityStatus.ConfirmationRequired => EligibilityViewStatus.ConfirmationRequired,
        _ => EligibilityViewStatus.Unknown
    };

    private static string FormatExplanation(string? explanation, CultureInfo culture) =>
        explanation is null
            ? string.Empty
            : Environment.NewLine + string.Format(
                culture,
                UiText.Get("Main.EligibilityExplanation", culture),
                explanation);

    private static string FormatUserFacingStatus(EligibilityView view, CultureInfo culture)
    {
        var key = view.Status switch
        {
            EligibilityViewStatus.Eligible => "Eligibility.UserFacingStatus.Eligible",
            EligibilityViewStatus.NotYetEligible => "Eligibility.UserFacingStatus.NotYetEligible",
            EligibilityViewStatus.Blocked => "Eligibility.UserFacingStatus.Blocked",
            EligibilityViewStatus.ConfirmationRequired => "Eligibility.UserFacingStatus.ConfirmationRequired",
            _ => "Eligibility.UserFacingStatus.Unknown"
        };
        var template = UiText.Get(key, culture);
        return view.Status == EligibilityViewStatus.Blocked
            ? string.Format(culture, template, view.ReasonText)
            : template;
    }

    private static string FormatPlannedAttempt(DateTimeOffset? plannedAttemptAtUtc, CultureInfo culture) =>
        plannedAttemptAtUtc?.ToLocalTime().ToString("g", culture)
        ?? UiText.Get("Eligibility.None", culture);

    private static string FormatExplainabilityDetails(EligibilityView view, CultureInfo culture) =>
        string.Format(
            culture,
            UiText.Get("Main.EligibilityExplainabilityDetails", culture),
            view.UserFacingStatus,
            Environment.NewLine,
            view.ReasonText,
            view.ConfidenceText,
            view.NextPlannedAttemptText,
            FormatExplanation(view.Explanation, culture));
}
