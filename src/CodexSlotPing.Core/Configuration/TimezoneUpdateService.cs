using CodexSlotPing.Core.Scheduler;

namespace CodexSlotPing.Core.Configuration;

public sealed class TimezoneUpdateService
{
    private readonly IConfigStore _configStore;
    private readonly ITaskSchedulerGateway _schedulerGateway;
    private readonly SchedulerHealthEvaluator _healthEvaluator;
    private readonly SchedulingCoordinator? _schedulingCoordinator;

    public TimezoneUpdateService(
        IConfigStore configStore,
        ITaskSchedulerGateway schedulerGateway,
        SchedulerHealthEvaluator? healthEvaluator = null,
        SchedulingCoordinator? schedulingCoordinator = null)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _schedulerGateway = schedulerGateway ?? throw new ArgumentNullException(nameof(schedulerGateway));
        _healthEvaluator = healthEvaluator ?? new SchedulerHealthEvaluator();
        _schedulingCoordinator = schedulingCoordinator;
    }

    public async Task<TimezoneUpdateResult> ApplyAsync(
        AppConfig config,
        string currentRunnerPath,
        string currentWindowsTimeZoneId,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunnerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentWindowsTimeZoneId);

        var taskPath = SchedulerTaskContract.ProductionTaskPath;
        var currentStatus = await _schedulerGateway.ReadStatusAsync(
            taskPath, currentRunnerPath, config.TimeZone, cancellationToken).ConfigureAwait(false);
        var health = _healthEvaluator.Evaluate(
            currentStatus, currentRunnerPath, config.TimeZone, currentWindowsTimeZoneId);
        var timezoneChanged = !string.Equals(config.TimeZone, "SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(config.TimeZone, currentWindowsTimeZoneId, StringComparison.OrdinalIgnoreCase);

        if (!timezoneChanged)
        {
            return new TimezoneUpdateResult(TimezoneUpdateState.UNCHANGED, config, health,
                health.DiagnosticCategory, false);
        }

        if (currentStatus.State is SchedulerState.CONFLICT or SchedulerState.ERROR)
        {
            var diagnostic = currentStatus.DiagnosticCategory ??
                             (currentStatus.State == SchedulerState.CONFLICT ? "task-ownership-conflict" : "scheduler-unavailable");
            return new TimezoneUpdateResult(TimezoneUpdateState.ERROR, config,
                health with { DiagnosticCategory = diagnostic }, diagnostic, false);
        }

        if (!userConfirmed)
        {
            return new TimezoneUpdateResult(TimezoneUpdateState.TIMEZONE_CHANGED, config, health,
                "timezone-confirmation-required", false);
        }

        if (!currentStatus.Exists || !currentStatus.IsOwned)
        {
            return Failure(config, MarkFailed(health, "owned-task-required"), "owned-task-required", false);
        }

        var updatedConfig = config with { TimeZone = currentWindowsTimeZoneId };
        var plan = _schedulingCoordinator is null
            ? SchedulePlan.None
            : (await _schedulingCoordinator.EvaluateAsync(cancellationToken).ConfigureAwait(false)).Plan;
        var definition = new SchedulerTaskDefinition(
            taskPath,
            currentRunnerPath,
            currentWindowsTimeZoneId,
            plan,
            config.WakeToRun,
            Enabled: currentStatus.IsEnabled && !config.IsPaused);

        SchedulerOperationResult schedulerUpdate;
        try
        {
            schedulerUpdate = await _schedulerGateway.InstallOrUpdateAsync(definition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(config, MarkFailed(health, "scheduler-update-failed"), "scheduler-update-failed", false);
        }

        if (!schedulerUpdate.Succeeded)
        {
            var failedStatus = MarkFailed(schedulerUpdate.Status ?? health,
                schedulerUpdate.DiagnosticCategory ?? "scheduler-update-failed");
            return new TimezoneUpdateResult(TimezoneUpdateState.ERROR, config, failedStatus,
                schedulerUpdate.DiagnosticCategory ?? "scheduler-update-failed", false);
        }

        try
        {
            await _configStore.SaveAsync(updatedConfig, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(config, MarkFailed(schedulerUpdate.Status ?? health, "timezone-config-save-failed"),
                "timezone-config-save-failed", false);
        }

        var verifiedStatus = await _schedulerGateway.ReadStatusAsync(
            taskPath, currentRunnerPath, updatedConfig.TimeZone, cancellationToken).ConfigureAwait(false);
        var verifiedHealth = _healthEvaluator.Evaluate(
            verifiedStatus, currentRunnerPath, updatedConfig.TimeZone, currentWindowsTimeZoneId, plan);
        if (verifiedHealth.State is not SchedulerState.ON and not SchedulerState.OFF)
        {
            var failedStatus = MarkFailed(verifiedHealth, verifiedHealth.DiagnosticCategory ?? "scheduler-readback-failed");
            return new TimezoneUpdateResult(TimezoneUpdateState.ERROR, updatedConfig, failedStatus,
                failedStatus.DiagnosticCategory, true);
        }

        return new TimezoneUpdateResult(TimezoneUpdateState.UPDATED, updatedConfig, verifiedHealth, null, true);
    }

    private static TimezoneUpdateResult Failure(
        AppConfig config,
        SchedulerStatus status,
        string diagnosticCategory,
        bool configSaved) =>
        new(TimezoneUpdateState.ERROR, config, status, diagnosticCategory, configSaved);

    private static SchedulerStatus MarkFailed(SchedulerStatus status, string diagnosticCategory)
    {
        if (status.State is SchedulerState.CONFLICT or SchedulerState.ERROR)
        {
            return status with { DiagnosticCategory = diagnosticCategory };
        }

        return status with { State = SchedulerState.ERROR, DiagnosticCategory = diagnosticCategory };
    }

}
