using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.App;

public enum UpdateHandoffStatus
{
    Ready,
    TestNowRunning,
    InvalidStagedPackage,
    LaunchFailed,
    UpdaterExitedBeforeReady,
    TimedOut,
    AlreadyInstalling
}

public sealed record UpdateHandoffResult(UpdateHandoffStatus Status, ProcessIdentity? UpdaterIdentity = null);

public interface IUpdaterHandoffProcess : IDisposable
{
    ProcessIdentity Identity { get; }
    bool IsSameProcessAlive();
    Task TerminateIfSameProcessAsync(CancellationToken cancellationToken);
}

public interface IUpdaterHandoffLauncher
{
    IUpdaterHandoffProcess? Start(ProcessStartInfo startInfo);
}

public sealed class SystemUpdaterHandoffLauncher : IUpdaterHandoffLauncher
{
    public IUpdaterHandoffProcess? Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null) return null;
            var identity = new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
            return new SystemUpdaterHandoffProcess(process, identity);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            process?.Dispose();
            return null;
        }
    }

    private sealed class SystemUpdaterHandoffProcess(Process process, ProcessIdentity identity) : IUpdaterHandoffProcess
    {
        public ProcessIdentity Identity { get; } = identity;

        public bool IsSameProcessAlive()
        {
            try
            {
                using var current = Process.GetProcessById(Identity.ProcessId);
                var actual = new ProcessIdentity(current.Id, new DateTimeOffset(current.StartTime.ToUniversalTime()));
                return !current.HasExited && actual == Identity;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public async Task TerminateIfSameProcessAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var current = Process.GetProcessById(Identity.ProcessId);
                var actual = new ProcessIdentity(current.Id, new DateTimeOffset(current.StartTime.ToUniversalTime()));
                if (actual != Identity || current.HasExited) return;
                current.Kill();
                await current.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                // The process exited or its identity no longer matches; never kill by PID alone.
            }
        }

        public void Dispose() => process.Dispose();
    }
}

public sealed class UpdateHandoffCoordinator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly UpdateControlPaths _paths;
    private readonly IUpdateIntentLifecycle _intentStore;
    private readonly IUpdateHandoffReadyStore _handoffStore;
    private readonly IUpdaterHandoffLauncher _launcher;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;
    private int _handoffInProgress;

    public UpdateHandoffCoordinator(
        UpdateControlPaths paths,
        IUpdateIntentLifecycle intentStore,
        IUpdateHandoffReadyStore handoffStore,
        IUpdaterHandoffLauncher launcher,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _intentStore = intentStore ?? throw new ArgumentNullException(nameof(intentStore));
        _handoffStore = handoffStore ?? throw new ArgumentNullException(nameof(handoffStore));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timeout = timeout ?? DefaultTimeout;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        if (_timeout <= TimeSpan.Zero || _pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<UpdateHandoffResult> BeginAsync(
        ReadyToInstallDescriptor descriptor,
        string installRoot,
        ProcessIdentity appIdentity,
        Func<bool> testNowIsRunning,
        Action? handoffStarted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(appIdentity);
        ArgumentNullException.ThrowIfNull(testNowIsRunning);
        if (testNowIsRunning()) return new(UpdateHandoffStatus.TestNowRunning);
        if (Interlocked.CompareExchange(ref _handoffInProgress, 1, 0) != 0)
        {
            return new(UpdateHandoffStatus.AlreadyInstalling);
        }

        UpdateHandoffResult result;
        try
        {
            result = await BeginCoreAsync(descriptor, installRoot, appIdentity, handoffStarted, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _handoffInProgress, 0);
            throw;
        }
        if (result.Status != UpdateHandoffStatus.Ready) Volatile.Write(ref _handoffInProgress, 0);
        return result;
    }

    private async Task<UpdateHandoffResult> BeginCoreAsync(
        ReadyToInstallDescriptor descriptor,
        string installRoot,
        ProcessIdentity appIdentity,
        Action? handoffStarted,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(descriptor.OperationId, "D", out var operationId) || operationId == Guid.Empty ||
            !SemVersion.TryParse(descriptor.Version, out var targetVersion) || !targetVersion!.IsStable ||
            !Path.IsPathFullyQualified(installRoot) || !Path.IsPathFullyQualified(descriptor.VerifiedExtractPath) ||
            appIdentity.ProcessId <= 0 || appIdentity.StartedUtc.Offset != TimeSpan.Zero)
        {
            return new(UpdateHandoffStatus.InvalidStagedPackage);
        }

        InstallIdentity identity;
        string updaterPath;
        try
        {
            identity = InstallIdentity.Create(InstallIdentity.ProductIdValue, installRoot);
            updaterPath = Path.Combine(Path.GetFullPath(descriptor.VerifiedExtractPath), "CodexSlotPing.Updater.exe");
        }
        catch (ArgumentException) { return new(UpdateHandoffStatus.InvalidStagedPackage); }

        var stage = PackageDirectoryValidator.ValidateDirectory(descriptor.VerifiedExtractPath, targetVersion);
        if (!stage.IsValid || !File.Exists(updaterPath) ||
            !string.Equals(identity.Key, _paths.InstallIdentity, StringComparison.Ordinal))
        {
            return new(UpdateHandoffStatus.InvalidStagedPackage);
        }

        var invocation = new UpdaterInvocation(operationId, targetVersion, identity.InstallRoot,
            descriptor.VerifiedExtractPath, appIdentity);
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = Path.GetFullPath(descriptor.VerifiedExtractPath),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        invocation.AppendArgumentsTo(startInfo);

        IUpdaterHandoffProcess? launched;
        try { launched = _launcher.Start(startInfo); }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(UpdateHandoffStatus.LaunchFailed);
        }
        if (launched is null) return new(UpdateHandoffStatus.LaunchFailed);

        using (launched)
        {
            try
            {
                handoffStarted?.Invoke();
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < _timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!launched.IsSameProcessAlive())
                    {
                        CleanupOwnedMarkers(operationId, launched.Identity);
                        return new(UpdateHandoffStatus.UpdaterExitedBeforeReady, launched.Identity);
                    }

                    var ready = _handoffStore.Read(operationId);
                    if (ready.Status == UpdateHandoffReadyReadStatus.Active &&
                        UpdateHandoffReadyValidator.Matches(ready.HandoffReady, operationId, identity.Key, launched.Identity) &&
                        launched.IsSameProcessAlive())
                    {
                        return new(UpdateHandoffStatus.Ready, launched.Identity);
                    }

                    var remaining = _timeout - stopwatch.Elapsed;
                    await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, cancellationToken).ConfigureAwait(false);
                }

                await launched.TerminateIfSameProcessAsync(cancellationToken).ConfigureAwait(false);
                if (!launched.IsSameProcessAlive()) CleanupOwnedMarkers(operationId, launched.Identity);
                return new(UpdateHandoffStatus.TimedOut, launched.Identity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await launched.TerminateIfSameProcessAsync(CancellationToken.None).ConfigureAwait(false);
                if (!launched.IsSameProcessAlive()) CleanupOwnedMarkers(operationId, launched.Identity);
                throw;
            }
            catch
            {
                await launched.TerminateIfSameProcessAsync(CancellationToken.None).ConfigureAwait(false);
                if (!launched.IsSameProcessAlive()) CleanupOwnedMarkers(operationId, launched.Identity);
                throw;
            }
        }
    }

    private void CleanupOwnedMarkers(Guid operationId, ProcessIdentity updaterIdentity)
    {
        var read = _intentStore.Read();
        if (read.Status == UpdateIntentReadStatus.Active && read.Intent?.OperationId == operationId &&
            ProcessIdentityMatcher.Matches(read.Intent, updaterIdentity))
        {
            _intentStore.TryDelete(operationId, updaterIdentity);
        }
        _handoffStore.TryDelete(operationId);
    }
}
