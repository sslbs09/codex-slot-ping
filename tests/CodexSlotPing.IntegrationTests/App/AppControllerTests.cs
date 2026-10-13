using CodexSlotPing.App;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.IntegrationTests.App;

public sealed class AppControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string RunnerPath = @"C:\portable\CodexSlotPing.Runner.exe";

    [Fact]
    public async Task RefreshAsync_UsesSchedulerReadbackInsteadOfConfigEnabledFlag()
    {
        var fixture = new Fixture(AppConfigDefaults.Create() with { Enabled = true },
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));

        var state = await fixture.Controller.RefreshAsync(CancellationToken.None);

        Assert.Equal(SchedulerState.OFF, state.Scheduler.State);
        Assert.False(state.Scheduler.Exists);
        Assert.Equal(1, fixture.Scheduler.ReadCount);
    }

    [Fact]
    public async Task SaveSettingsAsync_UpdatesEnabledTaskAndPreservesDisabledState()
    {
        foreach (var enabled in new[] { true, false })
        {
            var initial = Status(enabled ? SchedulerState.ON : SchedulerState.OFF,
                exists: true, owned: true, enabled, RunnerPath);
            var fixture = new Fixture(AppConfigDefaults.Create(), initial);
            var changed = fixture.ConfigStore.Config with { Prompt = "Updated prompt." };

            var result = await fixture.Controller.SaveSettingsAsync(changed, CancellationToken.None);

            Assert.True(result.Succeeded);
            var definition = Assert.Single(fixture.Scheduler.Definitions);
            Assert.Equal(enabled, definition.Enabled);
            Assert.Equal("Updated prompt.", fixture.ConfigStore.Config.Prompt);
        }
    }

    [Fact]
    public async Task RefreshAsync_ReportsPathAndTimeZoneDrift()
    {
        var pathFixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.ON, exists: true, owned: true, enabled: true, @"C:\old\CodexSlotPing.Runner.exe"));
        var pathState = await pathFixture.Controller.RefreshAsync(CancellationToken.None);
        Assert.Equal(SchedulerState.NEEDS_REPAIR, pathState.Scheduler.State);
        Assert.Contains("runner-action-mismatch", pathState.Scheduler.ContractMismatches);

        var lockedConfig = AppConfigDefaults.Create() with { TimeZone = "America/New_York" };
        var timezoneFixture = new Fixture(lockedConfig,
            Status(SchedulerState.ON, exists: true, owned: true, enabled: true, RunnerPath));
        var timezoneState = await timezoneFixture.Controller.RefreshAsync(CancellationToken.None);
        Assert.Equal(SchedulerState.TIMEZONE_CHANGED, timezoneState.Scheduler.State);
        Assert.Equal("timezone-mismatch", timezoneState.Scheduler.DiagnosticCategory);
    }

    [Fact]
    public async Task RefreshAsync_ReportsOwnedTaskTriggerTimeDrift()
    {
        var config = AppConfigDefaults.Create();
        var initial = Status(SchedulerState.ON, exists: true, owned: true, enabled: true, RunnerPath);
        var changedTimes = new[]
        {
            new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T07:00:00"),
            new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T12:02:00"),
            new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T17:01:00"),
            new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T22:01:00")
        };
        var status = initial with { Readback = initial.Readback! with { Triggers = changedTimes } };
        var fixture = new Fixture(config, status);

        var state = await fixture.Controller.RefreshAsync(CancellationToken.None);

        Assert.Equal(SchedulerState.NEEDS_REPAIR, state.Scheduler.State);
        Assert.Contains("trigger-times-mismatch", state.Scheduler.ContractMismatches);
    }

    [Fact]
    public async Task RunTestNowAsync_RequiresConfirmation()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));

        Assert.Null(await fixture.Controller.RunTestNowAsync(false, CancellationToken.None));
        Assert.Equal(0, fixture.TestNowCallCount);

        var result = await fixture.Controller.RunTestNowAsync(true, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(1, fixture.TestNowCallCount);
        Assert.NotNull(result!.Result);
    }

    [Fact]
    public async Task UiAndRunnerUseTheSameScheduledDecisionReason()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));

        var screen = await fixture.Controller.RefreshAsync(CancellationToken.None);
        var runnerClaim = await fixture.ClaimService.TryClaimCurrentSlotAsync(CancellationToken.None);

        Assert.Equal(EligibilityReason.ExternalManualUsageUnknown, screen.EligibilityDecision.Reason);
        Assert.Equal(screen.EligibilityDecision.Reason, runnerClaim.EligibilityDecision!.Reason);
    }

    [Fact]
    public async Task StaleTestNowDecisionCannotBypassNewClockRollbackState()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));
        var preview = await fixture.Controller.GetTestNowEligibilityDecisionAsync(CancellationToken.None);
        Assert.True(preview.RequiresConfirmation);

        var highWater = Now.AddMinutes(5);
        fixture.StateStore.SetState(StateDocument.Empty() with
        {
            HighestObservedUtc = highWater,
            Eligibility = EligibilityState.Empty() with { HighestObservedUtc = highWater }
        });
        fixture.TestNowRunner = async (testId, cancellationToken) =>
        {
            var claim = await fixture.ClaimService.TryClaimTestNowAsync(testId, userConfirmed: true, cancellationToken);
            return new TestNowRunResponse(claim.EligibilityDecision!, null);
        };

        var response = await fixture.Controller.RunTestNowAsync(true, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response!.Result);
        Assert.Equal(EligibilityReason.ClockRollback, response.Decision.Reason);
        Assert.Null(fixture.StateStore.State.ActiveTestNowClaim);
        Assert.False(fixture.StateStore.State.Eligibility!.HasUnresolvedClaim);
    }

    [Fact]
    public async Task SaveSettingsAsync_UsesCoreValidationBeforePersistence()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));
        var invalid = fixture.ConfigStore.Config with { Slots = ["bad", "12:01", "17:01", "22:01"] };

        var result = await fixture.Controller.SaveSettingsAsync(invalid, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("HH:mm", StringComparison.Ordinal));
        Assert.Equal(0, fixture.ConfigStore.SaveCount);
        Assert.Empty(fixture.Scheduler.Definitions);
        Assert.Equal(1, fixture.EnvironmentDetector.DetectCount);
    }

    [Fact]
    public void RunnerPathResolver_UsesPortableApplicationDirectory()
    {
        var appBase = Path.Combine(Path.GetTempPath(), "CodexSlotPing", "portable");

        var path = RunnerPathResolver.Resolve(appBase);

        Assert.Equal(Path.GetFullPath(Path.Combine(appBase, "Runner", "CodexSlotPing.Runner.exe")), path);
    }

    private static SchedulerStatus Status(
        SchedulerState state,
        bool exists,
        bool owned,
        bool enabled,
        string actionPath) =>
        new(
            SchedulerTaskContract.ProductionTaskPath,
            state,
            exists,
            owned,
            enabled,
            owned ? SchedulerTaskContract.OwnerMarker : null,
            new SchedulerTaskReadback(
                Array.Empty<SchedulerTriggerReadback>(),
                true,
                "IgnoreNew",
                "PT2M",
                0,
                null,
                false,
                "S-1-5-21-test",
                3,
                0,
                0,
                actionPath,
                null,
                Path.GetDirectoryName(actionPath)),
            Array.Empty<string>(),
            null);

    private sealed class Fixture
    {
        public Fixture(AppConfig config, SchedulerStatus schedulerStatus)
        {
            ConfigStore = new MemoryConfigStore(config);
            var timeZone = new FixedTimeZoneProvider();
            EnvironmentDetector = new ReadyEnvironmentDetector();
            Scheduler = new MemoryScheduler(schedulerStatus, RunnerPath);
            StateStore = new MemoryStateStore();
            Mutex = new RecordingStateMutex();
            ClaimService = new FormalSlotClaimService(new FixedClock(Now), timeZone, ConfigStore, StateStore, Mutex,
                new SlotAdmissionEvaluator(timeZone), new EligibilityEvaluator());
            Controller = new AppController(
                new FixedClock(Now),
                timeZone,
                ConfigStore,
                StateStore,
                ClaimService,
                Scheduler,
                EnvironmentDetector,
                RunnerPath,
                RunTestNowAsync,
                Path.Combine(Path.GetTempPath(), "CodexSlotPing", "logs"));
        }

        public MemoryConfigStore ConfigStore { get; }
        public MemoryScheduler Scheduler { get; }
        public ReadyEnvironmentDetector EnvironmentDetector { get; }
        public MemoryStateStore StateStore { get; }
        public RecordingStateMutex Mutex { get; }
        public FormalSlotClaimService ClaimService { get; }
        public AppController Controller { get; }
        public int TestNowCallCount { get; private set; }
        public Func<string, CancellationToken, Task<TestNowRunResponse>>? TestNowRunner { get; set; }

        private Task<TestNowRunResponse> RunTestNowAsync(string testId, CancellationToken cancellationToken)
        {
            TestNowCallCount++;
            if (TestNowRunner is not null)
            {
                return TestNowRunner(testId, cancellationToken);
            }

            return Task.FromResult(new TestNowRunResponse(
                new EligibilityDecision(EligibilityDecision.CurrentSchemaVersion, EligibilityIntent.TestNow,
                    EligibilityStatus.Eligible, EligibilityReason.Ready, null, Confidence.Unknown,
                    ObservationSource.Unknown, false),
                new TestRunResult(
                    testId,
                    new CodexInvocationResult(CodexResultClassification.CONFIRMED_SUCCESS, true, 0, 10,
                        false, null, Array.Empty<string>(), null, null),
                    Now)));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FixedTimeZoneProvider : ITimeZoneProvider
    {
        public string CurrentWindowsTimeZoneId => "UTC";
        public TimeZoneInfo Resolve(string windowsTimeZoneId) => TimeZoneInfo.Utc;
    }

    private sealed class ReadyEnvironmentDetector : ICodexEnvironmentDetector
    {
        private static readonly IReadOnlySet<string> Reasoning = CodexCliCapabilities.DocumentedReasoningEfforts;
        public int DetectCount { get; private set; }

        public Task<EnvironmentStatus> DetectAsync(CancellationToken cancellationToken)
        {
            DetectCount++;
            var capabilities = new CodexCliCapabilities("codex-cli fixture",
                CodexCliCapabilities.RequiredOptionNames, Reasoning);
            return Task.FromResult(new EnvironmentStatus(true, RunnerPath, capabilities.Version, true,
                Reasoning, Array.Empty<string>(), capabilities));
        }
    }

    private sealed class MemoryConfigStore(AppConfig config) : IConfigStore
    {
        public AppConfig Config { get; private set; } = config;
        public int SaveCount { get; private set; }
        public Task<AppConfig> LoadOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(Config);
        public Task SaveAsync(AppConfig config, CancellationToken cancellationToken)
        {
            Config = config;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStateStore : IStateStore
    {
        public StateDocument State { get; private set; } = StateDocument.Empty();
        public void SetState(StateDocument state) => State = state;
        public Task<StateDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task SaveAsync(StateDocument state, CancellationToken cancellationToken)
        {
            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateMutex : IStateMutex
    {
        private bool _held;
        public StateMutexLease? TryAcquire(TimeSpan timeout)
        {
            if (_held)
            {
                return null;
            }
            _held = true;
            return new StateMutexLease(() => _held = false);
        }
    }

    private sealed class MemoryScheduler(SchedulerStatus initial, string expectedRunner) : ITaskSchedulerGateway
    {
        public SchedulerStatus Current { get; private set; } = initial;
        public int ReadCount { get; private set; }
        public List<SchedulerTaskDefinition> Definitions { get; } = [];

        public Task<SchedulerStatus> ReadStatusAsync(string taskPath, string currentRunnerPath,
            string configuredTimeZoneId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Current);
        }

        public Task<SchedulerOperationResult> InstallOrUpdateAsync(SchedulerTaskDefinition definition,
            CancellationToken cancellationToken)
        {
            Definitions.Add(definition);
            Current = Status(definition.Enabled ? SchedulerState.ON : SchedulerState.OFF,
                exists: true, owned: true, definition.Enabled, expectedRunner);
            return Task.FromResult(new SchedulerOperationResult(true, null, Current));
        }

        public Task<SchedulerOperationResult> DisableAsync(string taskPath, CancellationToken cancellationToken)
        {
            Current = Current with { State = SchedulerState.OFF, IsEnabled = false };
            return Task.FromResult(new SchedulerOperationResult(true, null, Current));
        }

        public Task<SchedulerOperationResult> RemoveAsync(string taskPath, CancellationToken cancellationToken)
        {
            Current = Current with { State = SchedulerState.OFF, Exists = false, IsOwned = false, IsEnabled = false };
            return Task.FromResult(new SchedulerOperationResult(true, null, Current));
        }
    }
}
