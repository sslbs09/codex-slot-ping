using System.Windows;

namespace CodexSlotPing.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _instanceCoordinator;
    private TrayIconController? _trayIconController;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceCoordinator = new SingleInstanceCoordinator();
        if (!_instanceCoordinator.TryBecomePrimary())
        {
            await _instanceCoordinator.SendActivateAsync(TimeSpan.FromSeconds(3));
            Shutdown();
            return;
        }

        var preferences = new UiPreferencesStore();
        var controller = AppController.CreateDefault();
        var window = new MainWindow(controller, preferences);
        MainWindow = window;
        _trayIconController = new TrayIconController(window, controller);
        window.AttachTray(_trayIconController);
        _instanceCoordinator.ActivateRequested += () => Dispatcher.BeginInvoke(window.ShowFromTray);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconController?.Dispose();
        _instanceCoordinator?.Dispose();
        base.OnExit(e);
    }
}
