namespace CodexSlotPing.Core.Logging;

public interface IStructuredLogWriter
{
    Task AppendAsync(StructuredLogEntry entry, CancellationToken cancellationToken);
}
