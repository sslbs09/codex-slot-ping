namespace CodexSlotPing.Core.Codex;

public sealed record CodexInvocationRequest(
    string ExecutablePath,
    string Model,
    string ReasoningEffort,
    string Prompt,
    string WorkingDirectory,
    string InvocationId);
