namespace CodexSlotPing.Core.Configuration;

public interface IConfigStore
{
    Task<AppConfig> LoadOrCreateAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppConfig config, CancellationToken cancellationToken);
}
