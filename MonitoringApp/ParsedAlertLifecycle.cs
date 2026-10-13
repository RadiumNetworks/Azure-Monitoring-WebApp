namespace MonitoringApp;

/// <summary>
/// Applies critical categorization rules and resolution events to normalized alert lifecycles.
/// </summary>
public static class ParsedAlertLifecycle
{
    public static void Synchronize(
        IEnumerable<ParsedAlertRecord> parsedAlerts,
        IEnumerable<AlertRule> criticalRules,
        AlertRuleEvaluator evaluator,
        IReadOnlyList<ComputerInventoryEntry>? inventory = null)
    {
        ArgumentNullException.ThrowIfNull(parsedAlerts);
        ArgumentNullException.ThrowIfNull(criticalRules);
        ArgumentNullException.ThrowIfNull(evaluator);

        var rules = criticalRules
            .Where(rule => rule.Enabled &&
                rule.RuleType == AlertRuleTypes.Categorization &&
                rule.IsCritical)
            .OrderBy(rule => rule.Priority)
            .ToArray();
        var aggregateRules = rules
            .Where(rule => rule.ConditionType is
                AlertRuleConditionTypes.DomainControllerPercentageInDomain or
                AlertRuleConditionTypes.AllDomainControllersInSite)
            .ToArray();
        var directRules = rules.Except(aggregateRules).ToArray();
        var records = parsedAlerts
            .OrderBy(record => record.Alert.ReceivedAt)
            .ThenBy(record => record.MonitorCondition.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var aggregateCriticalAlertIds = EvaluateAggregateCriticalLifecycles(
            records,
            aggregateRules,
            evaluator,
            inventory ?? []);

        foreach (var lifecycle in records.GroupBy(
            record => string.IsNullOrWhiteSpace(record.AlertId)
                ? record.Id.ToString("D")
                : record.AlertId,
            StringComparer.OrdinalIgnoreCase))
        {
            var isCritical = lifecycle.Any(record =>
                record.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase) &&
                directRules.Any(rule => evaluator.MatchesRule(rule, record.Alert))) ||
                aggregateCriticalAlertIds.Contains(lifecycle.Key);
            var resolvedAt = lifecycle
                .Where(record => record.MonitorCondition.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
                .Select(record => record.ResolvedAt ?? record.Alert.ReceivedAt)
                .Cast<DateTimeOffset?>()
                .Max();

            foreach (var record in lifecycle)
            {
                record.IsCritical = isCritical;
                record.ResolvedAt = resolvedAt;
            }
        }
    }

    private static HashSet<string> EvaluateAggregateCriticalLifecycles(
        IReadOnlyList<ParsedAlertRecord> records,
        IReadOnlyList<AlertRule> aggregateRules,
        AlertRuleEvaluator evaluator,
        IReadOnlyList<ComputerInventoryEntry> inventory)
    {
        var criticalAlertIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (aggregateRules.Count == 0 || inventory.Count == 0)
        {
            return criticalAlertIds;
        }

        var activeByAlertId = new Dictionary<string, AlertRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var lifecycleId = string.IsNullOrWhiteSpace(record.AlertId)
                ? record.Id.ToString("D")
                : record.AlertId;
            if (record.MonitorCondition.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
            {
                activeByAlertId.Remove(lifecycleId);
                continue;
            }

            if (!record.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            activeByAlertId[lifecycleId] = record.Alert;
            var activeAlerts = activeByAlertId.Values.ToArray();
            var categorization = evaluator.Categorize(
                activeAlerts,
                aggregateRules,
                inventory,
                activeAlerts);
            foreach (var alertId in categorization.Categories
                .SelectMany(category => category.Alerts)
                .Select(alert => string.IsNullOrWhiteSpace(alert.AlertId)
                    ? alert.Id.ToString("D")
                    : alert.AlertId))
            {
                criticalAlertIds.Add(alertId);
            }
        }

        return criticalAlertIds;
    }
}
