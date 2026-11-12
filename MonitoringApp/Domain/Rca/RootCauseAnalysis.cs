namespace MonitoringApp;

public static class RcaStatuses
{
    public const string Open = "Open";
    public const string Closed = "Closed";
    public static readonly IReadOnlyList<string> All = [Open, "InAnalysis", "Actions", Closed];

    public static string Label(string value) => value == "InAnalysis" ? "In analysis" : value;
}

public sealed class RootCauseAnalysis
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset FollowUpDueAt { get; set; }
    public DateTimeOffset? CriticalEndedAt { get; set; }
    public bool IsActive { get; set; }
    public string EvidenceEventIdsJson { get; set; } = "[]";
    public string Analysis { get; set; } = string.Empty;
    public string AssignedTo { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped, System.Text.Json.Serialization.JsonIgnore]
    public string? AssigneeDisplayName { get; set; }
    public string Status { get; set; } = RcaStatuses.Open;
    public int Revision { get; set; }
}

public sealed class RcaPrompt
{
    public Guid Id { get; set; }
    public Guid RcaId { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string RawPrompt { get; set; } = string.Empty;
}

public sealed class RcaChange
{
    public Guid Id { get; set; }
    public Guid RcaId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped, System.Text.Json.Serialization.JsonIgnore]
    public string? ActorDisplayName { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped, System.Text.Json.Serialization.JsonIgnore]
    public string? BeforeDisplayName { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped, System.Text.Json.Serialization.JsonIgnore]
    public string? AfterDisplayName { get; set; }
}

public sealed class RcaAlertLink
{
    public Guid AlertEventId { get; set; }
    public Guid RcaId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
    public AlertRecord Alert { get; set; } = null!;
}
