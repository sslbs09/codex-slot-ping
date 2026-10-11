namespace CodexSlotPing.Core.State;

public interface IStateStore
{
    Task<StateDocument> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(StateDocument state, CancellationToken cancellationToken);
}
