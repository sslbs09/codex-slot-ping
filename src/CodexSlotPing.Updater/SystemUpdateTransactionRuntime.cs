using System.ComponentModel;
using System.Diagnostics;
using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal sealed class SystemUpdateTransactionRuntime(IProcessIdentityReader processIdentityReader) : IUpdateTransactionRuntime
{
    public SystemUpdateTransactionRuntime() : this(new SystemProcessIdentityReader()) { }

    public async Task<AppProcessWaitStatus> WaitForAppExitAsync(
        ProcessIdentity expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var read = processIdentityReader.Read(expected.ProcessId);
            if (read.Status == ProcessIdentityReadStatus.Missing ||
                (read.Status == ProcessIdentityReadStatus.Running && !SameIdentity(expected, read.Identity)))
            {
                return AppProcessWaitStatus.Exited;
            }
            if (read.Status == ProcessIdentityReadStatus.Unknown)
            {
                return AppProcessWaitStatus.Unknown;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        return AppProcessWaitStatus.TimedOut;
    }

    public async Task<RunnerDrainWaitResult> WaitForRunnerDrainAsync(
        UpdateControlPaths paths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (ExclusiveInstallLease.TryAcquire(paths, out var lease) == InstallLeaseAcquireStatus.Acquired && lease is not null)
                {
                    return RunnerDrainWaitResult.Acquired(lease);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return RunnerDrainWaitResult.Failed();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        return RunnerDrainWaitResult.TimedOut();
    }

    public Task<UpdateAppLaunchResult> LaunchAppAsync(
        string executablePath,
        string workingDirectory,
        Guid? operationId,
        bool rollback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (operationId.HasValue)
        {
            startInfo.ArgumentList.Add(rollback ? "--update-rollback" : "--update-operation");
            startInfo.ArgumentList.Add(operationId.Value.ToString("D"));
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return Task.FromResult(UpdateAppLaunchResult.Failed());
            }

            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime());
            return Task.FromResult(UpdateAppLaunchResult.Started(identity));
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception killException) when (killException is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
            return Task.FromResult(UpdateAppLaunchResult.Failed());
        }
        finally
        {
            process?.Dispose();
        }
    }

    public async Task TerminateIfSameProcessAsync(ProcessIdentity expected, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            var actual = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime());
            if (SameIdentity(expected, actual) && !process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            // The process exited, was reused, or cannot safely be identified; never kill by PID alone.
        }
    }

    private static bool SameIdentity(ProcessIdentity expected, ProcessIdentity? actual) =>
        actual is not null && actual.ProcessId == expected.ProcessId && actual.StartedUtc == expected.StartedUtc;
}

internal sealed class SystemProcessIdentityReader : IProcessIdentityReader
{
    public ProcessIdentityReadResult Read(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new ProcessIdentityReadResult(
                ProcessIdentityReadStatus.Running,
                new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime()));
        }
        catch (ArgumentException)
        {
            return new ProcessIdentityReadResult(ProcessIdentityReadStatus.Missing, null);
        }
        catch (InvalidOperationException)
        {
            return new ProcessIdentityReadResult(ProcessIdentityReadStatus.Missing, null);
        }
        catch (Win32Exception)
        {
            return new ProcessIdentityReadResult(ProcessIdentityReadStatus.Unknown, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new ProcessIdentityReadResult(ProcessIdentityReadStatus.Unknown, null);
        }
    }
}
