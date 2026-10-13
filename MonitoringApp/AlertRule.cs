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
    public bool IsCritical { get; set; }
    public string AnalysisClassification { get; set; } = AlertRuleAnalysisClassifications.None;
}

public static class AlertRuleTypes
{
    public const string Categorization = "Categorization";
    public const string Suppression = "Suppression";
    public const string InventoryRoleAssignment = "InventoryRoleAssignment";
    public const string InventoryStatus = "InventoryStatus";
    public const string InventoryHeartbeatFreshness = "InventoryHeartbeatFreshness";
}

public static class AlertRuleConditionTypes
{
    public const string AnyMatchingAlert = "AnyMatchingAlert";
    public const string RowCountGreaterThan = "RowCountGreaterThan";
    public const string RowCountEquals = "RowCountEquals";
    public const string OnlyFailedItems = "OnlyFailedItems";
    public const string NoFailedItems = "NoFailedItems";
    public const string DomainControllerPercentageInDomain = "DomainControllerPercentageInDomain";
    public const string AllDomainControllersInSite = "AllDomainControllersInSite";
}

public static class AlertRuleAnalysisClassifications
{
    public const string None = "None";
    public const string SystemOutage = "SystemOutage";
    public const string Suppressed = "Suppressed";
}

public sealed record AlertCategoryGroup(
    Guid RuleId,
    string Name,
    string Tone,
    bool Collapsed,
    IReadOnlyList<AlertRecord> Alerts,
    string AnalysisClassification = AlertRuleAnalysisClassifications.None,
    bool IsCritical = false);

public sealed record AlertCategorizationResult(
    IReadOnlyList<AlertCategoryGroup> Categories,
    IReadOnlyList<AlertRecord> Uncategorized);

public static class AlertRuleEvaluationOutcomes
{
    public const string Applied = "Applied";
    public const string Superseded = "Superseded";
    public const string NotMatched = "NotMatched";
    public const string Disabled = "Disabled";
}

public sealed record AlertRuleCriterionEvaluation(
    string Field,
    string Operation,
    string Expected,
    string Actual,
    bool Matched);

public sealed record AlertRuleDecisionTrace(
    Guid AlertEventId,
    string AlertId,
    Guid RuleId,
    string RuleName,
    int Priority,
    string AnalysisClassification,
    string Outcome,
    bool DirectMatch,
    Guid? TriggerEventId,
    Guid? WinningRuleId,
    string Reason,
    IReadOnlyList<AlertRuleCriterionEvaluation> Criteria);

public sealed record AlertRuleEvaluationResult(
    AlertCategorizationResult Categorization,
    IReadOnlyList<AlertRuleDecisionTrace> Decisions);