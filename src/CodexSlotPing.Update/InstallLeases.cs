namespace CodexSlotPing.Update;

public enum InstallLeaseAcquireStatus
{
    Acquired,
    Busy
}

public sealed class RunnerUpdateLease : IDisposable
{
    private FileStream? _stream;

    private RunnerUpdateLease(FileStream stream) => _stream = stream;

    public static InstallLeaseAcquireStatus TryAcquire(UpdateControlPaths paths, out RunnerUpdateLease? lease)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var status = InstallLeaseFile.TryOpen(paths, FileAccess.Read, FileShare.Read, out var stream);
        lease = stream is null ? null : new RunnerUpdateLease(stream);
        return status;
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}

public sealed class ExclusiveInstallLease : IDisposable
{
    private FileStream? _stream;

    private ExclusiveInstallLease(FileStream stream) => _stream = stream;

    public static InstallLeaseAcquireStatus TryAcquire(UpdateControlPaths paths, out ExclusiveInstallLease? lease)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var status = InstallLeaseFile.TryOpen(paths, FileAccess.ReadWrite, FileShare.None, out var stream);
        lease = stream is null ? null : new ExclusiveInstallLease(stream);
        return status;
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}

internal static class InstallLeaseFile
{
    public static InstallLeaseAcquireStatus TryOpen(
        UpdateControlPaths paths,
        FileAccess access,
        FileShare share,
        out FileStream? stream)
    {
        Directory.CreateDirectory(paths.DirectoryPath);
        try
        {
            stream = new FileStream(
                paths.LeaseFilePath,
                FileMode.OpenOrCreate,
                access,
                share,
                bufferSize: 1,
                FileOptions.None);
            return InstallLeaseAcquireStatus.Acquired;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            stream = null;
            return InstallLeaseAcquireStatus.Busy;
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var nativeCode = exception.HResult & 0xFFFF;
        return nativeCode is 32 or 33;
    }
}
