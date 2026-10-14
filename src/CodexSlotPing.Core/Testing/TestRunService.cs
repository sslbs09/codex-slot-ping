using CodexSlotPing.Core.Codex;
using CodexSlotPing.Core.Configuration;
using CodexSlotPing.Core.Eligibility;
using CodexSlotPing.Core.Logging;
using CodexSlotPing.Core.Persistence;
using CodexSlotPing.Core.State;
using CodexSlotPing.Core.Time;

namespace CodexSlotPing.Core.Testing;

public sealed class TestRunService
{
    private readonly IClock _clock;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly FormalSlotClaimService _claimService;
    private readonly ICodexEnvironmentDetector _environmentDetector;
    private readonly ICodexProcessLauncher _processLauncher;
    private readonly IStructuredLogWriter _structuredLogWriter;

    public TestRunService(
        IClock clock,
        ITimeZoneProvider timeZoneProvider,
        FormalSlotClaimService claimService,
        ICodexEnvironmentDetector environmentDetector,
        ICodexProcessLauncher processLauncher,
        IStructuredLogWriter structuredLogWriter)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZoneProvider = timeZoneProvider ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _claimService = claimService ?? throw new ArgumentNullException(nameof(claimService));
        _environmentDetector = environmentDetector ?? throw new ArgumentNullException(nameof(environmentDetector));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _structuredLogWriter = structuredLogWriter ?? throw new ArgumentNullException(nameof(structuredLogWriter));
    }

    public async Task<TestNowRunResponse> RunAsync(
        string testId,
        bool userConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testId);
        cancellationToken.ThrowIfCancellationRequested();

        var claim = await _claimService.TryClaimTestNowAsync(testId, userConfirmed, cancellationToken)
            .ConfigureAwait(false);
        if (!claim.IsClaimed)
        {
            if (claim.EligibilityDecision is null)
            {
                throw new IOException("Test Now could not read or claim the current eligibility state.");
            }

            return new TestNowRunResponse(claim.EligibilityDecision, null);
        }

        var config = claim.ConfigSnapshot
            ?? throw new InvalidDataException("The Test Now claim did not contain a configuration snapshot.");
        CodexInvocationResult invocation;
        DateTimeOffset? attemptStartedAtUtc = null;
        try
        {
            var environment = await _environmentDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
            if (!IsReadyFor(environment, config))
            {
                invocation = PrelaunchFailed("cli-environment-not-ready");
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                invocation = PrelaunchFailed("process-start-failed");
            }
            else
            {
                attemptStartedAtUtc = _clock.UtcNow.ToUniversalTime();
                invocation = await InvokeOnceAsync(testId, environment, config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            invocation = attemptStartedAtUtc is null
                ? PrelaunchFailed("process-start-failed")
                : AttemptedUnconfirmed("cancelled-after-process-start");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            invocation = attemptStartedAtUtc is null
                ? PrelaunchFailed("cli-environment-not-ready")
                : AttemptedUnconfirmed("stream-error");
        }

        invocation = NormalizeResult(invocation);
        if (!invocation.ProcessStarted)
        {
            attemptStartedAtUtc = null;
        }

        var completedAtUtc = _clock.UtcNow.ToUniversalTime();
        var record = new TestRunRecord(
            testId,
            completedAtUtc,
            config.Model,
            config.ReasoningEffort,
            invocation.DiagnosticCategory ?? invocation.Classification.ToString(),
            Math.Max(0, invocation.ElapsedMilliseconds),
            invocation.ExitCode);
        var finalized = await _claimService.FinalizeTestNowAsync(
            testId,
            record,
            invocation,
            attemptStartedAtUtc,
            CancellationToken.None).ConfigureAwait(false);
        if (!finalized)
        {
            throw new IOException("Test Now completed, but its claimed result could not be saved to local state.");
        }

        try
        {
            await _structuredLogWriter.AppendAsync(new StructuredLogEntry(
                completedAtUtc,
                GetLocalOffsetMinutes(completedAtUtc),
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
            // The durable result remains available even if log append fails.
        }

        return new TestNowRunResponse(
            claim.EligibilityDecision ?? throw new InvalidDataException("The Test Now claim did not contain a decision."),
            new TestRunResult(testId, invocation, completedAtUtc));
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
                return AttemptedUnconfirmed("cancelled-after-process-start");
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

    private static string CreateEmptyWorkingDirectory(string testId)
    {
        var workRoot = Path.Combine(UserDataPaths.ForCurrentUser().RootDirectory, "work");
        Directory.CreateDirectory(workRoot);
        var invocationDirectory = Path.Combine(workRoot, $"test-now-{testId}");
        Directory.CreateDirectory(invocationDirectory);
        return invocationDirectory;
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
