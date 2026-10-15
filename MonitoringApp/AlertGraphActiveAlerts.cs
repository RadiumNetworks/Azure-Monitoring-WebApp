namespace MonitoringApp;

public static class AlertGraphActiveAlerts
{
    public static HashSet<Guid> ResolveIds(IEnumerable<AlertGraphRecord> records)
    {
        var snapshot = records as IReadOnlyCollection<AlertGraphRecord> ?? records.ToArray();

        return snapshot
            .Where(record => !string.IsNullOrWhiteSpace(record.AlertId))
            .GroupBy(
                record => $"{record.SubscriptionId}\u001f{record.AlertId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.ReceivedAt).First())
            .Concat(snapshot.Where(record => string.IsNullOrWhiteSpace(record.AlertId)))
            .Where(record =>
                record.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase) &&
                !System.Text.RegularExpressions.Regex.IsMatch(
                    record.Comments,
                    @"\bResolved\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .Select(record => record.Id)
            .ToHashSet();
    }
}
