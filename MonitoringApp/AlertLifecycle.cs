using System.Text.RegularExpressions;

namespace MonitoringApp;

public static class AlertLifecycle
{
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

    private static bool IsFiredAndNotManuallyResolved(AlertRecord alert) =>
        alert.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(
            alert.Comments,
            @"\bResolved\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}