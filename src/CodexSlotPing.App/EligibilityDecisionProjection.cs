using System.Globalization;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.App.ReadModels;

namespace CodexSlotPing.App;

public static class EligibilityDecisionProjection
{
    public static EligibilityView Create(
        EligibilityDecision decision,
        CultureInfo? culture = null)
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
        return view with
        {
            DetailsText = FormatDetails(view, selectedCulture),
            TestNowConfirmationText = FormatTestNowConfirmation(view, selectedCulture)
        };
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
}
