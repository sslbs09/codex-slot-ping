namespace CodexSlotPing.Core.Persistence;

public sealed class PhysicalAtomicFileOperations : IAtomicFileOperations
{
    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    public void FlushToDisk(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
            return;
        }

        stream.Flush();
    }

    public void Replace(string temporaryPath, string destinationPath) => File.Replace(temporaryPath, destinationPath, null);

    public void Move(string temporaryPath, string destinationPath) => File.Move(temporaryPath, destinationPath);

    public void Delete(string path) => File.Delete(path);
}
