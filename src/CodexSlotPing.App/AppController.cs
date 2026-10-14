using System.Globalization;
using System.IO;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
using CodexSlotPing.Core.Time;
using CodexSlotPing.App.ReadModels;

namespace CodexSlotPing.App;

internal sealed record TodaySlotState(int Position, TimeOnly LocalTime, FormalSlotStatus Status);

public sealed record ConfigSaveResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    SchedulerView? SchedulerStatus,
    bool ConfigSaved);

public sealed class AppController
{
    private readonly IClock _clock;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly IConfigStore _configStore;
    private readonly IStateStore _stateStore;
    private readonly FormalSlotClaimService _claimService;
    private readonly ITaskSchedulerGateway _schedulerGateway;
    private readonly ICodexEnvironmentDetector _environmentDetector;
    private readonly string _runnerPath;
    private readonly Func<string, CancellationToken, Task<TestNowRunResponse>> _runTestNow;
    private readonly string _logsDirectory;
    private readonly NextSlotCalculator _nextSlotCalculator;
    private readonly SchedulingCoordinator _schedulingCoordinator;
    private readonly SchedulerHealthEvaluator _schedulerHealthEvaluator = new();
    private readonly TimezoneUpdateService _timezoneUpdateService;
    private readonly SemaphoreSlim _automationOperationGate = new(1, 1);
    private int _testNowActive;

    public AppController(
        IClock clock,
        ITimeZoneProvider timeZoneProvider,
        IConfigStore configStore,
        IStateStore stateStore,
        FormalSlotClaimService claimService,
        ITaskSchedulerGateway schedulerGateway,
        ICodexEnvironmentDetector environmentDetector,
        string runnerPath,
        Func<string, CancellationToken, Task<TestNowRunResponse>> runTestNow,
        string logsDirectory)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _claimService = claimService ?? throw new ArgumentNullException(nameof(claimService));
        _schedulerGateway = schedulerGateway ?? throw new ArgumentNullException(nameof(schedulerGateway));
        _environmentDetector = environmentDetector ?? throw new ArgumentNullException(nameof(environmentDetector));
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerPath);
        _runnerPath = Path.GetFullPath(runnerPath);
        _runTestNow = runTestNow ?? throw new ArgumentNullException(nameof(runTestNow));
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _logsDirectory = Path.GetFullPath(logsDirectory);
        _nextSlotCalculator = new NextSlotCalculator(_timeZoneProvider);
        _schedulingCoordinator = new SchedulingCoordinator(_clock, _claimService);
        _timezoneUpdateService = new TimezoneUpdateService(
            _configStore, _schedulerGateway, _schedulerHealthEvaluator, _schedulingCoordinator);
    }

    public static AppController CreateDefault(string? appBaseDirectory = null)
    {
        var paths = UserDataPaths.ForCurrentUser();
        var clock = new SystemClock();
        var timeZoneProvider = new WindowsTimeZoneProvider();
        var atomicFile = new AtomicJsonFile(new PhysicalAtomicFileOperations());
        var configStore = new JsonConfigStore(atomicFile, paths.ConfigPath);
        var stateStore = new JsonStateStore(atomicFile, paths.StatePath);
        var stateMutex = new NamedStateMutex();
        var pauseProtection = AutomationPauseProtection.CreateForCurrentSession();
        var environmentDetector = new CodexEnvironmentDetector(new CodexExecutableResolver());
        var claimService = new FormalSlotClaimService(
            clock,
            timeZoneProvider,
            configStore,
            stateStore,
            stateMutex,
            new SlotAdmissionEvaluator(timeZoneProvider),
            new EligibilityEvaluator(),
            pauseProtection);
        var runnerPath = RunnerPathResolver.Resolve(appBaseDirectory ?? AppContext.BaseDirectory);
        var testNowRunner = new RunnerProcessClient(runnerPath);

        return new AppController(
            clock,
            timeZoneProvider,
            configStore,
            stateStore,
            claimService,
            new WindowsTaskSchedulerGateway(),
            environmentDetector,
            runnerPath,
            testNowRunner.RunAsync,
            paths.LogsDirectory);
    }

    public Task<AppScreenState> RefreshAsync(CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(() => RefreshCoreAsync(cancellationToken), cancellationToken);

    private async Task<AppScreenState> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var persistentState = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        var pauseProtectionActive = config.IsPaused || _claimService.IsPauseProtectionActive;
        var scheduling = await _schedulingCoordinator.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var now = scheduling.EvaluatedAtUtc;
        var eligibilityDecision = scheduling.EligibilityDecision;
        var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var rawScheduler = await _schedulerGateway.ReadStatusAsync(
            SchedulerTaskContract.ProductionTaskPath,
            _runnerPath,
            config.TimeZone,
            cancellationToken).ConfigureAwait(false);
        var scheduler = _schedulerHealthEvaluator.Evaluate(rawScheduler, _runnerPath, config.TimeZone,
            currentTimeZoneId, scheduling.Plan);
        scheduler = await ReconcilePlanDriftAsync(
            config, scheduling.Plan, scheduler, currentTimeZoneId, pauseProtectionActive, cancellationToken)
            .ConfigureAwait(false);
        var nextSlot = _nextSlotCalculator.GetNext(config, persistentState, now, currentTimeZoneId);
        var todaySlots = BuildTodaySlots(config, persistentState, now, currentTimeZoneId);

        return AppStatusProjection.Create(
            config,
            persistentState,
            eligibilityDecision,
            scheduling.Plan,
            scheduler,
            environment,
            nextSlot,
            todaySlots,
            now,
            currentTimeZoneId,
            _logsDirectory,
            pauseProtectionActive);
    }

    public async Task<ConfigSaveResult> SaveSettingsAsync(
        AutomationView candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var currentConfig = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var config = AppStatusProjection.ToConfig(candidate, currentConfig);
        return await SaveSettingsCoreAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private Task<ConfigSaveResult> SaveSettingsCoreAsync(AppConfig candidate, CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(
            () => SaveSettingsCoreUnlockedAsync(candidate, cancellationToken), cancellationToken);

    private async Task<ConfigSaveResult> SaveSettingsCoreUnlockedAsync(
        AppConfig candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        var validation = ConfigValidator.Validate(candidate, currentTimeZoneId, environment.SupportedReasoningEfforts);
        if (!validation.IsValid || validation.NormalizedConfig is null)
        {
            return new ConfigSaveResult(false, validation.Errors, null, false);
        }

        var currentControl = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var savedConfig = validation.NormalizedConfig with { IsPaused = currentControl.IsPaused };
        var pauseProtectionActive = savedConfig.IsPaused || _claimService.IsPauseProtectionActive;
        SchedulerStatus status;
        try
        {
            var rawStatus = await _schedulerGateway.ReadStatusAsync(
                SchedulerTaskContract.ProductionTaskPath,
                _runnerPath,
                savedConfig.TimeZone,
                cancellationToken).ConfigureAwait(false);
            status = _schedulerHealthEvaluator.Evaluate(rawStatus, _runnerPath, savedConfig.TimeZone,
                currentTimeZoneId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            status = SchedulerError("scheduler-unavailable", savedConfig.TimeZone, currentTimeZoneId);
        }

        if (status.State == SchedulerState.TIMEZONE_CHANGED)
        {
            return new ConfigSaveResult(false,
                ["Timezone changed. Confirm updating the timezone lock and owned scheduler before saving."],
                AppStatusProjection.ProjectScheduler(status, candidate.WakeToRun), false);
        }

        var configToSave = status.Exists && status.IsOwned && !pauseProtectionActive
            ? savedConfig with { Enabled = status.IsEnabled }
            : savedConfig;
        try
        {
            await _configStore.SaveAsync(configToSave, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ConfigSaveResult(false, ["Settings could not be saved."],
                AppStatusProjection.ProjectScheduler(status, configToSave.WakeToRun), false);
        }

        if (!status.Exists || !status.IsOwned || status.State is SchedulerState.CONFLICT or SchedulerState.ERROR)
        {
            return new ConfigSaveResult(true, Array.Empty<string>(),
                AppStatusProjection.ProjectScheduler(status, configToSave.WakeToRun), true);
        }

        var scheduling = await _schedulingCoordinator.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var definition = CreateDefinition(
            configToSave,
            enabled: status.IsEnabled && !pauseProtectionActive,
            scheduling.Plan);
        SchedulerOperationResult update;
        try
        {
            update = await _schedulerGateway.InstallOrUpdateAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ConfigSaveResult(false, ["Settings were saved, but the owned scheduler needs repair."],
                AppStatusProjection.ProjectScheduler(
                    status with { State = SchedulerState.ERROR, DiagnosticCategory = "scheduler-update-failed" },
                    configToSave.WakeToRun), true);
        }

        if (!update.Succeeded)
        {
            return new ConfigSaveResult(false,
                ["Settings were saved, but the owned scheduler needs repair."],
                AppStatusProjection.ProjectScheduler(
                    update.Status ?? status with { State = SchedulerState.ERROR, DiagnosticCategory = update.DiagnosticCategory },
                    configToSave.WakeToRun),
                true);
        }

        var verified = await ReadHealthySchedulerAsync(
            configToSave, currentTimeZoneId, cancellationToken, definition.SchedulePlan).ConfigureAwait(false);
        if (verified.State is not SchedulerState.ON and not SchedulerState.OFF)
        {
            return new ConfigSaveResult(false,
                ["Settings were saved, but scheduler readback shows that repair is still needed."],
                AppStatusProjection.ProjectScheduler(verified, configToSave.WakeToRun), true);
        }

        return new ConfigSaveResult(true, Array.Empty<string>(),
            AppStatusProjection.ProjectScheduler(verified, configToSave.WakeToRun), true);
    }

    public Task<SchedulerOperationView> InstallOrUpdateSchedulerAsync(CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(
            () => InstallOrUpdateSchedulerCoreAsync(cancellationToken), cancellationToken);

    private async Task<SchedulerOperationView> InstallOrUpdateSchedulerCoreAsync(CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        var validation = ConfigValidator.Validate(config, currentTimeZoneId, environment.SupportedReasoningEfforts);
        if (!validation.IsValid || validation.NormalizedConfig is null)
        {
            return new SchedulerOperationView(false, "invalid-configuration");
        }

        if (!environment.IsReady)
        {
            return new SchedulerOperationView(false,
                environment.DiagnosticCodes.FirstOrDefault() ?? "cli-environment-not-ready");
        }

        if (!File.Exists(_runnerPath))
        {
            return new SchedulerOperationView(false, "runner-path-missing");
        }

        var installConfig = validation.NormalizedConfig with { Enabled = true };
        var scheduling = await _schedulingCoordinator.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var shouldEnableScheduler = !installConfig.IsPaused && !_claimService.IsPauseProtectionActive;
        var definition = CreateDefinition(installConfig, shouldEnableScheduler, scheduling.Plan);
        SchedulerOperationResult operation;
        try
        {
            operation = await _schedulerGateway.InstallOrUpdateAsync(
                definition, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new SchedulerOperationView(false, "scheduler-unavailable");
        }

        if (!operation.Succeeded)
        {
            return ProjectSchedulerOperation(operation);
        }

        await _configStore.SaveAsync(installConfig, cancellationToken).ConfigureAwait(false);
        var readback = await ReadHealthySchedulerAsync(
            installConfig, currentTimeZoneId, cancellationToken, definition.SchedulePlan).ConfigureAwait(false);
        return readback.State == (shouldEnableScheduler ? SchedulerState.ON : SchedulerState.OFF)
            ? new SchedulerOperationView(true, null)
            : new SchedulerOperationView(false, readback.DiagnosticCategory ?? "scheduler-readback-failed");
    }

    public Task<SchedulerOperationView> PauseAutomationAsync(CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(() => PauseAutomationCoreAsync(cancellationToken), cancellationToken);

    private async Task<SchedulerOperationView> PauseAutomationCoreAsync(CancellationToken cancellationToken)
    {
        // Persist through Core's claim mutex before touching Task Scheduler. A trigger
        // that races with the scheduler update will then be rejected by the Core gate.
        // Core also keeps an in-process fail-closed gate if this persistence attempt fails.
        var pausePersisted = await _claimService.SetAutomationPausedAsync(true, cancellationToken)
            .ConfigureAwait(false);

        AppConfig config;
        try
        {
            config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return PausePersistenceResult(pausePersisted,
                new SchedulerOperationView(false, "scheduler-unavailable"));
        }

        SchedulerStatus status;
        try
        {
            status = await _schedulerGateway.ReadStatusAsync(
                SchedulerTaskContract.ProductionTaskPath,
                _runnerPath,
                config.TimeZone,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return PausePersistenceResult(pausePersisted,
                new SchedulerOperationView(false, "scheduler-unavailable"));
        }

        if (!status.Exists || !status.IsEnabled)
        {
            var statusResult = status.State == SchedulerState.ERROR
                ? new SchedulerOperationView(false, status.DiagnosticCategory ?? "scheduler-unavailable")
                : new SchedulerOperationView(true, null);
            return PausePersistenceResult(pausePersisted, statusResult);
        }

        if (!status.IsOwned)
        {
            return PausePersistenceResult(pausePersisted,
                new SchedulerOperationView(false, "task-ownership-conflict"));
        }

        SchedulerOperationResult operation;
        try
        {
            operation = await _schedulerGateway.DisableAsync(
                SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return PausePersistenceResult(pausePersisted,
                new SchedulerOperationView(false, "scheduler-unavailable"));
        }

        if (!operation.Succeeded)
        {
            return PausePersistenceResult(pausePersisted, ProjectSchedulerOperation(operation));
        }

        try
        {
            var readback = await _schedulerGateway.ReadStatusAsync(
                SchedulerTaskContract.ProductionTaskPath,
                _runnerPath,
                config.TimeZone,
                cancellationToken).ConfigureAwait(false);
            var result = readback.State != SchedulerState.ERROR && !readback.IsEnabled
                ? new SchedulerOperationView(true, null)
                : new SchedulerOperationView(false, readback.DiagnosticCategory ?? "scheduler-readback-failed");
            return PausePersistenceResult(pausePersisted, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return PausePersistenceResult(pausePersisted,
                new SchedulerOperationView(false, "scheduler-readback-failed"));
        }
    }

    private static SchedulerOperationView PausePersistenceResult(
        bool persisted,
        SchedulerOperationView schedulerOperation) =>
        persisted
            ? schedulerOperation
            : new SchedulerOperationView(false, "automation-pause-not-persisted-protected");

    public Task<SchedulerOperationView> ResumeAutomationAsync(CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(() => ResumeAutomationCoreAsync(cancellationToken), cancellationToken);

    private async Task<SchedulerOperationView> ResumeAutomationCoreAsync(CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var pauseProtectionActive = config.IsPaused || _claimService.IsPauseProtectionActive;
        if (!pauseProtectionActive)
        {
            return new SchedulerOperationView(true, null);
        }

        // Re-evaluate current facts for every Resume. The old one-shot trigger is never reused.
        var scheduling = await _schedulingCoordinator.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        if (!config.Enabled)
        {
            SchedulerStatus disabledStatus;
            try
            {
                disabledStatus = await _schedulerGateway.ReadStatusAsync(
                    SchedulerTaskContract.ProductionTaskPath,
                    _runnerPath,
                    config.TimeZone,
                    cancellationToken).ConfigureAwait(false);
                if (disabledStatus.Exists && disabledStatus.IsEnabled)
                {
                    if (!disabledStatus.IsOwned)
                    {
                        return new SchedulerOperationView(false, "task-ownership-conflict");
                    }

                    var disable = await _schedulerGateway.DisableAsync(
                        SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
                    if (!disable.Succeeded)
                    {
                        return ProjectSchedulerOperation(disable);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return new SchedulerOperationView(false, "scheduler-unavailable");
            }

            return await _claimService.SetAutomationPausedAsync(false, cancellationToken).ConfigureAwait(false)
                ? new SchedulerOperationView(true, null)
                : new SchedulerOperationView(false, "automation-control-save-failed");
        }

        var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        var validation = ConfigValidator.Validate(config, currentTimeZoneId, environment.SupportedReasoningEfforts);
        if (!validation.IsValid || validation.NormalizedConfig is null)
        {
            return new SchedulerOperationView(false, "invalid-configuration");
        }

        if (!environment.IsReady)
        {
            return new SchedulerOperationView(false,
                environment.DiagnosticCodes.FirstOrDefault() ?? "cli-environment-not-ready");
        }

        SchedulerStatus existing;
        try
        {
            existing = await _schedulerGateway.ReadStatusAsync(
                SchedulerTaskContract.ProductionTaskPath,
                _runnerPath,
                config.TimeZone,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new SchedulerOperationView(false, "scheduler-unavailable");
        }

        if (existing.Exists && !existing.IsOwned)
        {
            return new SchedulerOperationView(false, "task-ownership-conflict");
        }

        var definition = CreateDefinition(validation.NormalizedConfig, enabled: false, scheduling.Plan);
        SchedulerOperationResult update;
        try
        {
            update = await _schedulerGateway.InstallOrUpdateAsync(definition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new SchedulerOperationView(false, "scheduler-unavailable");
        }

        if (!update.Succeeded)
        {
            return ProjectSchedulerOperation(update);
        }

        var verified = await ReadHealthySchedulerAsync(
            validation.NormalizedConfig,
            currentTimeZoneId,
            cancellationToken,
            definition.SchedulePlan).ConfigureAwait(false);
        if (verified.State != SchedulerState.OFF)
        {
            return new SchedulerOperationView(false, verified.DiagnosticCategory ?? "scheduler-readback-failed");
        }

        if (!await _claimService.SetAutomationPausedAsync(false, cancellationToken).ConfigureAwait(false))
        {
            return new SchedulerOperationView(false, "automation-control-save-failed");
        }

        var enabledDefinition = CreateDefinition(validation.NormalizedConfig, enabled: true, scheduling.Plan);
        try
        {
            var enable = await _schedulerGateway.InstallOrUpdateAsync(enabledDefinition, cancellationToken)
                .ConfigureAwait(false);
            if (!enable.Succeeded)
            {
                var pauseRestored = await RestorePauseAndDisableSchedulerAsync(cancellationToken).ConfigureAwait(false);
                return new SchedulerOperationView(
                    false,
                    pauseRestored ? enable.DiagnosticCategory ?? "scheduler-update-failed" : "automation-control-save-failed");
            }

            var enabledReadback = await ReadHealthySchedulerAsync(
                validation.NormalizedConfig,
                currentTimeZoneId,
                cancellationToken,
                enabledDefinition.SchedulePlan).ConfigureAwait(false);
            if (enabledReadback.State == SchedulerState.ON)
            {
                return new SchedulerOperationView(true, null);
            }

            var readbackPauseRestored = await RestorePauseAndDisableSchedulerAsync(cancellationToken).ConfigureAwait(false);
            return new SchedulerOperationView(
                false,
                readbackPauseRestored
                    ? enabledReadback.DiagnosticCategory ?? "scheduler-readback-failed"
                    : "automation-control-save-failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            var pauseRestored = await RestorePauseAndDisableSchedulerAsync(cancellationToken).ConfigureAwait(false);
            return new SchedulerOperationView(
                false,
                pauseRestored ? "scheduler-unavailable" : "automation-control-save-failed");
        }
    }

    private async Task<bool> RestorePauseAndDisableSchedulerAsync(CancellationToken cancellationToken)
    {
        var pauseSaved = await _claimService.SetAutomationPausedAsync(true, cancellationToken).ConfigureAwait(false);
        try
        {
            await _schedulerGateway.DisableAsync(
                SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Return the persisted gate result below; scheduler cleanup remains best effort.
        }

        return pauseSaved;
    }

    public Task<SchedulerOperationView> DisableSchedulerAsync(CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(() => DisableSchedulerCoreAsync(cancellationToken), cancellationToken);

    private async Task<SchedulerOperationView> DisableSchedulerCoreAsync(CancellationToken cancellationToken)
    {
        var operation = await _schedulerGateway.DisableAsync(
            SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
        if (!operation.Succeeded)
        {
            return ProjectSchedulerOperation(operation);
        }

        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        await _configStore.SaveAsync(config with { Enabled = false }, cancellationToken).ConfigureAwait(false);
        var readback = await ReadHealthySchedulerAsync(config, _timeZoneProvider.CurrentWindowsTimeZoneId, cancellationToken)
            .ConfigureAwait(false);
        return readback.State == SchedulerState.OFF
            ? new SchedulerOperationView(true, null)
            : new SchedulerOperationView(false, readback.DiagnosticCategory ?? "scheduler-readback-failed");
    }

    public Task<SchedulerOperationView> RemoveSchedulerAsync(CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(() => RemoveSchedulerCoreAsync(cancellationToken), cancellationToken);

    private async Task<SchedulerOperationView> RemoveSchedulerCoreAsync(CancellationToken cancellationToken)
    {
        var operation = await _schedulerGateway.RemoveAsync(
            SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
        if (!operation.Succeeded)
        {
            return ProjectSchedulerOperation(operation);
        }

        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        await _configStore.SaveAsync(config with { Enabled = false }, cancellationToken).ConfigureAwait(false);
        var readback = await ReadHealthySchedulerAsync(config, _timeZoneProvider.CurrentWindowsTimeZoneId, cancellationToken)
            .ConfigureAwait(false);
        return !readback.Exists
            ? new SchedulerOperationView(true, null)
            : new SchedulerOperationView(false, readback.DiagnosticCategory ?? "scheduler-remove-unconfirmed");
    }

    public async Task<ConfigSaveResult> SetWakeToRunAsync(bool wakeToRun, CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return await SaveSettingsCoreAsync(config with { WakeToRun = wakeToRun }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EligibilityView> GetTestNowEligibilityViewAsync(CancellationToken cancellationToken)
    {
        var decision = await _claimService.GetEligibilityDecisionAsync(
            EligibilityRequest.TestNow(_clock.UtcNow.ToUniversalTime()), cancellationToken).ConfigureAwait(false);
        return EligibilityDecisionProjection.Create(decision);
    }

    public async Task<TestNowRunView?> RunTestNowAsync(bool userConfirmed, CancellationToken cancellationToken)
    {
        if (!userConfirmed)
        {
            return null;
        }

        if (Interlocked.CompareExchange(ref _testNowActive, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            var response = await _runTestNow(Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
            return new TestNowRunView(
                EligibilityDecisionProjection.Create(response.Decision),
                response.Result is null ? null : AppStatusProjection.ProjectTestRunResult(response.Result));
        }
        finally
        {
            Volatile.Write(ref _testNowActive, 0);
        }
    }

    public Task<TimezoneUpdateView> ApplyCurrentTimeZoneChangeAsync(
        bool userConfirmed,
        CancellationToken cancellationToken) =>
        ExecuteAutomationOperationAsync(
            () => ApplyCurrentTimeZoneChangeCoreAsync(userConfirmed, cancellationToken), cancellationToken);

    private async Task<TimezoneUpdateView> ApplyCurrentTimeZoneChangeCoreAsync(
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        if (string.Equals(config.TimeZone, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.TimeZone, currentTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            var current = await ReadHealthySchedulerAsync(config, currentTimeZoneId, cancellationToken).ConfigureAwait(false);
            return ProjectTimezoneUpdate(new TimezoneUpdateResult(TimezoneUpdateState.UNCHANGED, config, current,
                current.DiagnosticCategory, false));
        }

        var currentStatus = await _schedulerGateway.ReadStatusAsync(
            SchedulerTaskContract.ProductionTaskPath, _runnerPath, config.TimeZone, cancellationToken).ConfigureAwait(false);
        if (currentStatus.Exists && currentStatus.IsOwned || currentStatus.State is SchedulerState.CONFLICT or SchedulerState.ERROR)
        {
            var result = await _timezoneUpdateService.ApplyAsync(
                config,
                _runnerPath,
                currentTimeZoneId,
                userConfirmed,
                cancellationToken,
                pauseProtectionActive: _claimService.IsPauseProtectionActive).ConfigureAwait(false);
            return ProjectTimezoneUpdate(result);
        }

        var health = _schedulerHealthEvaluator.Evaluate(currentStatus, _runnerPath, config.TimeZone,
            currentTimeZoneId);
        if (!userConfirmed)
        {
            return ProjectTimezoneUpdate(new TimezoneUpdateResult(TimezoneUpdateState.TIMEZONE_CHANGED, config, health,
                "timezone-confirmation-required", false));
        }

        var updatedConfig = config with { TimeZone = currentTimeZoneId };
        await _configStore.SaveAsync(updatedConfig, cancellationToken).ConfigureAwait(false);
        var updatedStatus = await ReadHealthySchedulerAsync(updatedConfig, currentTimeZoneId, cancellationToken)
            .ConfigureAwait(false);
        return ProjectTimezoneUpdate(new TimezoneUpdateResult(
            TimezoneUpdateState.UPDATED, updatedConfig, updatedStatus, null, true));
    }

    private async Task<T> ExecuteAutomationOperationAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _automationOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _automationOperationGate.Release();
        }
    }

    private static SchedulerOperationView ProjectSchedulerOperation(SchedulerOperationResult result) =>
        new(result.Succeeded, result.DiagnosticCategory);

    private static TimezoneUpdateView ProjectTimezoneUpdate(TimezoneUpdateResult result) => new(
        result.State switch
        {
            TimezoneUpdateState.UNCHANGED => TimezoneUpdateViewState.Unchanged,
            TimezoneUpdateState.TIMEZONE_CHANGED => TimezoneUpdateViewState.TimezoneChanged,
            TimezoneUpdateState.UPDATED => TimezoneUpdateViewState.Updated,
            _ => TimezoneUpdateViewState.Error
        },
        result.Succeeded,
        result.DiagnosticCategory,
        result.ConfigSaved);

    private async Task<SchedulerStatus> ReadHealthySchedulerAsync(
        AppConfig config,
        string currentTimeZoneId,
        CancellationToken cancellationToken,
        SchedulePlan? expectedPlan = null)
    {
        var raw = await _schedulerGateway.ReadStatusAsync(
            SchedulerTaskContract.ProductionTaskPath,
            _runnerPath,
            config.TimeZone,
            cancellationToken).ConfigureAwait(false);
        return _schedulerHealthEvaluator.Evaluate(raw, _runnerPath, config.TimeZone,
            currentTimeZoneId, expectedPlan);
    }

    private SchedulerTaskDefinition CreateDefinition(AppConfig config, bool enabled, SchedulePlan schedulePlan) =>
        new(
            SchedulerTaskContract.ProductionTaskPath,
            _runnerPath,
            config.TimeZone,
            schedulePlan,
            config.WakeToRun,
            Enabled: enabled);

    private async Task<SchedulerStatus> ReconcilePlanDriftAsync(
        AppConfig config,
        SchedulePlan plan,
        SchedulerStatus status,
        string currentWindowsTimeZoneId,
        bool pauseProtectionActive,
        CancellationToken cancellationToken)
    {
        string[] scheduleMismatchCodes = [
            "trigger-plan-mismatch",
            "trigger-contract-mismatch",
            "start-when-available-disabled"];
        if (pauseProtectionActive && status.Exists && status.IsOwned && status.IsEnabled)
        {
            try
            {
                var disable = await _schedulerGateway.DisableAsync(
                    SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
                if (!disable.Succeeded)
                {
                    return disable.Status ?? status with
                    {
                        State = SchedulerState.ERROR,
                        DiagnosticCategory = disable.DiagnosticCategory ?? "scheduler-disable-failed"
                    };
                }

                var disabledRaw = await _schedulerGateway.ReadStatusAsync(
                    SchedulerTaskContract.ProductionTaskPath,
                    _runnerPath,
                    config.TimeZone,
                    cancellationToken).ConfigureAwait(false);
                status = _schedulerHealthEvaluator.Evaluate(
                    disabledRaw, _runnerPath, config.TimeZone, currentWindowsTimeZoneId, plan);
                if (status.IsEnabled)
                {
                    return status with
                    {
                        State = SchedulerState.ERROR,
                        DiagnosticCategory = status.DiagnosticCategory ?? "scheduler-disable-unverified"
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return status with { State = SchedulerState.ERROR, DiagnosticCategory = "scheduler-disable-failed" };
            }
        }

        if (!status.Exists || !status.IsOwned || status.ContractMismatches.Count == 0 ||
            status.ContractMismatches.Any(code => !scheduleMismatchCodes.Contains(code, StringComparer.Ordinal)))
        {
            return status;
        }

        try
        {
            var update = await _schedulerGateway.InstallOrUpdateAsync(
                CreateDefinition(config, status.IsEnabled && !pauseProtectionActive, plan), cancellationToken)
                .ConfigureAwait(false);
            if (!update.Succeeded)
            {
                return update.Status ?? status with
                {
                    State = SchedulerState.ERROR,
                    DiagnosticCategory = update.DiagnosticCategory ?? "scheduler-update-failed"
                };
            }

            var raw = await _schedulerGateway.ReadStatusAsync(
                SchedulerTaskContract.ProductionTaskPath,
                _runnerPath,
                config.TimeZone,
                cancellationToken).ConfigureAwait(false);
            return _schedulerHealthEvaluator.Evaluate(
                raw, _runnerPath, config.TimeZone, currentWindowsTimeZoneId, plan);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return status with { State = SchedulerState.ERROR, DiagnosticCategory = "scheduler-update-failed" };
        }
    }

    private IReadOnlyList<TodaySlotState> BuildTodaySlots(
        AppConfig config,
        StateDocument state,
        DateTimeOffset nowUtc,
        string currentWindowsTimeZoneId)
    {
        TimeZoneInfo timeZone;
        try
        {
            timeZone = _timeZoneProvider.Resolve(currentWindowsTimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return Array.Empty<TodaySlotState>();
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);
        var matchingRecords = state.FormalSlots
            .Where(record => record.SlotId.LocalDate == today &&
                             string.Equals(record.SlotId.WindowsTimeZoneId, currentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(record => record.SlotId);
        return config.LegacyFixedSlots
            .Select(slot => TimeOnly.ParseExact(slot, "HH:mm", CultureInfo.InvariantCulture))
            .OrderBy(time => time)
            .Select((time, index) => new TodaySlotState(
                index + 1,
                time,
                matchingRecords.TryGetValue(new SlotId(currentWindowsTimeZoneId, today, time), out var record)
                    ? record.Status
                    : FormalSlotStatus.PENDING))
            .ToArray();
    }

    private static SchedulerStatus SchedulerError(string diagnostic, string timeZone, string currentTimeZone) =>
        new(SchedulerTaskContract.ProductionTaskPath, SchedulerState.ERROR, false, false, false,
            null, null, Array.Empty<string>(), diagnostic)
        {
            ConfiguredTimeZoneId = timeZone,
            CurrentWindowsTimeZoneId = currentTimeZone
        };
}
