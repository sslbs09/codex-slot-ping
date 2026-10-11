using System.Runtime.Versioning;

namespace CodexSlotPing.Core.Scheduler;

[SupportedOSPlatform("windows")]
public sealed class TaskSchedulerStaWorker
{
    private static readonly SemaphoreSlim OperationGate = new(1, 1);

    public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var gateAcquired = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                OperationGate.Wait(cancellationToken);
                gateAcquired = true;
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                if (gateAcquired)
                {
                    OperationGate.Release();
                }
            }
        })
        {
            IsBackground = true,
            Name = "CodexSlotPing.TaskScheduler.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
