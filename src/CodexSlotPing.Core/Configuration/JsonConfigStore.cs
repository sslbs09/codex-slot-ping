using CodexSlotPing.Core.Persistence;

namespace CodexSlotPing.Core.Configuration;

public sealed class JsonConfigStore : IConfigStore
{
    private readonly AtomicJsonFile _jsonFile;
    private readonly string _path;

    public JsonConfigStore(AtomicJsonFile jsonFile, string path)
    {
        _jsonFile = jsonFile ?? throw new ArgumentNullException(nameof(jsonFile));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<AppConfig> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var config = await _jsonFile.ReadAsync<AppConfig>(_path, cancellationToken).ConfigureAwait(false);
        if (config is null)
        {
            config = AppConfigDefaults.Create();
            await SaveAsync(config, cancellationToken).ConfigureAwait(false);
            return config;
        }

        EnsureSupported(config);
        return config;
    }

    public Task SaveAsync(AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureSupported(config);
        return _jsonFile.WriteAsync(_path, config, cancellationToken);
    }

    private static void EnsureSupported(AppConfig config)
    {
        if (config.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported config schema version: {config.SchemaVersion}.");
        }
    }
}
