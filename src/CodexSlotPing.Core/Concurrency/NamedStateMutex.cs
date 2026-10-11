using System.Security.Principal;

namespace CodexSlotPing.Core.Concurrency;

public sealed class NamedStateMutex : IStateMutex, IDisposable
{
    private bool _disposed;

    public NamedStateMutex(string? userSid = null)
    {
        userSid ??= GetCurrentUserSid();
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        Name = $"Local\\CodexSlotPing.State.{userSid}";
    }

    public string Name { get; }

    public StateMutexLease? TryAcquire(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var mutex = new Mutex(initiallyOwned: false, Name);
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            return new StateMutexLease(() =>
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                finally
                {
                    mutex.Dispose();
                }
            });
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose() => _disposed = true;

    private static string GetCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The named state mutex is available only on Windows.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID could not be resolved.");
    }
}
