using System.Text.Json;
using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Concurrency;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Testing;

public sealed class TestRunService
{
    private static readonly TimeSpan StateMutexTimeout = TimeSpan.FromSeconds(2);
    private readonly IClock _clock;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly IStateStore _stateStore;
    private readonly IStateMutex _stateMutex;
    private readonly ICodexEnvironmentDetector _environmentDetector;
    private readonly ICodexProcessLauncher _processLauncher;
    private readonly IStructuredLogWriter _structuredLogWriter;

    public TestRunService(
        IClock clock,
        ITimeZoneProvider timeZoneProvider,
        IStateStore stateStore,
        IStateMutex stateMutex,
        ICodexEnvironmentDetector environmentDetector,
        ICodexProcessLauncher processLauncher,
        IStructuredLogWriter structuredLogWriter)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _stateMutex = stateMutex ?? throw new ArgumentNullException(nameof(stateMutex));
        _environmentDetector = environmentDetector ?? throw new ArgumentNullException(nameof(environmentDetector));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _structuredLogWriter = structuredLogWriter ?? throw new ArgumentNullException(nameof(structuredLogWriter));
    }

    public async Task<TestRunResult> RunAsync(AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        var testId = Guid.NewGuid().ToString("N");
        CodexInvocationResult invocation;
        try
        {
            var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReadyFor(environment, config))
            {
                invocation = PrelaunchFailed("cli-environment-not-ready");
            }
            else
            {
                invocation = await InvokeOnceAsync(testId, environment, config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            invocation = PrelaunchFailed("cli-environment-not-ready");
        }

        invocation = NormalizeResult(invocation);
        var completedAt = _clock.UtcNow.ToUniversalTime();
        var record = new TestRunRecord(
            testId,
            completedAt,
            config.Model,
            config.ReasoningEffort,
            invocation.DiagnosticCategory ?? invocation.Classification.ToString(),
            Math.Max(0, invocation.ElapsedMilliseconds),
            invocation.ExitCode);

        var saved = await Task.Run(() => TrySaveLastTest(record), CancellationToken.None).ConfigureAwait(false);
        if (!saved)
        {
            throw new IOException("Test Now completed, but its result could not be saved to local state.");
        }

        try
        {
            await _structuredLogWriter.AppendAsync(new StructuredLogEntry(
                completedAt,
                GetLocalOffsetMinutes(completedAt),
                null,
                testId,
                StructuredLogKind.TEST_NOW,
                config.Model,
                config.ReasoningEffort,
                invocation.Classification,
                Math.Max(0, invocation.ElapsedMilliseconds),
                invocation.ExitCode,
                invocation.DiagnosticCategory,
                invocation.Usage), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The result remains available to the caller and in lastTest if appending a log fails.
        }

        return new TestRunResult(testId, invocation, completedAt);
    }

    private async Task<CodexInvocationResult> InvokeOnceAsync(
        string testId,
        EnvironmentStatus environment,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        string? workingDirectory = null;
        try
        {
            workingDirectory = CreateEmptyWorkingDirectory(testId);
            var request = new CodexInvocationRequest(
                environment.ExecutablePath!,
                config.Model,
                config.ReasoningEffort,
                config.Prompt,
                workingDirectory,
                testId,
                config.CaptureRawLogs);
            try
            {
                return await _processLauncher.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return AttemptedUnconfirmed("cancelled-after-process-start");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return AttemptedUnconfirmed("stream-error");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private static string CreateEmptyWorkingDirectory(string testId)
    {
        var workRoot = Path.Combine(UserDataPaths.ForCurrentUser().RootDirectory, "work");
        Directory.CreateDirectory(workRoot);
        var invocationDirectory = Path.Combine(workRoot, $"test-now-{testId}");
        Directory.CreateDirectory(invocationDirectory);
        return invocationDirectory;
    }

    private bool TrySaveLastTest(TestRunRecord record)
    {
        StateMutexLease? lease;
        try
        {
            lease = _stateMutex.TryAcquire(StateMutexTimeout);
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
                _stateStore.SaveAsync(state with { LastTest = record }, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception exception) when (IsStateFailure(exception))
            {
                return false;
            }
        }
    }

    private int GetLocalOffsetMinutes(DateTimeOffset timestampUtc)
    {
        try
        {
            var timeZone = _timeZoneProvider.Resolve(_timeZoneProvider.CurrentWindowsTimeZoneId);
            return checked((int)timeZone.GetUtcOffset(timestampUtc.UtcDateTime).TotalMinutes);
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

    private static bool IsStateFailure(Exception exception) =>
        exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException;

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
