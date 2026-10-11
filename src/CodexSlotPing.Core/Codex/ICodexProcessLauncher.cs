namespace CodexSlotPing.Core.Codex;

public interface ICodexProcessLauncher
{
    Task<CodexInvocationResult> RunAsync(CodexInvocationRequest request, CancellationToken cancellationToken);
}
