using System.Diagnostics;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;
using CodexSlotPing.Runner;

namespace CodexSlotPing.IntegrationTests.Runner;

public sealed class RunnerOrchestratorTests
{
    private const string EasternTimeZoneId = "Eastern Standard Time";
    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById(EasternTimeZoneId);
    private static readonly DateOnly TestDate = new(2026, 1, 15);

    [Fact]
    public async Task EligibleSlot_PersistsClaimBeforeLaunch_AndFinalizesSuccess()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)));

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.ConfirmedSuccess, exitCode);
        var record = Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(FormalSlotStatus.CONFIRMED_SUCCESS, record.Status);
        Assert.NotNull(fixture.StateStore.State.Eligibility!.LastAttemptAtUtc);
        Assert.NotNull(fixture.StateStore.State.Eligibility.LastConfirmedSuccessAtUtc);
        Assert.True(fixture.Launcher.ClaimedAtLaunch);
        Assert.Equal(1, fixture.Launcher.CallCount);
        Assert.Single(fixture.LogWriter.Entries);
        Assert.Equal(StructuredLogKind.FORMAL_SLOT, fixture.LogWriter.Entries[0].Kind);
        Assert.DoesNotContain(fixture.ConfigStore.Config.Prompt, System.Text.Json.JsonSerializer.Serialize(fixture.LogWriter.Entries[0]));
    }

    [Fact]
    public async Task ClockRollbackDuringRequest_PreservesAttemptStartInHighWaterMark()
    {
        var attemptStartedAtUtc = AtLocal(TestDate, new TimeOnly(7, 1));
        var fixture = CreateFixture(attemptStartedAtUtc);
        fixture.Launcher.OnRun = () => fixture.Clock.SetUtcNow(attemptStartedAtUtc.AddSeconds(-30));

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.ConfirmedSuccess, exitCode);
        Assert.Equal(attemptStartedAtUtc, fixture.StateStore.State.Eligibility!.LastAttemptAtUtc);
        Assert.Equal(attemptStartedAtUtc, fixture.StateStore.State.HighestObservedUtc);
        Assert.Equal(attemptStartedAtUtc, fixture.StateStore.State.Eligibility.HighestObservedUtc);
        var rolledBackDecision = new EligibilityEvaluator().Evaluate(
            fixture.StateStore.State.Eligibility,
            EligibilityRequest.Scheduled(attemptStartedAtUtc.AddSeconds(-15)));
        Assert.Equal(EligibilityReason.ClockRollback, rolledBackDecision.Reason);
    }

    [Fact]
    public async Task UsesConfigurationSnapshotThatWasValidatedByTheDurableClaim()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)));
        var claimTimeConfig = fixture.ConfigStore.Config with { Prompt = "claim-time configuration" };
        fixture.ConfigStore.OnLoad = loadCount => loadCount == 1 ? fixture.ConfigStore.Config : claimTimeConfig;

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.ConfirmedSuccess, exitCode);
        Assert.Equal("claim-time configuration", fixture.Launcher.LastRequest!.Prompt);
    }

    [Fact]
    public async Task FutureSlot_IsPendingAndDoesNotLaunch()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(6, 55)));

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task ExpiredSlot_IsMissedAndNeverLaunched()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 4)));

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        var record = Assert.Single(fixture.StateStore.State.FormalSlots);
        Assert.Equal(FormalSlotStatus.MISSED, record.Status);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task ClaimedSlotAfterCrash_IsNotLaunchedAgain()
    {
        var now = AtLocal(TestDate, new TimeOnly(7, 1));
        var state = StateDocument.Empty() with
        {
            FormalSlots = [Record(TestDate, new TimeOnly(7, 0), 1, FormalSlotStatus.CLAIMED, now)],
            Eligibility = EligibilityState.Empty() with { HasUnresolvedClaim = true }
        };
        var fixture = CreateFixture(now, state: state);

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Equal(FormalSlotStatus.CLAIMED, Assert.Single(fixture.StateStore.State.FormalSlots).Status);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task DisabledConfiguration_DoesNotReadSchedulerOrLaunch()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)),
            config: AppConfigDefaults.Create() with { Enabled = false, TimeZone = EasternTimeZoneId });

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Equal(0, fixture.Scheduler.ReadCount);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task DisabledOwnedTask_DoesNotClaimOrLaunch()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)));
        fixture.Scheduler.Status = fixture.CreateSchedulerStatus(enabled: false);

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task TimeZoneMismatch_FailsClosedBeforeClaim()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)),
            config: AppConfigDefaults.Create() with { TimeZone = "Pacific Standard Time" });

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task SchedulerTriggerTimeMismatch_FailsClosedBeforeClaim()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)));
        fixture.Scheduler.Status = fixture.Scheduler.Status with
        {
            Readback = fixture.Scheduler.Status.Readback! with
            {
                Triggers =
                [
                    new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T07:00:00"),
                    new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T12:02:00"),
                    new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T17:01:00"),
                    new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T22:01:00")
                ]
            }
        };

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task ClockRollback_BlocksClaimAndLaunch()
    {
        var now = AtLocal(TestDate, new TimeOnly(7, 1));
        var highWater = now.AddMinutes(1);
        var state = StateDocument.Empty() with
        {
            HighestObservedUtc = highWater,
            Eligibility = EligibilityState.Empty() with { HighestObservedUtc = highWater }
        };
        var fixture = CreateFixture(now, state: state);

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task MutexUnavailable_BlocksClaimAndLaunch()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)), mutexAvailable: false);

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task MalformedState_BlocksClaimAndLaunch()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)));
        fixture.StateStore.LoadFailure = new InvalidDataException("malformed state fixture");

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task ClaimWriteFailure_BlocksLaunch()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)));
        fixture.StateStore.SaveFailure = new IOException("injected claim write failure");

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task EnvironmentCheckFailure_FinalizesPrelaunchFailedWithoutLaunching()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)));
        fixture.EnvironmentDetector.Status = new EnvironmentStatus(false, null, null, null,
            new HashSet<string>(StringComparer.Ordinal), ["direct-executable-not-found"], null);

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.PrelaunchFailed, exitCode);
        Assert.Equal(FormalSlotStatus.PRELAUNCH_FAILED, Assert.Single(fixture.StateStore.State.FormalSlots).Status);
        Assert.Null(fixture.StateStore.State.Eligibility!.LastAttemptAtUtc);
        Assert.DoesNotContain(fixture.StateStore.State.Eligibility.Observations,
            observation => observation.Kind == EligibilityObservationKind.AttemptStarted);
        Assert.Equal(0, fixture.Launcher.CallCount);
        Assert.Single(fixture.LogWriter.Entries);
    }

    [Fact]
    public async Task ProcessCreationFailure_FinalizesPrelaunchFailed()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)),
            invocationResult: Result(CodexResultClassification.PRELAUNCH_FAILED, processStarted: false, "process-start-failed"));

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.PrelaunchFailed, exitCode);
        Assert.Equal(FormalSlotStatus.PRELAUNCH_FAILED, Assert.Single(fixture.StateStore.State.FormalSlots).Status);
        Assert.Equal(1, fixture.Launcher.CallCount);
        Assert.Null(fixture.StateStore.State.Eligibility!.LastAttemptAtUtc);
    }

    [Fact]
    public async Task UnknownExternalUsage_BlocksRunnerWithoutDispatch()
    {
        var fixture = CreateFixture(
            AtLocal(TestDate, new TimeOnly(7, 1)),
            state: StateDocument.Empty());

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task UsageLimitObservation_BlocksTheNextScheduledRunner()
    {
        var now = AtLocal(TestDate, new TimeOnly(7, 1));
        var state = KnownState() with
        {
            Eligibility = KnownState().Eligibility! with
            {
                Observations = [new EligibilityObservation(
                    EligibilityObservationKind.UsageLimitReached,
                    now.AddMinutes(-1),
                    ObservationSource.AppScheduled,
                    Confidence.High)]
            }
        };
        var fixture = CreateFixture(now, state: state);

        var exitCode = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.NoDispatch, exitCode);
        Assert.Empty(fixture.StateStore.State.FormalSlots);
        Assert.Equal(0, fixture.Launcher.CallCount);
    }

    [Fact]
    public async Task AttemptedResult_IsTerminalAndSecondRunnerDoesNotInvokeAgain()
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)),
            invocationResult: Result(CodexResultClassification.ATTEMPTED_UNCONFIRMED, processStarted: true, "stream-error"));

        var first = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);
        var second = await fixture.CreateRunner().RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.AttemptedUnconfirmed, first);
        Assert.Equal(RunnerExitCode.NoDispatch, second);
        Assert.Equal(FormalSlotStatus.ATTEMPTED_UNCONFIRMED, Assert.Single(fixture.StateStore.State.FormalSlots).Status);
        Assert.Equal(1, fixture.Launcher.CallCount);
    }

    [Theory]
    [InlineData("__FAKECODEX_DELAY__", "process-timeout", 250)]
    [InlineData("__FAKECODEX_QUOTA__", "usage-limit", 5000)]
    [InlineData("__FAKECODEX_NETWORK_UNKNOWN__", "stream-error", 5000)]
    public async Task FakeCodexUncertainOutcome_IsAttemptedAndNeverRetried(string prompt, string diagnostic, int timeoutMilliseconds)
    {
        var fixture = CreateFixture(AtLocal(TestDate, new TimeOnly(7, 1)),
            config: AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId, Prompt = prompt });
        var fakeLauncher = new CountingLauncher(new CodexProcessLauncher(
            new CodexInvocationBuilder(), TimeSpan.FromMilliseconds(timeoutMilliseconds)));

        var first = await fixture.CreateRunner(fakeLauncher).RunFormalSlotAsync(CancellationToken.None);
        var second = await fixture.CreateRunner(fakeLauncher).RunFormalSlotAsync(CancellationToken.None);

        Assert.Equal(RunnerExitCode.AttemptedUnconfirmed, first);
        Assert.Equal(RunnerExitCode.NoDispatch, second);
        Assert.Equal(FormalSlotStatus.ATTEMPTED_UNCONFIRMED, Assert.Single(fixture.StateStore.State.FormalSlots).Status);
        Assert.Equal(diagnostic, fixture.LogWriter.Entries[0].DiagnosticCategory);
        Assert.Equal(1, fakeLauncher.CallCount);
    }

    [Fact]
    public void RunnerArguments_ParseOwnedTaskPath()
    {
        var options = RunnerArguments.Parse(["--task-path", "\\CodexSlotPing-Test-0123456789abcdef0123456789abcdef"]);

        Assert.Equal("\\CodexSlotPing-Test-0123456789abcdef0123456789abcdef", options.TaskPath);
    }

    [Theory]
    [MemberData(nameof(MalformedArgumentCases))]
    public void RunnerArguments_RejectMalformedOrUnexpectedArguments(string[] args)
    {
        Assert.Throws<ArgumentException>(() => RunnerArguments.Parse(args));
    }

    public static IEnumerable<object[]> MalformedArgumentCases =>
    [
        [Array.Empty<string>()],
        [new[] { "--unknown", "value" }],
        [new[] { "--task-path", "relative" }],
        [new[] { "--task-path", "\\Valid", "extra" }]
    ];

    private static RunnerFixture CreateFixture(
        DateTimeOffset nowUtc,
        AppConfig? config = null,
        StateDocument? state = null,
        bool mutexAvailable = true,
        CodexInvocationResult? invocationResult = null) =>
        new(nowUtc, config, state, mutexAvailable, invocationResult);

    private static StateDocument KnownState() => StateDocument.Empty() with
    {
        Eligibility = EligibilityState.Empty() with { ExternalManualUsageUnknown = false }
    };

    private static DateTimeOffset AtLocal(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, EasternTimeZone), TimeSpan.Zero);
    }

    private static FormalSlotRecord Record(DateOnly date, TimeOnly time, int position, FormalSlotStatus status, DateTimeOffset updatedAt) =>
        new(new SlotId(EasternTimeZoneId, date, time), position, status, updatedAt, null);

    private static CodexInvocationResult Result(CodexResultClassification classification, bool processStarted, string? diagnostic) =>
        new(classification, processStarted,
            !processStarted ? null : classification == CodexResultClassification.CONFIRMED_SUCCESS ? 0 : 1,
            25, diagnostic == "process-timeout", null,
            Array.Empty<string>(), null, diagnostic);

    private static string GetFakeCodexPath()
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = outputDirectory.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the build configuration directory.");
        var repositoryRoot = outputDirectory;
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "CodexSlotPing.sln")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }

        if (repositoryRoot is null)
        {
            throw new InvalidOperationException("Could not locate the Codex Slot Ping repository root.");
        }

        return Path.Combine(repositoryRoot.FullName, "tests", "CodexSlotPing.FakeCodex", "bin", configuration,
            "net10.0", "CodexSlotPing.FakeCodex.exe");
    }

    private sealed class RunnerFixture
    {
        private readonly string _runnerPath = Path.Combine(Path.GetTempPath(), $"CodexSlotPing.Runner-{Guid.NewGuid():N}.exe");

        public RunnerFixture(DateTimeOffset nowUtc, AppConfig? config, StateDocument? state, bool mutexAvailable,
            CodexInvocationResult? invocationResult)
        {
            Clock = new FixedClock(nowUtc);
            TimeZoneProvider = new FixedTimeZoneProvider(EasternTimeZoneId, EasternTimeZone);
            ConfigStore = new InMemoryConfigStore(config ?? AppConfigDefaults.Create() with { TimeZone = EasternTimeZoneId });
            Mutex = new RecordingStateMutex(mutexAvailable);
            StateStore = new InMemoryStateStore(state ?? KnownState(), () => Mutex.IsHeld);
            TaskPath = $"\\CodexSlotPing-Test-{Guid.NewGuid():N}";
            Scheduler = new FakeSchedulerGateway(RunnerOrchestratorTests.CreateSchedulerStatus(TaskPath, _runnerPath, enabled: true));
            EnvironmentDetector = new FakeEnvironmentDetector(ReadyEnvironment(GetFakeCodexPath()));
            Launcher = new RecordingLauncher(StateStore, invocationResult ?? Result(CodexResultClassification.CONFIRMED_SUCCESS, true, null));
            LogWriter = new RecordingLogWriter();
        }

        public FixedClock Clock { get; }
        public FixedTimeZoneProvider TimeZoneProvider { get; }
        public InMemoryConfigStore ConfigStore { get; }
        public RecordingStateMutex Mutex { get; }
        public InMemoryStateStore StateStore { get; }
        public FakeSchedulerGateway Scheduler { get; }
        public FakeEnvironmentDetector EnvironmentDetector { get; }
        public RecordingLauncher Launcher { get; }
        public RecordingLogWriter LogWriter { get; }
        public string TaskPath { get; }

        public RunnerOrchestrator CreateRunner(ICodexProcessLauncher? processLauncher = null) => new(
            new RunnerOptions(TaskPath), _runnerPath, Clock, ConfigStore, StateStore, Mutex, TimeZoneProvider,
            Scheduler, EnvironmentDetector, processLauncher ?? Launcher, LogWriter);

        public SchedulerStatus CreateSchedulerStatus(bool enabled) =>
            RunnerOrchestratorTests.CreateSchedulerStatus(TaskPath, _runnerPath, enabled);
    }

    private static SchedulerStatus CreateSchedulerStatus(string taskPath, string runnerPath, bool enabled)
    {
        var readback = new SchedulerTaskReadback(
            Array.Empty<SchedulerTriggerReadback>(), false, "IgnoreNew", "PT2M", 0, null, false,
            "S-1-5-21-test", 3, 0, 0, runnerPath, $"--task-path \"{taskPath}\"", Path.GetDirectoryName(runnerPath));
        return new SchedulerStatus(taskPath, enabled ? SchedulerState.OWNED_ENABLED : SchedulerState.OWNED_DISABLED,
            true, true, enabled, SchedulerTaskContract.OwnerMarker, readback, Array.Empty<string>(), null);
    }

    private static EnvironmentStatus ReadyEnvironment(string executablePath)
    {
        var reasoning = new HashSet<string>(["low", "medium", "high", "xhigh", "max", "ultra"], StringComparer.Ordinal);
        var capabilities = new CodexCliCapabilities("codex-cli 0.153.4", CodexCliCapabilities.RequiredOptionNames, reasoning);
        return new EnvironmentStatus(true, executablePath, capabilities.Version, true, reasoning, Array.Empty<string>(), capabilities);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void SetUtcNow(DateTimeOffset value) => UtcNow = value.ToUniversalTime();
    }

    private sealed class FixedTimeZoneProvider : ITimeZoneProvider
    {
        private readonly string _timeZoneId;
        private readonly TimeZoneInfo _timeZone;

        public FixedTimeZoneProvider(string timeZoneId, TimeZoneInfo timeZone)
        {
            _timeZoneId = timeZoneId;
            _timeZone = timeZone;
            CurrentWindowsTimeZoneId = timeZoneId;
        }

        public string CurrentWindowsTimeZoneId { get; set; }
        public TimeZoneInfo Resolve(string windowsTimeZoneId) =>
            string.Equals(windowsTimeZoneId, _timeZoneId, StringComparison.OrdinalIgnoreCase)
                ? _timeZone
                : throw new TimeZoneNotFoundException(windowsTimeZoneId);
    }

    private sealed class InMemoryConfigStore(AppConfig config) : IConfigStore
    {
        public AppConfig Config { get; set; } = config;
        public Func<int, AppConfig>? OnLoad { get; set; }
        public int LoadCount { get; private set; }
        public Task<AppConfig> LoadOrCreateAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(OnLoad?.Invoke(LoadCount) ?? Config);
        }
        public Task SaveAsync(AppConfig config, CancellationToken cancellationToken)
        {
            Config = config;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryStateStore(StateDocument state, Func<bool> mutexHeld) : IStateStore
    {
        public StateDocument State { get; private set; } = state;
        public Exception? LoadFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public Task<StateDocument> LoadAsync(CancellationToken cancellationToken) => LoadFailure is null
            ? Task.FromResult(State)
            : Task.FromException<StateDocument>(LoadFailure);
        public Task SaveAsync(StateDocument state, CancellationToken cancellationToken)
        {
            if (!mutexHeld())
            {
                return Task.FromException(new InvalidOperationException("State write occurred without the mutex."));
            }
            if (SaveFailure is { } failure)
            {
                return Task.FromException(failure);
            }
            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateMutex(bool available) : IStateMutex
    {
        public bool IsHeld { get; private set; }
        public StateMutexLease? TryAcquire(TimeSpan timeout)
        {
            if (!available || IsHeld)
            {
                return null;
            }
            IsHeld = true;
            return new StateMutexLease(() => IsHeld = false);
        }
    }

    private sealed class FakeSchedulerGateway(SchedulerStatus status) : ITaskSchedulerGateway
    {
        public SchedulerStatus Status { get; set; } = status;
        public int ReadCount { get; private set; }
        public Task<SchedulerStatus> ReadStatusAsync(string taskPath, string currentRunnerPath, string configuredTimeZoneId,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Status);
        }
        public Task<SchedulerOperationResult> InstallOrUpdateAsync(SchedulerTaskDefinition definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SchedulerOperationResult> DisableAsync(string taskPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SchedulerOperationResult> RemoveAsync(string taskPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEnvironmentDetector(EnvironmentStatus status) : ICodexEnvironmentDetector
    {
        public EnvironmentStatus Status { get; set; } = status;
        public Task<EnvironmentStatus> DetectAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
    }

    private sealed class RecordingLauncher(InMemoryStateStore stateStore, CodexInvocationResult result) : ICodexProcessLauncher
    {
        public int CallCount { get; private set; }
        public bool ClaimedAtLaunch { get; private set; }
        public CodexInvocationRequest? LastRequest { get; private set; }
        public Action? OnRun { get; set; }
        public Task<CodexInvocationResult> RunAsync(CodexInvocationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            var record = stateStore.State.FormalSlots.SingleOrDefault();
            ClaimedAtLaunch = record?.Status == FormalSlotStatus.CLAIMED;
            if (!Directory.Exists(request.WorkingDirectory) || Directory.EnumerateFileSystemEntries(request.WorkingDirectory).Any())
            {
                throw new InvalidOperationException("The launcher did not receive an existing empty working directory.");
            }
            OnRun?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class CountingLauncher(ICodexProcessLauncher inner) : ICodexProcessLauncher
    {
        public int CallCount { get; private set; }
        public Task<CodexInvocationResult> RunAsync(CodexInvocationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return inner.RunAsync(request, cancellationToken);
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
}
