using System.Drawing;
using Forms = System.Windows.Forms;
using CodexSlotPing.App.ReadModels;

namespace CodexSlotPing.App;

public sealed class TrayIconController : IDisposable
{
    public static IReadOnlyList<string> MenuEntryNames =>
        [UiText.Get("Tray.Open"), UiText.Get("Tray.TestNow"), UiText.Get("Tray.SchedulerLabel"),
            UiText.Get("Tray.WakeComputer"), UiText.Get("Tray.OpenLogs"), UiText.Get("Tray.Exit")];

    private readonly MainWindow _window;
    private readonly AppController _controller;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _testNowItem;
    private readonly Forms.ToolStripMenuItem _schedulerItem;
    private readonly Forms.ToolStripMenuItem _wakeItem;
    private bool _updatingWakeState;
    private bool _disposed;

    public TrayIconController(MainWindow window, AppController controller)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        var menu = new Forms.ContextMenuStrip();
        var labels = MenuEntryNames;
        var openItem = new Forms.ToolStripMenuItem(labels[0]);
        _testNowItem = new Forms.ToolStripMenuItem(labels[1]);
        _schedulerItem = new Forms.ToolStripMenuItem(UiText.Format("Tray.SchedulerState", UiText.Get("State.Unknown"))) { Enabled = false };
        _wakeItem = new Forms.ToolStripMenuItem(labels[3]) { CheckOnClick = true };
        var logsItem = new Forms.ToolStripMenuItem(labels[4]);
        var exitItem = new Forms.ToolStripMenuItem(labels[5]);

        openItem.Click += (_, _) => _window.ShowFromTray();
        _testNowItem.Click += async (_, _) => await _window.RunTestNowFromTrayAsync();
        _wakeItem.CheckedChanged += async (_, _) => await WakeChangedAsync();
        logsItem.Click += (_, _) => _window.OpenLogsFromTray();
        exitItem.Click += async (_, _) => await _window.RequestExitAsync();
        menu.Items.AddRange([openItem, _testNowItem, _schedulerItem, _wakeItem, new Forms.ToolStripSeparator(), logsItem, exitItem]);

        _icon = new Forms.NotifyIcon
        {
            Text = "Codex Slot Ping",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => _window.ShowFromTray();
    }

    public void UpdateState(AppScreenState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _schedulerItem.Text = UiText.FormatTraySchedulerState(state.Scheduler.State);
        _updatingWakeState = true;
        _wakeItem.Checked = state.Scheduler.EffectiveWakeToRun;
        _updatingWakeState = false;
    }

    public void SetTestNowRunning(bool running) => _testNowItem.Enabled = !running;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private async Task WakeChangedAsync()
    {
        if (_updatingWakeState)
        {
            return;
        }

        _wakeItem.Enabled = false;
        try
        {
            var result = await _controller.SetWakeToRunAsync(_wakeItem.Checked, CancellationToken.None);
            if (!result.Succeeded)
            {
                _window.ShowTrayMessage(UiText.FormatDiagnostic(result.Errors.FirstOrDefault() ?? UiText.Get("Diagnostic.SettingsSaveFailed")));
                var state = await _controller.RefreshAsync(CancellationToken.None);
                UpdateState(state);
                return;
            }

            var refreshed = await _controller.RefreshAsync(CancellationToken.None);
            UpdateState(refreshed);
        }
        catch (Exception exception)
        {
            _window.ShowTrayMessage(UiText.Format("Settings.Exception", exception.Message));
            _updatingWakeState = true;
            _wakeItem.Checked = !_wakeItem.Checked;
            _updatingWakeState = false;
        }
        finally
        {
            _wakeItem.Enabled = true;
        }
    }
}
