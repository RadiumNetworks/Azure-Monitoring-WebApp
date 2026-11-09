namespace MonitoringApp;

internal static class RcaAssignmentCandidates
{
    internal static RcaAlertCandidate[] Build(
        AlertRecord[] alerts, RootCauseAnalysis[] cases, RcaAlertLink[] links, DateTimeOffset now)
    {
        var alertsById = alerts.ToDictionary(alert => alert.Id);
        var casesById = cases.ToDictionary(rca => rca.Id);
        var owners = new Dictionary<string, RootCauseAnalysis>();
        foreach (var link in links)
        {
            if (!alertsById.TryGetValue(link.AlertEventId, out var alert))
                continue;
            var key = RcaEvidence.LifecycleKey(alert);
            if (owners.TryGetValue(key, out var owner) && owner.Id != link.RcaId)
                throw new InvalidOperationException($"Alert lifecycle {alert.Id} belongs to conflicting RCAs.");
            owners[key] = casesById[link.RcaId];
        }
        foreach (var rca in cases.OrderBy(rca => rca.CreatedAt).ThenBy(rca => rca.Id))
            foreach (var eventId in RcaEvidence.Read(rca.Id, rca.EvidenceEventIdsJson))
                if (alertsById.TryGetValue(eventId, out var alert))
                    owners.TryAdd(RcaEvidence.LifecycleKey(alert), rca);
        var activeIds = AlertLifecycle.GetActiveAlerts(alerts).Select(alert => alert.Id).ToHashSet();
        var cutoff = now.AddHours(-24);
        return alerts.GroupBy(RcaEvidence.LifecycleKey)
            .Select(group => group.OrderByDescending(alert => alert.ReceivedAt).First())
            .Where(alert => activeIds.Contains(alert.Id) || alert.ReceivedAt >= cutoff)
            .OrderByDescending(alert => alert.ReceivedAt)
            .Select(alert =>
            {
                owners.TryGetValue(RcaEvidence.LifecycleKey(alert), out var owner);
                return new RcaAlertCandidate(alert, activeIds.Contains(alert.Id), owner?.Id, owner?.CategoryName, owner?.Revision);
            }).ToArray();
    }
}
