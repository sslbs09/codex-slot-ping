namespace CodexSlotPing.Core.Codex;

public interface ICodexEnvironmentDetector
{
    Task<EnvironmentStatus> DetectAsync(CancellationToken cancellationToken);
}
