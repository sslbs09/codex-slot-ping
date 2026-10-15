using CodexSlotPing.Update;

namespace CodexSlotPing.Updater.Tests;

public sealed class AppProcessWaitTests
{
    [Fact]
    public async Task WaitForAppExitAsync_TreatsSamePidWithDifferentStartTimeAsExited()
    {
        var expected = new ProcessIdentity(4321, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));
        var reader = new FakeProcessIdentityReader(new ProcessIdentityReadResult(
            ProcessIdentityReadStatus.Running,
            new ProcessIdentity(expected.ProcessId, expected.StartedUtc.AddSeconds(1))));
        var runtime = new SystemUpdateTransactionRuntime(reader);

        var result = await runtime.WaitForAppExitAsync(expected, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AppProcessWaitStatus.Exited, result);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task WaitForAppExitAsync_UnknownProcessIdentityDoesNotAssumeAppExited()
    {
        var expected = new ProcessIdentity(4321, new DateTimeOffset(2026, 10, 15, 4, 5, 6, TimeSpan.Zero));
        var reader = new FakeProcessIdentityReader(new ProcessIdentityReadResult(ProcessIdentityReadStatus.Unknown, null));
        var runtime = new SystemUpdateTransactionRuntime(reader);

        var result = await runtime.WaitForAppExitAsync(expected, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(AppProcessWaitStatus.Unknown, result);
        Assert.Equal(1, reader.ReadCount);
    }

    private sealed class FakeProcessIdentityReader(ProcessIdentityReadResult result) : IProcessIdentityReader
    {
        public int ReadCount { get; private set; }
        public ProcessIdentityReadResult Read(int processId)
        {
            ReadCount++;
            return result;
        }
    }
}
