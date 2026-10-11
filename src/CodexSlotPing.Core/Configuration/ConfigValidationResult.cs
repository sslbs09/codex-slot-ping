namespace CodexSlotPing.Core.Configuration;

public sealed record ConfigValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    AppConfig? NormalizedConfig);
