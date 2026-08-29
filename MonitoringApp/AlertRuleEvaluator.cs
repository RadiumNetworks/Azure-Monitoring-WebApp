using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Evaluates enabled database rules against the normalized query results shown in the inbox.
/// </summary>
public sealed class AlertRuleEvaluator(QueryResultPresenter queryResultPresenter)
{
    public AlertCategorizationResult Categorize(
        IReadOnlyList<AlertRecord> alerts,
        IEnumerable<AlertRule> rules)
    {
        var assignments = new Dictionary<Guid, AlertRule>();
        var orderedRules = rules.Where(rule => rule.Enabled).OrderBy(rule => rule.Priority).ToArray();

        foreach (var rule in orderedRules)
        {
            var triggers = alerts.Where(alert => Matches(rule, alert)).ToArray();
            var candidates = rule.ApplyToTarget
                ? triggers.SelectMany(trigger => alerts.Where(alert => SameTarget(alert, trigger)))
                : triggers;

            foreach (var alert in candidates)
            {
                assignments.TryAdd(alert.Id, rule);
            }
        }

        var categories = assignments
            .GroupBy(assignment => assignment.Value.Id)
            .Select(group =>
            {
                var rule = group.First().Value;
                var memberIds = group.Select(assignment => assignment.Key).ToHashSet();
                return (Rule: rule, Group: new AlertCategoryGroup(
                    rule.Id,
                    rule.CategoryName,
                    rule.Tone,
                    rule.Collapsed,
                    alerts.Where(alert => memberIds.Contains(alert.Id)).ToArray()));
            })
            .OrderBy(item => item.Rule.Priority)
            .Select(item => item.Group)
            .ToArray();
        var categorizedIds = assignments.Keys.ToHashSet();

        return new AlertCategorizationResult(
            categories,
            alerts.Where(alert => !categorizedIds.Contains(alert.Id)).ToArray());
    }

    private bool Matches(AlertRule rule, AlertRecord alert)
    {
        if (!string.IsNullOrWhiteSpace(rule.AlertNameContains) &&
            !alert.Name.Contains(rule.AlertNameContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryReadQueryResult(alert.RawJson, out var type, out var rowCount) ||
            !type.Equals(rule.QueryResultType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rule.ConditionType switch
        {
            AlertRuleConditionTypes.RowCountGreaterThan => rowCount > rule.Threshold,
            AlertRuleConditionTypes.OnlyFailedItem => HasOnlyFailedItem(alert, rule.FailedItemName),
            _ => false
        };
    }

    private bool HasOnlyFailedItem(AlertRecord alert, string failedItemName)
    {
        var presentation = queryResultPresenter.Parse(alert.RawJson);
        var failures = presentation?.Rows
            .SelectMany(row => row.Alerts)
            .Where(item => !item.Value.Equals("Passed", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        return failures.Length == 1 &&
            failures[0].Label.Equals(failedItemName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadQueryResult(string rawJson, out string type, out int rowCount)
    {
        type = string.Empty;
        rowCount = 0;
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("queryResult", out var queryResult) ||
                !queryResult.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !queryResult.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            type = typeElement.GetString() ?? string.Empty;
            rowCount = rows.GetArrayLength();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool SameTarget(AlertRecord left, AlertRecord right) =>
        left.TargetName.Equals(right.TargetName, StringComparison.OrdinalIgnoreCase);
}