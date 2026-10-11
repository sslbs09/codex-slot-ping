using System.Text.Json;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Tests.Logging;

public sealed class StructuredLogWriterTests
{
    [Fact]
    public async Task StructuredRecordContainsAllowedFieldsAndExcludesRawContent()
    {
        using var directory = new TemporaryDirectory();
        var logsDirectory = Path.Combine(directory.Path, "logs");
        var writer = new StructuredLogWriter(logsDirectory);
        var entry = FormalEntry();

        await writer.AppendAsync(entry, CancellationToken.None);

        var logPath = Path.Combine(logsDirectory, "2026-10-11.jsonl");
        var content = await File.ReadAllTextAsync(logPath);
        var line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var allowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "timestampUtc", "localOffsetMinutes", "slotId", "kind", "model", "reasoningEffort",
            "result", "elapsedMilliseconds", "exitCode", "diagnosticCategory", "usage"
        };
        Assert.Equal(
            allowedFields.OrderBy(name => name, StringComparer.Ordinal),
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(TimeSpan.Zero, root.GetProperty("timestampUtc").GetDateTimeOffset().Offset);
        Assert.Equal(480, root.GetProperty("localOffsetMinutes").GetInt32());
        Assert.Equal("2026-10-11/12:01@Taipei Standard Time", root.GetProperty("slotId").GetString());
        Assert.Equal("FORMAL_SLOT", root.GetProperty("kind").GetString());
        Assert.Equal("gpt-5.6-luna", root.GetProperty("model").GetString());
        Assert.Equal("low", root.GetProperty("reasoningEffort").GetString());
        Assert.Equal("CONFIRMED_SUCCESS", root.GetProperty("result").GetString());
        Assert.Equal(841, root.GetProperty("elapsedMilliseconds").GetInt64());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(64, root.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.Equal(8, root.GetProperty("usage").GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal(2, root.GetProperty("usage").GetProperty("cacheWriteInputTokens").GetInt64());
        Assert.Equal(3, root.GetProperty("usage").GetProperty("outputTokens").GetInt64());
        Assert.Equal(1, root.GetProperty("usage").GetProperty("reasoningTokens").GetInt64());
        Assert.DoesNotContain("prompt-secret-marker", content);
        Assert.DoesNotContain("response-secret-marker", content);
        Assert.DoesNotContain("OPENAI_API_KEY=secret-marker", content);
        Assert.DoesNotContain("stderr-secret-marker", content);
    }

    [Fact]
    public async Task TestNowRecordContainsTestIdWithoutSlotId()
    {
        using var directory = new TemporaryDirectory();
        var writer = new StructuredLogWriter(Path.Combine(directory.Path, "logs"));

        await writer.AppendAsync(new StructuredLogEntry(
            new DateTimeOffset(2026, 10, 11, 9, 15, 0, TimeSpan.Zero),
            480,
            null,
            "test-123",
            StructuredLogKind.TEST_NOW,
            "gpt-5.6-luna",
            "low",
            CodexResultClassification.ATTEMPTED_UNCONFIRMED,
            1200,
            1,
            "process-timeout",
            null), CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(directory.Path, "logs", "2026-10-11.jsonl"));
        using var document = JsonDocument.Parse(content);
        Assert.Equal("test-123", document.RootElement.GetProperty("testId").GetString());
        Assert.False(document.RootElement.TryGetProperty("slotId", out _));
    }

    [Fact]
    public async Task AppendWritesOneJsonObjectPerLine()
    {
        using var directory = new TemporaryDirectory();
        var writer = new StructuredLogWriter(Path.Combine(directory.Path, "logs"));

        await writer.AppendAsync(FormalEntry(), CancellationToken.None);
        await writer.AppendAsync(FormalEntry() with { ElapsedMilliseconds = 900 }, CancellationToken.None);

        var lines = (await File.ReadAllTextAsync(Path.Combine(directory.Path, "logs", "2026-10-11.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.NotNull(JsonDocument.Parse(line)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawLogStoreRequiresOptIn(bool optIn)
    {
        using var directory = new TemporaryDirectory();
        var rawDirectory = Path.Combine(directory.Path, "logs", "raw");
        var store = new RawLogStore(rawDirectory, new FixedClock(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero)));

        var path = await store.StoreAsync("response-secret-marker", "stderr-secret-marker", optIn, CancellationToken.None);

        if (!optIn)
        {
            Assert.Null(path);
            Assert.False(Directory.Exists(rawDirectory));
            return;
        }

        Assert.NotNull(path);
        Assert.Equal(rawDirectory, Path.GetDirectoryName(path));
        Assert.EndsWith(".log", path, StringComparison.OrdinalIgnoreCase);
        var raw = await File.ReadAllTextAsync(path);
        Assert.Contains("response-secret-marker", raw);
        Assert.Contains("stderr-secret-marker", raw);
    }

    [Fact]
    public async Task RawLogStorePrunesOnlyLogFilesOlderThanFourteenDays()
    {
        using var directory = new TemporaryDirectory();
        var rawDirectory = Path.Combine(directory.Path, "logs", "raw");
        Directory.CreateDirectory(rawDirectory);
        var oldFile = Path.Combine(rawDirectory, "old.log");
        var boundaryFile = Path.Combine(rawDirectory, "boundary.log");
        var recentFile = Path.Combine(rawDirectory, "recent.log");
        var unrelatedFile = Path.Combine(rawDirectory, "keep.txt");
        await File.WriteAllTextAsync(oldFile, "old");
        await File.WriteAllTextAsync(boundaryFile, "boundary");
        await File.WriteAllTextAsync(recentFile, "recent");
        await File.WriteAllTextAsync(unrelatedFile, "unrelated");
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(oldFile, now.AddDays(-14).AddSeconds(-1).UtcDateTime);
        File.SetLastWriteTimeUtc(boundaryFile, now.AddDays(-14).UtcDateTime);
        File.SetLastWriteTimeUtc(recentFile, now.AddDays(-13).UtcDateTime);
        File.SetLastWriteTimeUtc(unrelatedFile, now.AddDays(-90).UtcDateTime);
        var store = new RawLogStore(rawDirectory, new FixedClock(now));

        await store.StoreAsync("raw response", "raw stderr", true, CancellationToken.None);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(boundaryFile));
        Assert.True(File.Exists(recentFile));
        Assert.True(File.Exists(unrelatedFile));
    }

    [Fact]
    public async Task RawLogStorePrunesExpiredLogsEvenWhenCaptureIsDisabled()
    {
        using var directory = new TemporaryDirectory();
        var rawDirectory = Path.Combine(directory.Path, "logs", "raw");
        Directory.CreateDirectory(rawDirectory);
        var expiredFile = Path.Combine(rawDirectory, "expired.log");
        await File.WriteAllTextAsync(expiredFile, "expired raw output");
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(expiredFile, now.AddDays(-15).UtcDateTime);
        var store = new RawLogStore(rawDirectory, new FixedClock(now));

        var result = await store.StoreAsync("", "", optedIn: false, CancellationToken.None);

        Assert.Null(result);
        Assert.False(File.Exists(expiredFile));
        Assert.Empty(Directory.EnumerateFiles(rawDirectory));
    }

    [Fact]
    public async Task StructuredLogWriterRejectsAmbiguousIdentifiers()
    {
        using var directory = new TemporaryDirectory();
        var writer = new StructuredLogWriter(Path.Combine(directory.Path, "logs"));
        var entry = FormalEntry() with { TestId = "test-123" };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(entry, CancellationToken.None));
    }

    [Fact]
    public async Task StructuredLogWriterRejectsFreeformDiagnosticText()
    {
        using var directory = new TemporaryDirectory();
        var writer = new StructuredLogWriter(Path.Combine(directory.Path, "logs"));
        var entry = FormalEntry() with { DiagnosticCategory = "prompt-secret-marker" };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(entry, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "logs")));
    }

    private static StructuredLogEntry FormalEntry() => new(
        new DateTimeOffset(2026, 10, 11, 4, 1, 0, TimeSpan.Zero),
        480,
        "2026-10-11/12:01@Taipei Standard Time",
        null,
        StructuredLogKind.FORMAL_SLOT,
        "gpt-5.6-luna",
        "low",
        CodexResultClassification.CONFIRMED_SUCCESS,
        841,
        0,
        null,
        new CodexTokenUsage(64, 8, 2, 3, 1));

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-slot-ping-logging-{Guid.NewGuid():N}");

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
