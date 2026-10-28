using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;

namespace MonitoringApp;

public sealed class ScheduledQueryRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Revision { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string WorkspaceKey { get; set; } = "";
    public string WorkspaceKeysJson { get; set; } = "[]";
    public string LastWorkspaceResultsJson { get; set; } = "[]";
    [NotMapped, JsonIgnore]
    public IReadOnlyList<string> WorkspaceKeys
    {
        get
        {
            var keys = JsonSerializer.Deserialize<string[]>(WorkspaceKeysJson)
                ?? throw new InvalidOperationException("The scheduled rule workspace selection is invalid.");
            return keys.Length == 0 && !string.IsNullOrWhiteSpace(WorkspaceKey) ? [WorkspaceKey.Trim()] : keys;
        }
        set
        {
            WorkspaceKeysJson = JsonSerializer.Serialize(value);
            WorkspaceKey = "";
        }
    }
    [NotMapped, JsonIgnore]
    public IReadOnlyList<ScheduledQueryWorkspaceStatus> WorkspaceResults =>
        JsonSerializer.Deserialize<ScheduledQueryWorkspaceStatus[]>(LastWorkspaceResultsJson)
            ?? throw new InvalidOperationException("The scheduled rule workspace status is invalid.");
    public string Domain { get; set; } = "";
    public string Site { get; set; } = "";
    public string Role { get; set; } = "";
    public string Computers { get; set; } = "";
    public string Kql { get; set; } = "";
    public int IntervalMinutes { get; set; } = 5;
    public int WindowMinutes { get; set; } = 5;
    public int IngestionDelayMinutes { get; set; } = 2;
    public string Comparison { get; set; } = ">";
    public double Threshold { get; set; } = 90;
    public string Severity { get; set; } = "Sev2";
    public string CategoryName { get; set; } = "System Resource";
    public DateTime NextRunUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LastWindowEndUtc { get; set; }
    public string LastStatus { get; set; } = "Not evaluated";
    public string LastError { get; set; } = "";
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }

    public static readonly string[] Comparisons = ["=", "<", "<=", ">", ">="];

    public bool Matches(double value) => Comparison switch
    {
        "=" => value == Threshold,
        "<" => value < Threshold,
        "<=" => value <= Threshold,
        ">" => value > Threshold,
        ">=" => value >= Threshold,
        _ => throw new InvalidOperationException("Unsupported threshold comparison.")
    };

    public static ScheduledQueryRule Example(bool kerberos) => new()
    {
        Name = kerberos ? "Kerberos authentications stopped" : "DC CPU usage above 90%",
        CategoryName = kerberos ? "Authentication" : "System Resource",
        Comparison = kerberos ? "=" : ">",
        Threshold = kerberos ? 0 : 90,
        Kql = kerberos
            ? """
              Perf
              | where Computer in~ (_SelectedSystems)
              | where TimeGenerated >= _WindowStart and TimeGenerated < _WindowEnd
              | where ObjectName == "Security System-Wide Statistics" and CounterName == "Kerberos Authentications"
              | summarize Value = avg(CounterValue) by Series = Computer
              """
            : """
              Perf
              | where Computer in~ (_SelectedSystems)
              | where TimeGenerated >= _WindowStart and TimeGenerated < _WindowEnd
              | where ObjectName in ("Processor", "Processor Information") and CounterName == "% Idle Time" and InstanceName == "_Total"
              | summarize Value = 100.0 - avg(CounterValue) by Series = Computer
              """
    };
}

public sealed class ScheduledQueryTargetState
{
    public Guid RuleId { get; set; }
    public string WorkspaceKey { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string Computer { get; set; } = "";
    public string Status { get; set; } = "No data";
    public double? Value { get; set; }
    public DateTime CheckedUtc { get; set; }
    public string? ActiveAlertId { get; set; }
    public DateTime? FiredUtc { get; set; }
}

public sealed class ScheduledQueryNotification
{
    public long Id { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? DeliveredUtc { get; set; }
    public string LastError { get; set; } = "";
}

public sealed record ScheduledQueryEvaluation(
    DateTime WindowEndUtc, IReadOnlyList<ScheduledQueryWorkspaceEvaluation> Workspaces);

public sealed record ScheduledQueryWorkspaceEvaluation(
    string WorkspaceKey, IReadOnlyList<ComputerInventoryEntry> Targets,
    IReadOnlyDictionary<string, double?> Values, string? Error);

public sealed record ScheduledQueryWorkspaceStatus(string WorkspaceKey, int Targets, int Values, string? Error);

public sealed record ScheduledQueryExecutionPreview(
    AnalysisQueryExecutionPreview Query, string WorkspaceId,
    DateTime WindowStartUtc, DateTime WindowEndUtc, int ApiTimeRangeMinutes);

public static class ScheduledQueryAlert
{
    public static string Payload(ScheduledQueryRule rule, ComputerInventoryEntry target,
        ScheduledQueryTargetState state, DateTime now, bool fired)
    {
        var description = $"{rule.Name}: {target.Computer}, value {state.Value?.ToString("G", CultureInfo.InvariantCulture)} " +
            $"{rule.Comparison} {rule.Threshold.ToString("G", CultureInfo.InvariantCulture)}; window {rule.WindowMinutes} minutes.";
        return JsonSerializer.Serialize(new
        {
            scheduledQuery = new { ruleId = rule.Id, category = rule.CategoryName },
            data = new
            {
                essentials = new
                {
                    alertId = state.ActiveAlertId, alertRule = rule.Name, severity = rule.Severity,
                    alertState = fired ? "New" : "Closed", monitorCondition = fired ? "Fired" : "Resolved",
                    signalType = "Log", targetResourceName = target.Computer,
                    targetSubscriptionId = target.SubscriptionId, targetResourceGroup = target.ResourceGroup ?? "",
                    firedDateTime = state.FiredUtc is DateTime start ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : (DateTime?)null,
                    resolvedDateTime = fired ? (DateTime?)null : now,
                    description = fired ? description : $"Recovered. {description}"
                }
            },
            queryResult = new
            {
                type = "ScheduledKql",
                columns = new[] { "Computer", "Site", "Value", "Threshold", "Comparison", "WindowMinutes" }
                    .Select(name => new { name }),
                rows = new object?[][] { [target.Computer, target.Site, state.Value, rule.Threshold, rule.Comparison, rule.WindowMinutes] }
            }
        });
    }

    public static (Guid RuleId, string Category)? Category(AlertRecord alert)
    {
        if (!alert.AlertId.StartsWith("scheduled-query:", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(alert.RawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("scheduledQuery", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("ruleId", out var id) || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var ruleId) ||
                !metadata.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(category.GetString()))
                return null;
            return (ruleId, category.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
