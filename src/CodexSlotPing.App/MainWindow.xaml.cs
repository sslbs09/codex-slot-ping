using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexSlotPing.App.ReadModels;
using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfMessageBox = System.Windows.MessageBox;

namespace CodexSlotPing.App;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly AppUpdateController _updateController;
    private UiPreferencesStore? _preferencesStore;
    private TrayIconController? _tray;
    private AppScreenState? _screenState;
    private bool _exitRequested;
    private bool _testNowRunning;

    public MainWindow() : this(AppController.CreateDefault(), new UiPreferencesStore(), AppUpdateController.CreateDefault())
    {
    }

    public MainWindow(AppController controller) : this(controller, new UiPreferencesStore(), AppUpdateController.CreateDefault())
    {
    }

    public MainWindow(AppController controller, UiPreferencesStore preferencesStore)
        : this(controller, preferencesStore, AppUpdateController.CreateDefault())
    {
    }

    public MainWindow(AppController controller, UiPreferencesStore preferencesStore, AppUpdateController updateController)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _updateController = updateController ?? throw new ArgumentNullException(nameof(updateController));
        InitializeComponent();
        Closing += Window_Closing;
    }

    public void AttachTray(TrayIconController tray)
    {
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        if (_screenState is not null)
        {
            _tray.UpdateState(_screenState);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            _screenState = await _controller.RefreshAsync(CancellationToken.None);
            Render(_screenState);
            _tray?.UpdateState(_screenState);
        }
        catch (Exception exception)
        {
            StatusMessageText.Text = UiText.Format("Main.StatusRefreshFailed", exception.Message);
            ConnectionDot.Foreground = System.Windows.Media.Brushes.DarkOrange;
            ConnectionStatusText.Text = UiText.Get("Main.NeedsAttention");
        }
    }

    private void Render(AppScreenState state)
    {
        var codexReady = state.Codex.IsReady;
        ConnectionDot.Foreground = codexReady ? Brush("#31845B") : Brush("#B46935");
        ConnectionStatusText.Text = UiText.Get(codexReady ? "Main.Connected" : "Main.NeedsAttention");
        LoginStatusText.Text = state.Codex.ChatGptLoginConfirmed switch
        {
            true => UiText.Get("Main.LoggedIn"),
            false => UiText.Get("Main.LoginNotConfirmed"),
            _ => UiText.Get("Main.LoginUnknown")
        };
        VersionText.Text = state.Codex.CliVersion ?? UiText.Get("Main.CliVersionUnavailable");

        if (state.Schedule.IsDeferredUntilResume)
        {
            NextSlotTimeText.Text = "—";
            NextSlotCountdownText.Text = UiText.Get("Main.ScheduleRecalculateAfterResume");
        }
        else if (state.Schedule.NextAttemptAtUtc is not { } nextAttemptUtc)
        {
            NextSlotTimeText.Text = "—";
            NextSlotCountdownText.Text = state.Scheduler.State == SchedulerViewState.TimezoneChanged
                ? UiText.Get("Main.UpdateTimezoneForNext")
                : UiText.Get("Main.NoPlannedAttempt");
        }
        else
        {
            NextSlotTimeText.Text = nextAttemptUtc.ToLocalTime().ToString("g");
            NextSlotCountdownText.Text = nextAttemptUtc <= state.NowUtc
                ? UiText.Get("Main.AttemptPlannedNow")
                : UiText.Format("Main.StartsIn", FormatDuration(nextAttemptUtc - state.NowUtc));
        }

        TodayRows.ItemsSource = state.TodaySlots.Select(slot => new TodayRow(
            slot.LocalTime.ToString("HH:mm"),
            FormatSlotStatus(slot.Status))).ToArray();
        EligibilityDetailsText.Text = state.Eligibility.Details;
        ModelText.Text = state.Automation.Model;
        ReasoningText.Text = UiText.FormatReasoningEffort(state.Automation.ReasoningEffort);
        SchedulerText.Text = UiText.FormatSchedulerState(state.Scheduler.State);
        AutomationStatusText.Text = UiText.FormatAutomationControlState(
            state.Automation.Enabled,
            state.Automation.IsPaused,
            state.Automation.IsPauseProtectionActive);
        AutomationPauseButton.Content = UiText.Get(state.Automation.IsPauseProtectionActive
            ? "Automation.Resume"
            : "Automation.Pause");
        AutomationPauseButton.IsEnabled = state.Automation.Enabled || state.Automation.IsPauseProtectionActive;
        WakeText.Text = UiText.Get(state.Scheduler.EffectiveWakeToRun ? "State.Enabled" : "State.Disabled");

        var messages = new List<string>();
        if (!codexReady)
        {
            messages.Add(UiText.FormatCodexDiagnostics(state.Codex.DiagnosticCodes));
            EnvironmentInstructionsText.Text = UiText.Get("Main.OfficialCliInstructions");
            EnvironmentInstructionsText.Visibility = Visibility.Visible;
            CopyInstructionsButton.Visibility = Visibility.Visible;
        }
        else
        {
            EnvironmentInstructionsText.Visibility = Visibility.Collapsed;
            CopyInstructionsButton.Visibility = Visibility.Collapsed;
        }

        switch (state.Scheduler.State)
        {
            case SchedulerViewState.NeedsRepair:
                messages.Add(state.Scheduler.DiagnosticCategory == "runner-action-mismatch"
                    ? UiText.Get("Scheduler.RepairPathChanged")
                    : UiText.Get("Scheduler.RepairContract"));
                break;
            case SchedulerViewState.TimezoneChanged:
                messages.Add(UiText.Format("Scheduler.TimezoneChanged", state.Automation.TimeZone, state.CurrentWindowsTimeZoneId));
                break;
            case SchedulerViewState.Conflict:
                messages.Add(UiText.Get("Scheduler.Conflict"));
                break;
            case SchedulerViewState.Error:
                messages.Add(UiText.Get(state.Scheduler.DiagnosticCategory == "scheduler-enabled-while-paused"
                    ? "Scheduler.PauseDisableFailed"
                    : "Scheduler.ReadFailed"));
                break;
        }

        StatusMessageText.Text = string.Join(Environment.NewLine, messages);
        TimezoneActionButton.Visibility = state.Scheduler.State == SchedulerViewState.TimezoneChanged
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void TestNow_Click(object sender, RoutedEventArgs e) => await RunTestNowFromTrayAsync();

    private async void ToggleAutomation_Click(object sender, RoutedEventArgs e) =>
        await ToggleAutomationAsync(showFailureDialog: false);

    internal Task ToggleAutomationFromTrayAsync() => ToggleAutomationAsync(showFailureDialog: true);

    private async Task ToggleAutomationAsync(bool showFailureDialog)
    {
        if (_screenState is null)
        {
            return;
        }

        var wasPaused = _screenState.Automation.IsPauseProtectionActive;
        try
        {
            var operation = wasPaused
                ? await _controller.ResumeAutomationAsync(CancellationToken.None)
                : await _controller.PauseAutomationAsync(CancellationToken.None);
            await RefreshAsync();

            if (operation.Succeeded)
            {
                StatusMessageText.Text = UiText.Get(wasPaused
                    ? "Automation.ResumedMessage"
                    : "Automation.PausedMessage");
                return;
            }

            var message = operation.DiagnosticCategory switch
            {
                "automation-pause-not-persisted-protected" =>
                    UiText.Get("Automation.PauseNotPersistedProtected"),
                "automation-control-save-failed" => UiText.Get("Automation.ControlSaveFailed"),
                _ => UiText.Get("Automation.SchedulerSyncFailed")
            };
            StatusMessageText.Text = operation.DiagnosticCategory == "automation-pause-not-persisted-protected"
                ? message
                : $"{message} {UiText.FormatDiagnostic(operation.DiagnosticCategory ?? string.Empty)}";
            if (showFailureDialog)
            {
                ShowTrayMessage(StatusMessageText.Text);
            }
        }
        catch (Exception exception)
        {
            StatusMessageText.Text = UiText.Format("Settings.Exception", exception.Message);
            if (showFailureDialog)
            {
                ShowTrayMessage(StatusMessageText.Text);
            }
        }
    }

    internal async Task RunTestNowFromTrayAsync()
    {
        if (_screenState is null || _testNowRunning)
        {
            return;
        }

        ShowFromTray();

        EligibilityView testNowDecision;
        try
        {
            testNowDecision = await _controller.GetTestNowEligibilityViewAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(UiText.Format("Main.StatusRefreshFailed", exception.Message),
                UiText.Get("Main.TestNowFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!testNowDecision.RequiresConfirmation)
        {
            WpfMessageBox.Show(
                testNowDecision.DetailsText,
                UiText.Get("Main.TestNowResultTitle"),
                MessageBoxButton.OK,
                testNowDecision.IsEligible
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
            await RefreshAsync();
            return;
        }

        var answer = WpfMessageBox.Show(
            testNowDecision.TestNowConfirmationText,
            UiText.Get("Main.TestNowConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _testNowRunning = true;
        TestNowButton.IsEnabled = false;
        _tray?.SetTestNowRunning(true);
        StatusMessageText.Text = UiText.Get("Main.TestNowRunning");
        try
        {
            var response = await _controller.RunTestNowAsync(true, CancellationToken.None);
            if (response?.Result is { } result)
            {
                WpfMessageBox.Show(UiText.FormatTestResult(
                        result.Classification, result.ElapsedMilliseconds, result.DiagnosticCategory),
                    UiText.Get("Main.TestNowResultTitle"), MessageBoxButton.OK,
                    result.Classification == TestResultViewStatus.ConfirmedSuccess
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
            }
            else if (response is not null)
            {
                WpfMessageBox.Show(response.Decision.DetailsText,
                    UiText.Get("Main.TestNowResultTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                StatusMessageText.Text = UiText.Get("Main.TestNowAlreadyRunning");
            }
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(UiText.Format("Settings.Exception", exception.Message),
                UiText.Get("Main.TestNowFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _testNowRunning = false;
            TestNowButton.IsEnabled = true;
            _tray?.SetTestNowRunning(false);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async void OpenSettingsShortcut_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        if (_screenState is null)
        {
            return;
        }

        var window = new SettingsWindow(_controller, _screenState, _updateController, OnSettingsStateChanged) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await RefreshAsync();
        }
    }

    private void OnSettingsStateChanged(AppScreenState state)
    {
        _screenState = state;
        Render(state);
        _tray?.UpdateState(state);
    }

    internal async void OpenSettingsFromTray()
    {
        ShowFromTray();
        await OpenSettingsAsync();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
        => OpenLogsFromTray();

    internal void OpenLogsFromTray()
    {
        if (_screenState is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_screenState.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _screenState.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(UiText.Format("Main.LogsOpenFailed", exception.Message), UiText.Get("Main.OpenLogs"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    internal void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    internal void ShowTrayMessage(string message) =>
        WpfMessageBox.Show(message, "Codex Slot Ping", MessageBoxButton.OK, MessageBoxImage.Information);

    internal async Task RequestExitAsync()
    {
        if (_testNowRunning)
        {
            WpfMessageBox.Show(UiText.Get("Exit.TestNowRunning"), "Codex Slot Ping", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_screenState is null)
        {
            await RefreshAsync();
        }

        var schedulerOn = _screenState is { Scheduler.Exists: true, Scheduler.IsOwned: true, Scheduler.IsEnabled: true };
        var preferences = new UiPreferences();
        try
        {
            if (_preferencesStore is not null)
            {
                preferences = await _preferencesStore.LoadAsync(CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // A missing or unreadable UI preference never changes scheduler behavior.
        }

        if (AppExitPolicy.ShouldShowWarning(schedulerOn, preferences.SuppressExitWarning))
        {
            var dialog = new ExitWarningDialog { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (dialog.SuppressFutureWarnings && _preferencesStore is not null)
            {
                try
                {
                    await _preferencesStore.SaveAsync(new UiPreferences(SuppressExitWarning: true), CancellationToken.None);
                }
                catch (Exception exception)
                {
                    WpfMessageBox.Show(UiText.Format("Exit.PreferenceSaveFailed", exception.Message),
                        "Codex Slot Ping", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        if (!AppExitPolicy.TryRequestExit(_testNowRunning, () =>
            {
                _exitRequested = true;
                _tray?.Dispose();
                System.Windows.Application.Current.Shutdown();
            }))
        {
            WpfMessageBox.Show(UiText.Get("Exit.TestNowRunning"), "Codex Slot Ping", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (AppExitPolicy.ShouldHideToTray(_exitRequested))
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray?.Dispose();
    }

    private void CopyInstructions_Click(object sender, RoutedEventArgs e)
    {
        WpfClipboard.SetText(UiText.Get("Main.OfficialCliInstructions"));
        StatusMessageText.Text = UiText.Get("Main.InstructionsCopied");
    }

    private async void TimezoneAction_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            UiText.Get("Main.TimezoneChangeBody"),
            UiText.Get("Main.TimezoneChangeTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await _controller.ApplyCurrentTimeZoneChangeAsync(true, CancellationToken.None);
        if (!result.Succeeded)
        {
            WpfMessageBox.Show(UiText.FormatDiagnostic(result.DiagnosticCategory ?? UiText.Get("Main.TimezoneChangeFailed")), UiText.Get("Main.TimezoneChangeTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await RefreshAsync();
    }

    private static string FormatSlotStatus(SlotViewStatus status) => UiText.FormatSlotStatus(status);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    private static System.Windows.Media.Brush Brush(string color) =>
        new SolidColorBrush((System.Windows.Media.Color)WpfColorConverter.ConvertFromString(color));

    private sealed record TodayRow(string Time, string Status);

    private sealed class ExitWarningDialog : Window
    {
        private readonly System.Windows.Controls.CheckBox _suppressCheckBox;

        public ExitWarningDialog()
        {
            Title = UiText.Get("Exit.WarningTitle");
            Width = 430;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Background = Brush("#F6F5F1");

            _suppressCheckBox = new System.Windows.Controls.CheckBox
            {
                Content = UiText.Get("Exit.SuppressWarning"),
                Margin = new Thickness(0, 12, 0, 12)
            };
            var content = new StackPanel { Margin = new Thickness(22) };
            content.Children.Add(new TextBlock
            {
                Text = UiText.Get("Exit.Warning"),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = UiText.Get("Exit.Explanation"),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#68706A")
            });
            content.Children.Add(_suppressCheckBox);
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            var exitButton = new WpfButton { Content = UiText.Get("Exit.ExitApp"), Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
            var cancelButton = new WpfButton { Content = UiText.Get("Exit.Cancel"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            exitButton.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(exitButton);
            content.Children.Add(buttons);
            Content = content;
        }

        public bool SuppressFutureWarnings => _suppressCheckBox.IsChecked == true;
    }
}
