using CodexSlotPing.Core.Concurrency;

namespace CodexSlotPing.Core.Tests.Concurrency;

public sealed class NamedStateMutexTests
{
    [Fact]
    public void MutexName_UsesThePerUserStatePrefixAndSid()
    {
        var sid = $"test-sid-{Guid.NewGuid():N}";
        using var mutex = new NamedStateMutex(sid);

        Assert.Equal($"Local\\CodexSlotPing.State.{sid}", mutex.Name);
    }

    [Fact]
    public async Task SecondThreadCannotAcquireUntilTheCurrentLeaseIsReleased()
    {
        var sid = $"test-sid-{Guid.NewGuid():N}";
        using var owner = new NamedStateMutex(sid);
        using var contender = new NamedStateMutex(sid);
        using var releaseOwner = new ManualResetEventSlim(false);
        var ownerAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerTask = Task.Factory.StartNew(
            () =>
            {
                using var lease = owner.TryAcquire(TimeSpan.Zero)
                    ?? throw new InvalidOperationException("The initial mutex acquisition should succeed.");
                ownerAcquired.SetResult();
                if (!releaseOwner.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the owning thread.");
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        bool blocked;
        try
        {
            await ownerAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            blocked = contender.TryAcquire(TimeSpan.FromMilliseconds(100)) is null;
        }
        finally
        {
            releaseOwner.Set();
            await ownerTask;
        }

        Assert.True(blocked);
        using var acquiredLease = contender.TryAcquire(TimeSpan.FromSeconds(1));
        Assert.NotNull(acquiredLease);
    }
}
