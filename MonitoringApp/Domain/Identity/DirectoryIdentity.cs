using System.Security.Claims;

namespace MonitoringApp;

public sealed class IdentityDirectoryOptions
{
    public const string SectionName = "IdentityDirectory";
    public bool Enabled { get; set; }
}

public static class DirectoryIdentity
{
    public const string AuthenticationType = "MonitoringAppDirectory";
    public const string Issuer = "MonitoringApp.Directory";
    public const string UserIdClaim = "monitoringapp:user-id";
    public const string ActorClaim = "monitoringapp:actor";
    public const string JobAliasClaim = "monitoringapp:job-alias";

    public static string OwnerKey(Guid userId) => $"user:{userId:D}";

    public static bool TryReadUserId(string key, out Guid id)
    {
        id = default;
        return key.StartsWith("user:", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(key[5..], "D", out id) && id != Guid.Empty;
    }

    public static string? Actor(ClaimsPrincipal principal) => Values(principal, ActorClaim).SingleOrDefault();

    public static Guid? UserId(ClaimsPrincipal principal) =>
        Guid.TryParse(Values(principal, UserIdClaim).SingleOrDefault(), out var id) ? id : null;

    public static IEnumerable<string> JobActors(ClaimsPrincipal principal) =>
        Values(principal, ActorClaim).Concat(Values(principal, JobAliasClaim)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Values(ClaimsPrincipal principal, string type) =>
        principal.Identities.Where(identity => identity.AuthenticationType == AuthenticationType)
            .SelectMany(identity => identity.FindAll(type)).Where(claim => claim.Issuer == Issuer)
            .Select(claim => claim.Value);
}

public sealed record DirectoryAccount(
    Guid Id, string UserName, string DisplayName, string Provider,
    Guid? TenantId, Guid? ObjectId, bool IsEnabled, bool UsesStableIdentity);

public sealed record IdentityMigrationPreview(
    Guid UserId, string Fingerprint, IReadOnlyList<string> Aliases,
    int DashboardCollections, int Dashboards, int InboxLayouts, int Publications,
    int Jobs, IReadOnlyList<string> Conflicts);

public sealed class IdentityMigrationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ApprovedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string VerificationNote { get; set; } = string.Empty;
    public string OriginalStateJson { get; set; } = string.Empty;
}

public sealed class IdentityMigrationException(string resourceKey) : InvalidOperationException(resourceKey)
{
    public string ResourceKey { get; } = resourceKey;
}
