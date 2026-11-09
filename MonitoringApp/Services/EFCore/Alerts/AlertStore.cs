using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class AlertStore
{
    private readonly AlertPersistence alerts;
    private readonly ComputerInventoryPersistence inventory;
    private readonly AlertRulePersistence rules;
    private readonly ILogger<AlertStore> logger;

    public AlertStore(
        IDbContextFactory<AlertDbContext> contextFactory,
        DatabaseConfigurationStatus databaseConfiguration,
        QueryResultPresenter queryResultPresenter,
        AlertRuleEvaluator alertRuleEvaluator,
        ILogger<AlertStore> logger)
    {
        this.logger = logger;
        inventory = new(contextFactory, databaseConfiguration, queryResultPresenter, alertRuleEvaluator, logger, NotifyChanged);
        alerts = new(contextFactory, databaseConfiguration, queryResultPresenter, alertRuleEvaluator, logger, inventory, NotifyChanged);
        rules = new(contextFactory, databaseConfiguration, logger, alertRuleEvaluator, NotifyChanged);
    }

    public event Action? Changed;

    private void NotifyChanged() => Changed?.Invoke();

    internal void NotifyDataChanged()
    {
        foreach (Action subscriber in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                subscriber();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to notify a subscriber after an operational data change.");
            }
        }
    }

    public Task<IReadOnlyList<AlertRule>> GetAlertRulesAsync(CancellationToken cancellationToken = default) =>
        rules.GetAlertRulesAsync(cancellationToken);

    public Task<IReadOnlyList<AlertRule>> GetAllAlertRulesAsync(CancellationToken cancellationToken = default) =>
        rules.GetAllAlertRulesAsync(cancellationToken);

    public Task<AlertRule> SaveAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken = default) =>
        rules.SaveAlertRuleAsync(rule, cancellationToken);

    public Task<bool> DeleteAlertRuleAsync(Guid id, CancellationToken cancellationToken = default) =>
        rules.DeleteAlertRuleAsync(id, cancellationToken);

    public Task<IReadOnlyList<ComputerInventoryEntry>> GetComputerInventoryAsync(CancellationToken cancellationToken = default) =>
        inventory.GetComputerInventoryAsync(cancellationToken);

    public Task<IReadOnlyList<string>> GetAlertSubscriptionIdsAsync(CancellationToken cancellationToken = default) =>
        inventory.GetAlertSubscriptionIdsAsync(cancellationToken);

    public Task<ComputerInventoryEntry> CreateComputerInventoryEntryAsync(
        ComputerInventoryEntry entry, CancellationToken cancellationToken = default) =>
        inventory.CreateComputerInventoryEntryAsync(entry, cancellationToken);

    public Task<ComputerInventoryEntry> SaveComputerInventoryEntryAsync(
        ComputerInventoryEntry entry, CancellationToken cancellationToken = default) =>
        inventory.SaveComputerInventoryEntryAsync(entry, cancellationToken);

    public Task<bool> DeleteComputerInventoryEntryAsync(
        string subscriptionId, string computer, CancellationToken cancellationToken = default) =>
        inventory.DeleteComputerInventoryEntryAsync(subscriptionId, computer, cancellationToken);

    public Task<IReadOnlyList<AlertRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        alerts.GetAllAsync(cancellationToken);

    public Task<IReadOnlyList<AlertRecord>> GetFiredHistoryAsync(CancellationToken cancellationToken = default) =>
        alerts.GetFiredHistoryAsync(cancellationToken);

    /// <summary>
    /// Loads the same alerts as GetAllAsync but propagates configuration and database failures.
    /// </summary>
    public Task<IReadOnlyList<AlertRecord>> GetAllRequiredAsync(CancellationToken cancellationToken = default) =>
        alerts.GetAllAsync(cancellationToken, required: true);

    /// <summary>
    /// Loads the same snapshots as GetFiredHistoryAsync but propagates configuration and database failures.
    /// </summary>
    public Task<IReadOnlyList<AlertRecord>> GetFiredHistoryRequiredAsync(CancellationToken cancellationToken = default) =>
        alerts.GetFiredHistoryAsync(cancellationToken, required: true);

    public Task<IReadOnlyList<AlertRecord>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
        alerts.GetSinceAsync(since, cancellationToken);

    public Task<IReadOnlyList<AlertQueryRefreshTarget>> GetActiveAlertQueryRefreshTargetsAsync(
        string type, CancellationToken cancellationToken = default) =>
        alerts.GetActiveAlertQueryRefreshTargetsAsync(type, cancellationToken);

    public Task<AlertQueryRefreshUpdateResult> UpdateAlertQueryResultAsync(
        Guid id, UpdateAlertQueryResultRequest request, CancellationToken cancellationToken = default) =>
        alerts.UpdateAlertQueryResultAsync(id, request, cancellationToken);

    public Task<IReadOnlyList<AlertGraphRecord>> GetAlertGraphRecordsAsync(
        DateTimeOffset receivedSince, CancellationToken cancellationToken = default) =>
        alerts.GetAlertGraphRecordsAsync(receivedSince, cancellationToken);

    public Task<IReadOnlyList<AlertGraphRecord>> GetAlertGraphRecordsRequiredAsync(
        DateTimeOffset receivedSince, CancellationToken cancellationToken = default) =>
        alerts.GetAlertGraphRecordsAsync(receivedSince, cancellationToken, required: true);

    public Task<IReadOnlyList<CriticalAlertLifecycle>> GetCriticalAlertLifecyclesAsync(
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken = default) =>
        alerts.GetCriticalAlertLifecyclesAsync(rangeStart, rangeEnd, cancellationToken);

    /// <summary>
    /// Loads the same lifecycles as GetCriticalAlertLifecyclesAsync but propagates configuration and database failures.
    /// </summary>
    public Task<IReadOnlyList<CriticalAlertLifecycle>> GetCriticalAlertLifecyclesRequiredAsync(
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken = default) =>
        alerts.GetCriticalAlertLifecyclesAsync(rangeStart, rangeEnd, cancellationToken, required: true);

    public Task<IReadOnlyList<AlertRecord>> GetSinceRequiredAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
        alerts.GetSinceRequiredAsync(since, cancellationToken);

    public Task<AddAlertResult> AddAsync(JsonElement payload, CancellationToken cancellationToken = default) =>
        alerts.AddAsync(payload, cancellationToken);

    public Task<bool> UpdateCommentsAsync(Guid id, string comments, string user, CancellationToken cancellationToken = default) =>
        alerts.UpdateCommentsAsync(id, comments, user, cancellationToken);
}
