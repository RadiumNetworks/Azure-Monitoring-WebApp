namespace MonitoringApp;

/// <summary>
/// Defines a database-managed rule that places matching inbox alerts into a collapsed category.
/// </summary>
public sealed class AlertRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string RuleType { get; set; } = AlertRuleTypes.Categorization;
    public string AlertNameContains { get; set; } = string.Empty;
    public string QueryResultType { get; set; } = string.Empty;
    public string ConditionType { get; set; } = string.Empty;
    public int Threshold { get; set; }
    public string FailedItemName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public bool ApplyToTarget { get; set; }
    public bool Collapsed { get; set; } = true;
    public string Tone { get; set; } = "info";
    public string InventoryRole { get; set; } = string.Empty;
}

public static class AlertRuleTypes
{
    public const string Categorization = "Categorization";
    public const string InventoryRoleAssignment = "InventoryRoleAssignment";
}

public static class AlertRuleConditionTypes
{
    public const string RowCountGreaterThan = "RowCountGreaterThan";
    public const string OnlyFailedItem = "OnlyFailedItem";
}

public sealed record AlertCategoryGroup(
    Guid RuleId,
    string Name,
    string Tone,
    bool Collapsed,
    IReadOnlyList<AlertRecord> Alerts);

public sealed record AlertCategorizationResult(
    IReadOnlyList<AlertCategoryGroup> Categories,
    IReadOnlyList<AlertRecord> Uncategorized);