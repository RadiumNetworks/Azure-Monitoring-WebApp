namespace MonitoringApp;

public sealed class IncidentAnalysisSnapshotService(
    AlertStore alertStore,
    IncidentAnalysisSnapshotBuilder snapshotBuilder)
{
    public async Task<IncidentAnalysisSnapshot> BuildAsync(
        IReadOnlyCollection<Guid> primaryEventIds,
        IncidentAnalysisScope scope,
        AlertGraphLayer layer1,
        AlertGraphLayer layer2,
        AlertGraphLayer layer3,
        DateTimeOffset generatedAt,
        IncidentAnalysisSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primaryEventIds);
        if (primaryEventIds.Count == 0)
        {
            throw new ArgumentException("At least one primary event ID is required.", nameof(primaryEventIds));
        }

        options ??= new IncidentAnalysisSnapshotOptions();
        options.Validate();

        var alertTask = alertStore.GetAllAsync(cancellationToken);
        var graphTask = alertStore.GetAlertGraphRecordsAsync(
            generatedAt.AddDays(-options.SystemHistoryDays), cancellationToken);
        var inventoryTask = alertStore.GetComputerInventoryAsync(cancellationToken);
        var rulesTask = alertStore.GetAllAlertRulesAsync(cancellationToken);
        await Task.WhenAll(alertTask, graphTask, inventoryTask, rulesTask);

        var requestedIds = primaryEventIds.ToHashSet();
        var alerts = await alertTask;
        var primary = alerts.Where(alert => requestedIds.Contains(alert.Id)).ToArray();
        var missingIds = requestedIds.Except(primary.Select(alert => alert.Id)).Order().ToArray();
        if (missingIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Primary alert events were not found: {string.Join(", ", missingIds)}");
        }

        return snapshotBuilder.Build(
            primary,
            alerts,
            await graphTask,
            await inventoryTask,
            await rulesTask,
            generatedAt,
            scope,
            layer1,
            layer2,
            layer3,
            options);
    }
}