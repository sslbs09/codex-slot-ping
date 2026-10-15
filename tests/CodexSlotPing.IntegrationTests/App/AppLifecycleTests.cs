using CodexSlotPing.App;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.IntegrationTests.App;

public sealed class AppLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CloseHidesToTray_UnlessExitWasExplicitlyRequested()
    {
        Assert.True(AppExitPolicy.ShouldHideToTray(exitRequested: false));
        Assert.False(AppExitPolicy.ShouldHideToTray(exitRequested: true));
        Assert.False(AppExitPolicy.ShouldHideToTray(AppShutdownReason.UpdateHandoff));
        Assert.True(AppExitPolicy.ShouldHideToTray(AppShutdownReason.Normal));
    }

    [Fact]
    public void ActiveSchedulerExitWarning_IsClearAndOnlyShownWhenNotSuppressed()
    {
        Assert.Equal("Closing Codex Slot Ping does not disable planned attempts.", AppExitPolicy.ExitWarning);
        Assert.True(AppExitPolicy.ShouldShowWarning(schedulerOn: true, suppressed: false));
        Assert.False(AppExitPolicy.ShouldShowWarning(schedulerOn: false, suppressed: false));
        Assert.False(AppExitPolicy.ShouldShowWarning(schedulerOn: true, suppressed: true));
    }

    [Fact]
    public async Task SuppressExitWarning_PersistsAcrossStoreInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CodexSlotPing-ui-preferences-{Guid.NewGuid():N}.json");
        try
        {
            var first = new UiPreferencesStore(path);
            Assert.False((await first.LoadAsync(CancellationToken.None)).SuppressExitWarning);
            await first.SaveAsync(new UiPreferences(SuppressExitWarning: true), CancellationToken.None);

            var nextLaunch = new UiPreferencesStore(path);
            Assert.True((await nextLaunch.LoadAsync(CancellationToken.None)).SuppressExitWarning);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SecondLaunch_SendsOnlyActivateMessageToPrimary()
    {
        var key = $"lifecycle-{Guid.NewGuid():N}";
        using var primary = new SingleInstanceCoordinator(key);
        using var secondary = new SingleInstanceCoordinator(key);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivateRequested += () => activated.TrySetResult();

        Assert.True(primary.TryBecomePrimary());
        Assert.False(secondary.TryBecomePrimary());
        Assert.True(await secondary.SendActivateAsync(TimeSpan.FromSeconds(4)));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task RepeatedTestNowRequests_OnlyStartOneRequestWhileActive()
    {
        var calls = 0;
        var completion = new TaskCompletionSource<TestNowRunResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ControllerFixture((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return completion.Task;
        });

        var first = fixture.Controller.RunTestNowAsync(true, CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        var second = await fixture.Controller.RunTestNowAsync(true, CancellationToken.None);

        Assert.Null(second);
        Assert.Equal(1, calls);
        completion.SetResult(new TestNowRunResponse(
            new EligibilityDecision(EligibilityDecision.CurrentSchemaVersion, EligibilityIntent.TestNow,
                EligibilityStatus.Eligible, EligibilityReason.Ready, null, Confidence.Unknown,
                ObservationSource.Unknown, false),
            new TestRunResult("test-id", ConfirmedResult(), Now)));
        Assert.NotNull(await first);
    }

    [Fact]
    public async Task TrayWakeToggle_UpdatesSavedSettingsAndPreservesEnabledState()
    {
        var scheduler = new FakeScheduler(OwnedStatus(enabled: true));
        var fixture = new ControllerFixture((_, _) => Task.FromResult(new TestNowRunResponse(
            new EligibilityDecision(EligibilityDecision.CurrentSchemaVersion, EligibilityIntent.TestNow,
                EligibilityStatus.Eligible, EligibilityReason.Ready, null, Confidence.Unknown,
                ObservationSource.Unknown, false),
            new TestRunResult("unused", ConfirmedResult(), Now))),
            scheduler);

        var result = await fixture.Controller.SetWakeToRunAsync(true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(fixture.ConfigStore.Config.WakeToRun);
        Assert.True(Assert.Single(scheduler.Definitions).Enabled);
        Assert.True(Assert.Single(scheduler.Definitions).WakeToRun);
    }

    [Fact]
    public void ExitPolicy_RequestsApplicationShutdownWithoutSchedulerOperations()
    {
        var scheduler = new FakeScheduler(OwnedStatus(enabled: true));
        var shutdownCount = 0;

        var accepted = AppExitPolicy.TryRequestExit(testNowRunning: false, () => shutdownCount++);

        Assert.True(accepted);
        Assert.Equal(1, shutdownCount);
        Assert.Equal(0, scheduler.DisableCount);
        Assert.Equal(0, scheduler.RemoveCount);
    }

    [Fact]
    public void ExitPolicy_DefersShutdownWhileTestNowIsRunning()
    {
        var shutdownCount = 0;

        var deferred = AppExitPolicy.TryRequestExit(testNowRunning: true, () => shutdownCount++);
        var acceptedAfterCompletion = AppExitPolicy.TryRequestExit(testNowRunning: false, () => shutdownCount++);

        Assert.False(deferred);
        Assert.True(acceptedAfterCompletion);
        Assert.Equal(1, shutdownCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The Test Now request did not start in time.");
            }
            await Task.Delay(10);
        }
    }

    private static SchedulerStatus OwnedStatus(bool enabled) => new(
        SchedulerTaskContract.ProductionTaskPath,
        enabled ? SchedulerState.ON : SchedulerState.OFF,
        true,
        true,
        enabled,
        SchedulerTaskContract.OwnerMarker,
        new SchedulerTaskReadback(Array.Empty<SchedulerTriggerReadback>(), true, "IgnoreNew", "PT2M",
            0, null, false, "S-1-5-21-test", 3, 0, 0,
            ControllerFixture.RunnerPath, null, Path.GetDirectoryName(ControllerFixture.RunnerPath)),
        Array.Empty<string>(),
        null);

    private static CodexInvocationResult ConfirmedResult() => new(
        CodexResultClassification.CONFIRMED_SUCCESS, true, 0, 20, false, null,
        Array.Empty<string>(), null, null);

    private sealed class ControllerFixture
    {
        public const string RunnerPath = @"C:\portable\CodexSlotPing.Runner.exe";

        public ControllerFixture(Func<string, CancellationToken, Task<TestNowRunResponse>> runTestNow,
            FakeScheduler? scheduler = null)
        {
            ConfigStore = new MemoryConfigStore(AppConfigDefaults.Create());
            var zone = new FixedTimeZoneProvider();
            var stateStore = new MemoryStateStore();
            var mutex = new RecordingStateMutex();
            var claimService = new FormalSlotClaimService(new FixedClock(Now), zone, ConfigStore, stateStore, mutex,
                new SlotAdmissionEvaluator(zone), new EligibilityEvaluator());
            Controller = new AppController(new FixedClock(Now), zone, ConfigStore, stateStore, claimService,
                scheduler ?? new FakeScheduler(new SchedulerStatus(SchedulerTaskContract.ProductionTaskPath,
                    SchedulerState.OFF, false, false, false, null, null, Array.Empty<string>(), null)),
                new ReadyEnvironmentDetector(), RunnerPath, runTestNow,
                Path.Combine(Path.GetTempPath(), "CodexSlotPing", "logs"));
        }

        public MemoryConfigStore ConfigStore { get; }
        public AppController Controller { get; }

        public sealed class MemoryConfigStore(AppConfig config) : IConfigStore
        {
            public AppConfig Config { get; private set; } = config;
            public Task<AppConfig> LoadOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(Config);
            public Task SaveAsync(AppConfig config, CancellationToken cancellationToken)
            {
                Config = config;
                return Task.CompletedTask;
            }
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
        public Task<EnvironmentStatus> DetectAsync(CancellationToken cancellationToken)
        {
            var reasoning = CodexCliCapabilities.DocumentedReasoningEfforts;
            var capabilities = new CodexCliCapabilities("codex-cli fixture",
                CodexCliCapabilities.RequiredOptionNames, reasoning);
            return Task.FromResult(new EnvironmentStatus(true, ControllerFixture.RunnerPath, capabilities.Version,
                true, reasoning, Array.Empty<string>(), capabilities));
        }
    }

    private sealed class MemoryStateStore : IStateStore
    {
        public StateDocument State { get; private set; } = StateDocument.Empty();
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

    private sealed class FakeScheduler(SchedulerStatus status) : ITaskSchedulerGateway
    {
        public SchedulerStatus Status { get; private set; } = status;
        public List<SchedulerTaskDefinition> Definitions { get; } = [];
        public int DisableCount { get; private set; }
        public int RemoveCount { get; private set; }

        public Task<SchedulerStatus> ReadStatusAsync(string taskPath, string currentRunnerPath,
            string configuredTimeZoneId, CancellationToken cancellationToken) => Task.FromResult(Status);

        public Task<SchedulerOperationResult> InstallOrUpdateAsync(SchedulerTaskDefinition definition,
            CancellationToken cancellationToken)
        {
            Definitions.Add(definition);
            Status = OwnedStatus(definition.Enabled) with
            {
                Readback = Status.Readback! with
                {
                    WakeToRun = definition.WakeToRun,
                    Triggers = definition.SchedulePlan.OneShotTriggerAtUtc is { } at
                        ? [new SchedulerTriggerReadback(1, false, true, 0,
                            at.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                            at.AddDays(3650).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz"))]
                        : Array.Empty<SchedulerTriggerReadback>()
                }
            };
            return Task.FromResult(new SchedulerOperationResult(true, null, Status));
        }

        public Task<SchedulerOperationResult> DisableAsync(string taskPath, CancellationToken cancellationToken)
        {
            DisableCount++;
            return Task.FromResult(new SchedulerOperationResult(false, "not-used", Status));
        }

        public Task<SchedulerOperationResult> RemoveAsync(string taskPath, CancellationToken cancellationToken)
        {
            RemoveCount++;
            return Task.FromResult(new SchedulerOperationResult(false, "not-used", Status));
        }
    }
}
