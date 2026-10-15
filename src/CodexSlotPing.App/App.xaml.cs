using System.Diagnostics;
using System.Windows;
using CodexSlotPing.Update;
using WpfMessageBox = System.Windows.MessageBox;

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

        try
        {
            var installIdentity = InstallIdentity.Create(InstallIdentity.ProductIdValue, AppContext.BaseDirectory);
            var updatePaths = UpdateControlPaths.ForCurrentUser(installIdentity);
            var intentStore = new UpdateIntentStore(updatePaths);
            var journalStore = new UpdateTransactionJournalStore(updatePaths);
            var acknowledgementStore = new AppStartupAcknowledgementStore(updatePaths);
            var appIdentity = GetCurrentProcessIdentity();
            var startupCoordinator = new AppUpdateStartupCoordinator(
                installIdentity,
                updatePaths,
                intentStore,
                journalStore,
                new SystemProcessIdentityProbe(),
                new SystemUpdaterHandoffLauncher(),
                appIdentity);
            var startup = await startupCoordinator.ResolveAsync(e.Args);

            if (startup.Mode is AppUpdateStartupMode.UpdateInProgress)
            {
                ReleaseSingleInstance();
                ShowStartupMessage("Update.InProgress");
                Shutdown();
                return;
            }
            if (startup.Mode is AppUpdateStartupMode.RecoveryUpdaterLaunched)
            {
                ReleaseSingleInstance();
                ShowStartupMessage("Update.RecoveryStarting");
                Shutdown();
                return;
            }
            if (startup.Mode is AppUpdateStartupMode.RecoveryRequired)
            {
                ReleaseSingleInstance();
                ShowStartupMessage("Update.RecoveryRequired");
                Shutdown();
                return;
            }

            var controller = AppController.CreateDefault();
            var updateController = AppUpdateController.CreateDefault();
            MainWindow? window = null;
            window = new MainWindow(
                controller,
                new UiPreferencesStore(),
                updateController,
                () => OnMainWindowUsable(window!, startupCoordinator, startup, acknowledgementStore),
                suppressBackgroundUpdateCheck: startup.Mode == AppUpdateStartupMode.UpdateOperation);
            MainWindow = window;
            _trayIconController = new TrayIconController(window, controller);
            window.AttachTray(_trayIconController);
            _instanceCoordinator.ActivateRequested += () => Dispatcher.BeginInvoke(window.ShowFromTray);
            window.Show();
        }
        catch (Exception)
        {
            ReleaseSingleInstance();
            ShowStartupMessage("Update.RecoveryRequired");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconController?.Dispose();
        _instanceCoordinator?.Dispose();
        base.OnExit(e);
    }

    private static void OnMainWindowUsable(
        MainWindow window,
        AppUpdateStartupCoordinator startupCoordinator,
        AppUpdateStartupResult startup,
        IAppStartupAcknowledgementStore acknowledgementStore)
    {
        if (startup.Mode == AppUpdateStartupMode.UpdateOperation)
        {
            var result = startupCoordinator.TryPublishStartupAcknowledgement(startup, isPrimaryInstance: true, acknowledgementStore);
            if (result == AppStartupAcknowledgementWriteStatus.Created)
            {
                window.ShowUpdateNotice(UiText.Format("Update.UpdatedTo", startup.TargetVersion ?? string.Empty));
            }
            else
            {
                window.ShowUpdateNotice(UiText.Get("Update.RecoveryRequired"));
            }
        }
        else if (startup.Mode == AppUpdateStartupMode.RollbackNotice)
        {
            window.ShowUpdateNotice(UiText.Get("Update.RolledBack"));
        }
        else if (startup.HasCleanupWarning)
        {
            window.ShowUpdateNotice(UiText.Get("Update.CleanupWarning"));
        }
    }

    private static void ShowStartupMessage(string resourceKey) =>
        WpfMessageBox.Show(UiText.Get(resourceKey), "Codex Slot Ping", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static ProcessIdentity GetCurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }

    private void ReleaseSingleInstance()
    {
        _instanceCoordinator?.Dispose();
        _instanceCoordinator = null;
    }
}
