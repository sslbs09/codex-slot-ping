using CodexSlotPing.Update;

namespace CodexSlotPing.Updater;

internal sealed class SystemUpdateTransactionFileSystem : IUpdateTransactionFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool ProbeParentRename(string parentDirectory)
    {
        string? source = null;
        string? destination = null;
        try
        {
            var probe = Guid.NewGuid().ToString("N");
            source = Path.Combine(parentDirectory, $".CodexSlotPing-preflight-{probe}-a");
            destination = Path.Combine(parentDirectory, $".CodexSlotPing-preflight-{probe}-b");
            Directory.CreateDirectory(source);
            Directory.Move(source, destination);
            Directory.Delete(destination);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteEmpty(source);
            TryDeleteEmpty(destination);
            return false;
        }
    }

    public void CopyPackage(string sourceRoot, string destinationRoot, IReadOnlyList<string> relativeFiles)
    {
        if (Directory.Exists(destinationRoot) || File.Exists(destinationRoot))
        {
            throw new IOException("The transaction sibling already exists.");
        }

        Directory.CreateDirectory(destinationRoot);
        foreach (var relative in relativeFiles)
        {
            if (!PackageManifestValidator.IsValidPackagePath(relative))
            {
                throw new InvalidDataException("The package contains an invalid relative path.");
            }

            var source = Path.GetFullPath(Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsBelow(sourceRoot, source) || !IsBelow(destinationRoot, destination))
            {
                throw new InvalidDataException("The package file escaped its root.");
            }
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Package reparse points are not allowed.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough);
            input.CopyTo(output, 128 * 1024);
            output.Flush(flushToDisk: true);
        }
    }

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    private static bool IsBelow(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), candidate);
        return relative != "." && !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void TryDeleteEmpty(string? path)
    {
        if (path is null) return;
        try { if (Directory.Exists(path)) Directory.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
