using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

public enum UpdaterResultCode
{
    Success = 0,
    InvalidArguments = 2,
    IntentMismatch = 3,
    AppExitTimeout = 4,
    RunnerDrainTimeout = 5,
    PreflightFailed = 6,
    JournalFailure = 7,
    ReplacementFailed = 8,
    StartupFailedRolledBack = 9,
    RecoveryRequired = 10,
    UpdaterAlreadyRunning = 11
}

public sealed record UpdateTransactionResult(UpdaterResultCode Code, string Detail);

internal enum AppProcessWaitStatus
{
    Exited,
    TimedOut,
    Unknown
}

internal enum RunnerDrainWaitStatus
{
    Acquired,
    TimedOut,
    Failed
}

internal sealed record RunnerDrainWaitResult(RunnerDrainWaitStatus Status, IDisposable? Lease)
{
    public static RunnerDrainWaitResult Acquired(IDisposable lease) => new(RunnerDrainWaitStatus.Acquired, lease);
    public static RunnerDrainWaitResult TimedOut() => new(RunnerDrainWaitStatus.TimedOut, null);
    public static RunnerDrainWaitResult Failed() => new(RunnerDrainWaitStatus.Failed, null);
}

internal sealed record UpdateAppLaunchResult(bool Succeeded, ProcessIdentity? Identity)
{
    public static UpdateAppLaunchResult Started(ProcessIdentity identity) => new(true, identity);
    public static UpdateAppLaunchResult Failed() => new(false, null);
}

internal interface IUpdateTransactionRuntime
{
    Task<AppProcessWaitStatus> WaitForAppExitAsync(ProcessIdentity app, TimeSpan timeout, CancellationToken cancellationToken);
    Task<RunnerDrainWaitResult> WaitForRunnerDrainAsync(UpdateControlPaths paths, TimeSpan timeout, CancellationToken cancellationToken);
    Task<UpdateAppLaunchResult> LaunchAppAsync(string executablePath, string workingDirectory, Guid? operationId, CancellationToken cancellationToken);
    Task TerminateIfSameProcessAsync(ProcessIdentity process, CancellationToken cancellationToken);
}

internal enum ProcessIdentityReadStatus
{
    Running,
    Missing,
    Unknown
}

internal sealed record ProcessIdentityReadResult(ProcessIdentityReadStatus Status, ProcessIdentity? Identity);

internal interface IProcessIdentityReader
{
    ProcessIdentityReadResult Read(int processId);
}

internal interface IUpdateTransactionFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    bool ProbeParentRename(string parentDirectory);
    void CopyPackage(string sourceRoot, string destinationRoot, IReadOnlyList<string> relativeFiles);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path);
}

internal enum StartupAcknowledgementWaitStatus
{
    Received,
    TimedOut,
    Rejected,
    StorageFailure
}

internal sealed record StartupAcknowledgementWaitResult(
    StartupAcknowledgementWaitStatus Status,
    AppStartupAcknowledgement? Acknowledgement)
{
    public static StartupAcknowledgementWaitResult Received(AppStartupAcknowledgement acknowledgement) => new(StartupAcknowledgementWaitStatus.Received, acknowledgement);
    public static StartupAcknowledgementWaitResult TimedOut() => new(StartupAcknowledgementWaitStatus.TimedOut, null);
    public static StartupAcknowledgementWaitResult Rejected() => new(StartupAcknowledgementWaitStatus.Rejected, null);
    public static StartupAcknowledgementWaitResult StorageFailure() => new(StartupAcknowledgementWaitStatus.StorageFailure, null);
}

internal interface IStartupAcknowledgementWaiter
{
    Task<StartupAcknowledgementWaitResult> WaitAsync(
        Guid operationId,
        string installIdentity,
        SemVersion targetVersion,
        ProcessIdentity launchedAppIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
