using System.Diagnostics;
using CodexSlotPing.Core.Logging;

namespace CodexSlotPing.Core.Codex;

public sealed class CodexProcessLauncher : ICodexProcessLauncher
{
    private static readonly TimeSpan FinalDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly CodexInvocationBuilder _invocationBuilder;
    private readonly CodexOutputClassifier _outputClassifier;
    private readonly RawLogStore? _rawLogStore;
    private readonly TimeSpan _timeout;

    public CodexProcessLauncher(
        CodexInvocationBuilder invocationBuilder,
        TimeSpan? timeout = null,
        CodexOutputClassifier? outputClassifier = null,
        RawLogStore? rawLogStore = null)
    {
        _invocationBuilder = invocationBuilder ?? throw new ArgumentNullException(nameof(invocationBuilder));
        _outputClassifier = outputClassifier ?? new CodexOutputClassifier();
        _rawLogStore = rawLogStore;
        _timeout = timeout ?? TimeSpan.FromSeconds(100);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<CodexInvocationResult> RunAsync(CodexInvocationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.CaptureRawLogs && _rawLogStore is null)
        {
            return PrelaunchFailed("raw-log-store-unavailable");
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = _invocationBuilder.Build(request);
        }
        catch (ArgumentException)
        {
            return PrelaunchFailed("invalid-invocation-request");
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return PrelaunchFailed("process-start-failed");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return PrelaunchFailed("process-start-failed");
        }

        var stopwatch = Stopwatch.StartNew();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var standardInputTask = WritePromptAndCloseAsync(process.StandardInput, request.Prompt, cancellationToken);

        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var timedOut = false;
        var cancelledAfterStart = false;

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await standardInputTask.ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A child may exit before consuming stdin; the process attempt is already in flight.
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelledAfterStart = cancellationToken.IsCancellationRequested;
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await ObserveInputTaskAsync(standardInputTask).ConfigureAwait(false);
        }

        var stdout = await DrainOutputAsync(standardOutputTask).ConfigureAwait(false);
        var stderr = await DrainOutputAsync(standardErrorTask).ConfigureAwait(false);
        stopwatch.Stop();

        var result = _outputClassifier.Classify(stdout, stderr, process.ExitCode, timedOut, processStarted: true);
        if (cancelledAfterStart)
        {
            result = result with
            {
                Classification = CodexResultClassification.ATTEMPTED_UNCONFIRMED,
                DiagnosticCategory = "cancelled-after-process-start"
            };
        }

        string? rawLogPath = null;
        var warningCategories = result.WarningCategories.ToList();
        if (request.CaptureRawLogs)
        {
            try
            {
                rawLogPath = await _rawLogStore!.StoreAsync(stdout, stderr, optedIn: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warningCategories.Add("raw-log-write-failed");
            }
        }

        return result with
        {
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            RawLogPath = rawLogPath,
            WarningCategories = warningCategories.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static async Task WritePromptAndCloseAsync(StreamWriter standardInput, string prompt, CancellationToken cancellationToken)
    {
        try
        {
            await standardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            await standardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            standardInput.Close();
        }
    }

    private static async Task ObserveInputTaskAsync(Task inputTask)
    {
        try
        {
            await inputTask.ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string> DrainOutputAsync(Task<string> outputTask)
    {
        try
        {
            return await outputTask.WaitAsync(FinalDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static CodexInvocationResult PrelaunchFailed(string diagnosticCategory) =>
        new(
            CodexResultClassification.PRELAUNCH_FAILED,
            false,
            null,
            0,
            false,
            null,
            Array.Empty<string>(),
            null,
            diagnosticCategory);
}
