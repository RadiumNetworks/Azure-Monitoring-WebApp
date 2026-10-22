namespace MonitoringApp;

public static class DefaultAlertRules
{
    public static IReadOnlyList<AlertRule> Create() =>
    [
        new()
        {
            Id = new("d7ab8e49-2cfa-4a2e-a635-964bee230001"),
            Name = "Inventory heartbeat freshness",
            Priority = 0,
            RuleType = AlertRuleTypes.InventoryHeartbeatFreshness,
            Threshold = InventoryHeartbeatFreshnessRule.DefaultMinutes,
            Collapsed = false
        },
        new()
        {
            Id = new("7b28f935-087b-4c20-8635-84e2c65484e9"),
            Name = "Inventory Heartbeat Status",
            Priority = 1,
            RuleType = AlertRuleTypes.InventoryStatus,
            AlertNameContains = "Inventory Heartbeat Status",
            QueryResultType = InventoryHeartbeatBatch.QueryResultType,
            ConditionType = AlertRuleConditionTypes.AnyMatchingAlert,
            Collapsed = false
        },
        new()
        {
            Id = new("d82b566a-ce6e-4201-b7b9-9a366426e7ba"),
            Name = "Suppress healthy DCPort results",
            Priority = 5,
            QueryResultType = "DCPort",
            ConditionType = AlertRuleConditionTypes.RowCountEquals,
            CategoryName = "Suppressed alerts",
            AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed
        },
        new()
        {
            Id = new("c4c346f4-2f67-465e-94b0-c018a6de0001"),
            Name = "Domain controller majority indicates domain service outage",
            Priority = 10,
            ConditionType = AlertRuleConditionTypes.DomainControllerPercentageInDomain,
            Threshold = 50,
            CategoryName = "Domain Service Outage",
            Collapsed = false,
            Tone = "failure",
            IsCritical = true,
            AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage
        },
        new()
        {
            Id = new("c4c346f4-2f67-465e-94b0-c018a6de0002"),
            Name = "All site domain controllers indicate site outage",
            Priority = 20,
            ConditionType = AlertRuleConditionTypes.AllDomainControllersInSite,
            CategoryName = "Domain Service Site Outage",
            Collapsed = false,
            Tone = "failure",
            IsCritical = true,
            AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage
        },
        new()
        {
            Id = new("d82b566a-ce6e-4201-b7b9-9a366426e7b8"),
            Name = "Suppress isolated DFSREvent failure",
            Priority = 20,
            AlertNameContains = "DCDiag",
            QueryResultType = "DCDiag",
            ConditionType = AlertRuleConditionTypes.OnlyFailedItems,
            FailedItemName = "DFSREvent",
            CategoryName = "Suppressed alerts",
            AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed
        },
        new()
        {
            Id = new("47a96c56-ccf5-4f4e-97ce-6a72bb462f91"),
            Name = "Port failures indicate system outage",
            Priority = 30,
            AlertNameContains = "Port",
            QueryResultType = "DCPort",
            ConditionType = AlertRuleConditionTypes.RowCountGreaterThan,
            Threshold = 10,
            CategoryName = "System Outage",
            ApplyToTarget = true,
            Collapsed = false,
            Tone = "failure",
            IsCritical = true,
            AnalysisClassification = AlertRuleAnalysisClassifications.SystemOutage
        },
        new()
        {
            Id = new("d82b566a-ce6e-4201-b7b9-9a366426e7b9"),
            Name = "Suppress healthy DCDiag results",
            Priority = 30,
            AlertNameContains = "DCDiag",
            QueryResultType = "DCDiag",
            ConditionType = AlertRuleConditionTypes.NoFailedItems,
            CategoryName = "Suppressed alerts",
            AnalysisClassification = AlertRuleAnalysisClassifications.Suppressed
        },
        new()
        {
            Id = new("956fbb9c-cadb-4f49-ad1e-78c09a8a1301"),
            Name = "DCDiag targets are domain controllers",
            Priority = 100,
            RuleType = AlertRuleTypes.InventoryRoleAssignment,
            QueryResultType = "DCDiag",
            InventoryRole = "Domain Controller",
            Collapsed = false
        },
        new()
        {
            Id = new("956fbb9c-cadb-4f49-ad1e-78c09a8a1302"),
            Name = "Replication targets are domain controllers",
            Priority = 110,
            RuleType = AlertRuleTypes.InventoryRoleAssignment,
            QueryResultType = "Replication",
            InventoryRole = "Domain Controller",
            Collapsed = false
        }
    ];
}
