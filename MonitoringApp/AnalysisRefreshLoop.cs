namespace MonitoringApp;

public static class AnalysisRefreshLoop
{
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(
        Func<Task> refresh,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider ?? TimeProvider.System);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await refresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
