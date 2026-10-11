using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSlotPing.Core.Logging;

public sealed class StructuredLogWriter : IStructuredLogWriter
{
    private static readonly HashSet<string> DiagnosticCategoryCodes = new(StringComparer.Ordinal)
    {
        "agent-message-missing",
        "cancelled-after-process-start",
        "completion-unconfirmed",
        "malformed-jsonl",
        "multiple-terminal-events",
        "nonzero-exit",
        "process-start-failed",
        "process-timeout",
        "raw-log-store-unavailable",
        "raw-log-write-failed",
        "stream-error",
        "truncated-jsonl",
        "turn-failed",
        "unknown-terminal-event",
        "usage-limit"
    };

    private readonly string _logsDirectory;
    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    public StructuredLogWriter(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _logsDirectory = Path.GetFullPath(logsDirectory);
        _serializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task AppendAsync(StructuredLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry);

        var localDate = entry.TimestampUtc
            .ToOffset(TimeSpan.FromMinutes(entry.LocalOffsetMinutes))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var logPath = Path.Combine(_logsDirectory, $"{localDate}.jsonl");
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, _serializerOptions);
        var line = new byte[json.Length + 1];
        Buffer.BlockCopy(json, 0, line, 0, json.Length);
        line[^1] = (byte)'\n';

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            await using var stream = new FileStream(
                logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    private static void Validate(StructuredLogEntry entry)
    {
        if (entry.TimestampUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Structured log timestamps must be UTC.", nameof(entry));
        }

        if (entry.LocalOffsetMinutes is < -840 or > 840)
        {
            throw new ArgumentException("The local UTC offset is outside the supported range.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.Model) || string.IsNullOrWhiteSpace(entry.ReasoningEffort) || entry.ElapsedMilliseconds < 0)
        {
            throw new ArgumentException("The structured log entry is incomplete.", nameof(entry));
        }

        var hasSlotId = !string.IsNullOrWhiteSpace(entry.SlotId);
        var hasTestId = !string.IsNullOrWhiteSpace(entry.TestId);
        var correctIdentifier = entry.Kind switch
        {
            StructuredLogKind.FORMAL_SLOT => hasSlotId && !hasTestId,
            StructuredLogKind.TEST_NOW => hasTestId && !hasSlotId,
            _ => false
        };
        if (!correctIdentifier)
        {
            throw new ArgumentException("A structured log entry must contain exactly the identifier for its kind.", nameof(entry));
        }

        if (entry.DiagnosticCategory is not null && !DiagnosticCategoryCodes.Contains(entry.DiagnosticCategory))
        {
            throw new ArgumentException("Diagnostic category must be a known privacy-safe code.", nameof(entry));
        }

        if (entry.Usage is { } usage &&
            new[] { usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens, usage.OutputTokens, usage.ReasoningTokens }
                .Any(value => value < 0))
        {
            throw new ArgumentException("Token usage values must not be negative.", nameof(entry));
        }
    }
}
