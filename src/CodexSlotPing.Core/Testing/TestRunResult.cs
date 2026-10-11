using CodexSlotPing.Core.Codex;

namespace CodexSlotPing.Core.Testing;

public sealed record TestRunResult(
    string TestId,
    CodexInvocationResult Invocation,
    DateTimeOffset CompletedAtUtc);
