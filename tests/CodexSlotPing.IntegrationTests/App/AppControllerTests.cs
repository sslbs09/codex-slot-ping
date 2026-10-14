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
using CodexSlotPing.App.ReadModels;
using System.Globalization;

namespace CodexSlotPing.IntegrationTests.App;

public sealed class AppControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string RunnerPath = @"C:\portable\CodexSlotPing.Runner.exe";

    [Fact]
    public async Task RefreshAsync_ReturnsAppOwnedReadModelsInsteadOfCoreFacts()
    {
        var config = AppConfigDefaults.Create() with { Model = "gpt-read-model-test" };
        var fixture = new Fixture(config,
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));
        fixture.StateStore.SetState(StateDocument.Empty() with
        {
            LastTest = new TestRunRecord("test-123", Now.AddMinutes(-20), "gpt-read-model-test", "high",
                "confirmed-success", 12_345, 0)
        });

        var screen = await fixture.Controller.RefreshAsync(CancellationToken.None);

        var readModelTypes = new[]
        {
            typeof(AppScreenState), typeof(AutomationView), typeof(EligibilityView), typeof(ScheduleView),
            typeof(SchedulerView), typeof(CodexEnvironmentView), typeof(NextSlotView), typeof(TodaySlotView),
            typeof(LatestKnownResultView)
        };
        Assert.Empty(readModelTypes
            .SelectMany(type => type.GetProperties())
            .Where(property => HasCoreType(property.PropertyType))
            .Select(property => property.Name));

        Assert.Equal("gpt-read-model-test", screen.Automation.Model);
        Assert.True(screen.Automation.Enabled);
        Assert.Equal(EligibilityViewStatus.Eligible, screen.Eligibility.Status);
        Assert.False(string.IsNullOrWhiteSpace(screen.Eligibility.Reason));
        Assert.False(string.IsNullOrWhiteSpace(screen.Eligibility.Confidence));
        Assert.NotNull(screen.Schedule.NextAttemptAtUtc);
        Assert.Equal(
            screen.Schedule.NextAttemptAtUtc!.Value.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture),
            screen.Eligibility.NextPlannedAttemptText);
        Assert.Equal(SchedulerViewState.Disabled, screen.Scheduler.State);
        Assert.Equal("confirmed-success", screen.LatestKnownResult!.ResultCategory);

        static bool HasCoreType(Type type) =>
            type.Namespace?.StartsWith("CodexSlotPing.Core", StringComparison.Ordinal) == true ||
            type.GetGenericArguments().Any(HasCoreType);
    }

    [Fact]
    public async Task RefreshAsync_UsesSchedulerReadbackInsteadOfConfigEnabledFlag()
    {
        var fixture = new Fixture(AppConfigDefaults.Create() with { Enabled = true },
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));

        var state = await fixture.Controller.RefreshAsync(CancellationToken.None);

        Assert.Equal(SchedulerViewState.Disabled, state.Scheduler.State);
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
            var changed = AppStatusProjection.ProjectAutomation(fixture.ConfigStore.Config) with
            {
                Prompt = "Updated prompt."
            };

            var result = await fixture.Controller.SaveSettingsAsync(changed, CancellationToken.None);

            Assert.True(result.Succeeded);
            var definition = Assert.Single(fixture.Scheduler.Definitions);
            Assert.Equal(enabled, definition.Enabled);
            Assert.Equal("Updated prompt.", fixture.ConfigStore.Config.Prompt);
        }
    }

    [Fact]
    public async Task SaveSettingsAsync_PreservesPauseIntentFromCurrentConfig()
    {
        var fixture = new Fixture(
            AppConfigDefaults.Create() with { Enabled = true, IsPaused = true },
            Status(SchedulerState.OWNED_DISABLED, exists: true, owned: true, enabled: false, RunnerPath));
        var staleView = AppStatusProjection.ProjectAutomation(fixture.ConfigStore.Config) with
        {
            IsPaused = false,
            Prompt = "Updated while paused."
        };

        var result = await fixture.Controller.SaveSettingsAsync(staleView, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(fixture.ConfigStore.Config.Enabled);
        Assert.True(fixture.ConfigStore.Config.IsPaused);
        Assert.False(Assert.Single(fixture.Scheduler.Definitions).Enabled);
    }

    [Fact]
    public async Task PauseAutomation_PersistsCoreProtectionWhenSchedulerDisableFails()
    {
        var fixture = new Fixture(
            AppConfigDefaults.Create() with { Enabled = true },
            Status(SchedulerState.ON, exists: true, owned: true, enabled: true, RunnerPath));
        fixture.Scheduler.FailDisable = true;

        var operation = await fixture.Controller.PauseAutomationAsync(CancellationToken.None);
        var scheduledClaim = await fixture.ClaimService.TryClaimScheduledAttemptAsync(CancellationToken.None);
        var testNow = await fixture.ClaimService.TryClaimTestNowAsync(
            "pause-flow-test-now", userConfirmed: true, CancellationToken.None);

        Assert.False(operation.Succeeded);
        Assert.True(fixture.ConfigStore.Config.Enabled);
        Assert.True(fixture.ConfigStore.Config.IsPaused);
        Assert.Equal(SlotClaimBlockReason.AutomationPaused, scheduledClaim.BlockReason);
        Assert.True(testNow.IsClaimed);
        var screen = await fixture.Controller.RefreshAsync(CancellationToken.None);
        Assert.True(screen.Automation.IsPaused);
        Assert.Equal(SchedulerViewState.Error, screen.Scheduler.State);
        Assert.Equal("scheduler-enabled-while-paused", screen.Scheduler.DiagnosticCategory);
    }

    [Fact]
    public async Task ResumeAutomation_WritesFreshPlanBeforeClearingPause()
    {
        var oldTriggerAt = Now.AddHours(1);
        var disabledScheduler = Status(SchedulerState.OWNED_DISABLED,
            exists: true, owned: true, enabled: false, RunnerPath);
        disabledScheduler = disabledScheduler with
        {
            Readback = disabledScheduler.Readback! with
            {
                Triggers = [new SchedulerTriggerReadback(
                    1, false, true, 0,
                    oldTriggerAt.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                    oldTriggerAt.AddDays(3650).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz"))]
            }
        };
        var fixture = new Fixture(
            AppConfigDefaults.Create() with { Enabled = true, IsPaused = true },
            disabledScheduler);
        fixture.Clock.Now = Now.AddMinutes(2);
        fixture.StateStore.SetState(StateDocument.Empty() with
        {
            Eligibility = EligibilityState.Empty() with
            {
                ExternalManualUsageUnknown = false,
                LastConfirmedSuccessAtUtc = fixture.Clock.Now.AddHours(-2)
            }
        });
        var expectedFreshPlan = fixture.Clock.Now.AddHours(3);

        var operation = await fixture.Controller.ResumeAutomationAsync(CancellationToken.None);

        var definitions = fixture.Scheduler.Definitions;
        Assert.True(operation.Succeeded);
        Assert.Equal(2, definitions.Count);
        Assert.Equal(new[] { true, false }, fixture.Scheduler.PausedAtUpdates);
        Assert.False(definitions[0].Enabled);
        Assert.True(definitions[1].Enabled);
        Assert.Equal(definitions[0].SchedulePlan, definitions[1].SchedulePlan);
        Assert.NotEqual(oldTriggerAt, definitions[0].SchedulePlan.OneShotTriggerAtUtc);
        Assert.Equal(expectedFreshPlan, definitions[0].SchedulePlan.OneShotTriggerAtUtc);
        Assert.False(fixture.ConfigStore.Config.IsPaused);
        Assert.True(fixture.ConfigStore.Config.Enabled);
    }

    [Fact]
    public async Task ResumeAutomation_RestoresPauseIfSchedulerEnableFails()
    {
        var fixture = new Fixture(
            AppConfigDefaults.Create() with { Enabled = true, IsPaused = true },
            Status(SchedulerState.OWNED_DISABLED, exists: true, owned: true, enabled: false, RunnerPath));
        fixture.Scheduler.FailInstallOnCall = 2;

        var operation = await fixture.Controller.ResumeAutomationAsync(CancellationToken.None);
        var scheduledClaim = await fixture.ClaimService.TryClaimScheduledAttemptAsync(CancellationToken.None);

        Assert.False(operation.Succeeded);
        Assert.True(fixture.ConfigStore.Config.IsPaused);
        Assert.Equal(SlotClaimBlockReason.AutomationPaused, scheduledClaim.BlockReason);
        Assert.False(fixture.Scheduler.Current.IsEnabled);
        Assert.Equal(2, fixture.Scheduler.Definitions.Count);
    }

    [Fact]
    public async Task RefreshAsync_ReportsPathAndTimeZoneDrift()
    {
        var pathFixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.ON, exists: true, owned: true, enabled: true, @"C:\old\CodexSlotPing.Runner.exe"));
        var pathState = await pathFixture.Controller.RefreshAsync(CancellationToken.None);
        Assert.Equal(SchedulerViewState.NeedsRepair, pathState.Scheduler.State);
        Assert.Contains("runner-action-mismatch", pathState.Scheduler.ContractMismatches);

        var lockedConfig = AppConfigDefaults.Create() with { TimeZone = "America/New_York" };
        var timezoneFixture = new Fixture(lockedConfig,
            Status(SchedulerState.ON, exists: true, owned: true, enabled: true, RunnerPath));
        var timezoneState = await timezoneFixture.Controller.RefreshAsync(CancellationToken.None);
        Assert.Equal(SchedulerViewState.TimezoneChanged, timezoneState.Scheduler.State);
        Assert.Equal("timezone-mismatch", timezoneState.Scheduler.DiagnosticCategory);
    }

    [Fact]
    public async Task RefreshAsync_ReplacesLegacyDailyTriggersWithCurrentOneShotPlan()
    {
        var config = AppConfigDefaults.Create();
        var initial = Status(SchedulerState.ON, exists: true, owned: true, enabled: true, RunnerPath);
        var changedTimes = new[] { new SchedulerTriggerReadback(2, true, true, 1, "2099-01-01T07:00:00") };
        var status = initial with { Readback = initial.Readback! with { Triggers = changedTimes } };
        var fixture = new Fixture(config, status);

        var state = await fixture.Controller.RefreshAsync(CancellationToken.None);

        Assert.Equal(SchedulerViewState.Enabled, state.Scheduler.State);
        var definition = Assert.Single(fixture.Scheduler.Definitions);
        Assert.NotNull(definition.SchedulePlan.OneShotTriggerAtUtc);
        Assert.Single(fixture.Scheduler.Current.Readback!.Triggers);
        Assert.True(fixture.Scheduler.Current.Readback.Triggers[0].IsOneShot);
    }

    [Fact]
    public async Task ExternalUsageUnknown_StillProducesLocalOneShotPlan()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.ON, exists: true, owned: true, enabled: true, RunnerPath));

        var state = await fixture.Controller.RefreshAsync(CancellationToken.None);

        Assert.Equal(EligibilityViewStatus.Eligible, state.Eligibility.Status);
        Assert.Contains(UiText.Get("Eligibility.Reason.ExternalManualUsageUnknown"), state.Eligibility.DetailsText);
        Assert.NotNull(state.Schedule.NextAttemptAtUtc);
        Assert.True(Assert.Single(fixture.Scheduler.Definitions).SchedulePlan.OneShotTriggerAtUtc.HasValue);
    }

    [Fact]
    public async Task UsageLimitWithoutTrustedReset_ProducesNoTrigger()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.ON, exists: true, owned: true, enabled: true, RunnerPath));
        var observedAt = Now.AddMinutes(-5);
        var usageLimit = new EligibilityObservation(
            EligibilityObservationKind.UsageLimitReached,
            observedAt,
            ObservationSource.AppScheduled,
            Confidence.High);
        fixture.StateStore.SetState(StateDocument.Empty() with
        {
            HighestObservedUtc = Now,
            Eligibility = EligibilityState.Empty() with
            {
                HighestObservedUtc = Now,
                Observations = [usageLimit]
            }
        });

        var state = await fixture.Controller.RefreshAsync(CancellationToken.None);

        Assert.Equal(EligibilityViewStatus.Blocked, state.Eligibility.Status);
        Assert.Null(state.Schedule.NextAttemptAtUtc);
        Assert.Empty(fixture.Scheduler.Definitions);
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

        Assert.Equal(EligibilityViewStatus.Eligible, screen.Eligibility.Status);
        Assert.Contains(UiText.Get("Eligibility.Reason.Ready"), screen.Eligibility.DetailsText);
        Assert.Contains(UiText.Get("Eligibility.Reason.ExternalManualUsageUnknown"), screen.Eligibility.DetailsText);
        Assert.Equal(screen.Eligibility.Reason,
            UiText.Get($"Eligibility.Reason.{runnerClaim.EligibilityDecision!.Reason}"));
    }

    [Fact]
    public async Task StaleTestNowDecisionCannotBypassNewClockRollbackState()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));
        var preview = await fixture.Controller.GetTestNowEligibilityViewAsync(CancellationToken.None);
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
        Assert.Contains(UiText.Get("Eligibility.Reason.ClockRollback"), response.Decision.DetailsText);
        Assert.Null(fixture.StateStore.State.ActiveTestNowClaim);
        Assert.False(fixture.StateStore.State.Eligibility!.HasUnresolvedClaim);
    }

    [Fact]
    public async Task SaveSettingsAsync_UsesCoreValidationBeforePersistence()
    {
        var fixture = new Fixture(AppConfigDefaults.Create(),
            Status(SchedulerState.OFF, exists: false, owned: false, enabled: false, RunnerPath));
        var invalid = AppStatusProjection.ProjectAutomation(fixture.ConfigStore.Config) with
        {
            LegacyFixedSlots = ["bad", "12:01", "17:01", "22:01"]
        };

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
            Scheduler.IsPaused = () => ConfigStore.Config.IsPaused;
            StateStore = new MemoryStateStore();
            Mutex = new RecordingStateMutex();
            Clock = new FixedClock(Now);
            ClaimService = new FormalSlotClaimService(Clock, timeZone, ConfigStore, StateStore, Mutex,
                new SlotAdmissionEvaluator(timeZone), new EligibilityEvaluator());
            Controller = new AppController(
                Clock,
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
        public FixedClock Clock { get; }
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
        public DateTimeOffset Now { get; set; } = now;
        public DateTimeOffset UtcNow => Now;
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
        public List<bool> PausedAtUpdates { get; } = [];
        public Func<bool>? IsPaused { get; set; }
        public bool FailDisable { get; set; }
        public int? FailInstallOnCall { get; set; }

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
            PausedAtUpdates.Add(IsPaused?.Invoke() ?? false);
            if (FailInstallOnCall == Definitions.Count)
            {
                return Task.FromResult(new SchedulerOperationResult(false, "scheduler-update-failed", Current));
            }

            var readback = Status(definition.Enabled ? SchedulerState.ON : SchedulerState.OFF,
                exists: true, owned: true, definition.Enabled, expectedRunner).Readback!;
            var triggerAtUtc = definition.SchedulePlan.OneShotTriggerAtUtc;
            var triggers = triggerAtUtc is { } at
                ? new[] { new SchedulerTriggerReadback(1, false, true, 0,
                    at.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                    at.AddDays(3650).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz")) }
                : Array.Empty<SchedulerTriggerReadback>();
            Current = Status(definition.Enabled ? SchedulerState.ON : SchedulerState.OFF,
                exists: true, owned: true, definition.Enabled, expectedRunner) with
            {
                Readback = readback with { Triggers = triggers }
            };
            return Task.FromResult(new SchedulerOperationResult(true, null, Current));
        }

        public Task<SchedulerOperationResult> DisableAsync(string taskPath, CancellationToken cancellationToken)
        {
            if (FailDisable)
            {
                return Task.FromResult(new SchedulerOperationResult(false, "scheduler-disable-failed", Current));
            }

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
