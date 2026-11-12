namespace MonitoringApp;

public sealed record OpenRcaNotice(
    Guid Id, string CategoryName, IReadOnlyList<string> Domains, string Status, string AssignedTo)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AssigneeDisplayName { get; init; }
    public string StatusLabel => RcaStatuses.Label(Status);
    public string AssigneeLabel => string.IsNullOrWhiteSpace(AssignedTo) ? "Unassigned" : AssigneeDisplayName ?? AssignedTo;
    public string Label => Domains.Count == 0
        ? $"Open RCA {CategoryName}"
        : $"Open RCA {CategoryName} {string.Join(", ", Domains)}";
}

public sealed record RcaAlertSelection(Guid AlertEventId, Guid? ExpectedRcaId, int? ExpectedRcaRevision);

public sealed record RcaAlertCandidate(AlertRecord Alert, bool IsActive, Guid? RcaId, string? RcaName, int? RcaRevision)
{
    public RcaAlertSelection Selection => new(Alert.Id, RcaId, RcaRevision);
}
