using System.Text.Json;

namespace CodexSlotPing.Update;

public sealed record PackageManifest(
    int SchemaVersion,
    string ProductId,
    string Version,
    IReadOnlyList<string> Files);

public sealed record PackageManifestParseResult(
    bool IsValid,
    PackageManifest? Manifest,
    IReadOnlyList<string> Errors);

public sealed record PackageValidationResult(
    bool IsValid,
    PackageManifest? Manifest,
    IReadOnlyList<string> Errors);

public static class PackageManifestParser
{
    private static readonly HashSet<string> AllowedProperties =
        new(["schemaVersion", "productId", "version", "files"], StringComparer.Ordinal);

    public static PackageManifestParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("manifest-not-object");
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedProperties.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
                {
                    return Invalid("manifest-property-invalid");
                }
            }

            if (values.Count != AllowedProperties.Count ||
                !values.TryGetValue("schemaVersion", out var schemaVersionValue))
            {
                return Invalid("manifest-shape-invalid");
            }

            if (schemaVersionValue.ValueKind != JsonValueKind.Number ||
                !schemaVersionValue.TryGetInt32(out var schemaVersion) ||
                !values.TryGetValue("productId", out var productIdValue) || productIdValue.ValueKind != JsonValueKind.String ||
                !values.TryGetValue("version", out var versionValue) || versionValue.ValueKind != JsonValueKind.String ||
                !values.TryGetValue("files", out var filesValue) || filesValue.ValueKind != JsonValueKind.Array)
            {
                return Invalid("manifest-shape-invalid");
            }
            if (schemaVersion != 1)
            {
                return Invalid("unsupported-schema-version");
            }

            var files = new List<string>();
            foreach (var file in filesValue.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(file.GetString()))
                {
                    return Invalid("manifest-file-entry-invalid");
                }

                files.Add(file.GetString()!);
            }

            return new PackageManifestParseResult(
                true,
                new PackageManifest(schemaVersion, productIdValue.GetString()!, versionValue.GetString()!, files),
                []);
        }
        catch (JsonException)
        {
            return Invalid("manifest-json-invalid");
        }
    }

    private static PackageManifestParseResult Invalid(string error) =>
        new(false, null, [error]);
}

public static class PackageContract
{
    public const string ManifestFileName = "codex-slot-ping.manifest.json";

    private static readonly string[] RequiredProductFiles =
    [
        "CodexSlotPing.App.exe",
        "CodexSlotPing.Runner.exe",
        "CodexSlotPing.Updater.exe"
    ];

    public static IReadOnlyList<string> RequiredFiles => Array.AsReadOnly(RequiredProductFiles);

    public static PackageValidationResult Validate(
        PackageManifest manifest,
        SemVersion targetVersion,
        IEnumerable<string> extractedRegularFiles) =>
        PackageManifestValidator.Validate(manifest, targetVersion, extractedRegularFiles);
}

public static class PackageManifestValidator
{
    public static PackageValidationResult Validate(
        PackageManifest manifest,
        SemVersion targetVersion,
        IEnumerable<string> extractedRegularFiles)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(targetVersion);
        ArgumentNullException.ThrowIfNull(extractedRegularFiles);

        var errors = new List<string>();
        if (manifest.SchemaVersion != 1)
        {
            errors.Add("unsupported-schema-version");
        }
        if (!string.Equals(manifest.ProductId, "CodexSlotPing", StringComparison.Ordinal))
        {
            errors.Add("wrong-product-id");
        }
        if (!SemVersion.TryParse(manifest.Version, out var manifestVersion) ||
            !string.Equals(manifestVersion!.ToString(), targetVersion.ToString(), StringComparison.Ordinal))
        {
            errors.Add("wrong-version");
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifest.Files)
        {
            if (!IsValidPackagePath(path))
            {
                errors.Add("invalid-file-path");
                continue;
            }
            if (!declared.Add(path))
            {
                errors.Add("duplicate-file-path");
            }
        }

        if (!declared.Contains(PackageContract.ManifestFileName))
        {
            errors.Add("manifest-not-owned");
        }
        foreach (var requiredFile in PackageContract.RequiredFiles)
        {
            if (!declared.Contains(requiredFile))
            {
                errors.Add(requiredFile switch
                {
                    "CodexSlotPing.App.exe" => "missing-required-app",
                    "CodexSlotPing.Runner.exe" => "missing-required-runner",
                    _ => "missing-required-updater"
                });
            }
        }

        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in extractedRegularFiles)
        {
            if (!IsValidPackagePath(path))
            {
                errors.Add("invalid-extracted-file-path");
            }
            else if (!actual.Add(path))
            {
                errors.Add("duplicate-extracted-file");
            }
        }

        if (declared.Except(actual, StringComparer.OrdinalIgnoreCase).Any())
        {
            errors.Add("missing-owned-file");
        }
        if (actual.Except(declared, StringComparer.OrdinalIgnoreCase).Any())
        {
            errors.Add("undeclared-file");
        }

        return new PackageValidationResult(errors.Count == 0, manifest, errors);
    }

    public static bool IsValidPackagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(path) || path.Contains(':') || path.Any(char.IsControl))
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                                    segment.EndsWith(' ') || segment.EndsWith('.') ||
                                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                                    IsReservedWindowsName(segment)))
        {
            return false;
        }

        return true;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9';
    }
}
