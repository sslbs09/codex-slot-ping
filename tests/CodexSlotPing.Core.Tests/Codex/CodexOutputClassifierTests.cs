using System.Reflection;
using System.Text.Json;
using CodexSlotPing.Core.Codex;

namespace CodexSlotPing.Core.Tests.Codex;

public sealed class CodexOutputClassifierTests
{
    private readonly CodexOutputClassifier _classifier = new();

    [Fact]
    public void CompleteTurnWithAgentMessageAndUsage_IsConfirmed()
    {
        var result = Classify("success.jsonl");

        Assert.Equal(CodexResultClassification.CONFIRMED_SUCCESS, result.Classification);
        Assert.True(result.ProcessStarted);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new CodexTokenUsage(64, 8, 2, 3, 1), result.Usage);
        Assert.Null(result.DiagnosticCategory);
        Assert.DoesNotContain("Fixture response marker", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void StderrSkillYamlWarnings_DoNotOverrideJsonlSuccess()
    {
        var result = _classifier.Classify(
            ReadFixture("warnings-success.jsonl"),
            "Warning: skill metadata could not be loaded.\r\nWarning: YAML front matter is invalid.",
            0,
            timedOut: false,
            processStarted: true);

        Assert.Equal(CodexResultClassification.CONFIRMED_SUCCESS, result.Classification);
        Assert.Contains("stderr-output", result.WarningCategories);
        Assert.DoesNotContain("YAML front matter", string.Join(' ', result.WarningCategories));
    }

    [Fact]
    public void UsageLimitAndTurnFailure_AreAttemptedWithoutRetainingMessage()
    {
        var result = Classify("usage-limit.jsonl");

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("usage-limit", result.DiagnosticCategory);
        Assert.DoesNotContain("usage limit", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonzeroExit_IsAttemptedEvenWithCompletedJsonl()
    {
        var result = _classifier.Classify(ReadFixture("success.jsonl"), string.Empty, 17, false, true);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("nonzero-exit", result.DiagnosticCategory);
    }

    [Fact]
    public void Timeout_IsAttemptedEvenWithCompletedJsonl()
    {
        var result = _classifier.Classify(ReadFixture("success.jsonl"), string.Empty, 1, true, true);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("process-timeout", result.DiagnosticCategory);
    }

    [Fact]
    public void MalformedJsonLine_IsAttempted()
    {
        var result = _classifier.Classify("{not-json}\n", string.Empty, 0, false, true);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("malformed-jsonl", result.DiagnosticCategory);
    }

    [Fact]
    public void PartialFinalLine_IsAttempted()
    {
        var result = _classifier.Classify(ReadFixture("success.jsonl").TrimEnd('\r', '\n'), string.Empty, 0, false, true);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("truncated-jsonl", result.DiagnosticCategory);
    }

    [Fact]
    public void UnknownNonterminalEvent_IsIgnored()
    {
        var stream = ReadFixture("success.jsonl").Replace(
            "{\"type\":\"turn.started\"}\n",
            "{\"type\":\"turn.started\"}\n{\"type\":\"future.progress\",\"detail\":\"ignored\"}\n",
            StringComparison.Ordinal);

        var result = _classifier.Classify(stream, string.Empty, 0, false, true);

        Assert.Equal(CodexResultClassification.CONFIRMED_SUCCESS, result.Classification);
    }

    [Fact]
    public void ErrorAfterTurnCompleted_InvalidatesSuccess()
    {
        var stream = ReadFixture("success.jsonl") + "{\"type\":\"error\",\"message\":\"late error\"}\n";

        var result = _classifier.Classify(stream, string.Empty, 0, false, true);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("stream-error", result.DiagnosticCategory);
        Assert.DoesNotContain("late error", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void MissingTerminalEvent_IsAttempted()
    {
        var stream = string.Join('\n', ReadFixture("success.jsonl").Split('\n').SkipLast(2)) + "\n";

        var result = _classifier.Classify(stream, string.Empty, 0, false, true);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("completion-unconfirmed", result.DiagnosticCategory);
    }

    [Fact]
    public void MissingAgentMessage_IsAttempted()
    {
        const string stream = "{\"type\":\"turn.started\"}\n{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1}}\n";

        var result = _classifier.Classify(stream, string.Empty, 0, false, true);

        Assert.Equal(CodexResultClassification.ATTEMPTED_UNCONFIRMED, result.Classification);
        Assert.Equal("agent-message-missing", result.DiagnosticCategory);
    }

    [Fact]
    public void ProcessNotStarted_IsPrelaunchFailure()
    {
        var result = _classifier.Classify(string.Empty, string.Empty, null, false, false);

        Assert.Equal(CodexResultClassification.PRELAUNCH_FAILED, result.Classification);
        Assert.False(result.ProcessStarted);
    }

    private CodexInvocationResult Classify(string fixtureName) =>
        _classifier.Classify(ReadFixture(fixtureName), string.Empty, 0, false, true);

    private static string ReadFixture(string fixtureName)
    {
        var assembly = typeof(CodexOutputClassifierTests).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith($".{fixtureName}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
