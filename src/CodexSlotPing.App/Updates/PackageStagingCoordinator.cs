using System.IO;
using System.Security.Cryptography;
using CodexSlotPing.Update;

namespace CodexSlotPing.App.Updates;

public enum PackageStagingFailure
{
    InvalidUpdateMetadata,
    DownloadFailed,
    InvalidChecksumSidecar,
    ChecksumMismatch,
    InvalidArchive,
    InvalidManifest,
    BinaryVersionMismatch,
    IoFailure
}

public sealed record PackageStagingProgress(string Version, long BytesReceived, long TotalBytes, int PercentComplete);

public sealed record ReadyToInstallDescriptor(
    string Version,
    string OperationId,
    string VerifiedZipPath,
    string VerifiedSidecarPath,
    string VerifiedExtractPath,
    PackageManifest Manifest);

public sealed record PackageStagingResult(
    bool IsReady,
    ReadyToInstallDescriptor? Descriptor,
    PackageStagingFailure? Failure,
    IReadOnlyList<string> Errors)
{
    public static PackageStagingResult Ready(ReadyToInstallDescriptor descriptor) => new(true, descriptor, null, []);
    public static PackageStagingResult Failed(PackageStagingFailure failure, params string[] errors) => new(false, null, failure, errors);
}

public sealed class PackageStagingCoordinator
{
    private readonly UpdatePackageDownloader _downloader;
    private readonly PackageArchiveExtractor _archiveExtractor;
    private readonly string _updatesRoot;

    public PackageStagingCoordinator(
        UpdatePackageDownloader downloader,
        PackageArchiveExtractor archiveExtractor,
        string? updatesRoot = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _archiveExtractor = archiveExtractor ?? throw new ArgumentNullException(nameof(archiveExtractor));
        _updatesRoot = Path.GetFullPath(updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexSlotPing", "updates"));
    }

    public async Task<PackageStagingResult> StageAsync(
        UpdateEvaluation evaluation,
        IProgress<PackageStagingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!TryGetTarget(evaluation, out var targetVersion, out var zipAsset, out var sidecarAsset))
        {
            return PackageStagingResult.Failed(PackageStagingFailure.InvalidUpdateMetadata, "update-metadata-invalid");
        }

        string? operationRoot = null;
        var ready = false;
        try
        {
            var versionRoot = Path.Combine(_updatesRoot, targetVersion!.ToString());
            Directory.CreateDirectory(versionRoot);
            var operationId = Guid.NewGuid().ToString("D");
            var candidateOperationRoot = Path.Combine(versionRoot, operationId);
            if (Directory.Exists(candidateOperationRoot) || File.Exists(candidateOperationRoot))
            {
                return PackageStagingResult.Failed(PackageStagingFailure.IoFailure, "operation-id-collision");
            }
            Directory.CreateDirectory(candidateOperationRoot);
            operationRoot = candidateOperationRoot;

            var zipPartPath = Path.Combine(operationRoot, "package.zip.part");
            var sidecarPartPath = Path.Combine(operationRoot, "package.zip.sha256.part");
            var zipPath = Path.Combine(operationRoot, "package.zip");
            var sidecarPath = Path.Combine(operationRoot, "package.zip.sha256");
            var totalBytes = checked(zipAsset!.Size + sidecarAsset!.Size);
            long zipBytes = 0;
            var archiveProgress = new Progress<PackageDownloadProgress>(download =>
                ReportProgress(progress, targetVersion.ToString(), download.BytesReceived, totalBytes));

            var zipResult = await _downloader.DownloadAssetAsync(
                zipAsset, targetVersion, UpdatePackageAssetKind.Archive, zipPartPath, archiveProgress, cancellationToken).ConfigureAwait(false);
            if (!zipResult.Succeeded)
            {
                return PackageStagingResult.Failed(
                    PackageStagingFailure.DownloadFailed,
                    $"archive-download-{zipResult.Failure?.ToString().ToLowerInvariant()}");
            }
            zipBytes = zipResult.BytesReceived;
            File.Move(zipPartPath, zipPath);

            var sidecarProgress = new Progress<PackageDownloadProgress>(download =>
                ReportProgress(progress, targetVersion.ToString(), zipBytes + download.BytesReceived, totalBytes));
            var sidecarResult = await _downloader.DownloadAssetAsync(
                sidecarAsset, targetVersion, UpdatePackageAssetKind.Sha256Sidecar, sidecarPartPath, sidecarProgress, cancellationToken).ConfigureAwait(false);
            if (!sidecarResult.Succeeded)
            {
                return PackageStagingResult.Failed(
                    PackageStagingFailure.DownloadFailed,
                    $"sidecar-download-{sidecarResult.Failure?.ToString().ToLowerInvariant()}");
            }
            File.Move(sidecarPartPath, sidecarPath);

            var sidecarBytes = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
            var parsedSidecar = Sha256SidecarParser.Parse(sidecarBytes, zipAsset.Name);
            if (!parsedSidecar.IsValid)
            {
                return new PackageStagingResult(false, null, PackageStagingFailure.InvalidChecksumSidecar, parsedSidecar.Errors);
            }

            var expectedHash = Convert.FromHexString(parsedSidecar.Sha256!);
            await using var archiveStream = new FileStream(
                zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                return PackageStagingResult.Failed(PackageStagingFailure.ChecksumMismatch, "archive-checksum-mismatch");
            }

            var extractPartPath = Path.Combine(operationRoot, "extract.part");
            var extractPath = Path.Combine(operationRoot, "extract");
            var validation = await _archiveExtractor.ExtractAndValidateAsync(
                zipPath, extractPartPath, targetVersion, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                var failure = validation.Failure switch
                {
                    PackageArchiveFailure.InvalidArchive => PackageStagingFailure.InvalidArchive,
                    PackageArchiveFailure.InvalidManifest => PackageStagingFailure.InvalidManifest,
                    PackageArchiveFailure.BinaryVersionMismatch => PackageStagingFailure.BinaryVersionMismatch,
                    _ => PackageStagingFailure.IoFailure
                };
                return new PackageStagingResult(false, null, failure, validation.Errors);
            }

            Directory.Move(extractPartPath, extractPath);
            var descriptor = new ReadyToInstallDescriptor(
                targetVersion.ToString(), operationId, zipPath, sidecarPath, extractPath, validation.Manifest!);
            ready = true;
            ReportProgress(progress, targetVersion.ToString(), totalBytes, totalBytes);
            return PackageStagingResult.Ready(descriptor);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return PackageStagingResult.Failed(PackageStagingFailure.IoFailure, "staging-io-failure");
        }
        catch (UnauthorizedAccessException)
        {
            return PackageStagingResult.Failed(PackageStagingFailure.IoFailure, "staging-access-denied");
        }
        catch (CryptographicException)
        {
            return PackageStagingResult.Failed(PackageStagingFailure.InvalidChecksumSidecar, "checksum-data-invalid");
        }
        finally
        {
            if (!ready && operationRoot is not null)
            {
                TryDeleteDirectory(operationRoot);
            }
        }
    }

    private static bool TryGetTarget(
        UpdateEvaluation evaluation,
        out SemVersion? targetVersion,
        out GitHubReleaseAsset? zipAsset,
        out GitHubReleaseAsset? sidecarAsset)
    {
        targetVersion = null;
        zipAsset = evaluation.ZipAsset;
        sidecarAsset = evaluation.ChecksumAsset;
        if (evaluation.Status != UpdateEvaluationStatus.UpdateAvailable ||
            !SemVersion.TryParse(evaluation.TargetVersion, out targetVersion) || !targetVersion!.IsStable ||
            zipAsset is null || sidecarAsset is null)
        {
            return false;
        }

        var zipName = $"CodexSlotPing-v{targetVersion}-win-x64.zip";
        return string.Equals(zipAsset.Name, zipName, StringComparison.Ordinal) &&
            string.Equals(sidecarAsset.Name, zipName + ".sha256", StringComparison.Ordinal) &&
            zipAsset.Size > 0 && zipAsset.Size <= UpdatePackageDownloader.MaximumArchiveBytes &&
            sidecarAsset.Size > 0 && sidecarAsset.Size <= Sha256SidecarParser.MaximumBytes;
    }

    private static void ReportProgress(
        IProgress<PackageStagingProgress>? progress,
        string version,
        long bytesReceived,
        long totalBytes)
    {
        var percent = totalBytes <= 0 ? 0 : (int)Math.Clamp(bytesReceived * 100L / totalBytes, 0L, 100L);
        progress?.Report(new PackageStagingProgress(version, bytesReceived, totalBytes, percent));
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
