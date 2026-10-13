using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CodexSlotPing.Core.Testing;

namespace CodexSlotPing.App;

internal sealed class RunnerProcessClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _runnerPath;

    public RunnerProcessClient(string runnerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerPath);
        _runnerPath = Path.GetFullPath(runnerPath);
    }

    public async Task<TestNowRunResponse> RunAsync(string testId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testId);
        var startInfo = new ProcessStartInfo
        {
            FileName = _runnerPath,
            WorkingDirectory = Path.GetDirectoryName(_runnerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--test-now");
        startInfo.ArgumentList.Add(testId);
        startInfo.ArgumentList.Add("--confirmed");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new IOException("The Codex Slot Ping Runner could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<TestNowRunResponse>(output, JsonOptions)
                ?? throw new InvalidDataException("The Runner returned an empty Test Now response.");
        }
        catch (JsonException exception)
        {
            throw new IOException(
                $"The Runner did not return a valid Test Now response (exit code {process.ExitCode}). {error}",
                exception);
        }
    }
}
