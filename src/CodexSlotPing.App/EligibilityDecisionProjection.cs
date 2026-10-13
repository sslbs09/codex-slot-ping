using System.Globalization;
using CodexSlotPing.Core.Eligibility;

namespace CodexSlotPing.App;

public sealed record EligibilityDecisionDisplay(
    string Status,
    string Reason,
    string Confidence,
    string? NextEligibleAt);

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
            decision.NextEligibleAtUtc?.ToLocalTime().ToString("g", selectedCulture));
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
            display.NextEligibleAt ?? UiText.Get("Eligibility.None", selectedCulture));
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
            display.NextEligibleAt ?? UiText.Get("Eligibility.None", selectedCulture));
    }
}
