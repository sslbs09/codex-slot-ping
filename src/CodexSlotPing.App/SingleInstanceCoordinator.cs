using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace CodexSlotPing.App;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string ActivationMessage = "ACTIVATE\n";
    private readonly string _mutexName;
    private readonly string _pipeName;
    private MutexOwner? _mutexOwner;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceCoordinator(string? instanceKey = null)
    {
        var key = NormalizeKey(instanceKey ?? GetCurrentUserKey());
        _mutexName = $"Local\\CodexSlotPing.App.{key}";
        _pipeName = $"CodexSlotPing.Activate.{key}";
    }

    public event Action? ActivateRequested;

    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutexOwner is not null)
        {
            throw new InvalidOperationException("Single-instance ownership has already been attempted.");
        }

        _mutexOwner = new MutexOwner(_mutexName);
        if (!_mutexOwner.OwnsMutex)
        {
            _mutexOwner.Dispose();
            _mutexOwner = null;
            return false;
        }

        _listenerCancellation = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenAsync(_listenerCancellation.Token));
        return true;
    }

    public async Task<bool> SendActivateAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            var timeoutMilliseconds = checked((int)Math.Ceiling(timeout.TotalMilliseconds));
            await pipe.ConnectAsync(timeoutMilliseconds).ConfigureAwait(false);
            var payload = Encoding.ASCII.GetBytes(ActivationMessage);
            await pipe.WriteAsync(payload).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation?.Cancel();
        _mutexOwner?.Dispose();
        _listenerCancellation?.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 64, leaveOpen: true);
                var message = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(message, "ACTIVATE", StringComparison.Ordinal))
                {
                    ActivateRequested?.Invoke();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string GetCurrentUserKey()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsIdentity.GetCurrent().User?.Value
                   ?? throw new InvalidOperationException("The current Windows user SID could not be resolved.");
        }

        return Environment.UserName;
    }

    private static string NormalizeKey(string value)
    {
        var normalized = new string(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '-').ToArray());
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The single-instance key must contain at least one valid character.", nameof(value));
        }

        return normalized;
    }

    private sealed class MutexOwner : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private bool _ownsMutex;
        private bool _disposed;

        public MutexOwner(string mutexName)
        {
            _thread = new Thread(() => RunOwnerThread(mutexName))
            {
                IsBackground = true,
                Name = "CodexSlotPing single-instance mutex owner"
            };
            _thread.Start();
            _ready.Wait();
        }

        public bool OwnsMutex => _ownsMutex;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _ready.Dispose();
            _release.Dispose();
        }

        private void RunOwnerThread(string mutexName)
        {
            using var mutex = new Mutex(initiallyOwned: false, mutexName);
            try
            {
                try
                {
                    _ownsMutex = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    _ownsMutex = true;
                }

                _ready.Set();
                if (_ownsMutex)
                {
                    _release.Wait();
                    mutex.ReleaseMutex();
                }
            }
            finally
            {
                if (!_ready.IsSet)
                {
                    _ready.Set();
                }
            }
        }
    }
}
