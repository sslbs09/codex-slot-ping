using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Configuration;
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
    private const string OfficialCliInstructions =
        "Install the official Codex CLI: npm install -g @openai/codex\r\nThen sign in with your ChatGPT account: codex login";

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
            StatusMessageText.Text = $"Status could not be refreshed: {exception.Message}";
            ConnectionDot.Foreground = System.Windows.Media.Brushes.DarkOrange;
            ConnectionStatusText.Text = "Needs attention";
        }
    }

    private void Render(AppScreenState state)
    {
        var codexReady = state.Codex.IsReady;
        ConnectionDot.Foreground = codexReady ? Brush("#31845B") : Brush("#B46935");
        ConnectionStatusText.Text = codexReady ? "Connected" : "Needs attention";
        LoginStatusText.Text = state.Codex.ChatGptLoginConfirmed switch
        {
            true => "Logged in using ChatGPT",
            false => "ChatGPT login not confirmed",
            _ => "Login status could not be confirmed"
        };
        VersionText.Text = state.Codex.CliVersion ?? "Codex CLI version unavailable";

        if (state.NextSlot is null)
        {
            NextSlotTimeText.Text = "—";
            NextSlotCountdownText.Text = state.Scheduler.State == SchedulerState.TIMEZONE_CHANGED
                ? "Update the timezone lock to calculate the next slot."
                : "No eligible future slot is available.";
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
                ? $"Admission window closes in {FormatDuration(remaining)}"
                : $"Starts in {FormatDuration(remaining)}";
        }

        TodayRows.ItemsSource = state.TodaySlots.Select(slot => new TodayRow(
            slot.LocalTime.ToString("HH:mm"),
            FormatSlotStatus(slot.Status))).ToArray();
        ModelText.Text = state.Config.Model;
        ReasoningText.Text = state.Config.ReasoningEffort;
        SchedulerText.Text = FormatScheduler(state.Scheduler);
        WakeText.Text = state.EffectiveWakeToRun ? "ON" : "OFF";

        var messages = new List<string>();
        if (!codexReady)
        {
            messages.Add(string.Join(" · ", state.Codex.DiagnosticCodes));
            EnvironmentInstructionsText.Text = OfficialCliInstructions;
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
                    ? "Scheduler needs repair / path changed."
                    : "Scheduler needs repair. Readback does not match the required contract.");
                break;
            case SchedulerState.TIMEZONE_CHANGED:
                messages.Add($"Timezone changed / scheduler needs update. Configured: {state.Config.TimeZone}; current: {state.CurrentWindowsTimeZoneId}.");
                break;
            case SchedulerState.CONFLICT:
                messages.Add("A same-name task is not owned by this app; it was left untouched.");
                break;
            case SchedulerState.ERROR:
                messages.Add("Task Scheduler state could not be verified.");
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

        var answer = WpfMessageBox.Show(
            "This sends your configured prompt to Codex as a real request and may count toward usage limits. Continue?",
            "Confirm Test Now",
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
        StatusMessageText.Text = "Test Now is running. A second request will not be started.";
        try
        {
            var result = await _controller.RunTestNowAsync(true, CancellationToken.None);
            if (result is not null)
            {
                WpfMessageBox.Show(FormatTestResult(result), "Test Now result", MessageBoxButton.OK,
                    result.Invocation.Classification == CodexResultClassification.CONFIRMED_SUCCESS
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
            }
            else
            {
                StatusMessageText.Text = "A Test Now request is already running.";
            }
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Test Now failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _testNowRunning = false;
            TestNowButton.IsEnabled = true;
            _tray?.SetTestNowRunning(false);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
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
            WpfMessageBox.Show($"Logs could not be opened: {exception.Message}", "Open logs", MessageBoxButton.OK,
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
            WpfMessageBox.Show(AppExitPolicy.TestNowExitMessage, "Codex Slot Ping", MessageBoxButton.OK,
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
                    WpfMessageBox.Show($"Exit will continue, but the preference could not be saved: {exception.Message}",
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
            WpfMessageBox.Show(AppExitPolicy.TestNowExitMessage, "Codex Slot Ping", MessageBoxButton.OK,
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
        WpfClipboard.SetText(OfficialCliInstructions);
        StatusMessageText.Text = "Official install and login instructions copied.";
    }

    private async void TimezoneAction_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            "This saves the current Windows timezone and updates the owned scheduled task. The task's enabled or disabled state will be preserved. Continue?",
            "Update timezone",
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
            WpfMessageBox.Show(result.DiagnosticCategory ?? "Timezone update failed.", "Timezone update",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await RefreshAsync();
    }

    private static string FormatSlotStatus(FormalSlotStatus status) => status switch
    {
        FormalSlotStatus.CONFIRMED_SUCCESS => "✓ Confirmed",
        FormalSlotStatus.ATTEMPTED_UNCONFIRMED => "△ Attempted · unconfirmed",
        FormalSlotStatus.PRELAUNCH_FAILED => "× Prelaunch failed",
        FormalSlotStatus.MISSED => "– Missed",
        FormalSlotStatus.CLAIMED => "○ In progress",
        _ => "○ Pending"
    };

    private static string FormatScheduler(SchedulerStatus status) => status.State switch
    {
        SchedulerState.ON or SchedulerState.OWNED_ENABLED => "ON",
        SchedulerState.OFF or SchedulerState.OWNED_DISABLED or SchedulerState.NOT_INSTALLED => "OFF",
        SchedulerState.NEEDS_REPAIR => "Needs repair",
        SchedulerState.TIMEZONE_CHANGED => "Timezone changed",
        SchedulerState.CONFLICT => "Conflict",
        SchedulerState.ERROR => "Unknown",
        _ => "Unknown"
    };

    private static string FormatTestResult(TestRunResult result) =>
        $"{result.Invocation.Classification}\r\nElapsed: {result.Invocation.ElapsedMilliseconds} ms" +
        (result.Invocation.DiagnosticCategory is null ? string.Empty : $"\r\nReason: {result.Invocation.DiagnosticCategory}");

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
            Title = "Codex Slot Ping is scheduled";
            Width = 430;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Background = Brush("#F6F5F1");

            _suppressCheckBox = new System.Windows.Controls.CheckBox
            {
                Content = "Don't show this warning again",
                Margin = new Thickness(0, 12, 0, 12)
            };
            var content = new StackPanel { Margin = new Thickness(22) };
            content.Children.Add(new TextBlock
            {
                Text = AppExitPolicy.ExitWarning,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = "The scheduled task runs independently of this window. Exit only closes the App.",
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#68706A")
            });
            content.Children.Add(_suppressCheckBox);
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            var exitButton = new WpfButton { Content = "Exit App", Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
            var cancelButton = new WpfButton { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            exitButton.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(exitButton);
            content.Children.Add(buttons);
            Content = content;
        }

        public bool SuppressFutureWarnings => _suppressCheckBox.IsChecked == true;
    }
}
