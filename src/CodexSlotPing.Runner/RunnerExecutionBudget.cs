using CodexSlotPing.Core.Codex;

namespace CodexSlotPing.Runner;

public static class RunnerExecutionBudget
{
    private static readonly TimeSpan TaskExecutionLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FinalizationReserve = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumChildRuntime = TimeSpan.FromSeconds(100);

    public static TimeSpan GetCodexTimeout(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        var available = TaskExecutionLimit - elapsed - CodexProcessLauncher.MaximumOutputDrainTime -
                        FinalizationReserve - SafetyMargin;
        if (available <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return available < MaximumChildRuntime ? available : MaximumChildRuntime;
    }
}
