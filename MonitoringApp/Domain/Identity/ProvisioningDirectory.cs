namespace MonitoringApp;

public static class ProvisioningSyncStatuses
{
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

public sealed class ScimConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public bool IsEnabled { get; set; }
    public int Revision { get; set; } = 1;
}

public sealed class ScimUserState
{
    public Guid UserId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ExternalId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public string SyncStatus { get; set; } = ProvisioningSyncStatuses.Pending;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int Revision { get; set; } = 1;
}

public sealed class ScimGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public Guid ExternalId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public string SyncStatus { get; set; } = ProvisioningSyncStatuses.Pending;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int Revision { get; set; } = 1;
}

public sealed class ScimGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConnectionId { get; set; }
    public bool IsDeleted { get; set; }
}
