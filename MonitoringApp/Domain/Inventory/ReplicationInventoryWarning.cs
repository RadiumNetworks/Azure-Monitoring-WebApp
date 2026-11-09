using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MonitoringApp;

public static class ReplicationInventoryWarning
{
    public const string QueryResultType = "InventoryUnknownTarget";
    public const string AlertName = "Unknown replication inventory target";
    public static readonly Guid DefaultRuleId = new("e9b56b85-cd67-4cd2-b693-62bf5a6d0001");

    public static bool IsReplication(AlertRecord alert) =>
        InventoryRoleRuleMatcher.ReadQueryResultType(alert.RawJson)
            .Equals("Replication", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ReadSystems(AlertRecord alert)
    {
        using var document = JsonDocument.Parse(alert.RawJson);
        if (!QueryResultPayload.TryGetQueryResult(document.RootElement, out var result) ||
            !QueryResultPayload.TryReadTable(result, out var table))
            throw new InvalidOperationException("The Replication query result must contain columns and rows.");

        var systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            foreach (var column in new[] { "SourceDSA", "DestDSA" })
            {
                if (!table.ColumnIndexes.TryGetValue(column, out var index) || index >= row.Length ||
                    row[index].ValueKind != JsonValueKind.String)
                    continue;
                var value = row[index].GetString()?.Trim() ?? string.Empty;
                if (!AlertRecord.IsMeaningfulTarget(value))
                    continue;
                var computer = AlertRecord.GetTargetName(value).Trim();
                if (computer.Length is > 0 and <= 256 && AlertRecord.IsMeaningfulTarget(computer))
                    systems.Add(computer);
            }
        }
        return systems.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static AlertRecord Create(AlertRecord source, AlertRule rule, string computer)
    {
        var sourceId = string.IsNullOrWhiteSpace(source.AlertId) ? source.Id.ToString() : source.AlertId;
        var key = JsonSerializer.Serialize(new[] { sourceId, source.SubscriptionId.Trim().ToUpperInvariant(), computer.ToUpperInvariant() });
        var alertId = $"inventory-unknown:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            inventoryWarning = new { ruleId = rule.Id, sourceAlertId = source.AlertId, sourceAlertName = source.Name },
            data = new
            {
                essentials = new
                {
                    alertId, alertRule = AlertName, severity = "Sev2", alertState = "New",
                    monitorCondition = "Fired", signalType = "Log", targetResourceName = computer,
                    targetSubscriptionId = source.SubscriptionId.Trim(), targetResourceGroup = source.ResourceGroup,
                    firedDateTime = now,
                    description = $"Replication reports system '{computer}', which is not registered in inventory for subscription " +
                        $"'{source.SubscriptionId.Trim()}'. Source alert: {source.Name} ({source.AlertId}). No inventory data was changed."
                },
                alertContext = new { linkToSearchResultsUI = source.SearchResultsUrl }
            },
            queryResult = new
            {
                type = QueryResultType, rowCount = 1,
                columns = new[] { new { name = "Computer" }, new { name = "SourceAlertId" }, new { name = "SourceAlertName" } },
                rows = new[] { new[] { computer, source.AlertId, source.Name } }
            }
        });
        using var document = JsonDocument.Parse(payload);
        return AlertPayload.Parse(document.RootElement).Alert;
    }
}
