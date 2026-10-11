namespace CodexSlotPing.Core.Codex;

public sealed record CodexTokenUsage(
    long? InputTokens,
    long? CachedInputTokens,
    long? CacheWriteInputTokens,
    long? OutputTokens,
    long? ReasoningTokens);

public sealed record CodexInvocationResult(
    CodexResultClassification Classification,
    bool ProcessStarted,
    int? ExitCode,
    long ElapsedMilliseconds,
    bool TimedOut,
    CodexTokenUsage? Usage,
    IReadOnlyList<string> WarningCategories,
    string? RawLogPath,
    string? DiagnosticCategory);
