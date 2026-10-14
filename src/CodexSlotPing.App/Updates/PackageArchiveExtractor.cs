using System.IO.Compression;
using System.IO;
using CodexSlotPing.Update;

namespace CodexSlotPing.App.Updates;

public enum PackageArchiveFailure
{
    InvalidArchive,
    InvalidManifest,
    BinaryVersionMismatch,
    IoFailure
}

public sealed record PackageFileVersion(string? FileVersion, string? ProductVersion);

public interface IPackageFileVersionInspector
{
    PackageFileVersion Inspect(string filePath);
}

public sealed class WindowsPackageFileVersionInspector : IPackageFileVersionInspector
{
    public PackageFileVersion Inspect(string filePath)
    {
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath);
        return new PackageFileVersion(info.FileVersion, info.ProductVersion);
    }
}

public sealed record PackageVersionValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public static class PackageVersionValidator
{
    public static PackageVersionValidationResult Validate(
        string extractionRoot,
        SemVersion targetVersion,
        IPackageFileVersionInspector inspector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionRoot);
        ArgumentNullException.ThrowIfNull(targetVersion);
        ArgumentNullException.ThrowIfNull(inspector);

        var errors = new List<string>();
        foreach (var fileName in PackageContract.RequiredFiles)
        {
            var path = Path.Combine(extractionRoot, fileName);
            try
            {
                var fileVersion = inspector.Inspect(path);
                if (!IsCompatible(fileVersion, targetVersion))
                {
                    errors.Add($"binary-version-mismatch:{fileName}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"binary-version-unreadable:{fileName}");
            }
        }

        return new PackageVersionValidationResult(errors.Count == 0, errors);
    }

    private static bool IsCompatible(PackageFileVersion fileVersion, SemVersion targetVersion)
    {
        if (!Version.TryParse(fileVersion.FileVersion, out var windowsVersion) ||
            !int.TryParse(targetVersion.Major.ToString(), out var major) ||
            !int.TryParse(targetVersion.Minor.ToString(), out var minor) ||
            !int.TryParse(targetVersion.Patch.ToString(), out var patch) ||
            windowsVersion.Major != major || windowsVersion.Minor != minor || windowsVersion.Build != patch ||
            windowsVersion.Revision is not (-1 or 0) ||
            !SemVersion.TryParse(fileVersion.ProductVersion, out var productVersion))
        {
            return false;
        }

        return productVersion!.CompareTo(targetVersion) == 0;
    }
}

public sealed record PackageArchiveValidationResult(
    bool IsValid,
    PackageManifest? Manifest,
    PackageArchiveFailure? Failure,
    IReadOnlyList<string> Errors);

public sealed class PackageArchiveExtractor
{
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumEntries = 10_000;
    public const long MaximumSingleEntryBytes = 1024L * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSymbolicLink = 0xA000;
    private readonly IPackageFileVersionInspector _versionInspector;

    public PackageArchiveExtractor(IPackageFileVersionInspector versionInspector) =>
        _versionInspector = versionInspector ?? throw new ArgumentNullException(nameof(versionInspector));

    public async Task<PackageArchiveValidationResult> ExtractAndValidateAsync(
        string archivePath,
        string extractionPath,
        SemVersion targetVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionPath);
        ArgumentNullException.ThrowIfNull(targetVersion);

        var extractionRoot = Path.GetFullPath(extractionPath);
        var createdRoot = false;
        var completed = false;
        try
        {
            if (Directory.Exists(extractionRoot) || File.Exists(extractionRoot))
            {
                return Invalid(PackageArchiveFailure.IoFailure, "extraction-path-already-exists");
            }
            var archiveInfo = new FileInfo(archivePath);
            if (!archiveInfo.Exists || archiveInfo.Length <= 0 || archiveInfo.Length > MaximumArchiveBytes)
            {
                return Invalid(PackageArchiveFailure.InvalidArchive, "archive-size-invalid");
            }

            await using var archiveFile = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: true);
            var metadata = InspectMetadata(archive);
            if (metadata.Failure is not null)
            {
                return Invalid(PackageArchiveFailure.InvalidArchive, metadata.Failure);
            }
            if (!metadata.Entries.Any(item => !item.IsDirectory &&
                string.Equals(item.RelativePath, PackageContract.ManifestFileName, StringComparison.Ordinal)))
            {
                return Invalid(PackageArchiveFailure.InvalidManifest, "manifest-missing");
            }

            Directory.CreateDirectory(extractionRoot);
            createdRoot = true;
            long actualExpandedBytes = 0;
            foreach (var item in metadata.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = GetSafeDestinationPath(extractionRoot, item.RelativePath);
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                var parent = Path.GetDirectoryName(destinationPath)!;
                Directory.CreateDirectory(parent);
                await using var source = item.Entry.Open();
                await using var destination = new FileStream(
                    destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var bytesForEntry = await CopyEntryAsync(
                    source, destination, item.Entry.Length, actualExpandedBytes, cancellationToken).ConfigureAwait(false);
                actualExpandedBytes += bytesForEntry;
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            var manifestPath = Path.Combine(extractionRoot, PackageContract.ManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length > MaximumManifestBytes)
            {
                return Invalid(PackageArchiveFailure.InvalidManifest, "manifest-size-invalid");
            }
            var parsed = PackageManifestParser.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (!parsed.IsValid)
            {
                return new PackageArchiveValidationResult(false, null, PackageArchiveFailure.InvalidManifest, parsed.Errors);
            }

            var actualFiles = Directory.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(extractionRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();
            var contract = PackageContract.Validate(parsed.Manifest!, targetVersion, actualFiles);
            if (!contract.IsValid)
            {
                return new PackageArchiveValidationResult(false, parsed.Manifest, PackageArchiveFailure.InvalidManifest, contract.Errors);
            }

            var versions = PackageVersionValidator.Validate(extractionRoot, targetVersion, _versionInspector);
            if (!versions.IsValid)
            {
                return new PackageArchiveValidationResult(false, parsed.Manifest, PackageArchiveFailure.BinaryVersionMismatch, versions.Errors);
            }

            completed = true;
            return new PackageArchiveValidationResult(true, parsed.Manifest, null, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Invalid(PackageArchiveFailure.InvalidArchive, "archive-data-invalid");
        }
        catch (IOException)
        {
            return Invalid(PackageArchiveFailure.IoFailure, "staging-io-failure");
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(PackageArchiveFailure.IoFailure, "staging-io-failure");
        }
        finally
        {
            if (createdRoot && !completed)
            {
                TryDeleteDirectory(extractionRoot);
            }
        }
    }

    private static (IReadOnlyList<PlannedEntry> Entries, string? Failure) InspectMetadata(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            return ([], "entry-count-limit-exceeded");
        }

        var nodes = new Dictionary<string, PathNode>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PlannedEntry>(archive.Entries.Count);
        long expandedSize = 0;
        foreach (var entry in archive.Entries)
        {
            var attributes = entry.ExternalAttributes;
            var dosAttributes = (FileAttributes)(attributes & 0xFFFF);
            var unixType = (attributes >> 16) & UnixFileTypeMask;
            if ((dosAttributes & FileAttributes.ReparsePoint) != 0 || unixType == UnixSymbolicLink ||
                (unixType != 0 && unixType != UnixRegularFile && unixType != UnixDirectory))
            {
                return ([], "non-regular-entry-type");
            }

            var endsWithSeparator = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var relativePath = endsWithSeparator ? entry.FullName[..^1] : entry.FullName;
            if (!PackageManifestValidator.IsValidPackagePath(relativePath))
            {
                return ([], "unsafe-entry-path");
            }

            var isDirectory = endsWithSeparator || (dosAttributes & FileAttributes.Directory) != 0 || unixType == UnixDirectory;
            if ((unixType == UnixRegularFile && isDirectory) || (isDirectory && entry.Length != 0))
            {
                return ([], "entry-type-mismatch");
            }
            if (entry.Length < 0 || entry.Length > MaximumSingleEntryBytes)
            {
                return ([], "single-entry-size-limit-exceeded");
            }
            if (!isDirectory)
            {
                if (expandedSize > MaximumExpandedBytes - entry.Length)
                {
                    return ([], "expanded-size-limit-exceeded");
                }
                expandedSize += entry.Length;
            }

            var pathFailure = RegisterPath(nodes, relativePath, isDirectory);
            if (pathFailure is not null)
            {
                return ([], pathFailure);
            }
            entries.Add(new PlannedEntry(entry, relativePath, isDirectory));
        }

        return (entries, null);
    }

    private static string? RegisterPath(Dictionary<string, PathNode> nodes, string path, bool isDirectory)
    {
        var segments = path.Split('/');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var ancestor = string.Join('/', segments.Take(index + 1));
            if (nodes.TryGetValue(ancestor, out var ancestorNode))
            {
                if (ancestorNode.IsFile) return "file-directory-collision";
            }
            else
            {
                nodes.Add(ancestor, new PathNode(isFile: false));
            }
        }

        if (nodes.TryGetValue(path, out var existing))
        {
            if (existing.IsFile || !isDirectory || existing.HasExplicitDirectory)
            {
                return existing.IsFile || isDirectory ? "file-directory-collision" : "duplicate-entry-path";
            }
            existing.HasExplicitDirectory = true;
        }
        else
        {
            nodes.Add(path, new PathNode(isFile: !isDirectory, hasExplicitDirectory: isDirectory));
        }

        return null;
    }

    private static string GetSafeDestinationPath(string root, string relativePath)
    {
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An archive path escaped the extraction root.");
        }
        return destination;
    }

    private static async Task<long> CopyEntryAsync(
        Stream source,
        Stream destination,
        long expectedEntryBytes,
        long totalBytesBeforeEntry,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (copied > MaximumSingleEntryBytes - read || totalBytesBeforeEntry + copied > MaximumExpandedBytes - read ||
                copied + read > expectedEntryBytes)
            {
                throw new InvalidDataException("The expanded archive exceeded its declared limits.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }
        if (copied != expectedEntryBytes)
        {
            throw new InvalidDataException("The archive entry size did not match its metadata.");
        }
        return copied;
    }

    private static PackageArchiveValidationResult Invalid(PackageArchiveFailure failure, string error) =>
        new(false, null, failure, [error]);

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed record PlannedEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);

    private sealed class PathNode(bool isFile, bool hasExplicitDirectory = false)
    {
        public bool IsFile { get; } = isFile;
        public bool HasExplicitDirectory { get; set; } = hasExplicitDirectory;
    }
}
