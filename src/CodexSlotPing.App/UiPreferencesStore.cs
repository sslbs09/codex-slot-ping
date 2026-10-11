using System.IO;
using CodexSlotPing.Core.Persistence;

namespace CodexSlotPing.App;

public sealed record UiPreferences(bool SuppressExitWarning = false);

public sealed class UiPreferencesStore
{
    private readonly AtomicJsonFile _jsonFile;
    private readonly string _path;

    public UiPreferencesStore(string? path = null)
    {
        var resolvedPath = path ?? Path.Combine(UserDataPaths.ForCurrentUser().RootDirectory, "ui-preferences.json");
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        _path = Path.GetFullPath(resolvedPath);
        _jsonFile = new AtomicJsonFile(new PhysicalAtomicFileOperations());
    }

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken)
    {
        return await _jsonFile.ReadAsync<UiPreferences>(_path, cancellationToken).ConfigureAwait(false)
               ?? new UiPreferences();
    }

    public Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return _jsonFile.WriteAsync(_path, preferences, cancellationToken);
    }
}
