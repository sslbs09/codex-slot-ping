using System.Net.Http;

namespace CodexSlotPing.App.Updates;

public sealed class UpdateCheckCoordinator
{
    private static readonly TimeSpan DefaultCacheAge = TimeSpan.FromHours(24);
    private readonly Func<CancellationToken, Task<IReadOnlyList<GitHubRelease>>> _loadReleases;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheAge;
    private readonly object _gate = new();
    private Task<UpdateCheckResult>? _inFlight;
    private UpdateCheckResult? _successfulCache;

    public UpdateCheckCoordinator(GitHubReleaseClient releaseClient, TimeProvider? timeProvider = null)
        : this(
            (releaseClient ?? throw new ArgumentNullException(nameof(releaseClient))).GetAllReleasesAsync,
            timeProvider)
    {
    }

    public UpdateCheckCoordinator(
        Func<CancellationToken, Task<IReadOnlyList<GitHubRelease>>> loadReleases,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheAge = null)
    {
        _loadReleases = loadReleases ?? throw new ArgumentNullException(nameof(loadReleases));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheAge = cacheAge ?? DefaultCacheAge;
        if (_cacheAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheAge));
        }
    }

    public Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        bool manualRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        Task<UpdateCheckResult> operation;
        lock (_gate)
        {
            if (_inFlight is not null)
            {
                operation = _inFlight;
            }
            else if (!manualRefresh && IsCacheFreshFor(currentVersion))
            {
                return Task.FromResult(_successfulCache! with { FromCache = true });
            }
            else
            {
                var completion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = completion.Task;
                _inFlight = operation;
                _ = CompleteCheckAsync(completion, currentVersion, cancellationToken);
            }
        }

        return operation.WaitAsync(cancellationToken);
    }

    private async Task CompleteCheckAsync(
        TaskCompletionSource<UpdateCheckResult> completion,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var releases = await _loadReleases(cancellationToken).ConfigureAwait(false);
            var evaluation = UpdateEvaluator.Evaluate(currentVersion, releases);
            var result = new UpdateCheckResult(evaluation, _timeProvider.GetUtcNow(), false);
            if (evaluation.Status != UpdateEvaluationStatus.Failed)
            {
                lock (_gate)
                {
                    _successfulCache = result;
                }
            }
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (ReleaseDiscoveryException exception)
        {
            completion.TrySetResult(Failed(currentVersion, exception.ErrorCategory));
        }
        catch (OperationCanceledException)
        {
            completion.TrySetResult(Failed(currentVersion, UpdateErrorCategory.Timeout));
        }
        catch (HttpRequestException)
        {
            completion.TrySetResult(Failed(currentVersion, UpdateErrorCategory.Network));
        }
        catch (System.Text.Json.JsonException)
        {
            completion.TrySetResult(Failed(currentVersion, UpdateErrorCategory.MalformedResponse));
        }
        catch
        {
            completion.TrySetResult(Failed(currentVersion, UpdateErrorCategory.ApiError));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, completion.Task))
                {
                    _inFlight = null;
                }
            }
        }
    }

    private bool IsCacheFreshFor(string currentVersion)
    {
        if (_successfulCache is not { } cached ||
            !string.Equals(cached.Evaluation.CurrentVersion, currentVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var age = _timeProvider.GetUtcNow() - cached.CheckedAtUtc;
        return age >= TimeSpan.Zero && age < _cacheAge;
    }

    private UpdateCheckResult Failed(string currentVersion, UpdateErrorCategory errorCategory) =>
        new(UpdateEvaluator.Failure(currentVersion, errorCategory), _timeProvider.GetUtcNow(), false);
}
