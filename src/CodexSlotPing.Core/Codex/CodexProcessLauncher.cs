using System.Diagnostics;

namespace CodexSlotPing.Core.Codex;

public sealed class CodexProcessLauncher : ICodexProcessLauncher
{
    private static readonly TimeSpan FinalDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly CodexInvocationBuilder _invocationBuilder;
    private readonly TimeSpan _timeout;

    public CodexProcessLauncher(CodexInvocationBuilder invocationBuilder, TimeSpan? timeout = null)
    {
        _invocationBuilder = invocationBuilder ?? throw new ArgumentNullException(nameof(invocationBuilder));
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

        // Task 7 classifies the JSONL stream. Keep output local so no prompt or response text enters the result object.
        _ = stdout.Length;
        _ = stderr.Length;

        return new CodexInvocationResult(
            CodexResultClassification.ATTEMPTED_UNCONFIRMED,
            true,
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            timedOut,
            null,
            Array.Empty<string>(),
            null,
            cancelledAfterStart ? "cancelled-after-process-start" : timedOut ? "process-timeout" : null);
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
