using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class ParsedAlertLifecyclePersistence
{
    internal static async Task SynchronizeAsync(
        AlertDbContext context, AlertRuleEvaluator evaluator, string? alertId, CancellationToken cancellationToken)
    {
        var rules = await LoadCriticalRulesAsync(context, cancellationToken);
        var query = context.ParsedAlerts.Include(record => record.Alert).AsQueryable();
        if (!HasAggregateRules(rules) && !string.IsNullOrWhiteSpace(alertId))
            query = query.Where(record => record.AlertId == alertId);
        var records = await query.ToArrayAsync(cancellationToken);
        await SynchronizeCoreAsync(context, evaluator, records, rules, cancellationToken);
    }

    internal static async Task SynchronizeLoadedAsync(
        AlertDbContext context, AlertRuleEvaluator evaluator, IReadOnlyList<ParsedAlertRecord> records,
        CancellationToken cancellationToken)
    {
        var rules = await LoadCriticalRulesAsync(context, cancellationToken);
        await SynchronizeCoreAsync(context, evaluator, records, rules, cancellationToken);
    }

    private static Task<AlertRule[]> LoadCriticalRulesAsync(
        AlertDbContext context, CancellationToken cancellationToken) =>
        context.AlertRules.AsNoTracking()
            .Where(rule => rule.Enabled && rule.RuleType == AlertRuleTypes.Categorization && rule.IsCritical)
            .OrderBy(rule => rule.Priority)
            .ToArrayAsync(cancellationToken);

    private static bool HasAggregateRules(IEnumerable<AlertRule> rules) =>
        rules.Any(rule => rule.ConditionType is AlertRuleConditionTypes.DomainControllerPercentageInDomain or
            AlertRuleConditionTypes.AllDomainControllersInSite);

    private static async Task SynchronizeCoreAsync(
        AlertDbContext context, AlertRuleEvaluator evaluator, IReadOnlyList<ParsedAlertRecord> records,
        AlertRule[] rules, CancellationToken cancellationToken)
    {
        var inventory = HasAggregateRules(rules)
            ? await context.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken)
            : [];
        ParsedAlertLifecycle.Synchronize(records, rules, evaluator, inventory);
    }
}
