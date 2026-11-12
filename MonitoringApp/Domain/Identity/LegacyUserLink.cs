namespace MonitoringApp;

public static class LegacyUserLinkStatuses
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Conflict = "Conflict";
    public const string Rejected = "Rejected";
}

public sealed class LegacyUserLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Alias { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public Guid? TargetUserId { get; set; }
    public string Status { get; set; } = LegacyUserLinkStatuses.Pending;
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? VerificationNote { get; set; }
    public int Revision { get; set; } = 1;
}
