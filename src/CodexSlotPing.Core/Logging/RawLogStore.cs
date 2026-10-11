using System.Text;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Logging;

public sealed class RawLogStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _rawDirectory;
    private readonly IClock _clock;

    public RawLogStore(string rawDirectory, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawDirectory);
        _rawDirectory = Path.GetFullPath(rawDirectory);
        _clock = clock ?? new SystemClock();
    }

    public async Task<string?> StoreAsync(
        string standardOutput,
        string standardError,
        bool optedIn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (!optedIn)
        {
            await PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rawDirectory);
        await PruneExpiredAsync(cancellationToken).ConfigureAwait(false);

        var path = Path.Combine(_rawDirectory, $"{Guid.NewGuid():N}.log");
        var contents = Utf8NoBom.GetBytes($"--- stdout ---{Environment.NewLine}{standardOutput}{Environment.NewLine}--- stderr ---{Environment.NewLine}{standardError}");
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            throw;
        }
    }

    public Task PruneExpiredAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_rawDirectory))
        {
            return Task.CompletedTask;
        }

        var cutoffUtc = _clock.UtcNow.ToUniversalTime() - Retention;
        foreach (var path in Directory.EnumerateFiles(_rawDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(path) < cutoffUtc.UtcDateTime)
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }
}
