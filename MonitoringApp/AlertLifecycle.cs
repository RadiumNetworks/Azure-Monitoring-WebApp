using System.Text.RegularExpressions;

namespace MonitoringApp;

/// <summary>
/// Determines the current lifecycle state of received alerts. It reduces alert history to the latest active event for each alert identifier.
/// </summary>
public static class AlertLifecycle
{
    /// <summary>
    /// Returns alerts whose latest event is fired and which have not been manually marked resolved. Results are ordered with the newest alert first.
    /// </summary>
    public static IReadOnlyList<AlertRecord> GetActiveAlerts(IEnumerable<AlertRecord> alerts)
    {
        var snapshot = alerts.ToArray();
        return snapshot
            .Where(alert => !string.IsNullOrWhiteSpace(alert.AlertId))
            .GroupBy(alert => alert.AlertId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(alert => alert.ReceivedAt).First())
            .Concat(snapshot.Where(alert => string.IsNullOrWhiteSpace(alert.AlertId)))
            .Where(IsFiredAndNotManuallyResolved)
            .OrderByDescending(alert => alert.ReceivedAt)
            .ToArray();
    }

    /// <summary>
    /// Checks whether an alert is fired and its comments do not contain a standalone Resolved marker. The comparison is case-insensitive.
    /// </summary>
    private static bool IsFiredAndNotManuallyResolved(AlertRecord alert) =>
        alert.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(
            alert.Comments,
            @"\bResolved\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}