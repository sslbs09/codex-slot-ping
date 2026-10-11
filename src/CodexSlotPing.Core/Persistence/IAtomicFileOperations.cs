namespace CodexSlotPing.Core.Persistence;

public interface IAtomicFileOperations
{
    bool FileExists(string path);

    void CreateDirectory(string path);

    Stream OpenRead(string path);

    Stream CreateNew(string path);

    void FlushToDisk(Stream stream);

    void Replace(string temporaryPath, string destinationPath);

    void Move(string temporaryPath, string destinationPath);

    void Delete(string path);
}
