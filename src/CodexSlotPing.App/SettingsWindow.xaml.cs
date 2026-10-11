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

        var reasoningValues = _screenState.Codex.SupportedReasoningEfforts.Count > 0
            ? _screenState.Codex.SupportedReasoningEfforts.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            : CodexCliCapabilities.DocumentedReasoningEfforts.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        foreach (var reasoning in reasoningValues.Append(config.ReasoningEffort).Distinct(StringComparer.Ordinal))
        {
            ReasoningCombo.Items.Add(reasoning);
        }
        ReasoningCombo.SelectedItem = config.ReasoningEffort;

        TimeZoneCombo.Items.Add("SYSTEM");
        if (!string.Equals(_screenState.CurrentWindowsTimeZoneId, "SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            TimeZoneCombo.Items.Add(_screenState.CurrentWindowsTimeZoneId);
        }
        if (!TimeZoneCombo.Items.Contains(config.TimeZone))
        {
            TimeZoneCombo.Items.Add(config.TimeZone);
        }
        TimeZoneCombo.SelectedItem = config.TimeZone;

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
                ReasoningEffort = ReasoningCombo.SelectedItem?.ToString() ?? string.Empty,
                Prompt = PromptText.Text,
                TimeZone = TimeZoneCombo.SelectedItem?.ToString() ?? "SYSTEM",
                WakeToRun = WakeCheck.IsChecked == true,
                CaptureRawLogs = RawLogsCheck.IsChecked == true
            };
            var result = await _controller.SaveSettingsAsync(candidate, CancellationToken.None);
            if (!result.Succeeded)
            {
                StatusText.Text = string.Join(Environment.NewLine, result.Errors);
                return;
            }

            StatusText.Text = "Settings saved.";
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            "This creates or updates the owned Windows scheduled task. At each eligible slot it sends your configured prompt to Codex and may count toward usage limits. Continue?",
            "Confirm scheduler installation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunSchedulerOperationAsync(_controller.InstallOrUpdateSchedulerAsync, "Scheduler installed and read back as ON.");
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show("Disable the owned Codex Slot Ping scheduled task?", "Disable scheduler",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await RunSchedulerOperationAsync(_controller.DisableSchedulerAsync, "Scheduler is OFF.");
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show("Remove the owned Codex Slot Ping scheduled task? This does not delete your local settings or logs.",
            "Remove scheduler", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await RunSchedulerOperationAsync(_controller.RemoveSchedulerAsync, "Owned scheduled task removed.");
        }
    }

    private async Task RunSchedulerOperationAsync(
        Func<CancellationToken, Task<SchedulerOperationResult>> operation,
        string successMessage)
    {
        try
        {
            var result = await operation(CancellationToken.None);
            StatusText.Text = result.Succeeded ? successMessage : result.DiagnosticCategory ?? "Scheduler operation failed.";
            _screenState = await _controller.RefreshAsync(CancellationToken.None);
            TimezoneUpdateButton.Visibility = _screenState.Scheduler.State == SchedulerState.TIMEZONE_CHANGED
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void TimezoneUpdate_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            "This saves the current Windows timezone and updates the owned scheduled task. Its current enabled or disabled state is preserved. Continue?",
            "Confirm timezone update",
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
                StatusText.Text = result.DiagnosticCategory ?? "Timezone update failed.";
                return;
            }

            _screenState = await _controller.RefreshAsync(CancellationToken.None);
            TimeZoneCombo.Items.Clear();
            TimeZoneCombo.Items.Add("SYSTEM");
            if (!TimeZoneCombo.Items.Contains(_screenState.CurrentWindowsTimeZoneId))
            {
                TimeZoneCombo.Items.Add(_screenState.CurrentWindowsTimeZoneId);
            }
            TimeZoneCombo.SelectedItem = _screenState.Config.TimeZone;
            TimezoneUpdateButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Current timezone saved and scheduler readback verified.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void RawLogs_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var answer = WpfMessageBox.Show(
            "Raw Codex output can contain responses and diagnostic text. It is stored locally and removed after 14 days. Enable raw capture?",
            "Confirm raw log capture",
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
