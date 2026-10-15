namespace CodexSlotPing.Update;

public sealed record PackageDirectoryValidationResult(
    bool IsValid,
    string Root,
    PackageManifest? Manifest,
    SemVersion? Version,
    IReadOnlyList<string> RegularFiles,
    IReadOnlyList<string> Errors);

public static class PackageDirectoryValidator
{
    public static PackageDirectoryValidationResult ValidateDirectory(string root, SemVersion? expectedVersion = null)
    {
        var errors = new List<string>();
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            return Invalid(string.Empty, null, null, files, "root-not-absolute");
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!Directory.Exists(normalizedRoot))
            {
                return Invalid(normalizedRoot, null, null, files, "root-not-directory");
            }
            if ((File.GetAttributes(normalizedRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return Invalid(normalizedRoot, null, null, files, "root-reparse-point");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(string.Empty, null, null, files, "root-access-denied");
        }
        catch (IOException)
        {
            return Invalid(string.Empty, null, null, files, "root-io-failure");
        }
        catch (ArgumentException)
        {
            return Invalid(string.Empty, null, null, files, "root-path-invalid");
        }

        var pending = new Stack<string>();
        pending.Push(normalizedRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add("directory-access-denied");
                continue;
            }
            catch (IOException)
            {
                errors.Add("directory-read-failed");
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (UnauthorizedAccessException)
                {
                    errors.Add("entry-access-denied");
                    continue;
                }
                catch (IOException)
                {
                    errors.Add("entry-read-failed");
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add("reparse-point-not-allowed");
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                var relative = Path.GetRelativePath(normalizedRoot, entry)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                files.Add(relative);
            }
        }

        if (errors.Count > 0)
        {
            return new PackageDirectoryValidationResult(false, normalizedRoot, null, null, files, errors);
        }

        var manifestPath = Path.Combine(normalizedRoot, PackageContract.ManifestFileName);
        if (!files.Contains(PackageContract.ManifestFileName, StringComparer.OrdinalIgnoreCase))
        {
            return Invalid(normalizedRoot, null, null, files, "manifest-missing");
        }

        PackageManifestParseResult parsed;
        try
        {
            parsed = PackageManifestParser.Parse(File.ReadAllBytes(manifestPath));
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(normalizedRoot, null, null, files, "manifest-access-denied");
        }
        catch (IOException)
        {
            return Invalid(normalizedRoot, null, null, files, "manifest-read-failed");
        }

        if (!parsed.IsValid || parsed.Manifest is null)
        {
            return Invalid(normalizedRoot, null, null, files, parsed.Errors.ToArray());
        }
        if (!SemVersion.TryParse(parsed.Manifest.Version, out var manifestVersion))
        {
            return Invalid(normalizedRoot, parsed.Manifest, null, files, "manifest-version-invalid");
        }

        var expected = expectedVersion ?? manifestVersion!;
        var validation = PackageManifestValidator.Validate(parsed.Manifest, expected, files);
        return new PackageDirectoryValidationResult(
            validation.IsValid,
            normalizedRoot,
            parsed.Manifest,
            manifestVersion,
            files.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            validation.Errors);
    }

    private static PackageDirectoryValidationResult Invalid(
        string root,
        PackageManifest? manifest,
        SemVersion? version,
        IReadOnlyList<string> files,
        params string[] errors) =>
        new(false, root, manifest, version, files, errors);
}
