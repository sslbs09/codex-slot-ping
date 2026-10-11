using System.Globalization;
using System.IO;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Testing;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.App;

public sealed record TodaySlotState(int Position, TimeOnly LocalTime, FormalSlotStatus Status);

public sealed record AppScreenState(
    AppConfig Config,
    StateDocument PersistentState,
    EnvironmentStatus Codex,
    SchedulerStatus Scheduler,
    SlotScheduleEntry? NextSlot,
    IReadOnlyList<TodaySlotState> TodaySlots,
    DateTimeOffset NowUtc,
    string CurrentWindowsTimeZoneId,
    string LogsDirectory)
{
    public bool EffectiveWakeToRun => Scheduler.Exists && Scheduler.IsOwned && Scheduler.Readback is not null
        ? Scheduler.Readback.WakeToRun
        : Config.WakeToRun;
}

public sealed record ConfigSaveResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    SchedulerStatus? SchedulerStatus,
    bool ConfigSaved);

public sealed class AppController
{
    private readonly IClock _clock;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly IConfigStore _configStore;
    private readonly IStateStore _stateStore;
    private readonly ITaskSchedulerGateway _schedulerGateway;
    private readonly ICodexEnvironmentDetector _environmentDetector;
    private readonly string _runnerPath;
    private readonly Func<AppConfig, CancellationToken, Task<TestRunResult>> _runTestNow;
    private readonly string _logsDirectory;
    private readonly NextSlotCalculator _nextSlotCalculator;
    private readonly SchedulerHealthEvaluator _schedulerHealthEvaluator = new();
    private readonly TimezoneUpdateService _timezoneUpdateService;
    private int _testNowActive;

    public AppController(
        IClock clock,
        ITimeZoneProvider timeZoneProvider,
        IConfigStore configStore,
        IStateStore stateStore,
        ITaskSchedulerGateway schedulerGateway,
        ICodexEnvironmentDetector environmentDetector,
        string runnerPath,
        Func<AppConfig, CancellationToken, Task<TestRunResult>> runTestNow,
        string logsDirectory)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _schedulerGateway = schedulerGateway ?? throw new ArgumentNullException(nameof(schedulerGateway));
        _environmentDetector = environmentDetector ?? throw new ArgumentNullException(nameof(environmentDetector));
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerPath);
        _runnerPath = Path.GetFullPath(runnerPath);
        _runTestNow = runTestNow ?? throw new ArgumentNullException(nameof(runTestNow));
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _logsDirectory = Path.GetFullPath(logsDirectory);
        _nextSlotCalculator = new NextSlotCalculator(_timeZoneProvider);
        _timezoneUpdateService = new TimezoneUpdateService(_configStore, _schedulerGateway, _schedulerHealthEvaluator);
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
        var environmentDetector = new CodexEnvironmentDetector(new CodexExecutableResolver());
        var rawLogStore = new RawLogStore(Path.Combine(paths.LogsDirectory, "raw"), clock);
        var processLauncher = new CodexProcessLauncher(new CodexInvocationBuilder(), rawLogStore: rawLogStore);
        var testRunService = new TestRunService(
            clock,
            timeZoneProvider,
            stateStore,
            stateMutex,
            environmentDetector,
            processLauncher,
            new StructuredLogWriter(paths.LogsDirectory));

        return new AppController(
            clock,
            timeZoneProvider,
            configStore,
            stateStore,
            new WindowsTaskSchedulerGateway(),
            environmentDetector,
            RunnerPathResolver.Resolve(appBaseDirectory ?? AppContext.BaseDirectory),
            testRunService.RunAsync,
            paths.LogsDirectory);
    }

    public async Task<AppScreenState> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var persistentState = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        var now = _clock.UtcNow.ToUniversalTime();
        var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var rawScheduler = await _schedulerGateway.ReadStatusAsync(
            SchedulerTaskContract.ProductionTaskPath,
            _runnerPath,
            config.TimeZone,
            cancellationToken).ConfigureAwait(false);
        var scheduler = _schedulerHealthEvaluator.Evaluate(rawScheduler, _runnerPath, config.TimeZone, currentTimeZoneId);
        var nextSlot = _nextSlotCalculator.GetNext(config, persistentState, now, currentTimeZoneId);
        var todaySlots = BuildTodaySlots(config, persistentState, now, currentTimeZoneId);

        return new AppScreenState(config, persistentState, environment, scheduler, nextSlot, todaySlots,
            now, currentTimeZoneId, _logsDirectory);
    }

    public async Task<ConfigSaveResult> SaveSettingsAsync(AppConfig candidate, CancellationToken cancellationToken)
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

        var savedConfig = validation.NormalizedConfig;
        SchedulerStatus status;
        try
        {
            var rawStatus = await _schedulerGateway.ReadStatusAsync(
                SchedulerTaskContract.ProductionTaskPath,
                _runnerPath,
                savedConfig.TimeZone,
                cancellationToken).ConfigureAwait(false);
            status = _schedulerHealthEvaluator.Evaluate(rawStatus, _runnerPath, savedConfig.TimeZone, currentTimeZoneId);
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
                ["Timezone changed. Confirm updating the timezone lock and owned scheduler before saving."], status, false);
        }

        var configToSave = status.Exists && status.IsOwned
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
            return new ConfigSaveResult(false, ["Settings could not be saved."], status, false);
        }

        if (!status.Exists || !status.IsOwned || status.State is SchedulerState.CONFLICT or SchedulerState.ERROR)
        {
            return new ConfigSaveResult(true, Array.Empty<string>(), status, true);
        }

        var definition = CreateDefinition(configToSave, enabled: status.IsEnabled);
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
                status with { State = SchedulerState.ERROR, DiagnosticCategory = "scheduler-update-failed" }, true);
        }

        if (!update.Succeeded)
        {
            return new ConfigSaveResult(false,
                ["Settings were saved, but the owned scheduler needs repair."],
                update.Status ?? status with { State = SchedulerState.ERROR, DiagnosticCategory = update.DiagnosticCategory },
                true);
        }

        var verified = await ReadHealthySchedulerAsync(configToSave, currentTimeZoneId, cancellationToken).ConfigureAwait(false);
        if (verified.State is not SchedulerState.ON and not SchedulerState.OFF)
        {
            return new ConfigSaveResult(false,
                ["Settings were saved, but scheduler readback shows that repair is still needed."], verified, true);
        }

        return new ConfigSaveResult(true, Array.Empty<string>(), verified, true);
    }

    public async Task<SchedulerOperationResult> InstallOrUpdateSchedulerAsync(CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        var validation = ConfigValidator.Validate(config, currentTimeZoneId, environment.SupportedReasoningEfforts);
        if (!validation.IsValid || validation.NormalizedConfig is null)
        {
            return new SchedulerOperationResult(false, "invalid-configuration", null);
        }

        if (!environment.IsReady)
        {
            return new SchedulerOperationResult(false,
                environment.DiagnosticCodes.FirstOrDefault() ?? "cli-environment-not-ready", null);
        }

        if (!File.Exists(_runnerPath))
        {
            return new SchedulerOperationResult(false, "runner-path-missing", null);
        }

        var installConfig = validation.NormalizedConfig with { Enabled = true };
        SchedulerOperationResult operation;
        try
        {
            operation = await _schedulerGateway.InstallOrUpdateAsync(
                CreateDefinition(installConfig, enabled: true), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new SchedulerOperationResult(false, "scheduler-unavailable", null);
        }

        if (!operation.Succeeded)
        {
            return operation;
        }

        await _configStore.SaveAsync(installConfig, cancellationToken).ConfigureAwait(false);
        var readback = await ReadHealthySchedulerAsync(installConfig, currentTimeZoneId, cancellationToken).ConfigureAwait(false);
        return readback.State == SchedulerState.ON
            ? new SchedulerOperationResult(true, null, readback)
            : new SchedulerOperationResult(false, readback.DiagnosticCategory ?? "scheduler-readback-failed", readback);
    }

    public async Task<SchedulerOperationResult> DisableSchedulerAsync(CancellationToken cancellationToken)
    {
        var operation = await _schedulerGateway.DisableAsync(
            SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
        if (!operation.Succeeded)
        {
            return operation;
        }

        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        await _configStore.SaveAsync(config with { Enabled = false }, cancellationToken).ConfigureAwait(false);
        var readback = await ReadHealthySchedulerAsync(config, _timeZoneProvider.CurrentWindowsTimeZoneId, cancellationToken)
            .ConfigureAwait(false);
        return readback.State == SchedulerState.OFF
            ? new SchedulerOperationResult(true, null, readback)
            : new SchedulerOperationResult(false, readback.DiagnosticCategory ?? "scheduler-readback-failed", readback);
    }

    public async Task<SchedulerOperationResult> RemoveSchedulerAsync(CancellationToken cancellationToken)
    {
        var operation = await _schedulerGateway.RemoveAsync(
            SchedulerTaskContract.ProductionTaskPath, cancellationToken).ConfigureAwait(false);
        if (!operation.Succeeded)
        {
            return operation;
        }

        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        await _configStore.SaveAsync(config with { Enabled = false }, cancellationToken).ConfigureAwait(false);
        var readback = await ReadHealthySchedulerAsync(config, _timeZoneProvider.CurrentWindowsTimeZoneId, cancellationToken)
            .ConfigureAwait(false);
        return !readback.Exists
            ? new SchedulerOperationResult(true, null, readback)
            : new SchedulerOperationResult(false, readback.DiagnosticCategory ?? "scheduler-remove-unconfirmed", readback);
    }

    public async Task<ConfigSaveResult> SetWakeToRunAsync(bool wakeToRun, CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return await SaveSettingsAsync(config with { WakeToRun = wakeToRun }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TestRunResult?> RunTestNowAsync(bool userConfirmed, CancellationToken cancellationToken)
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
            var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
            var validation = ConfigValidator.Validate(config, _timeZoneProvider.CurrentWindowsTimeZoneId,
                environment.SupportedReasoningEfforts);
            if (!validation.IsValid || validation.NormalizedConfig is null)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
            }

            return await _runTestNow(validation.NormalizedConfig, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _testNowActive, 0);
        }
    }

    public async Task<TimezoneUpdateResult> ApplyCurrentTimeZoneChangeAsync(
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var currentTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        if (string.Equals(config.TimeZone, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.TimeZone, currentTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            var current = await ReadHealthySchedulerAsync(config, currentTimeZoneId, cancellationToken).ConfigureAwait(false);
            return new TimezoneUpdateResult(TimezoneUpdateState.UNCHANGED, config, current,
                current.DiagnosticCategory, false);
        }

        var currentStatus = await _schedulerGateway.ReadStatusAsync(
            SchedulerTaskContract.ProductionTaskPath, _runnerPath, config.TimeZone, cancellationToken).ConfigureAwait(false);
        if (currentStatus.Exists && currentStatus.IsOwned || currentStatus.State is SchedulerState.CONFLICT or SchedulerState.ERROR)
        {
            return await _timezoneUpdateService.ApplyAsync(
                config,
                _runnerPath,
                currentTimeZoneId,
                userConfirmed,
                cancellationToken).ConfigureAwait(false);
        }

        var health = _schedulerHealthEvaluator.Evaluate(currentStatus, _runnerPath, config.TimeZone, currentTimeZoneId);
        if (!userConfirmed)
        {
            return new TimezoneUpdateResult(TimezoneUpdateState.TIMEZONE_CHANGED, config, health,
                "timezone-confirmation-required", false);
        }

        var updatedConfig = config with { TimeZone = currentTimeZoneId };
        await _configStore.SaveAsync(updatedConfig, cancellationToken).ConfigureAwait(false);
        var updatedStatus = await ReadHealthySchedulerAsync(updatedConfig, currentTimeZoneId, cancellationToken)
            .ConfigureAwait(false);
        return new TimezoneUpdateResult(TimezoneUpdateState.UPDATED, updatedConfig, updatedStatus, null, true);
    }

    private async Task<SchedulerStatus> ReadHealthySchedulerAsync(
        AppConfig config,
        string currentTimeZoneId,
        CancellationToken cancellationToken)
    {
        var raw = await _schedulerGateway.ReadStatusAsync(
            SchedulerTaskContract.ProductionTaskPath,
            _runnerPath,
            config.TimeZone,
            cancellationToken).ConfigureAwait(false);
        return _schedulerHealthEvaluator.Evaluate(raw, _runnerPath, config.TimeZone, currentTimeZoneId);
    }

    private SchedulerTaskDefinition CreateDefinition(AppConfig config, bool enabled)
    {
        var times = config.Slots
            .Select(slot => TimeOnly.ParseExact(slot, "HH:mm", CultureInfo.InvariantCulture))
            .OrderBy(time => time)
            .ToArray();
        return new SchedulerTaskDefinition(
            SchedulerTaskContract.ProductionTaskPath,
            _runnerPath,
            config.TimeZone,
            times,
            config.WakeToRun,
            Enabled: enabled);
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
        return config.Slots
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
