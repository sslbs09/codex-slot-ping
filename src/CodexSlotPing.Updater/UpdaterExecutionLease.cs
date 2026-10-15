using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal enum UpdaterExecutionLeaseStatus
{
    Acquired,
    Busy,
    Failed
}

internal sealed record UpdaterExecutionLeaseResult(UpdaterExecutionLeaseStatus Status, IDisposable? Lease);

internal sealed class UpdaterExecutionLease : IDisposable
{
    private readonly FileStream _stream;

    private UpdaterExecutionLease(FileStream stream) => _stream = stream;

    public static UpdaterExecutionLeaseResult TryAcquire(UpdateControlPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.DirectoryPath);
            var stream = new FileStream(
                paths.UpdaterExecutionLockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            return new UpdaterExecutionLeaseResult(UpdaterExecutionLeaseStatus.Acquired, new UpdaterExecutionLease(stream));
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return new UpdaterExecutionLeaseResult(UpdaterExecutionLeaseStatus.Busy, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new UpdaterExecutionLeaseResult(UpdaterExecutionLeaseStatus.Failed, null);
        }
    }

    public void Dispose() => _stream.Dispose();

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;
}
