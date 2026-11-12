namespace MonitoringApp;

public static class DirectoryUserOrigins
{
    public const string Sql = "Sql";
    public const string EntraLogin = "EntraLogin";
    public const string Scim = "Scim";
    public const string Legacy = "Legacy";
}

public sealed class DirectoryUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? LocalDisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Email { get; set; }
    public string Origin { get; set; } = DirectoryUserOrigins.Legacy;
    public bool IsEnabled { get; set; } = true;
    public bool UsesStableIdentity { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SqlUserIdentity
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
}

public sealed class EntraUserIdentity
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ObjectId { get; set; }
}

public sealed class HistoricalUserAlias
{
    public Guid AliasUserId { get; set; }
    public Guid MainUserId { get; set; }
}
