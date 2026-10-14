namespace CodexSlotPing.Core.State;

/// <summary>
/// Temporary fail-closed pause signal shared by the tray process and its scheduled runner.
/// The persisted AppConfig flag remains the durable source of pause state.
/// </summary>
public sealed class AutomationPauseProtection : IDisposable
{
    private const string SharedEventName = @"Local\CodexSlotPing.AutomationPauseProtection";
    private readonly EventWaitHandle? _signal;
    private int _localProtectionActive;

    public AutomationPauseProtection()
    {
    }

    public AutomationPauseProtection(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named pause protection is supported on Windows only.");
        }

        _signal = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
    }

    public static AutomationPauseProtection CreateForCurrentSession() => new(SharedEventName);

    public bool IsActive => Volatile.Read(ref _localProtectionActive) != 0 || _signal?.WaitOne(0) == true;

    public void Activate()
    {
        Volatile.Write(ref _localProtectionActive, 1);
        _signal?.Set();
    }

    public void Deactivate()
    {
        _signal?.Reset();
        Volatile.Write(ref _localProtectionActive, 0);
    }

    public void Dispose() => _signal?.Dispose();
}
