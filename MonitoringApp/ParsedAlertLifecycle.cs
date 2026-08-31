namespace MonitoringApp;

/// <summary>
/// Applies critical categorization rules and resolution events to normalized alert lifecycles.
/// </summary>
public static class ParsedAlertLifecycle
{
    public static void Synchronize(
        IEnumerable<ParsedAlertRecord> parsedAlerts,
        IEnumerable<AlertRule> criticalRules,
        AlertRuleEvaluator evaluator)
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

        foreach (var lifecycle in parsedAlerts.GroupBy(
            record => string.IsNullOrWhiteSpace(record.AlertId)
                ? record.Id.ToString("D")
                : record.AlertId,
            StringComparer.OrdinalIgnoreCase))
        {
            var isCritical = lifecycle.Any(record =>
                record.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase) &&
                rules.Any(rule => evaluator.MatchesRule(rule, record.Alert)));
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
}
