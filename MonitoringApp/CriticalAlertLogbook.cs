namespace MonitoringApp;

/// <summary>
/// Creates system-authored logbook entries for newly ingested critical lifecycle events.
/// </summary>
public static class CriticalAlertLogbook
{
    public const string SystemUser = "System";

    public static LogbookEntry? CreateEntry(
        AlertRecord alert,
        bool isCritical,
        DateTimeOffset createdAt)
    {
        if (!isCritical)
        {
            return null;
        }

        var action = alert.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase)
            ? "fired"
            : alert.MonitorCondition.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
                ? "resolved"
                : null;
        if (action is null)
        {
            return null;
        }

        var alertName = Display(alert.Name);
        var target = Display(alert.TargetDisplayName);
        var severity = Display(alert.Severity);
        var alertId = Display(alert.AlertId);

        return new LogbookEntry
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            User = SystemUser,
            Comment = $"Critical alert {action}: {alertName}; Target: {target}; Severity: {severity}; Alert ID: {alertId}."
        };
    }

    private static string Display(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Trim();
}
