using System.Diagnostics;
using System.Text.Json;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.Scheduler;
using CodexSlotPing.Core.Slots;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Runner;

public sealed class RunnerOrchestrator
{
    private static readonly TimeSpan FinalizationMutexTimeout = TimeSpan.FromSeconds(2);
    private readonly RunnerOptions _options;
    private readonly string _currentRunnerPath;
    private readonly IClock _clock;
    private readonly IConfigStore _configStore;
    private readonly IStateStore _stateStore;
    private readonly IStateMutex _stateMutex;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly ITaskSchedulerGateway _taskSchedulerGateway;
    private readonly ICodexEnvironmentDetector _environmentDetector;
    private readonly ICodexProcessLauncher _processLauncher;
    private readonly IStructuredLogWriter _structuredLogWriter;
    private readonly FormalSlotClaimService _claimService;
    private readonly SchedulerHealthEvaluator _schedulerHealthEvaluator = new();

    public RunnerOrchestrator(
        RunnerOptions options,
        string currentRunnerPath,
        IClock clock,
        IConfigStore configStore,
        IStateStore stateStore,
        IStateMutex stateMutex,
        ITimeZoneProvider timeZoneProvider,
        ITaskSchedulerGateway taskSchedulerGateway,
        ICodexEnvironmentDetector environmentDetector,
        ICodexProcessLauncher processLauncher,
        IStructuredLogWriter structuredLogWriter)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunnerPath);
        _currentRunnerPath = Path.GetFullPath(currentRunnerPath);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _stateMutex = stateMutex ?? throw new ArgumentNullException(nameof(stateMutex));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _taskSchedulerGateway = taskSchedulerGateway ?? throw new ArgumentNullException(nameof(taskSchedulerGateway));
        _environmentDetector = environmentDetector ?? throw new ArgumentNullException(nameof(environmentDetector));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _structuredLogWriter = structuredLogWriter ?? throw new ArgumentNullException(nameof(structuredLogWriter));
        _claimService = new FormalSlotClaimService(
            _clock,
            _timeZoneProvider,
            _configStore,
            _stateStore,
            _stateMutex,
            new SlotAdmissionEvaluator(_timeZoneProvider));
    }

    public async Task<RunnerExitCode> RunFormalSlotAsync(CancellationToken cancellationToken)
    {
        var runTimer = Stopwatch.StartNew();
        AppConfig preflightConfig;
        try
        {
            preflightConfig = await _configStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return RunnerExitCode.NoDispatch;
        }

        if (!preflightConfig.Enabled)
        {
            return RunnerExitCode.NoDispatch;
        }

        string currentWindowsTimeZoneId;
        try
        {
            currentWindowsTimeZoneId = _timeZoneProvider.CurrentWindowsTimeZoneId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RunnerExitCode.NoDispatch;
        }

        SchedulerStatus schedulerStatus;
        try
        {
            schedulerStatus = await _taskSchedulerGateway.ReadStatusAsync(
                _options.TaskPath,
                _currentRunnerPath,
                preflightConfig.TimeZone,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return RunnerExitCode.NoDispatch;
        }

        SchedulerStatus health;
        try
        {
            health = _schedulerHealthEvaluator.Evaluate(
                schedulerStatus,
                _currentRunnerPath,
                preflightConfig.TimeZone,
                currentWindowsTimeZoneId,
                preflightConfig.Slots);
        }
        catch (ArgumentException)
        {
            return RunnerExitCode.NoDispatch;
        }

        if (health.State != SchedulerState.ON || !health.Exists || !health.IsOwned || !health.IsEnabled)
        {
            return RunnerExitCode.NoDispatch;
        }

        SlotClaimResult claim;
        try
        {
            claim = await _claimService.TryClaimCurrentSlotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return RunnerExitCode.NoDispatch;
        }

        if (!claim.IsClaimed || claim.SlotId is null || claim.ConfigSnapshot is null)
        {
            return RunnerExitCode.NoDispatch;
        }

        var config = claim.ConfigSnapshot;
        CodexInvocationResult invocationResult;
        try
        {
            var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
            if (!IsReadyFor(environment, config))
            {
                invocationResult = PrelaunchFailed("cli-environment-not-ready");
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                invocationResult = PrelaunchFailed("process-start-failed");
            }
            else
            {
                var childTimeout = RunnerExecutionBudget.GetCodexTimeout(runTimer.Elapsed);
                invocationResult = childTimeout <= TimeSpan.Zero
                    ? PrelaunchFailed("runner-time-budget-exhausted")
                    : await InvokeOnceAsync(environment, config, cancellationToken, childTimeout).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            invocationResult = PrelaunchFailed("process-start-failed");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            invocationResult = PrelaunchFailed("cli-environment-not-ready");
        }

        invocationResult = NormalizeResult(invocationResult);
        var finalized = await FinalizeClaimAsync(claim.SlotId, config, invocationResult).ConfigureAwait(false);
        if (!finalized)
        {
            return invocationResult.ProcessStarted
                ? RunnerExitCode.AttemptedUnconfirmed
                : RunnerExitCode.PrelaunchFailed;
        }

        return ToExitCode(invocationResult.Classification);
    }

    private async Task<CodexInvocationResult> InvokeOnceAsync(
        EnvironmentStatus environment,
        AppConfig config,
        CancellationToken cancellationToken,
        TimeSpan childTimeout)
    {
        string? workingDirectory = null;
        try
        {
            workingDirectory = CreateEmptyWorkingDirectory();
            var request = new CodexInvocationRequest(
                environment.ExecutablePath!,
                config.Model,
                config.ReasoningEffort,
                config.Prompt,
                workingDirectory,
                Guid.NewGuid().ToString("N"),
                config.CaptureRawLogs,
                childTimeout);
            try
            {
                return await _processLauncher.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return cancellationToken.IsCancellationRequested
                    ? PrelaunchFailed("process-start-failed")
                    : AttemptedUnconfirmed("cancelled-after-process-start");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return AttemptedUnconfirmed("stream-error");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return PrelaunchFailed("process-start-failed");
        }
        finally
        {
            if (workingDirectory is not null)
            {
                TryDeleteEmptyWorkingDirectory(workingDirectory);
            }
        }
    }

    private static string CreateEmptyWorkingDirectory()
    {
        var workRoot = Path.Combine(UserDataPaths.ForCurrentUser().RootDirectory, "work");
        Directory.CreateDirectory(workRoot);
        var invocationDirectory = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(invocationDirectory);
        return invocationDirectory;
    }

    private async Task<bool> FinalizeClaimAsync(SlotId slotId, AppConfig config, CodexInvocationResult result)
    {
        var stateFinalized = await Task.Run(() => TryFinalizeClaim(slotId, result), CancellationToken.None).ConfigureAwait(false);
        if (!stateFinalized)
        {
            return false;
        }

        try
        {
            var timestamp = _clock.UtcNow.ToUniversalTime();
            var offsetMinutes = GetLocalOffsetMinutes(timestamp);
            await _structuredLogWriter.AppendAsync(new StructuredLogEntry(
                timestamp,
                offsetMinutes,
                slotId.ToString(),
                null,
                StructuredLogKind.FORMAL_SLOT,
                config.Model,
                config.ReasoningEffort,
                result.Classification,
                Math.Max(0, result.ElapsedMilliseconds),
                result.ExitCode,
                result.DiagnosticCategory,
                result.Usage), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // State is already terminal. A missing log must never make the slot eligible again.
        }

        return true;
    }

    private bool TryFinalizeClaim(SlotId slotId, CodexInvocationResult result)
    {
        StateMutexLease? lease;
        try
        {
            lease = _stateMutex.TryAcquire(FinalizationMutexTimeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }

        if (lease is null)
        {
            return false;
        }

        using (lease)
        {
            try
            {
                var state = _stateStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
                var index = -1;
                for (var i = 0; i < state.FormalSlots.Count; i++)
                {
                    if (state.FormalSlots[i].SlotId == slotId)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0 || state.FormalSlots[index].Status != FormalSlotStatus.CLAIMED)
                {
                    return false;
                }

                var nextStatus = result.Classification switch
                {
                    CodexResultClassification.CONFIRMED_SUCCESS => FormalSlotStatus.CONFIRMED_SUCCESS,
                    CodexResultClassification.ATTEMPTED_UNCONFIRMED => FormalSlotStatus.ATTEMPTED_UNCONFIRMED,
                    CodexResultClassification.PRELAUNCH_FAILED => FormalSlotStatus.PRELAUNCH_FAILED,
                    _ => throw new InvalidOperationException("Unknown Codex result classification.")
                };
                var current = state.FormalSlots[index];
                if (!StateTransitions.CanTransition(current.Status, nextStatus))
                {
                    return false;
                }

                var records = state.FormalSlots.ToArray();
                records[index] = current with
                {
                    Status = nextStatus,
                    UpdatedAtUtc = _clock.UtcNow.ToUniversalTime(),
                    DiagnosticCategory = result.DiagnosticCategory
                };
                _stateStore.SaveAsync(state with { FormalSlots = records }, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception exception) when (IsStorageFailure(exception) || exception is InvalidOperationException)
            {
                return false;
            }
        }
    }

    private int GetLocalOffsetMinutes(DateTimeOffset timestampUtc)
    {
        try
        {
            var zone = _timeZoneProvider.Resolve(_timeZoneProvider.CurrentWindowsTimeZoneId);
            return checked((int)zone.GetUtcOffset(timestampUtc.UtcDateTime).TotalMinutes);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return checked((int)TimeZoneInfo.Local.GetUtcOffset(timestampUtc.UtcDateTime).TotalMinutes);
        }
    }

    private static bool IsReadyFor(EnvironmentStatus environment, AppConfig config) =>
        environment.IsReady &&
        !string.IsNullOrWhiteSpace(environment.ExecutablePath) &&
        Path.IsPathRooted(environment.ExecutablePath) &&
        environment.SupportedReasoningEfforts.Contains(config.ReasoningEffort);

    private static CodexInvocationResult NormalizeResult(CodexInvocationResult result)
    {
        if (result.ProcessStarted && result.Classification == CodexResultClassification.PRELAUNCH_FAILED)
        {
            return result with
            {
                Classification = CodexResultClassification.ATTEMPTED_UNCONFIRMED,
                DiagnosticCategory = "stream-error"
            };
        }

        if (!result.ProcessStarted && result.Classification != CodexResultClassification.PRELAUNCH_FAILED)
        {
            return result with
            {
                Classification = CodexResultClassification.PRELAUNCH_FAILED,
                DiagnosticCategory = "process-start-failed"
            };
        }

        return result;
    }

    private static CodexInvocationResult PrelaunchFailed(string diagnosticCategory) => new(
        CodexResultClassification.PRELAUNCH_FAILED,
        false,
        null,
        0,
        false,
        null,
        Array.Empty<string>(),
        null,
        diagnosticCategory);

    private static CodexInvocationResult AttemptedUnconfirmed(string diagnosticCategory) => new(
        CodexResultClassification.ATTEMPTED_UNCONFIRMED,
        true,
        null,
        0,
        false,
        null,
        Array.Empty<string>(),
        null,
        diagnosticCategory);

    private static RunnerExitCode ToExitCode(CodexResultClassification classification) => classification switch
    {
        CodexResultClassification.CONFIRMED_SUCCESS => RunnerExitCode.ConfirmedSuccess,
        CodexResultClassification.PRELAUNCH_FAILED => RunnerExitCode.PrelaunchFailed,
        _ => RunnerExitCode.AttemptedUnconfirmed
    };

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ObjectDisposedException;

    private static void TryDeleteEmptyWorkingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
