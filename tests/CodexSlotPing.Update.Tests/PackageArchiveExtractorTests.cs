using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodexSlotPing.App.Updates;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class PackageArchiveExtractorTests
{
    private const string Version = "0.3.0";
    private static readonly string[] ProductFiles =
    [
        PackageContract.ManifestFileName,
        "CodexSlotPing.App.exe",
        "CodexSlotPing.Runner.exe",
        "CodexSlotPing.Updater.exe"
    ];

    [Fact]
    public async Task ExtractAndValidateAsync_AcceptsValidPackageAndChecksAllThreeExecutables()
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles);
        var inspector = new RecordingInspector();
        var target = Path.Combine(temp.Path, "extract.part");

        var result = await new PackageArchiveExtractor(inspector).ExtractAndValidateAsync(archive, target, SemVersion.Parse(Version));

        Assert.True(result.IsValid);
        Assert.Equal(3, inspector.InspectedFiles.Count);
        Assert.True(File.Exists(Path.Combine(target, "CodexSlotPing.App.exe")));
        Assert.Equal(ProductFiles.Order(StringComparer.OrdinalIgnoreCase), result.Manifest!.Files.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/drive-rooted.txt")]
    public async Task ExtractAndValidateAsync_RejectsRootedAndTraversalPaths(string maliciousPath)
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles.Append(maliciousPath));
        var target = Path.Combine(temp.Path, "extract.part");

        var result = await Extract(archive, target);

        Assert.False(result.IsValid);
        Assert.Equal(PackageArchiveFailure.InvalidArchive, result.Failure);
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(temp.Path, "outside.txt")));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsCaseInsensitiveDuplicateFiles()
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles.Append("Docs/Readme.txt").Append("docs/readme.TXT"));

        var result = await Extract(archive, Path.Combine(temp.Path, "extract.part"));

        Assert.Equal(PackageArchiveFailure.InvalidArchive, result.Failure);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsFileDirectoryCollision()
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles.Append("conflict").Append("CONFLICT/child.txt"));

        var result = await Extract(archive, Path.Combine(temp.Path, "extract.part"));

        Assert.Equal(PackageArchiveFailure.InvalidArchive, result.Failure);
    }

    [Theory]
    [InlineData(unchecked((int)(0xA000u << 16)), "link.txt")]
    [InlineData(0x400, "reparse.txt")]
    [InlineData(unchecked((int)(0x1000u << 16)), "device.txt")]
    public async Task ExtractAndValidateAsync_RejectsSymlinkReparseAndNonRegularEntries(int attributes, string path)
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles, specialEntry: (path, attributes));

        var result = await Extract(archive, Path.Combine(temp.Path, "extract.part"));

        Assert.Equal(PackageArchiveFailure.InvalidArchive, result.Failure);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsEntryCountLimitBeforeExtracting()
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles, extraEmptyEntryCount: PackageArchiveExtractor.MaximumEntries);
        var target = Path.Combine(temp.Path, "extract.part");

        var result = await Extract(archive, target);

        Assert.Equal(PackageArchiveFailure.InvalidArchive, result.Failure);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsExpandedSizeLimitFromMetadata()
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles);
        SetCentralDirectoryUncompressedSizes(archive, [800_000_000, 800_000_000, 800_000_000, 1]);

        var result = await Extract(archive, Path.Combine(temp.Path, "extract.part"));

        Assert.Equal(PackageArchiveFailure.InvalidArchive, result.Failure);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsSingleEntrySizeLimitFromMetadata()
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles);
        SetCentralDirectoryUncompressedSizes(archive, [PackageArchiveExtractor.MaximumSingleEntryBytes + 1]);

        var result = await Extract(archive, Path.Combine(temp.Path, "extract.part"));

        Assert.Equal(PackageArchiveFailure.InvalidArchive, result.Failure);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsMissingManifestAndExtraUndeclaredFile()
    {
        using var temp = new TemporaryDirectory();
        var noManifest = CreateArchive(temp.Path, ProductFiles.Where(path => path != PackageContract.ManifestFileName), includeManifest: false);
        var missingResult = await Extract(noManifest, Path.Combine(temp.Path, "missing.part"));
        Assert.Equal(PackageArchiveFailure.InvalidManifest, missingResult.Failure);

        var extra = CreateArchive(temp.Path, ProductFiles.Append("undeclared.dll"), manifestFiles: ProductFiles);
        var extraResult = await Extract(extra, Path.Combine(temp.Path, "extra.part"));
        Assert.Equal(PackageArchiveFailure.InvalidManifest, extraResult.Failure);
        Assert.Contains("undeclared-file", extraResult.Errors);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsOneBinaryVersionMismatch()
    {
        using var temp = new TemporaryDirectory();
        var archive = CreateArchive(temp.Path, ProductFiles);
        var inspector = new RecordingInspector(path => path.EndsWith("CodexSlotPing.Runner.exe", StringComparison.Ordinal)
            ? new PackageFileVersion("0.3.1.0", "0.3.1")
            : new PackageFileVersion("0.3.0.0", "0.3.0"));

        var result = await new PackageArchiveExtractor(inspector).ExtractAndValidateAsync(
            archive, Path.Combine(temp.Path, "extract.part"), SemVersion.Parse(Version));

        Assert.Equal(PackageArchiveFailure.BinaryVersionMismatch, result.Failure);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "extract.part")));
    }

    private static Task<PackageArchiveValidationResult> Extract(string archive, string target) =>
        new PackageArchiveExtractor(new RecordingInspector()).ExtractAndValidateAsync(archive, target, SemVersion.Parse(Version));

    private static string CreateArchive(
        string root,
        IEnumerable<string> extraFiles,
        bool includeManifest = true,
        (string Name, int Attributes)? specialEntry = null,
        int extraEmptyEntryCount = 0,
        IEnumerable<string>? manifestFiles = null)
    {
        var archivePath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var paths = extraFiles.ToList();
        if (includeManifest && !paths.Contains(PackageContract.ManifestFileName, StringComparer.Ordinal))
        {
            paths.Insert(0, PackageContract.ManifestFileName);
        }
        foreach (var path in paths)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            var content = path == PackageContract.ManifestFileName
                ? Json(manifestFiles ?? paths)
                : Encoding.UTF8.GetBytes("fixture");
            entryStream.Write(content);
        }
        if (specialEntry is { } special)
        {
            var entry = archive.CreateEntry(special.Name);
            entry.ExternalAttributes = special.Attributes;
            using var stream = entry.Open();
            stream.WriteByte(1);
        }
        for (var index = 0; index < extraEmptyEntryCount; index++)
        {
            archive.CreateEntry($"entries/{index:D5}.txt");
        }
        return archivePath;
    }

    private static byte[] Json(IEnumerable<string> files) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        productId = "CodexSlotPing",
        version = Version,
        files
    }));

    private static void SetCentralDirectoryUncompressedSizes(string archivePath, IReadOnlyList<long> sizes)
    {
        var bytes = File.ReadAllBytes(archivePath);
        var found = 0;
        for (var offset = 0; offset <= bytes.Length - 46 && found < sizes.Count; offset++)
        {
            if (BitConverter.ToUInt32(bytes, offset) != 0x02014b50)
            {
                continue;
            }
            if (sizes[found] > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(sizes));
            BitConverter.GetBytes((uint)sizes[found++]).CopyTo(bytes, offset + 24);
        }
        Assert.Equal(sizes.Count, found);
        File.WriteAllBytes(archivePath, bytes);
    }

    private sealed class RecordingInspector(Func<string, PackageFileVersion>? inspect = null) : IPackageFileVersionInspector
    {
        public List<string> InspectedFiles { get; } = [];
        public PackageFileVersion Inspect(string filePath)
        {
            InspectedFiles.Add(Path.GetFileName(filePath));
            return inspect?.Invoke(filePath) ?? new PackageFileVersion("0.3.0.0", "0.3.0");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
