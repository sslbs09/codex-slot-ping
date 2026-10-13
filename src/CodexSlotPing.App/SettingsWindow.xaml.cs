using System.Windows;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Scheduler;
using WpfMessageBox = System.Windows.MessageBox;

namespace CodexSlotPing.App;

public partial class SettingsWindow : Window
{
    private AppScreenState _screenState;
    private bool _initializing = true;
    private readonly AppController _controller;

    public SettingsWindow(AppController controller, AppScreenState screenState)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _screenState = screenState ?? throw new ArgumentNullException(nameof(screenState));
        InitializeComponent();
        InitializeFields();
    }

    private void InitializeFields()
    {
        var config = _screenState.Config;
        Slot1Text.Text = config.Slots.ElementAtOrDefault(0) ?? "07:00";
        Slot2Text.Text = config.Slots.ElementAtOrDefault(1) ?? "12:01";
        Slot3Text.Text = config.Slots.ElementAtOrDefault(2) ?? "17:01";
        Slot4Text.Text = config.Slots.ElementAtOrDefault(3) ?? "22:01";
        ModelText.Text = config.Model;
        PromptText.Text = config.Prompt;
        WakeCheck.IsChecked = config.WakeToRun;
        RawLogsCheck.IsChecked = config.CaptureRawLogs;

        ReasoningCombo.DisplayMemberPath = nameof(LocalizedOption.Display);
        ReasoningCombo.SelectedValuePath = nameof(LocalizedOption.Value);
        var reasoningValues = _screenState.Codex.SupportedReasoningEfforts.Count > 0
            ? _screenState.Codex.SupportedReasoningEfforts.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            : CodexCliCapabilities.DocumentedReasoningEfforts.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        foreach (var reasoning in reasoningValues.Append(config.ReasoningEffort).Distinct(StringComparer.Ordinal))
        {
            ReasoningCombo.Items.Add(new LocalizedOption(reasoning, UiText.FormatReasoningEffort(reasoning)));
        }
        ReasoningCombo.SelectedValue = config.ReasoningEffort;

        TimeZoneCombo.DisplayMemberPath = nameof(LocalizedOption.Display);
        TimeZoneCombo.SelectedValuePath = nameof(LocalizedOption.Value);
        AddTimeZoneOption("SYSTEM");
        if (!string.Equals(_screenState.CurrentWindowsTimeZoneId, "SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            AddTimeZoneOption(_screenState.CurrentWindowsTimeZoneId);
        }
        if (!TimeZoneCombo.Items.Cast<LocalizedOption>().Any(option =>
                string.Equals(option.Value, config.TimeZone, StringComparison.OrdinalIgnoreCase)))
        {
            AddTimeZoneOption(config.TimeZone);
        }
        TimeZoneCombo.SelectedValue = config.TimeZone;
        EligibilityDetailsText.Text = EligibilityDecisionProjection.FormatDetails(_screenState.EligibilityDecision);

        TimezoneUpdateButton.Visibility = _screenState.Scheduler.State == SchedulerState.TIMEZONE_CHANGED
            ? Visibility.Visible
            : Visibility.Collapsed;
        _initializing = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = _screenState.Config with
            {
                Slots = [Slot1Text.Text.Trim(), Slot2Text.Text.Trim(), Slot3Text.Text.Trim(), Slot4Text.Text.Trim()],
                Model = ModelText.Text.Trim(),
                ReasoningEffort = ReasoningCombo.SelectedValue?.ToString() ?? string.Empty,
                Prompt = PromptText.Text,
                TimeZone = TimeZoneCombo.SelectedValue?.ToString() ?? "SYSTEM",
                WakeToRun = WakeCheck.IsChecked == true,
                CaptureRawLogs = RawLogsCheck.IsChecked == true
            };
            var result = await _controller.SaveSettingsAsync(candidate, CancellationToken.None);
            if (!result.Succeeded)
            {
                StatusText.Text = string.Join(Environment.NewLine, result.Errors.Select(error => UiText.FormatDiagnostic(error)));
                return;
            }

            StatusText.Text = UiText.Get("Settings.Saved");
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = UiText.Format("Diagnostic.SettingsException", exception.Message);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            UiText.Get("Settings.InstallConfirmBody"),
            UiText.Get("Settings.InstallConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunSchedulerOperationAsync(_controller.InstallOrUpdateSchedulerAsync, "Settings.InstallSuccess");
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(UiText.Get("Settings.DisableConfirmBody"), UiText.Get("Settings.DisableConfirmTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await RunSchedulerOperationAsync(_controller.DisableSchedulerAsync, "Settings.DisableSuccess");
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(UiText.Get("Settings.RemoveConfirmBody"),
            UiText.Get("Settings.RemoveConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await RunSchedulerOperationAsync(_controller.RemoveSchedulerAsync, "Settings.RemoveSuccess");
        }
    }

    private async Task RunSchedulerOperationAsync(
        Func<CancellationToken, Task<SchedulerOperationResult>> operation,
        string successMessageKey)
    {
        try
        {
            var result = await operation(CancellationToken.None);
            StatusText.Text = result.Succeeded
                ? UiText.Get(successMessageKey)
                : UiText.FormatDiagnostic(result.DiagnosticCategory ?? UiText.Get("Settings.OperationFailed"));
            _screenState = await _controller.RefreshAsync(CancellationToken.None);
            EligibilityDetailsText.Text = EligibilityDecisionProjection.FormatDetails(_screenState.EligibilityDecision);
            TimezoneUpdateButton.Visibility = _screenState.Scheduler.State == SchedulerState.TIMEZONE_CHANGED
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            StatusText.Text = UiText.Format("Settings.Exception", exception.Message);
        }
    }

    private async void TimezoneUpdate_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            UiText.Get("Settings.TimezoneConfirmBody"),
            UiText.Get("Settings.TimezoneConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _controller.ApplyCurrentTimeZoneChangeAsync(true, CancellationToken.None);
            if (!result.Succeeded)
            {
                StatusText.Text = UiText.FormatDiagnostic(result.DiagnosticCategory ?? UiText.Get("Settings.TimezoneUpdateFailed"));
                return;
            }

            _screenState = await _controller.RefreshAsync(CancellationToken.None);
            TimeZoneCombo.Items.Clear();
            AddTimeZoneOption("SYSTEM");
            if (!TimeZoneCombo.Items.Cast<LocalizedOption>().Any(option =>
                    string.Equals(option.Value, _screenState.CurrentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase)))
            {
                AddTimeZoneOption(_screenState.CurrentWindowsTimeZoneId);
            }
            TimeZoneCombo.SelectedValue = _screenState.Config.TimeZone;
            TimezoneUpdateButton.Visibility = Visibility.Collapsed;
            StatusText.Text = UiText.Get("Settings.TimezoneUpdated");
        }
        catch (Exception exception)
        {
            StatusText.Text = UiText.Format("Settings.Exception", exception.Message);
        }
    }

    private void RawLogs_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var answer = WpfMessageBox.Show(
            UiText.Get("Settings.RawLogsConfirmBody"),
            UiText.Get("Settings.RawLogsConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            _initializing = true;
            RawLogsCheck.IsChecked = false;
            _initializing = false;
        }
    }

    private void RawLogs_Unchecked(object sender, RoutedEventArgs e)
    {
    }

    private void AddTimeZoneOption(string value)
    {
        TimeZoneCombo.Items.Add(new LocalizedOption(value, UiText.FormatTimeZone(value)));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record LocalizedOption(string Value, string Display);
}
