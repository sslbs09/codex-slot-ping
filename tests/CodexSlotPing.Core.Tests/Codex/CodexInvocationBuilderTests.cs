using CodexSlotPing.Core.Codex;

namespace CodexSlotPing.Core.Tests.Codex;

public sealed class CodexInvocationBuilderTests
{
    [Fact]
    public void BuildsTheFrozenArgumentVectorAndKeepsPromptOutOfArguments()
    {
        using var workingDirectory = new TemporaryDirectory();
        var prompt = "Keep this prompt only on stdin: secret sentinel";
        var request = new CodexInvocationRequest(
            Path.GetFullPath(Path.Combine(workingDirectory.Path, "codex.exe")),
            "gpt-5.6-luna",
            "low",
            prompt,
            workingDirectory.Path,
            "formal-slot-id");
        var builder = new CodexInvocationBuilder();

        var startInfo = builder.Build(request);

        Assert.Equal(request.ExecutablePath, startInfo.FileName);
        Assert.Equal(workingDirectory.Path, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Empty(Directory.GetFileSystemEntries(workingDirectory.Path));
        Assert.Equal(
            new[]
            {
                "--ask-for-approval", "never",
                "--sandbox", "read-only",
                "--model", "gpt-5.6-luna",
                "--config", "model_reasoning_effort=low",
                "exec",
                "--ignore-user-config",
                "--skip-git-repo-check",
                "--ephemeral",
                "--json",
                "-"
            },
            startInfo.ArgumentList);
        Assert.DoesNotContain(prompt, startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void RemovesCredentialAndEndpointOverridesByNameAndPreservesCodexHome()
    {
        using var workingDirectory = new TemporaryDirectory();
        var request = Request(workingDirectory.Path);
        var sensitiveNames = new[]
        {
            "OPENAI_API_KEY",
            "CODEX_API_KEY",
            "CODEX_ACCESS_TOKEN",
            "OPENAI_BASE_URL",
            "CODEX_OSS_BASE_URL",
            "CODEX_APP_SERVER_CHATGPT_BASE_URL",
            "CODEX_AUTHAPI_BASE_URL",
            "CODEX_AGENT_IDENTITY_AUTHAPI_BASE_URL",
            "CODEX_AGENT_IDENTITY_JWKS_BASE_URL"
        };
        var inheritedValues = sensitiveNames
            .Select(name => new KeyValuePair<string, string?>(name, "test-only-redacted-value"))
            .Append(new KeyValuePair<string, string?>("CODEX_HOME", "C:\\test-codex-home"));

        var startInfo = new CodexInvocationBuilder().Build(request, inheritedValues);

        foreach (var name in sensitiveNames)
        {
            Assert.DoesNotContain(name, startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal("C:\\test-codex-home", startInfo.Environment["CODEX_HOME"]);
    }

    private static CodexInvocationRequest Request(string workingDirectory) => new(
        Path.GetFullPath(Path.Combine(workingDirectory, "codex.exe")),
        "gpt-5.6-luna",
        "low",
        "Hi.",
        workingDirectory,
        "test-id");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexInvocation-{Guid.NewGuid():N}");
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
