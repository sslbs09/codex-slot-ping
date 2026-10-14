using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class PackageStagingCoordinatorTests
{
    private const string Version = "0.3.0";
    private const string ZipName = "CodexSlotPing-v0.3.0-win-x64.zip";
    private static readonly string[] Files =
    [
        PackageContract.ManifestFileName,
        "CodexSlotPing.App.exe",
        "CodexSlotPing.Runner.exe",
        "CodexSlotPing.Updater.exe"
    ];

    [Fact]
    public async Task StageAsync_ReturnsReadyToInstallOnlyAfterVerifiedExtraction()
    {
        using var temp = new TemporaryDirectory();
        var package = CreatePackage(temp.Path);
        var handler = PackageHandler(package.Zip, package.Sidecar);
        using var client = new HttpClient(handler);
        var updatesRoot = Path.Combine(temp.Path, "local-app-data", "CodexSlotPing", "updates");
        var unrelated = PrepareUnrelatedStaging(updatesRoot);
        var otherVersion = PrepareOtherVersionStaging(updatesRoot);
        var installRoot = Path.Combine(temp.Path, "installed-app");
        Directory.CreateDirectory(installRoot);
        var installMarker = Path.Combine(installRoot, "keep.txt");
        File.WriteAllText(installMarker, "unchanged");
        var progress = new RecordingProgress<PackageStagingProgress>();
        var coordinator = Coordinator(client, updatesRoot);

        var result = await coordinator.StageAsync(Evaluation(package.Zip.Length, package.Sidecar.Length), progress);

        Assert.True(result.IsReady);
        var descriptor = Assert.IsType<ReadyToInstallDescriptor>(result.Descriptor);
        Assert.Equal(Version, descriptor.Version);
        Assert.True(File.Exists(descriptor.VerifiedZipPath));
        Assert.True(File.Exists(descriptor.VerifiedSidecarPath));
        Assert.True(File.Exists(Path.Combine(descriptor.VerifiedExtractPath, "CodexSlotPing.App.exe")));
        Assert.Equal(Files.Order(StringComparer.OrdinalIgnoreCase), descriptor.Manifest.Files.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(progress.Values, value => value.BytesReceived > 0);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep.txt")));
        Assert.Equal("older", File.ReadAllText(Path.Combine(otherVersion, "keep.txt")));
        Assert.Equal("unchanged", File.ReadAllText(installMarker));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(descriptor.VerifiedZipPath)!, "package.zip.part")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(descriptor.VerifiedZipPath)!, "package.zip.sha256.part")));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(descriptor.VerifiedZipPath)!, "extract.part")));
    }

    [Fact]
    public async Task StageAsync_ChecksumMismatchFailsClosedBeforeExtractionAndCleansOperation()
    {
        using var temp = new TemporaryDirectory();
        var package = CreatePackage(temp.Path);
        var invalidZip = Encoding.UTF8.GetBytes("not a zip");
        var wrongSidecar = Encoding.ASCII.GetBytes(new string('0', 64) + "  " + ZipName + "\n");
        using var client = new HttpClient(PackageHandler(invalidZip, wrongSidecar));
        var updatesRoot = Path.Combine(temp.Path, "updates");
        var unrelated = PrepareUnrelatedStaging(updatesRoot);
        var otherVersion = PrepareOtherVersionStaging(updatesRoot);

        var result = await Coordinator(client, updatesRoot).StageAsync(Evaluation(invalidZip.Length, wrongSidecar.Length));

        Assert.False(result.IsReady);
        Assert.Equal(PackageStagingFailure.ChecksumMismatch, result.Failure);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep.txt")));
        Assert.Equal("older", File.ReadAllText(Path.Combine(otherVersion, "keep.txt")));
        Assert.Single(Directory.GetDirectories(Path.Combine(updatesRoot, Version)));
    }

    [Fact]
    public async Task StageAsync_RemovesOperationAfterDownloadFailure()
    {
        using var temp = new TemporaryDirectory();
        var updatesRoot = Path.Combine(temp.Path, "updates");
        var unrelated = PrepareUnrelatedStaging(updatesRoot);
        var otherVersion = PrepareOtherVersionStaging(updatesRoot);
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

        var result = await Coordinator(client, updatesRoot).StageAsync(Evaluation(100, 80));

        Assert.False(result.IsReady);
        Assert.Equal(PackageStagingFailure.DownloadFailed, result.Failure);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep.txt")));
        Assert.Equal("older", File.ReadAllText(Path.Combine(otherVersion, "keep.txt")));
        Assert.Single(Directory.GetDirectories(Path.Combine(updatesRoot, Version)));
    }

    [Fact]
    public async Task StageAsync_CancellationRemovesOnlyCurrentOperation()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var updatesRoot = Path.Combine(temp.Path, "updates");
        var unrelated = PrepareUnrelatedStaging(updatesRoot);
        var otherVersion = PrepareOtherVersionStaging(updatesRoot);
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingStream())
            };
            return Task.FromResult(response);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Coordinator(client, updatesRoot).StageAsync(Evaluation(100, 80), cancellationToken: cancellation.Token));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep.txt")));
        Assert.Equal("older", File.ReadAllText(Path.Combine(otherVersion, "keep.txt")));
        Assert.Single(Directory.GetDirectories(Path.Combine(updatesRoot, Version)));
    }

    [Fact]
    public async Task StageAsync_RejectsNonUpdateAvailableMetadataWithoutCreatingStaging()
    {
        using var temp = new TemporaryDirectory();
        using var client = new HttpClient(new DelegateHandler((_, _) => throw new InvalidOperationException("network must not be used")));
        var updatesRoot = Path.Combine(temp.Path, "updates");

        var result = await Coordinator(client, updatesRoot).StageAsync(Evaluation(1, 1) with { Status = UpdateEvaluationStatus.UpToDate });

        Assert.Equal(PackageStagingFailure.InvalidUpdateMetadata, result.Failure);
        Assert.False(Directory.Exists(updatesRoot));
    }

    private static PackageStagingCoordinator Coordinator(HttpClient client, string updatesRoot) =>
        new(new UpdatePackageDownloader(client), new PackageArchiveExtractor(new TestVersionInspector()), updatesRoot);

    private static UpdateEvaluation Evaluation(long zipSize, long sidecarSize) =>
        new(
            UpdateEvaluationStatus.UpdateAvailable,
            "0.2.0",
            Version,
            "0.3.0",
            "release notes",
            new Uri("https://github.com/sslbs09/codex-slot-ping/releases/tag/v0.3.0"),
            new GitHubReleaseAsset(ZipName, AssetUrl(ZipName), zipSize),
            new GitHubReleaseAsset(ZipName + ".sha256", AssetUrl(ZipName + ".sha256"), sidecarSize),
            null);

    private static Uri AssetUrl(string name) =>
        new($"https://github.com/sslbs09/codex-slot-ping/releases/download/v{Version}/{name}");

    private static DelegateHandler PackageHandler(byte[] zip, byte[] sidecar) => new((request, _) =>
    {
        var bytes = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? sidecar : zip;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    });

    private static (byte[] Zip, byte[] Sidecar) CreatePackage(string root)
    {
        var zipPath = Path.Combine(root, "fixture.zip");
        using (var output = File.Create(zipPath))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var path in Files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(path == PackageContract.ManifestFileName
                    ? JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schemaVersion = 1,
                        productId = "CodexSlotPing",
                        version = Version,
                        files = Files
                    })
                    : Encoding.UTF8.GetBytes("test executable fixture"));
            }
        }
        var zip = File.ReadAllBytes(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var sidecar = Encoding.ASCII.GetBytes($"{hash}  {ZipName}\n");
        return (zip, sidecar);
    }

    private static string PrepareUnrelatedStaging(string updatesRoot)
    {
        var unrelated = Path.Combine(updatesRoot, Version, "unrelated-operation");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        return unrelated;
    }

    private static string PrepareOtherVersionStaging(string updatesRoot)
    {
        var otherVersion = Path.Combine(updatesRoot, "0.2.5", "older-operation");
        Directory.CreateDirectory(otherVersion);
        File.WriteAllText(Path.Combine(otherVersion, "keep.txt"), "older");
        return otherVersion;
    }

    private sealed class TestVersionInspector : IPackageFileVersionInspector
    {
        public PackageFileVersion Inspect(string filePath) => new("0.3.0.0", "0.3.0");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }

    private sealed class BlockingStream : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 2;
        public override long Position { get => _read ? 1 : 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) await Task.Delay(Timeout.Infinite, cancellationToken);
            _read = true;
            buffer.Span[0] = 1;
            return 1;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
