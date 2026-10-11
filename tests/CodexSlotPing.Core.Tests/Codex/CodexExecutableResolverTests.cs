using CodexSlotPing.Core.Codex;

namespace CodexSlotPing.Core.Tests.Codex;

public sealed class CodexExecutableResolverTests
{
    [Fact]
    public void DirectNativeExecutable_IsResolvedAsAnAbsolutePath()
    {
        using var directory = new TemporaryDirectory();
        var executablePath = Path.Combine(directory.Path, "codex.exe");
        File.WriteAllText(executablePath, string.Empty);
        var resolver = new CodexExecutableResolver(() => directory.Path);

        var result = resolver.Resolve();

        Assert.True(result.IsDirectlyLaunchable);
        Assert.Equal(Path.GetFullPath(executablePath), result.AbsolutePath);
        Assert.EndsWith(".exe", result.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NpmShim_ResolvesOnlyItsPackageMatchedNativeBinary()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "codex.cmd"), "shim fixture");
        var nativePath = Path.Combine(
            directory.Path,
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
        Directory.CreateDirectory(Path.GetDirectoryName(nativePath)!);
        File.WriteAllText(nativePath, string.Empty);
        var resolver = new CodexExecutableResolver(() => directory.Path);

        var result = resolver.Resolve();

        Assert.True(result.IsDirectlyLaunchable);
        Assert.Equal(Path.GetFullPath(nativePath), result.AbsolutePath);
        Assert.Equal(CodexExecutableSource.NpmPackageNativeBinary, result.Source);
    }

    [Fact]
    public void MissingNpmPackageBinary_DoesNotSilentlyFallBackToAnotherInstall()
    {
        using var shimDirectory = new TemporaryDirectory();
        using var otherInstallDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(shimDirectory.Path, "codex.cmd"), "shim fixture");
        File.WriteAllText(Path.Combine(otherInstallDirectory.Path, "codex.exe"), string.Empty);
        var path = string.Join(Path.PathSeparator, shimDirectory.Path, otherInstallDirectory.Path);
        var resolver = new CodexExecutableResolver(() => path);

        var result = resolver.Resolve();

        Assert.False(result.IsDirectlyLaunchable);
        Assert.Null(result.AbsolutePath);
        Assert.Equal("npm-native-binary-not-found", result.DiagnosticCode);
    }

    [Fact]
    public void ScriptOnlyInstall_IsNotDirectlyLaunchable()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "codex.ps1"), "script fixture");
        var resolver = new CodexExecutableResolver(() => directory.Path);

        var result = resolver.Resolve();

        Assert.False(result.IsDirectlyLaunchable);
        Assert.Null(result.AbsolutePath);
        Assert.Equal("direct-executable-not-found", result.DiagnosticCode);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexResolver-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
