using System.Text.Json;

namespace MonitoringApp;

/// <summary>
/// Evaluates inventory-role assignment rules against an ingested alert.
/// </summary>
public static class InventoryRoleRuleMatcher
{
    public static string? FindRole(AlertRecord alert, IEnumerable<AlertRule> rules)
    {
        var queryResultType = ReadQueryResultType(alert.RawJson);
        return rules
            .Where(rule => rule.Enabled && rule.RuleType == AlertRuleTypes.InventoryRoleAssignment)
            .OrderBy(rule => rule.Priority)
            .FirstOrDefault(rule =>
                (string.IsNullOrWhiteSpace(rule.AlertNameContains) ||
                    alert.Name.Contains(rule.AlertNameContains, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(rule.QueryResultType) ||
                    queryResultType.Equals(rule.QueryResultType, StringComparison.OrdinalIgnoreCase)))
            ?.InventoryRole;
    }

    internal static string ReadQueryResultType(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.TryGetProperty("queryResult", out var queryResult) &&
                queryResult.ValueKind == JsonValueKind.Object &&
                queryResult.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String
                    ? type.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
