namespace MonitoringApp;

public enum MaintenanceValidationCode
{
    ConfirmationInvalid,
    DatabaseUnavailable,
    CommentsTooLong
}

public sealed class MaintenanceValidationException(
    MaintenanceValidationCode code, string message, Guid? alertId = null) : InvalidOperationException(message)
{
    public MaintenanceValidationCode Code { get; } = code;
    public Guid? AlertId { get; } = alertId;
}

public sealed class MaintenanceAccessDeniedException(string message) : UnauthorizedAccessException(message);
