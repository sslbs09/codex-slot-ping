using CodexSlotPing.Core.Codex;

namespace CodexSlotPing.Core.Tests.Codex;

public sealed class CodexProcessLauncherTests
{
    [Fact]
    public async Task LaunchesFakeCodexDirectlyAndDoesNotReturnPromptOrResponseText()
    {
        using var workingDirectory = new TemporaryDirectory();
        var prompt = "__FAKECODEX_SUCCESS__";
        var request = new CodexInvocationRequest(FakeCodexPath(), "gpt-5.6-luna", "low", prompt, workingDirectory.Path, "test-id");
        var launcher = new CodexProcessLauncher(new CodexInvocationBuilder(), TimeSpan.FromSeconds(5));

        var result = await launcher.RunAsync(request, CancellationToken.None);

        Assert.True(result.ProcessStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.DoesNotContain(prompt, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("REDACTED_RESPONSE", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutKillsTheFakeChildAndReturnsAnUnconfirmedAttempt()
    {
        using var workingDirectory = new TemporaryDirectory();
        var request = new CodexInvocationRequest(
            FakeCodexPath(),
            "gpt-5.6-luna",
            "low",
            "__FAKECODEX_DELAY__",
            workingDirectory.Path,
            "test-id");
        var launcher = new CodexProcessLauncher(new CodexInvocationBuilder(), TimeSpan.FromMilliseconds(300));

        var result = await launcher.RunAsync(request, CancellationToken.None);

        Assert.True(result.ProcessStarted);
        Assert.True(result.TimedOut, result.ToString());
        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
    }

    [Fact]
    public async Task PromptIsDeliveredThroughStdin()
    {
        using var workingDirectory = new TemporaryDirectory();
        var request = new CodexInvocationRequest(
            FakeCodexPath(),
            "gpt-5.6-luna",
            "low",
            "__FAKECODEX_NONZERO__",
            workingDirectory.Path,
            "test-id");
        var launcher = new CodexProcessLauncher(new CodexInvocationBuilder(), TimeSpan.FromSeconds(5));

        var result = await launcher.RunAsync(request, CancellationToken.None);

        Assert.True(result.ProcessStarted);
        Assert.Equal(17, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ProcessStartFailure_IsReportedAsPrelaunchFailure()
    {
        using var workingDirectory = new TemporaryDirectory();
        var request = new CodexInvocationRequest(
            Path.Combine(workingDirectory.Path, "missing-codex.exe"),
            "gpt-5.6-luna",
            "low",
            "Hi.",
            workingDirectory.Path,
            "test-id");
        var launcher = new CodexProcessLauncher(new CodexInvocationBuilder(), TimeSpan.FromSeconds(1));

        var result = await launcher.RunAsync(request, CancellationToken.None);

        Assert.False(result.ProcessStarted);
        Assert.Equal(CodexResultClassification.PRELAUNCH_FAILED, result.Classification);
    }

    private static string FakeCodexPath()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = currentDirectory.Parent?.Name ?? "Release";
        var repository = currentDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodexSlotPing.sln")))
        {
            repository = repository.Parent;
        }

        if (repository is null)
        {
            throw new DirectoryNotFoundException("Could not locate the solution root for the FakeCodex test fixture.");
        }

        return Path.Combine(
            repository.FullName,
            "tests",
            "CodexSlotPing.FakeCodex",
            "bin",
            configuration,
            "net10.0",
            "CodexSlotPing.FakeCodex.exe");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexProcess-{Guid.NewGuid():N}");
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
