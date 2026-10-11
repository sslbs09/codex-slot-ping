using CodexSlotPing.Core.Codex;
using Xunit.Abstractions;

namespace CodexSlotPing.Core.Tests.Codex;

public sealed class CodexEnvironmentDetectorTests
{
    private readonly ITestOutputHelper _output;

    public CodexEnvironmentDetectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task InstalledCli_IsCheckedWithReadOnlyCommandsAndNoPrompt()
    {
        var resolver = new CodexExecutableResolver();
        var resolution = resolver.Resolve();
        if (!resolution.IsDirectlyLaunchable)
        {
            return;
        }

        var detector = new CodexEnvironmentDetector(resolver);

        var status = await detector.DetectAsync(CancellationToken.None);

        Assert.True(status.DirectExecutableFound);
        Assert.Equal(resolution.AbsolutePath, status.ExecutablePath);
        Assert.StartsWith("codex-cli ", status.CliVersion, StringComparison.Ordinal);
        Assert.NotNull(status.Capabilities);
        Assert.True(status.Capabilities.SupportsRequiredInvocationContract, string.Join(",", status.DiagnosticCodes));
        Assert.Contains("low", status.SupportedReasoningEfforts);
        _output.WriteLine($"CLI version: {status.CliVersion}");
        _output.WriteLine($"ChatGPT login confirmed: {status.ChatGptLoginConfirmed}");
        _output.WriteLine($"Required options supported: {status.Capabilities.SupportsRequiredInvocationContract}");
    }
}
