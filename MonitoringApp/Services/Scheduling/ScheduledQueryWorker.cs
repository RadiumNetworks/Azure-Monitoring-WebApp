namespace MonitoringApp;

public sealed class ScheduledQueryWorker(
    ScheduledQueryService service, DatabaseConfigurationStatus database,
    TimeProvider clock, ILogger<ScheduledQueryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!database.IsValid)
        {
            logger.LogError("Scheduled KQL alerts cannot run: {Error}", database.Error);
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try { await service.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled KQL alert checks failed. Check database connectivity and apply the scheduled-query migration.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
