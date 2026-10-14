using System.Globalization;
using System.Resources;
using CodexSlotPing.App.ReadModels;

namespace CodexSlotPing.App;

public static class UiText
{
    private static readonly ResourceManager Resources = new(
        "CodexSlotPing.App.Localization.Strings",
        typeof(UiText).Assembly);

    public static CultureInfo ResolveCulture(CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        return uiCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("zh-CN")
            : CultureInfo.GetCultureInfo("en-US");
    }

    public static string Get(string key) => Get(key, CultureInfo.CurrentUICulture);

    public static string Get(string key, CultureInfo uiCulture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var culture = ResolveCulture(uiCulture);
        return Resources.GetString(key, culture) ?? key;
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(ResolveCulture(CultureInfo.CurrentUICulture), Get(key), arguments);

    public static string FormatReasoningEffort(string canonicalValue, CultureInfo? culture = null)
    {
        var key = canonicalValue switch
        {
            "low" => "Reasoning.Low",
            "medium" => "Reasoning.Medium",
            "high" => "Reasoning.High",
            "xhigh" => "Reasoning.XHigh",
            "max" => "Reasoning.Max",
            "ultra" => "Reasoning.Ultra",
            _ => null
        };
        return key is null ? canonicalValue : Get(key, culture ?? CultureInfo.CurrentUICulture);
    }

    public static string FormatTimeZone(string canonicalValue, CultureInfo? culture = null) =>
        string.Equals(canonicalValue, "SYSTEM", StringComparison.OrdinalIgnoreCase)
            ? Get("Settings.TimezoneSystem", culture ?? CultureInfo.CurrentUICulture)
            : canonicalValue;

    public static string FormatSchedulerState(SchedulerViewState state, CultureInfo? culture = null)
    {
        var key = state switch
        {
            SchedulerViewState.Enabled => "State.Enabled",
            SchedulerViewState.Disabled => "State.Disabled",
            SchedulerViewState.NotInstalled => "State.NotInstalled",
            SchedulerViewState.NeedsRepair => "State.NeedsRepair",
            SchedulerViewState.TimezoneChanged => "State.TimezoneChanged",
            SchedulerViewState.Conflict => "State.Conflict",
            SchedulerViewState.Error => "State.Error",
            _ => "State.Unknown"
        };
        return Get(key, culture ?? CultureInfo.CurrentUICulture);
    }

    public static string FormatAutomationControlState(
        bool enabled,
        bool isPaused,
        CultureInfo? culture = null) =>
        FormatAutomationControlState(enabled, isPaused, isPaused, culture);

    public static string FormatAutomationControlState(
        bool enabled,
        bool isPaused,
        bool pauseProtectionActive,
        CultureInfo? culture = null)
    {
        var key = (enabled, isPaused, pauseProtectionActive) switch
        {
            (_, true, _) when !enabled => "Automation.State.DisabledAndPaused",
            (_, true, _) => "Automation.State.Paused",
            (true, false, true) => "Automation.State.PauseNotPersisted",
            (true, false, false) => "Automation.State.Enabled",
            _ => "Automation.State.Disabled"
        };
        return Get(key, culture ?? CultureInfo.CurrentUICulture);
    }

    public static string FormatTraySchedulerState(SchedulerViewState state, CultureInfo? culture = null) =>
        string.Format(
            ResolveCulture(culture ?? CultureInfo.CurrentUICulture),
            Get("Tray.SchedulerState", culture ?? CultureInfo.CurrentUICulture),
            FormatSchedulerState(state, culture));

    public static string FormatSlotStatus(SlotViewStatus status, CultureInfo? culture = null)
    {
        var key = status switch
        {
            SlotViewStatus.ConfirmedSuccess => "Slot.Confirmed",
            SlotViewStatus.AttemptedUnconfirmed => "Slot.AttemptedUnconfirmed",
            SlotViewStatus.PrelaunchFailed => "Slot.PrelaunchFailed",
            SlotViewStatus.Missed => "Slot.Missed",
            SlotViewStatus.Claimed => "Slot.InProgress",
            _ => "Slot.Pending"
        };
        return Get(key, culture ?? CultureInfo.CurrentUICulture);
    }

    public static string FormatTestResult(TestResultViewStatus classification, long elapsedMilliseconds,
        string? diagnosticCategory)
    {
        var status = classification switch
        {
            TestResultViewStatus.ConfirmedSuccess => Get("Slot.Confirmed"),
            TestResultViewStatus.AttemptedUnconfirmed => Get("Slot.AttemptedUnconfirmed"),
            _ => Get("Slot.PrelaunchFailed")
        };
        var summary = Format("TestResult.Summary", status, elapsedMilliseconds);
        return string.IsNullOrWhiteSpace(diagnosticCategory)
            ? summary
            : $"{summary}{Environment.NewLine}{Format("TestResult.Reason", FormatDiagnostic(diagnosticCategory))}";
    }

    public static string FormatCodexDiagnostics(IEnumerable<string> diagnosticCodes)
    {
        ArgumentNullException.ThrowIfNull(diagnosticCodes);
        return string.Join(" · ", diagnosticCodes.Select(FormatCodexDiagnostic));
    }

    public static string FormatCodexDiagnostic(string diagnosticCode)
    {
        if (diagnosticCode.StartsWith("unsupported-option:", StringComparison.Ordinal))
        {
            return Format("Diagnostic.UnsupportedOption", diagnosticCode["unsupported-option:".Length..]);
        }

        var key = diagnosticCode switch
        {
            "direct-executable-not-found" or "npm-native-binary-not-found" => "Diagnostic.CodexNotFound",
            "cli-version-unconfirmed" => "Diagnostic.CliVersionUnknown",
            "chatgpt-login-unconfirmed" => "Diagnostic.LoginUnknown",
            "chatgpt-login-not-confirmed" => "Diagnostic.LoginNotConfirmed",
            "cli-capabilities-unconfirmed" => "Diagnostic.CliCapabilitiesUnknown",
            _ => null
        };
        return key is null ? FormatDiagnostic(diagnosticCode) : Get(key);
    }

    public static string FormatDiagnostic(string? message, CultureInfo? culture = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var selectedCulture = culture ?? CultureInfo.CurrentUICulture;
        var exactKey = message switch
        {
            "schemaVersion must be 1." => "Validation.SchemaVersion",
            "exactly four slot times are required." => "Validation.FourSlots",
            "slot times must be unique." => "Validation.UniqueSlots",
            "slot times must be at least six minutes apart, including across midnight." => "Validation.SlotGap",
            "timeZone must be SYSTEM or a Windows timezone ID." => "Validation.TimezoneRequired",
            "the configured timezone lock does not match the current Windows timezone ID." => "Validation.TimezoneMismatch",
            "model must be a non-empty selection request." => "Validation.ModelRequired",
            "reasoningEffort is not supported by the detected CLI compatibility set." => "Validation.ReasoningUnsupported",
            "prompt must be non-empty after trimming." => "Validation.PromptRequired",
            "prompt must contain valid Unicode scalar values." => "Validation.PromptUnicode",
            "prompt must not exceed 200 Unicode scalar values." => "Validation.PromptLength",
            "strictSlotWindowMinutes is fixed at 3 in v0.1." => "Validation.StrictWindow",
            "Timezone changed. Confirm updating the timezone lock and owned scheduler before saving." => "Validation.TimezoneChangedBeforeSave",
            "Settings could not be saved." => "Diagnostic.SettingsSaveFailed",
            "Settings were saved, but the owned scheduler needs repair." => "Diagnostic.SettingsSavedSchedulerRepair",
            "Settings were saved, but scheduler readback shows that repair is still needed." => "Diagnostic.SchedulerReadbackNeedsRepair",
            "scheduler-unavailable" => "Diagnostic.SchedulerUnavailable",
            "scheduler-readback-failed" or "readback-verification-failed" => "Diagnostic.SchedulerReadbackFailed",
            "scheduler-remove-unconfirmed" or "remove-verification-failed" => "Diagnostic.SchedulerRemoveFailed",
            "scheduler-update-failed" => "Diagnostic.SchedulerUpdateFailed",
            "invalid-configuration" => "Diagnostic.InvalidConfiguration",
            "cli-environment-not-ready" => "Diagnostic.CliNotReady",
            "runner-path-missing" or "invalid-runner-path" => "Diagnostic.RunnerMissing",
            "timezone-confirmation-required" or "timezone-mismatch" => "Validation.TimezoneChangedBeforeSave",
            "task-ownership-conflict" => "Diagnostic.TaskOwnershipConflict",
            "invalid-task-path" or "invalid-slot-definition" => "Diagnostic.InvalidTaskDefinition",
            "runner-action-mismatch" or "task-path-argument-mismatch" or "working-directory-mismatch" => "Diagnostic.SchedulerPathChanged",
            "owned-task-required" => "Diagnostic.OwnedTaskRequired",
            "timezone-config-save-failed" => "Diagnostic.TimezoneConfigSaveFailed",
            "automation-pause-not-persisted-protected" => "Automation.PauseNotPersistedProtected",
            "stream-error" => "Diagnostic.StreamError",
            "process-start-failed" => "Diagnostic.ProcessStartFailed",
            "cancelled-after-process-start" => "Diagnostic.Cancelled",
            "agent-message-missing" => "Diagnostic.AgentMessageMissing",
            "usage-limit" => "Diagnostic.UsageLimit",
            "stderr-output" => "Diagnostic.StderrOutput",
            _ => null
        };
        if (exactKey is not null)
        {
            return Get(exactKey, selectedCulture);
        }

        if (message.StartsWith("slot ", StringComparison.Ordinal) &&
            message.EndsWith(" must use 24-hour HH:mm format.", StringComparison.Ordinal))
        {
            var slotNumber = message[5..message.IndexOf(' ', 5)];
            return string.Format(ResolveCulture(selectedCulture), Get("Validation.SlotFormat", selectedCulture), slotNumber);
        }

        if (message.StartsWith("unsupported-option:", StringComparison.Ordinal))
        {
            return string.Format(ResolveCulture(selectedCulture), Get("Diagnostic.UnsupportedOption", selectedCulture),
                message["unsupported-option:".Length..]);
        }

        if (message.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':'))
        {
            return string.Format(ResolveCulture(selectedCulture), Get("Diagnostic.UnknownWithCode", selectedCulture), message);
        }

        return message;
    }
}
