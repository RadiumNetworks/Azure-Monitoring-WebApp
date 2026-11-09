namespace MonitoringApp;

public enum ComputerInventoryErrorCode
{
    Subscription, Computer, DomainLength, SiteLength, ResourceGroupLength, RoleLength,
    UnobservedSubscription, Duplicate, Missing,
    CreateUnconfirmed, UpdateUnconfirmed, DeleteUnconfirmed,
    SavedNotificationFailed, DeletedNotificationFailed
}

public sealed class ComputerInventoryException(
    ComputerInventoryErrorCode code, string message, Exception? innerException = null, params object[] arguments)
    : InvalidOperationException(message, innerException)
{
    public ComputerInventoryErrorCode Code { get; } = code;
    public IReadOnlyList<object> Arguments { get; } = Array.AsReadOnly(arguments.ToArray());
}
