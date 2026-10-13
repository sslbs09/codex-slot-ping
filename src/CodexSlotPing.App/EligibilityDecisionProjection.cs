using System.Globalization;
using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.App;

public sealed record EligibilityDecisionDisplay(
    string Status,
    string Reason,
    string Confidence,
    string? NextEligibleAt,
    string? Explanation);

public static class EligibilityDecisionProjection
{
    public static EligibilityDecisionDisplay Create(
        EligibilityDecision decision,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var selectedCulture = UiText.ResolveCulture(culture ?? CultureInfo.CurrentUICulture);
        return new EligibilityDecisionDisplay(
            UiText.Get($"Eligibility.Status.{decision.Status}", selectedCulture),
            UiText.Get($"Eligibility.Reason.{decision.Reason}", selectedCulture),
            UiText.Get($"Eligibility.Confidence.{decision.Confidence}", selectedCulture),
            decision.NextEligibleAtUtc?.ToLocalTime().ToString("g", selectedCulture),
            decision.ExplanationReason is { } explanationReason
                ? UiText.Get($"Eligibility.Reason.{explanationReason}", selectedCulture)
                : null);
    }

    public static string FormatTestNowConfirmation(
        EligibilityDecision decision,
        CultureInfo? culture = null)
    {
        var selectedCulture = UiText.ResolveCulture(culture ?? CultureInfo.CurrentUICulture);
        var display = Create(decision, selectedCulture);
        return string.Format(
            selectedCulture,
            UiText.Get("Main.TestNowConfirmWithDecision", selectedCulture),
            UiText.Get("Main.TestNowConfirmBody", selectedCulture),
            Environment.NewLine,
            display.Status,
            display.Reason,
            display.Confidence,
            display.NextEligibleAt ?? UiText.Get("Eligibility.None", selectedCulture),
            FormatExplanation(display.Explanation, selectedCulture));
    }

    public static string FormatDetails(
        EligibilityDecision decision,
        CultureInfo? culture = null)
    {
        var selectedCulture = UiText.ResolveCulture(culture ?? CultureInfo.CurrentUICulture);
        var display = Create(decision, selectedCulture);
        return string.Format(
            selectedCulture,
            UiText.Get("Main.EligibilityDetails", selectedCulture),
            display.Status,
            Environment.NewLine,
            display.Reason,
            display.Confidence,
            display.NextEligibleAt ?? UiText.Get("Eligibility.None", selectedCulture),
            FormatExplanation(display.Explanation, selectedCulture));
    }

    private static string FormatExplanation(string? explanation, CultureInfo culture) =>
        explanation is null
            ? string.Empty
            : Environment.NewLine + string.Format(
                culture,
                UiText.Get("Main.EligibilityExplanation", culture),
                explanation);
}
