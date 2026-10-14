using System.Net.Http;
using CodexSlotPing.App.ReadModels;
using CodexSlotPing.Update;

namespace CodexSlotPing.App;

public sealed class AppUpdateController
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly UpdateCheckCoordinator _coordinator;
    private readonly string _currentVersion;

    public AppUpdateController(UpdateCheckCoordinator coordinator, string currentVersion)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        _currentVersion = currentVersion;
        CurrentState = UpdateReadModel.Unknown(currentVersion);
    }

    public event Action<UpdateReadModel>? StateChanged;

    public UpdateReadModel CurrentState { get; private set; }

    public static AppUpdateController CreateDefault()
    {
        var client = new GitHubReleaseClient(SharedHttpClient);
        var coordinator = new UpdateCheckCoordinator(client);
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 2, 0, 0);
        var currentVersion = $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        return new AppUpdateController(coordinator, currentVersion);
    }

    public async Task<UpdateReadModel> CheckForUpdatesAsync(
        bool manualRefresh = true,
        CancellationToken cancellationToken = default)
    {
        SetState(UpdateReadModel.Checking(_currentVersion));
        try
        {
            var result = await _coordinator.CheckAsync(_currentVersion, manualRefresh, cancellationToken);
            var projected = UpdateReadModelProjection.Create(result.Evaluation, result.CheckedAtUtc);
            SetState(projected);
            return projected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(UpdateReadModel.Unknown(_currentVersion));
            throw;
        }
    }

    private void SetState(UpdateReadModel state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
    }
}
