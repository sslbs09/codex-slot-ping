namespace CodexSlotPing.Core.Concurrency;

public interface IStateMutex
{
    StateMutexLease? TryAcquire(TimeSpan timeout);
}

public sealed class StateMutexLease : IDisposable
{
    private Action? _release;

    public StateMutexLease(Action release)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
