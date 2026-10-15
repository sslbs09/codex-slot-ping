using System.Text.Json;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class PackageDirectoryValidatorTests
{
    [Fact]
    public void ValidateDirectory_AcceptsExactManifestOwnedPackage()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "extract");
        WritePackage(root, "0.3.1");

        var result = PackageDirectoryValidator.ValidateDirectory(root, SemVersion.Parse("0.3.1"));

        Assert.True(result.IsValid, string.Join(",", result.Errors));
        Assert.Equal("0.3.1", result.Version!.ToString());
        Assert.Equal(ExactFiles.Order(StringComparer.OrdinalIgnoreCase), result.RegularFiles.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateDirectory_RejectsUnownedFilesAndWrongVersion()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "extract");
        WritePackage(root, "0.3.1");
        File.WriteAllText(Path.Combine(root, "unowned.txt"), "not in the manifest");

        var extra = PackageDirectoryValidator.ValidateDirectory(root, SemVersion.Parse("0.3.1"));
        var wrongVersion = PackageDirectoryValidator.ValidateDirectory(root, SemVersion.Parse("0.3.2"));

        Assert.Contains("undeclared-file", extra.Errors);
        Assert.Contains("wrong-version", wrongVersion.Errors);
    }

    private static void WritePackage(string root, string version)
    {
        Directory.CreateDirectory(root);
        foreach (var file in PackageContract.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(root, file), "fixture");
        }
        File.WriteAllText(Path.Combine(root, PackageContract.ManifestFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productId = "CodexSlotPing",
            version,
            files = ExactFiles
        }));
    }

    private static string[] ExactFiles => PackageContract.RequiredFiles.Append(PackageContract.ManifestFileName).ToArray();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexSlotPing.Update.Tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
