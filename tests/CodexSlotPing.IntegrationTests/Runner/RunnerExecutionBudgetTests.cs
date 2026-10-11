using CodexSlotPing.Runner;

namespace CodexSlotPing.IntegrationTests.Runner;

public sealed class RunnerExecutionBudgetTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(32, 71)]
    [InlineData(103, 0)]
    public void ChildTimeout_ReservesTaskDrainAndFinalizationTime(int elapsedSeconds, int expectedSeconds)
    {
        var timeout = RunnerExecutionBudget.GetCodexTimeout(TimeSpan.FromSeconds(elapsedSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }
}
