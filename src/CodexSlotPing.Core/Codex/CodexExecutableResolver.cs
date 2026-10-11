namespace CodexSlotPing.Core.Codex;

public enum CodexExecutableSource
{
    PathNativeExecutable,
    NpmPackageNativeBinary
}

public sealed record CodexExecutableResolution(
    bool IsDirectlyLaunchable,
    string? AbsolutePath,
    CodexExecutableSource? Source,
    string? DiagnosticCode);

public sealed class CodexExecutableResolver
{
    private readonly Func<string?> _pathProvider;

    public CodexExecutableResolver(Func<string?>? pathProvider = null)
    {
        _pathProvider = pathProvider ?? (() => Environment.GetEnvironmentVariable("PATH"));
    }

    public CodexExecutableResolution Resolve()
    {
        var pathValue = _pathProvider();
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return NotFound("direct-executable-not-found");
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string absoluteDirectory;
            try
            {
                absoluteDirectory = Path.GetFullPath(directory);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var directExecutable = Path.Combine(absoluteDirectory, "codex.exe");
            if (File.Exists(directExecutable))
            {
                return new CodexExecutableResolution(
                    true,
                    Path.GetFullPath(directExecutable),
                    CodexExecutableSource.PathNativeExecutable,
                    null);
            }

            var npmShim = Path.Combine(absoluteDirectory, "codex.cmd");
            if (File.Exists(npmShim))
            {
                var nativeBinary = Path.Combine(
                    absoluteDirectory,
                    "node_modules",
                    "@openai",
                    "codex",
                    "node_modules",
                    "@openai",
                    "codex-win32-x64",
                    "vendor",
                    "x86_64-pc-windows-msvc",
                    "bin",
                    "codex.exe");

                return File.Exists(nativeBinary)
                    ? new CodexExecutableResolution(
                        true,
                        Path.GetFullPath(nativeBinary),
                        CodexExecutableSource.NpmPackageNativeBinary,
                        null)
                    : NotFound("npm-native-binary-not-found");
            }
        }

        return NotFound("direct-executable-not-found");
    }

    private static CodexExecutableResolution NotFound(string diagnosticCode) =>
        new(false, null, null, diagnosticCode);
}
