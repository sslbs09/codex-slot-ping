using System.Text;
using CodexSlotPing.Update;

namespace CodexSlotPing.Update.Tests;

public sealed class PackageManifestTests
{
    private const string Version = "0.3.0";
    private static readonly string[] RequiredFiles =
    [
        PackageContract.ManifestFileName,
        "CodexSlotPing.App.exe",
        "CodexSlotPing.Runner.exe",
        "CodexSlotPing.Updater.exe"
    ];

    [Fact]
    public void Validate_AcceptsExactV1OwnershipSet()
    {
        var parsed = PackageManifestParser.Parse(Json(Version, RequiredFiles));
        Assert.True(parsed.IsValid);

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), RequiredFiles);

        Assert.True(result.IsValid);
        Assert.Equal(1, parsed.Manifest!.SchemaVersion);
        Assert.Equal("CodexSlotPing", parsed.Manifest.ProductId);
    }

    [Theory]
    [InlineData("OtherProduct", "0.3.0", "wrong-product-id")]
    [InlineData("CodexSlotPing", "0.3.1", "wrong-version")]
    public void Validate_RejectsWrongProductOrVersion(string productId, string version, string expectedError)
    {
        var parsed = PackageManifestParser.Parse(Json(version, RequiredFiles, productId));

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), RequiredFiles);

        Assert.Contains(expectedError, result.Errors);
    }

    [Fact]
    public void Validate_RejectsCaseInsensitiveDuplicatePaths()
    {
        var files = RequiredFiles.Append("codexslotping.app.exe").ToArray();
        var parsed = PackageManifestParser.Parse(Json(Version, files));

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), files);

        Assert.Contains("duplicate-file-path", result.Errors);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("folder/./file.txt")]
    [InlineData("folder/../file.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("folder\\file.txt")]
    public void Validate_RejectsNonPackageRelativePaths(string path)
    {
        var files = RequiredFiles.Append(path).ToArray();
        var parsed = PackageManifestParser.Parse(Json(Version, files));

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), files);

        Assert.Contains("invalid-file-path", result.Errors);
    }

    [Fact]
    public void Validate_RejectsMissingActualFile()
    {
        var files = RequiredFiles.Append("docs/readme.txt").ToArray();
        var parsed = PackageManifestParser.Parse(Json(Version, files));

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), RequiredFiles);

        Assert.Contains("missing-owned-file", result.Errors);
    }

    [Fact]
    public void Validate_RejectsUndeclaredExtractedFile()
    {
        var parsed = PackageManifestParser.Parse(Json(Version, RequiredFiles));

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), RequiredFiles.Append("extra.dll"));

        Assert.Contains("undeclared-file", result.Errors);
    }

    [Fact]
    public void Validate_RequiresManifestToOwnItself()
    {
        var files = RequiredFiles.Where(path => path != PackageContract.ManifestFileName).ToArray();
        var parsed = PackageManifestParser.Parse(Json(Version, files));

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), files);

        Assert.Contains("manifest-not-owned", result.Errors);
    }

    [Theory]
    [InlineData("CodexSlotPing.App.exe", "missing-required-app")]
    [InlineData("CodexSlotPing.Runner.exe", "missing-required-runner")]
    [InlineData("CodexSlotPing.Updater.exe", "missing-required-updater")]
    public void Validate_RequiresAllProductExecutables(string missing, string expectedError)
    {
        var files = RequiredFiles.Where(path => path != missing).ToArray();
        var parsed = PackageManifestParser.Parse(Json(Version, files));

        var result = PackageManifestValidator.Validate(parsed.Manifest!, SemVersion.Parse(Version), files);

        Assert.Contains(expectedError, result.Errors);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"productId\":\"CodexSlotPing\",\"version\":\"0.3.0\",\"files\":[]}")]
    [InlineData("{\"schemaVersion\":\"1\",\"productId\":\"CodexSlotPing\",\"version\":\"0.3.0\",\"files\":[]}")]
    [InlineData("{\"schemaVersion\":null,\"productId\":\"CodexSlotPing\",\"version\":\"0.3.0\",\"files\":[]}")]
    [InlineData("{\"schemaVersion\":true,\"productId\":\"CodexSlotPing\",\"version\":\"0.3.0\",\"files\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"productId\":\"CodexSlotPing\",\"version\":\"0.3.0\",\"files\":[4]}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"productId\":\"CodexSlotPing\",\"version\":\"0.3.0\",\"files\":[]}")]
    [InlineData("not-json")]
    public void Parse_RejectsMalformedOrUnsupportedManifest(string json)
    {
        var result = PackageManifestParser.Parse(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    private static byte[] Json(string version, IEnumerable<string> files, string productId = "CodexSlotPing") =>
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productId,
            version,
            files
        }));
}
