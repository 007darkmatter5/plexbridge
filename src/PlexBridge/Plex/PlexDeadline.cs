namespace PlexBridge.Plex;

/// <summary>
/// Wall-clock bound for a probe. A DNS lookup, connect, or body read that ignores cancellation
/// still returns here, and the abandoned task cannot report a later result.
/// </summary>
public static class PlexDeadline
{
    public static async Task<T> Run<T>(TimeSpan limit, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> work)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeout = new CancellationTokenSource();
        var task = work(linked.Token);
        var delay = Task.Delay(limit, timeout.Token);
        var canceled = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        try
        {
            await Task.WhenAny(task, delay, canceled);
            if (task.IsCompleted)
                return await task;
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new PlexRequestException("The server did not respond before the timeout.");
        }
        finally
        {
            linked.Cancel();
            timeout.Cancel();
            Observe(task);
            Observe(delay);
            Observe(canceled);
        }
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static finished => { _ = finished.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
