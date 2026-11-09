namespace MonitoringApp;

public enum AlertRuleErrorCode
{
    Name, Type, HeartbeatFreshness, RetiredRoleAssignment, InventoryRole, InventoryRoleMatch,
    AnalysisClassification, CategoryName, RequiredMatch, Priority, MatchLength, AnyMatchingAlert,
    RowCountThreshold, FailedItemsRequired, FailedItemsLength, DomainConditionType,
    DomainPercentage, SiteConditionType, ConditionType, Tone,
    DuplicateReplicationWarning, DuplicateHeartbeatFreshness, DuplicateName, InvalidId,
    ChangeUnconfirmed, SavedNotificationFailed, DeletedNotificationFailed
}

public sealed class AlertRuleException(
    AlertRuleErrorCode code, string message, Exception? innerException = null, params object[] arguments)
    : InvalidOperationException(message, innerException)
{
    public AlertRuleErrorCode Code { get; } = code;
    public IReadOnlyList<object> Arguments { get; } = Array.AsReadOnly(arguments.ToArray());
}
