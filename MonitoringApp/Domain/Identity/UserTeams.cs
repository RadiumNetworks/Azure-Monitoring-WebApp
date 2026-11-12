namespace MonitoringApp;

public sealed class UserTeam
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int Revision { get; set; } = 1;
}

public sealed class UserTeamGroupMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public Guid GroupId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Revision { get; set; } = 1;
}

public sealed class UserTeamInventoryRole
{
    public Guid TeamId { get; set; }
    public string RoleKey { get; set; } = "";

    public static string NormalizeRoleKey(string role) => role.Trim().ToUpperInvariant();
}

public sealed record InventoryTeamOption(Guid Id, string Name, IReadOnlyList<string> RoleKeys);

public sealed class UserTeamMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TeamId { get; set; }
    public Guid? GroupMappingId { get; set; }
    public Guid? SourceGroupId { get; set; }
}
