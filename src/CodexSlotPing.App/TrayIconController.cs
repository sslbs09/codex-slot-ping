using System.Drawing;
using Forms = System.Windows.Forms;
using CodexSlotPing.Core.Scheduler;

namespace CodexSlotPing.App;

public sealed class TrayIconController : IDisposable
{
    public static IReadOnlyList<string> MenuEntryNames { get; } =
        ["Open", "Test now", "Scheduler state", "Wake computer", "Open logs", "Exit"];

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
        var openItem = new Forms.ToolStripMenuItem("Open");
        _testNowItem = new Forms.ToolStripMenuItem("Test now");
        _schedulerItem = new Forms.ToolStripMenuItem("Scheduler state: checking") { Enabled = false };
        _wakeItem = new Forms.ToolStripMenuItem("Wake computer") { CheckOnClick = true };
        var logsItem = new Forms.ToolStripMenuItem("Open logs");
        var exitItem = new Forms.ToolStripMenuItem("Exit");

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
        _schedulerItem.Text = $"Scheduler state: {state.Scheduler.State}";
        _updatingWakeState = true;
        _wakeItem.Checked = state.EffectiveWakeToRun;
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
                _window.ShowTrayMessage(result.Errors.FirstOrDefault() ?? "Wake setting could not be saved.");
                var state = await _controller.RefreshAsync(CancellationToken.None);
                UpdateState(state);
                return;
            }

            var refreshed = await _controller.RefreshAsync(CancellationToken.None);
            UpdateState(refreshed);
        }
        catch (Exception exception)
        {
            _window.ShowTrayMessage(exception.Message);
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
