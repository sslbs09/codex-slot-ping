using CodexSlotPing.Core.Scheduler;

namespace CodexSlotPing.Core.Tests.Scheduler;

public sealed class SchedulerHealthEvaluatorTests
{
    private const string TaskPath = "\\Codex Slot Ping";

    [Fact]
    public void MovedRunnerPath_ShowsNeedsRepair_AndRepairUpdatesOwnedAction()
    {
        var oldRunnerPath = @"C:\Old\CodexSlotPing.Runner.exe";
        var currentRunnerPath = Path.Combine(Path.GetTempPath(), "New", "CodexSlotPing.Runner.exe");
        var evaluator = new SchedulerHealthEvaluator();

        var stale = evaluator.Evaluate(
            OwnedStatus(oldRunnerPath), currentRunnerPath, "SYSTEM", TimeZoneInfo.Local.Id);

        Assert.Equal(SchedulerState.NEEDS_REPAIR, stale.State);
        Assert.False(IsHealthy(stale));
        Assert.Equal(Path.GetFullPath(oldRunnerPath), stale.RegisteredRunnerPath);
        Assert.Equal(Path.GetFullPath(currentRunnerPath), stale.ExpectedRunnerPath);

        // A successful owned Install/Update is reflected by Task Scheduler
        // readback using the portable directory's new executable path.
        var repaired = evaluator.Evaluate(
            OwnedStatus(currentRunnerPath), currentRunnerPath, "SYSTEM", TimeZoneInfo.Local.Id);

        Assert.Equal(SchedulerState.ON, repaired.State);
        Assert.Equal(Path.GetFullPath(currentRunnerPath), repaired.RegisteredRunnerPath);
        Assert.True(IsHealthy(repaired));

        var foreign = evaluator.Evaluate(
            new SchedulerStatus(TaskPath, SchedulerState.CONFLICT, true, false, true,
                "third-party task", null, ["task-ownership-conflict"], "task-ownership-conflict"),
            currentRunnerPath, "SYSTEM", TimeZoneInfo.Local.Id);
        Assert.Equal(SchedulerState.CONFLICT, foreign.State);
        Assert.False(foreign.IsOwned);
    }

    [Fact]
    public void ChangedExplicitTimezone_IsReportedAndSystemModeFollowsWindows()
    {
        var currentZone = TimeZoneInfo.Local.Id;
        var otherZone = OtherTimeZone(currentZone);
        var runnerPath = Path.Combine(Path.GetTempPath(), "CodexSlotPing.Runner.exe");
        var evaluator = new SchedulerHealthEvaluator();

        var explicitLock = evaluator.Evaluate(OwnedStatus(runnerPath), runnerPath, otherZone, currentZone);
        var systemMode = evaluator.Evaluate(OwnedStatus(runnerPath), runnerPath, "SYSTEM", currentZone);

        Assert.Equal(SchedulerState.TIMEZONE_CHANGED, explicitLock.State);
        Assert.False(IsHealthy(explicitLock));
        Assert.Equal(otherZone, explicitLock.ConfiguredTimeZoneId);
        Assert.Equal(currentZone, explicitLock.CurrentWindowsTimeZoneId);
        Assert.Equal(SchedulerState.ON, systemMode.State);
    }

    private static SchedulerStatus OwnedStatus(string actionPath) =>
        new(TaskPath, SchedulerState.OWNED_ENABLED, true, true, true,
            "Codex Slot Ping; Owner=CodexSlotPing; Schema=1", Readback(actionPath),
            Array.Empty<string>(), null);

    private static SchedulerTaskReadback Readback(string actionPath) =>
        new(
            [
                new(2, true, true, 1, "2099-01-01T07:00:00"),
                new(2, true, true, 1, "2099-01-01T12:01:00"),
                new(2, true, true, 1, "2099-01-01T17:01:00"),
                new(2, true, true, 1, "2099-01-01T22:01:00")
            ],
            false, "IgnoreNew", "PT2M", 0, null, false, "S-1-test", 3, 0,
            0, actionPath, $"--task-path \"{TaskPath}\"", Path.GetDirectoryName(actionPath));

    private static bool IsHealthy(SchedulerStatus status) =>
        status.State is SchedulerState.ON or SchedulerState.OFF;

    private static string OtherTimeZone(string currentZone) =>
        string.Equals(currentZone, "UTC", StringComparison.OrdinalIgnoreCase)
            ? "Pacific Standard Time"
            : "UTC";
}
