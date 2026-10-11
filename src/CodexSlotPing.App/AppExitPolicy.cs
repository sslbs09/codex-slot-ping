namespace CodexSlotPing.App;

public static class AppExitPolicy
{
    public const string ExitWarning = "Closing Codex Slot Ping does not disable scheduled slots.";

    public static bool ShouldHideToTray(bool exitRequested) => !exitRequested;

    public static bool ShouldShowWarning(bool schedulerOn, bool suppressed) => schedulerOn && !suppressed;

    public static void RequestExit(Action shutdownApplication)
    {
        ArgumentNullException.ThrowIfNull(shutdownApplication);
        shutdownApplication();
    }
}
