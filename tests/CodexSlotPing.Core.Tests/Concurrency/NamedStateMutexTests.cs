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
        var ownerTask = Task.Run(() =>
        {
            using var lease = owner.TryAcquire(TimeSpan.Zero)
                ?? throw new InvalidOperationException("The initial mutex acquisition should succeed.");
            ownerAcquired.SetResult();
            if (!releaseOwner.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("The test did not release the owning thread.");
            }
        });
        await ownerAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        bool blocked;
        try
        {
            blocked = await Task.Run(() => contender.TryAcquire(TimeSpan.FromMilliseconds(100)) is null);
        }
        finally
        {
            releaseOwner.Set();
            await ownerTask;
        }

        Assert.True(blocked);
        var acquiredAfterRelease = await Task.Run(() =>
        {
            using var acquiredLease = contender.TryAcquire(TimeSpan.FromSeconds(1));
            return acquiredLease is not null;
        });

        Assert.True(acquiredAfterRelease);
    }
}
