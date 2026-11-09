namespace MonitoringApp;

public sealed class RcaWorker(
    RcaReconciliationService reconciliation,
    TimeProvider timeProvider,
    ILogger<RcaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        do
        {
            try
            {
                await reconciliation.ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RCA reconciliation failed. Pending snapshots will be retried in 30 seconds.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
