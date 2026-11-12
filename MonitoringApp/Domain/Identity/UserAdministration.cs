namespace MonitoringApp;

public sealed record UserTeamAssignment(
    Guid TeamId, string TeamName, bool Manual, bool Effective, string? GroupName, string? ConnectionName);
public sealed record UserAdministrationUser(
    string Key, Guid? Id, string UserName, string DisplayName, string? LocalDisplayName,
    string? GivenName, string? FamilyName, string? Email, string Origin,
    string? SqlUsername, string? SqlRole, Guid? TenantId, Guid? ObjectId,
    bool IsEnabled, bool UsesStableIdentity, bool? ScimActive, bool ScimDeleted,
    string? SyncStatus, DateTimeOffset? LastSyncedAt, string? ConnectionName,
    string Version, IReadOnlyList<UserTeamAssignment> TeamSources)
{
    public bool IsScim => SyncStatus is not null;
    public bool IsHistorical { get; init; }
    public string? HistoricalMainKey { get; init; }
    public bool HistoricalMainAvailable { get; init; }
    public IReadOnlyList<UserAdministrationHistoricalAlias> HistoricalAliases { get; init; } = [];
    public bool CanManageSql => SqlUsername is not null && TenantId is null && !IsScim;
    public bool CanEditProfile => CanManageSql || IsHistorical && HistoricalMainKey is null;
    public bool EffectiveEnabled => IsEnabled && ScimActive != false && !ScimDeleted;
    public bool Unassigned => !TeamSources.Any(source => source.Effective);
    public string Provider => IsScim ? "Scim" : TenantId is not null ? "Entra" : SqlUsername is not null ? "Sql" : "Legacy";
    public string EffectiveDisplayName => new[] { LocalDisplayName, DisplayName,
        string.Join(" ", new[] { GivenName, FamilyName }.Where(value => !string.IsNullOrWhiteSpace(value))), UserName }
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? Key;
}
public sealed record UserAdministrationHistoricalAlias(string Key, string UserName, string DisplayName, bool IsAvailable);
public sealed record UserAdministrationTeam(Guid Id, string Name, bool IsEnabled, int Revision)
{
    public IReadOnlyList<string> InventoryRoleKeys { get; init; } = [];
}
public sealed record UserAdministrationInventoryRole(string Key, string Name, bool IsObserved);
public sealed record UserAdministrationSnapshot(
    IReadOnlyList<UserAdministrationUser> Users, IReadOnlyList<UserAdministrationTeam> Teams)
{
    public IReadOnlyList<UserAdministrationGroup> Groups { get; init; } = [];
    public IReadOnlyList<UserAdministrationMapping> Mappings { get; init; } = [];
    public IReadOnlyList<UserAdministrationInventoryRole> InventoryRoles { get; init; } = [];
}
public sealed record UserAdministrationGroup(Guid Id, Guid ConnectionId, string ConnectionName, Guid TenantId,
    Guid ExternalId, string Name, bool IsActive, bool IsDeleted, string SyncStatus, DateTimeOffset? LastSyncedAt);
public sealed record UserAdministrationMapping(Guid Id, Guid TeamId, Guid GroupId, bool IsEnabled, int Revision);
public sealed record SqlUserProfile(string DisplayName, string? GivenName, string? FamilyName, string? Email);
public sealed record UserAdministrationEdit(
    string Key, string Version, string? LocalDisplayName, bool IsEnabled, IReadOnlyList<Guid> ManualTeamIds,
    SqlUserProfile? Profile = null, string? SqlRole = null, string? NewPassword = null,
    IReadOnlyList<string>? HistoricalAliasKeys = null);

public sealed class UserAdministrationException(string resourceKey) : InvalidOperationException(resourceKey)
{
    public string ResourceKey { get; } = resourceKey;
}

public sealed class IdentityAdministrationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorUserId { get; set; }
    public bool IsOpenAuthentication { get; set; }
    public Guid? TargetUserId { get; set; }
    public Guid? TargetTeamId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string BeforeJson { get; set; } = "null";
    public string AfterJson { get; set; } = "null";
}
