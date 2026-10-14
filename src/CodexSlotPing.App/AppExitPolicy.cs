namespace CodexSlotPing.App;

public static class AppExitPolicy
{
    public const string ExitWarning = "Closing Codex Slot Ping does not disable planned attempts.";
    public const string TestNowExitMessage = "Test Now is still running. Wait for its result before exiting.";

    public static bool ShouldHideToTray(bool exitRequested) => !exitRequested;

    public static bool ShouldShowWarning(bool schedulerOn, bool suppressed) => schedulerOn && !suppressed;

    public static bool TryRequestExit(bool testNowRunning, Action shutdownApplication)
    {
        ArgumentNullException.ThrowIfNull(shutdownApplication);
        if (testNowRunning)
        {
            return false;
        }

        shutdownApplication();
        return true;
    }
}
