using System.Windows;
using CodexSlotPing.App.ReadModels;
using WpfMessageBox = System.Windows.MessageBox;

namespace CodexSlotPing.App;

public partial class SettingsWindow : Window
{
    private AppScreenState _screenState;
    private bool _initializing = true;
    private readonly AppController _controller;
    private readonly AppUpdateController _updateController;
    private readonly Action<AppScreenState>? _stateChanged;

    public SettingsWindow(
        AppController controller,
        AppScreenState screenState,
        AppUpdateController updateController,
        Action<AppScreenState>? stateChanged = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _screenState = screenState ?? throw new ArgumentNullException(nameof(screenState));
        _updateController = updateController ?? throw new ArgumentNullException(nameof(updateController));
        _stateChanged = stateChanged;
        InitializeComponent();
        InitializeFields();
        RenderUpdateState(_updateController.CurrentState);
        _updateController.StateChanged += UpdateController_StateChanged;
        Closed += (_, _) => _updateController.StateChanged -= UpdateController_StateChanged;
    }

    private void InitializeFields()
    {
        var config = _screenState.Automation;
        Slot1Text.Text = config.LegacyFixedSlots.ElementAtOrDefault(0) ?? "07:00";
        Slot2Text.Text = config.LegacyFixedSlots.ElementAtOrDefault(1) ?? "12:01";
        Slot3Text.Text = config.LegacyFixedSlots.ElementAtOrDefault(2) ?? "17:01";
        Slot4Text.Text = config.LegacyFixedSlots.ElementAtOrDefault(3) ?? "22:01";
        ModelText.Text = config.Model;
        PromptText.Text = config.Prompt;
        WakeCheck.IsChecked = config.WakeToRun;
        RawLogsCheck.IsChecked = config.CaptureRawLogs;

        ReasoningCombo.DisplayMemberPath = nameof(LocalizedOption.Display);
        ReasoningCombo.SelectedValuePath = nameof(LocalizedOption.Value);
        var reasoningValues = _screenState.Codex.SupportedReasoningEfforts
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
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
        EligibilityDetailsText.Text = _screenState.Eligibility.Details;

        TimezoneUpdateButton.Visibility = _screenState.Scheduler.State == SchedulerViewState.TimezoneChanged
            ? Visibility.Visible
            : Visibility.Collapsed;
        RenderAutomationControl();
        RenderSchedulerState();
        _initializing = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = _screenState.Automation with
            {
                LegacyFixedSlots = [Slot1Text.Text.Trim(), Slot2Text.Text.Trim(), Slot3Text.Text.Trim(), Slot4Text.Text.Trim()],
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

        await RunSchedulerOperationAsync(
            _controller.InstallOrUpdateSchedulerAsync,
            _screenState.Automation.IsPauseProtectionActive
                ? "Settings.InstallPausedSuccess"
                : "Settings.InstallSuccess");
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

    private async void AutomationPause_Click(object sender, RoutedEventArgs e)
    {
        var wasPaused = _screenState.Automation.IsPauseProtectionActive;
        try
        {
            var result = wasPaused
                ? await _controller.ResumeAutomationAsync(CancellationToken.None)
                : await _controller.PauseAutomationAsync(CancellationToken.None);
            SetScreenState(await _controller.RefreshAsync(CancellationToken.None));
            StatusText.Text = result.Succeeded
                ? UiText.Get(wasPaused ? "Automation.ResumedMessage" : "Automation.PausedMessage")
                : UiText.FormatDiagnostic(result.DiagnosticCategory ?? "automation-control-save-failed");
        }
        catch (Exception exception)
        {
            StatusText.Text = UiText.Format("Settings.Exception", exception.Message);
        }
    }

    private async Task RunSchedulerOperationAsync(
        Func<CancellationToken, Task<SchedulerOperationView>> operation,
        string successMessageKey)
    {
        try
        {
            var result = await operation(CancellationToken.None);
            StatusText.Text = result.Succeeded
                ? UiText.Get(successMessageKey)
                : UiText.FormatDiagnostic(result.DiagnosticCategory ?? UiText.Get("Settings.OperationFailed"));
            _screenState = await _controller.RefreshAsync(CancellationToken.None);
            SetScreenState(_screenState);
            EligibilityDetailsText.Text = _screenState.Eligibility.Details;
            TimezoneUpdateButton.Visibility = _screenState.Scheduler.State == SchedulerViewState.TimezoneChanged
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
            SetScreenState(_screenState);
            TimeZoneCombo.Items.Clear();
            AddTimeZoneOption("SYSTEM");
            if (!TimeZoneCombo.Items.Cast<LocalizedOption>().Any(option =>
                    string.Equals(option.Value, _screenState.CurrentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase)))
            {
                AddTimeZoneOption(_screenState.CurrentWindowsTimeZoneId);
            }
            TimeZoneCombo.SelectedValue = _screenState.Automation.TimeZone;
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

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            await _updateController.CheckForUpdatesAsync(manualRefresh: true, CancellationToken.None);
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = UiText.Format("Settings.Exception", exception.Message);
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void UpdateController_StateChanged(UpdateReadModel state) => RenderUpdateState(state);

    private void RenderUpdateState(UpdateReadModel state)
    {
        UpdateStatusText.Text = state.Status switch
        {
            UpdateViewStatus.Unknown => UiText.Get("Update.Unknown"),
            UpdateViewStatus.Checking => UiText.Get("Update.Checking"),
            UpdateViewStatus.UpToDate => UiText.Format("Update.UpToDate", state.CurrentVersion),
            UpdateViewStatus.UpdateAvailable => UiText.Format("Update.Available", state.TargetVersion ?? string.Empty),
            UpdateViewStatus.Failed => UiText.Format("Update.Failed", UiText.Get(state.ErrorCategory switch
            {
                UpdateFailureCategory.Network => "Update.Error.Network",
                UpdateFailureCategory.Timeout => "Update.Error.Timeout",
                UpdateFailureCategory.RateLimited => "Update.Error.RateLimited",
                UpdateFailureCategory.MalformedResponse => "Update.Error.MalformedResponse",
                UpdateFailureCategory.NoStableRelease => "Update.Error.NoStableRelease",
                UpdateFailureCategory.InvalidAssetContract => "Update.Error.InvalidAssetContract",
                UpdateFailureCategory.InvalidCurrentVersion => "Update.Error.InvalidCurrentVersion",
                _ => "Update.Error.Api"
            })),
            _ => UiText.Get("Update.Unknown")
        };

        UpdateReleaseText.Text = state.ReleaseNotes ?? string.Empty;
        UpdateReleaseText.Visibility = string.IsNullOrWhiteSpace(state.ReleaseNotes) ? Visibility.Collapsed : Visibility.Visible;
        UpdateReleaseUrlText.Text = state.ReleaseUrl ?? string.Empty;
        UpdateReleaseUrlText.Visibility = string.IsNullOrWhiteSpace(state.ReleaseUrl) ? Visibility.Collapsed : Visibility.Visible;
        var assetLines = new[] { state.ZipAsset, state.ChecksumAsset }
            .Where(asset => asset is not null)
            .Select(asset => $"{asset!.FileName} ({asset.Size:N0} bytes)");
        UpdateAssetText.Text = string.Join(Environment.NewLine, assetLines);
        UpdateAssetText.Visibility = string.IsNullOrWhiteSpace(UpdateAssetText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderAutomationControl()
    {
        AutomationStatusText.Text = UiText.FormatAutomationControlState(
            _screenState.Automation.Enabled,
            _screenState.Automation.IsPaused,
            _screenState.Automation.IsPauseProtectionActive);
        AutomationPauseButton.Content = UiText.Get(_screenState.Automation.IsPauseProtectionActive
            ? "Automation.Resume"
            : "Automation.Pause");
        AutomationPauseButton.IsEnabled =
            _screenState.Automation.Enabled || _screenState.Automation.IsPauseProtectionActive;
    }

    private void RenderSchedulerState()
    {
        SchedulerStatusText.Text = _screenState.Scheduler.DiagnosticCategory == "scheduler-enabled-while-paused"
            ? UiText.Get("Scheduler.PauseDisableFailed")
            : UiText.FormatSchedulerState(_screenState.Scheduler.State);
    }

    private void SetScreenState(AppScreenState state)
    {
        _screenState = state;
        EligibilityDetailsText.Text = state.Eligibility.Details;
        RenderAutomationControl();
        RenderSchedulerState();
        _stateChanged?.Invoke(state);
    }

    private sealed record LocalizedOption(string Value, string Display);
}
