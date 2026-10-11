using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;

namespace CodexSlotPing.App;

public partial class MainWindow : Window
{
    private const string OfficialCliInstructions =
        "Install the official Codex CLI: npm install -g @openai/codex\r\nThen sign in with your ChatGPT account: codex login";

    private readonly AppController _controller;
    private AppScreenState? _screenState;

    public MainWindow() : this(AppController.CreateDefault())
    {
    }

    public MainWindow(AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            _screenState = await _controller.RefreshAsync(CancellationToken.None);
            Render(_screenState);
        }
        catch (Exception exception)
        {
            StatusMessageText.Text = $"Status could not be refreshed: {exception.Message}";
            ConnectionDot.Foreground = Brushes.DarkOrange;
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
        WakeText.Text = state.Config.WakeToRun ? "ON" : "OFF";

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

    private async void TestNow_Click(object sender, RoutedEventArgs e)
    {
        if (_screenState is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            "This sends your configured prompt to Codex as a real request and may count toward usage limits. Continue?",
            "Confirm Test Now",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        TestNowButton.IsEnabled = false;
        try
        {
            var result = await _controller.RunTestNowAsync(true, CancellationToken.None);
            if (result is not null)
            {
                MessageBox.Show(FormatTestResult(result), "Test Now result", MessageBoxButton.OK,
                    result.Invocation.Classification == CodexResultClassification.CONFIRMED_SUCCESS
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
            }
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Test Now failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestNowButton.IsEnabled = true;
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
            MessageBox.Show($"Logs could not be opened: {exception.Message}", "Open logs", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CopyInstructions_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(OfficialCliInstructions);
        StatusMessageText.Text = "Official install and login instructions copied.";
    }

    private async void TimezoneAction_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
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
            MessageBox.Show(result.DiagnosticCategory ?? "Timezone update failed.", "Timezone update",
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

    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private sealed record TodayRow(string Time, string Status);
}
