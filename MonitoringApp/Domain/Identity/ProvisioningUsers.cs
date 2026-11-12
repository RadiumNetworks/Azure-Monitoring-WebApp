namespace MonitoringApp;

public sealed record ProvisioningSource(Guid ConnectionId, Guid TenantId, string Name);
public sealed record ProvisioningProfile(
    Guid ExternalId, string UserName, string DisplayName, string? GivenName, string? FamilyName, string? Email, bool Active);
public sealed record ProvisioningUser(
    Guid Id, ProvisioningProfile Profile, DateTimeOffset Created, DateTimeOffset Modified, int Revision, int SyncRevision)
{
    public string Version => $"W/\"{Revision}-{SyncRevision}\"";
}
public sealed record ProvisioningFilter(string Attribute, string Value);
public sealed record ProvisioningPage(int TotalResults, IReadOnlyList<ProvisioningUser> Users);

public enum ProvisioningError { NotFound, Conflict, Disabled, SourceMismatch, Version, Unavailable, InvalidMember }

public sealed class ProvisioningException(ProvisioningError code, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public ProvisioningError Code { get; } = code;
}

public interface IProvisioningUserStore
{
    Task EnsureSourceAsync(ProvisioningSource source, CancellationToken cancellationToken);
    Task<ProvisioningUser> GetAsync(ProvisioningSource source, Guid id, CancellationToken cancellationToken);
    Task<ProvisioningPage> ListAsync(ProvisioningSource source, ProvisioningFilter? filter, int startIndex, int count, CancellationToken cancellationToken);
    Task<ProvisioningUser> CreateAsync(ProvisioningSource source, ProvisioningProfile profile, CancellationToken cancellationToken);
    Task<ProvisioningUser> ReplaceAsync(ProvisioningSource source, Guid id, ProvisioningProfile profile, string? expectedVersion, CancellationToken cancellationToken);
    Task DeleteAsync(ProvisioningSource source, Guid id, string? expectedVersion, CancellationToken cancellationToken);
}
