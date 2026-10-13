using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfMessageBox = System.Windows.MessageBox;

namespace CodexSlotPing.App;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private UiPreferencesStore? _preferencesStore;
    private TrayIconController? _tray;
    private AppScreenState? _screenState;
    private bool _exitRequested;
    private bool _testNowRunning;

    public MainWindow() : this(AppController.CreateDefault(), new UiPreferencesStore())
    {
    }

    public MainWindow(AppController controller) : this(controller, new UiPreferencesStore())
    {
    }

    public MainWindow(AppController controller, UiPreferencesStore preferencesStore)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
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

        if (state.NextSlot is null)
        {
            NextSlotTimeText.Text = "—";
            NextSlotCountdownText.Text = state.Scheduler.State == SchedulerState.TIMEZONE_CHANGED
                ? UiText.Get("Main.UpdateTimezoneForNext")
                : UiText.Get("Main.NoEligibleFutureSlot");
        }
        else
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(state.CurrentWindowsTimeZoneId);
            var localStart = TimeZoneInfo.ConvertTime(state.NextSlot.StartUtc, timeZone);
            NextSlotTimeText.Text = localStart.ToString("HH:mm");
            var windowEnd = state.NextSlot.StartUtc.AddMinutes(4);
            var remaining = state.NowUtc < windowEnd && state.NowUtc >= state.NextSlot.StartUtc
                ? windowEnd - state.NowUtc
                : state.NextSlot.StartUtc - state.NowUtc;
            NextSlotCountdownText.Text = state.NowUtc >= state.NextSlot.StartUtc && state.NowUtc < windowEnd
                ? UiText.Format("Main.AdmissionWindowClosesIn", FormatDuration(remaining))
                : UiText.Format("Main.StartsIn", FormatDuration(remaining));
        }

        TodayRows.ItemsSource = state.TodaySlots.Select(slot => new TodayRow(
            slot.LocalTime.ToString("HH:mm"),
            FormatSlotStatus(slot.Status))).ToArray();
        EligibilityDetailsText.Text = EligibilityDecisionProjection.FormatDetails(state.EligibilityDecision);
        ModelText.Text = state.Config.Model;
        ReasoningText.Text = UiText.FormatReasoningEffort(state.Config.ReasoningEffort);
        SchedulerText.Text = FormatScheduler(state.Scheduler);
        WakeText.Text = UiText.Get(state.EffectiveWakeToRun ? "State.Enabled" : "State.Disabled");

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
            case SchedulerState.NEEDS_REPAIR:
                messages.Add(state.Scheduler.DiagnosticCategory == "runner-action-mismatch"
                    ? UiText.Get("Scheduler.RepairPathChanged")
                    : UiText.Get("Scheduler.RepairContract"));
                break;
            case SchedulerState.TIMEZONE_CHANGED:
                messages.Add(UiText.Format("Scheduler.TimezoneChanged", state.Config.TimeZone, state.CurrentWindowsTimeZoneId));
                break;
            case SchedulerState.CONFLICT:
                messages.Add(UiText.Get("Scheduler.Conflict"));
                break;
            case SchedulerState.ERROR:
                messages.Add(UiText.Get("Scheduler.ReadFailed"));
                break;
        }

        StatusMessageText.Text = string.Join(Environment.NewLine, messages);
        TimezoneActionButton.Visibility = state.Scheduler.State == SchedulerState.TIMEZONE_CHANGED
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void TestNow_Click(object sender, RoutedEventArgs e) => await RunTestNowFromTrayAsync();

    internal async Task RunTestNowFromTrayAsync()
    {
        if (_screenState is null || _testNowRunning)
        {
            return;
        }

        ShowFromTray();

        EligibilityDecision testNowDecision;
        try
        {
            testNowDecision = await _controller.GetTestNowEligibilityDecisionAsync(CancellationToken.None);
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
                EligibilityDecisionProjection.FormatDetails(testNowDecision),
                UiText.Get("Main.TestNowResultTitle"),
                MessageBoxButton.OK,
                testNowDecision.Status == EligibilityStatus.Eligible
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
            await RefreshAsync();
            return;
        }

        var answer = WpfMessageBox.Show(
            EligibilityDecisionProjection.FormatTestNowConfirmation(testNowDecision),
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
                WpfMessageBox.Show(FormatTestResult(result), UiText.Get("Main.TestNowResultTitle"), MessageBoxButton.OK,
                    result.Invocation.Classification == CodexResultClassification.CONFIRMED_SUCCESS
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
            }
            else if (response is not null)
            {
                WpfMessageBox.Show(EligibilityDecisionProjection.FormatDetails(response.Decision),
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

        var window = new SettingsWindow(_controller, _screenState) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await RefreshAsync();
        }
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

    private static string FormatSlotStatus(FormalSlotStatus status) => UiText.FormatSlotStatus(status);

    private static string FormatScheduler(SchedulerStatus status) => UiText.FormatSchedulerState(status.State);

    private static string FormatTestResult(TestRunResult result) => UiText.FormatTestResult(
        result.Invocation.Classification,
        result.Invocation.ElapsedMilliseconds,
        result.Invocation.DiagnosticCategory);

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
