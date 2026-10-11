using System.Text.Json;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Tests.Testing;

public sealed class TestRunServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly LocalDate = new(2026, 1, 15);

    [Fact]
    public async Task RunAsync_UsesUniqueTestIdsAndChangesOnlyLastTest()
    {
        using var testData = new IsolatedLocalAppData();
        var fixture = CreateFixture(CreateStateWithFormalHistory());
        var before = fixture.StateStore.State;
        var beforeNextSlot = GetNextSlot(fixture, before);

        var first = await fixture.Service.RunAsync(fixture.Config, CancellationToken.None);
        var second = await fixture.Service.RunAsync(fixture.Config, CancellationToken.None);

        Assert.NotEqual(first.TestId, second.TestId);
        Assert.Equal(Now, first.CompletedAtUtc);
        Assert.Equal(second.TestId, fixture.StateStore.State.LastTest!.TestId);
        Assert.Equal(second.CompletedAtUtc, fixture.StateStore.State.LastTest.CompletedAtUtc);
        Assert.Equal(before.HighestObservedUtc, fixture.StateStore.State.HighestObservedUtc);
        Assert.Equal(before.FormalSlots, fixture.StateStore.State.FormalSlots);
        Assert.Equal(beforeNextSlot, GetNextSlot(fixture, fixture.StateStore.State));
        Assert.Equal(2, fixture.Launcher.CallCount);
        Assert.Equal(2, fixture.StateStore.SaveCount);
        Assert.True(fixture.StateStore.SaveObservedMutexHeld);
        Assert.Equal(2, fixture.LogWriter.Entries.Count);
        Assert.All(fixture.LogWriter.Entries, entry =>
        {
            Assert.Equal(StructuredLogKind.TEST_NOW, entry.Kind);
            Assert.Null(entry.SlotId);
            Assert.NotNull(entry.TestId);
        });
        Assert.DoesNotContain(fixture.Config.Prompt,
            JsonSerializer.Serialize(fixture.LogWriter.Entries));
    }

    [Fact]
    public async Task CancellationBeforeLaunch_DoesNotCallLauncherOrChangeState()
    {
        using var testData = new IsolatedLocalAppData();
        var fixture = CreateFixture(CreateStateWithFormalHistory());
        var before = fixture.StateStore.State;
        using var cancellation = new CancellationTokenSource();
        fixture.EnvironmentDetector.OnDetect = token =>
        {
            cancellation.Cancel();
            return Task.FromResult(fixture.EnvironmentDetector.Status);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.RunAsync(fixture.Config, cancellation.Token));

        Assert.Equal(0, fixture.Launcher.CallCount);
        Assert.Equal(0, fixture.StateStore.SaveCount);
        Assert.Equal(before, fixture.StateStore.State);
        Assert.Empty(fixture.LogWriter.Entries);
    }

    [Fact]
    public async Task FailedTestNow_IsRecordedWithoutChangingFormalSlotState()
    {
        using var testData = new IsolatedLocalAppData();
        var before = CreateStateWithFormalHistory();
        var failed = new CodexInvocationResult(
            CodexResultClassification.ATTEMPTED_UNCONFIRMED,
            true,
            1,
            250,
            false,
            null,
            Array.Empty<string>(),
            null,
            "usage-limit");
        var fixture = CreateFixture(before, failed);

        var result = await fixture.Service.RunAsync(fixture.Config, CancellationToken.None);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Invocation.Classification);
        Assert.Equal("usage-limit", fixture.StateStore.State.LastTest!.ResultCategory);
        Assert.Equal(result.TestId, fixture.StateStore.State.LastTest.TestId);
        Assert.Equal(before.FormalSlots, fixture.StateStore.State.FormalSlots);
        Assert.Equal(before.HighestObservedUtc, fixture.StateStore.State.HighestObservedUtc);
        Assert.Equal(1, fixture.Launcher.CallCount);
        Assert.Single(fixture.LogWriter.Entries);
        Assert.Equal(StructuredLogKind.TEST_NOW, fixture.LogWriter.Entries[0].Kind);
        Assert.Equal(result.TestId, fixture.LogWriter.Entries[0].TestId);
        Assert.Null(fixture.LogWriter.Entries[0].SlotId);
    }

    private static TestRunFixture CreateFixture(StateDocument state, CodexInvocationResult? result = null) =>
        new(state, result);

    private static StateDocument CreateStateWithFormalHistory() => StateDocument.Empty() with
    {
        HighestObservedUtc = Now.AddMinutes(-2),
        FormalSlots =
        [
            new FormalSlotRecord(
                new SlotId("UTC", LocalDate, new TimeOnly(7, 0)),
                1,
                FormalSlotStatus.CONFIRMED_SUCCESS,
                Now.AddHours(-5),
                null),
            new FormalSlotRecord(
                new SlotId("UTC", LocalDate, new TimeOnly(12, 1)),
                2,
                FormalSlotStatus.MISSED,
                Now.AddMinutes(-1),
                "admission-window-expired")
        ],
        LastTest = new TestRunRecord("previous-test", Now.AddDays(-1), "gpt-5.6-luna", "low", "CONFIRMED_SUCCESS", 10, 0)
    };

    private static SlotScheduleEntry? GetNextSlot(TestRunFixture fixture, StateDocument state) =>
        new NextSlotCalculator(fixture.TimeZoneProvider).GetNext(fixture.Config, state, Now, "UTC");

    private sealed class TestRunFixture
    {
        private readonly RecordingStateMutex _mutex;

        public TestRunFixture(StateDocument state, CodexInvocationResult? result)
        {
            Config = AppConfigDefaults.Create() with { TimeZone = "UTC" };
            Clock = new FixedClock(Now);
            TimeZoneProvider = new FixedTimeZoneProvider();
            _mutex = new RecordingStateMutex();
            StateStore = new InMemoryStateStore(state, () => _mutex.IsHeld);
            EnvironmentDetector = new FakeEnvironmentDetector(ReadyEnvironment());
            Launcher = new RecordingLauncher(result ?? ConfirmedSuccess());
            LogWriter = new RecordingLogWriter();
            Service = new TestRunService(Clock, TimeZoneProvider, StateStore, _mutex, EnvironmentDetector, Launcher, LogWriter);
        }

        public AppConfig Config { get; }
        public FixedClock Clock { get; }
        public FixedTimeZoneProvider TimeZoneProvider { get; }
        public InMemoryStateStore StateStore { get; }
        public FakeEnvironmentDetector EnvironmentDetector { get; }
        public RecordingLauncher Launcher { get; }
        public RecordingLogWriter LogWriter { get; }
        public TestRunService Service { get; }
    }

    private static CodexInvocationResult ConfirmedSuccess() => new(
        CodexResultClassification.CONFIRMED_SUCCESS,
        true,
        0,
        42,
        false,
        new CodexTokenUsage(10, 2, 0, 4, 1),
        Array.Empty<string>(),
        null,
        null);

    private static EnvironmentStatus ReadyEnvironment()
    {
        var reasoning = new HashSet<string>(["low", "medium", "high", "xhigh", "max", "ultra"], StringComparer.Ordinal);
        var capabilities = new CodexCliCapabilities("codex-cli fixture", CodexCliCapabilities.RequiredOptionNames, reasoning);
        return new EnvironmentStatus(true, Path.Combine(Path.GetTempPath(), "codex.exe"), capabilities.Version,
            true, reasoning, Array.Empty<string>(), capabilities);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FixedTimeZoneProvider : ITimeZoneProvider
    {
        public string CurrentWindowsTimeZoneId => "UTC";
        public TimeZoneInfo Resolve(string windowsTimeZoneId) =>
            string.Equals(windowsTimeZoneId, "UTC", StringComparison.Ordinal)
                ? TimeZoneInfo.Utc
                : throw new TimeZoneNotFoundException(windowsTimeZoneId);
    }

    private sealed class FakeEnvironmentDetector(EnvironmentStatus status) : ICodexEnvironmentDetector
    {
        public EnvironmentStatus Status { get; } = status;
        public Func<CancellationToken, Task<EnvironmentStatus>>? OnDetect { get; set; }
        public Task<EnvironmentStatus> DetectAsync(CancellationToken cancellationToken) =>
            OnDetect?.Invoke(cancellationToken) ?? Task.FromResult(Status);
    }

    private sealed class RecordingLauncher(CodexInvocationResult result) : ICodexProcessLauncher
    {
        public int CallCount { get; private set; }
        public CodexInvocationRequest? LastRequest { get; private set; }
        public Task<CodexInvocationResult> RunAsync(CodexInvocationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            if (!Directory.Exists(request.WorkingDirectory) || Directory.EnumerateFileSystemEntries(request.WorkingDirectory).Any())
            {
                throw new InvalidOperationException("The Test Now launcher did not receive an empty working directory.");
            }
            return Task.FromResult(result);
        }
    }

    private sealed class InMemoryStateStore(StateDocument state, Func<bool> mutexHeld) : IStateStore
    {
        public StateDocument State { get; private set; } = state;
        public int SaveCount { get; private set; }
        public bool SaveObservedMutexHeld { get; private set; }
        public Task<StateDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task SaveAsync(StateDocument state, CancellationToken cancellationToken)
        {
            SaveCount++;
            SaveObservedMutexHeld |= mutexHeld();
            if (!mutexHeld())
            {
                return Task.FromException(new InvalidOperationException("Test Now state write did not hold the mutex."));
            }
            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateMutex : IStateMutex
    {
        public bool IsHeld { get; private set; }
        public StateMutexLease? TryAcquire(TimeSpan timeout)
        {
            if (IsHeld)
            {
                return null;
            }
            IsHeld = true;
            return new StateMutexLease(() => IsHeld = false);
        }
    }

    private sealed class RecordingLogWriter : IStructuredLogWriter
    {
        public List<StructuredLogEntry> Entries { get; } = [];
        public Task AppendAsync(StructuredLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class IsolatedLocalAppData : IDisposable
    {
        private readonly string? _previous;
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"CodexSlotPing-TestNow-{Guid.NewGuid():N}");

        public IsolatedLocalAppData()
        {
            _previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _previous);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
