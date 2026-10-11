namespace CodexSlotPing.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
