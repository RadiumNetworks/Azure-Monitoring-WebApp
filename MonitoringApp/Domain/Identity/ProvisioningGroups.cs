namespace MonitoringApp;

public sealed record ProvisioningGroupProfile(Guid ExternalId, string DisplayName, IReadOnlyList<Guid> Members);
public sealed record ProvisioningGroup(Guid Id, ProvisioningGroupProfile Profile, DateTimeOffset? Modified, int Revision)
{
    public string Version => $"W/\"{Revision}\"";
}
public sealed record ProvisioningGroupPage(int TotalResults, IReadOnlyList<ProvisioningGroup> Groups);

public interface IProvisioningGroupStore
{
    Task<ProvisioningGroup> GetAsync(ProvisioningSource source, Guid id, CancellationToken cancellationToken);
    Task<ProvisioningGroupPage> ListAsync(ProvisioningSource source, ProvisioningFilter? filter, int startIndex, int count, CancellationToken cancellationToken);
    Task<ProvisioningGroup> CreateAsync(ProvisioningSource source, ProvisioningGroupProfile profile, CancellationToken cancellationToken);
    Task<ProvisioningGroup> ReplaceAsync(ProvisioningSource source, Guid id, ProvisioningGroupProfile profile, string version, CancellationToken cancellationToken);
    Task DeleteAsync(ProvisioningSource source, Guid id, string? version, CancellationToken cancellationToken);
}
