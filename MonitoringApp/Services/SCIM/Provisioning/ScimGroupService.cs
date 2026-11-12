using System.Text.Json;

namespace MonitoringApp;

public interface IScimGroupService
{
    Task<ScimGroupResponse> GetAsync(ProvisioningSource source, Guid id, CancellationToken token);
    Task<ScimListResponse<ScimGroupResponse>> ListAsync(ProvisioningSource source, string? filter, int startIndex, int count, CancellationToken token);
    Task<ScimGroupResponse> CreateAsync(ProvisioningSource source, JsonElement body, CancellationToken token);
    Task<ScimGroupResponse> ReplaceAsync(ProvisioningSource source, Guid id, JsonElement body, string? version, CancellationToken token);
    Task<ScimGroupResponse> PatchAsync(ProvisioningSource source, Guid id, JsonElement body, string? version, CancellationToken token);
    Task DeleteAsync(ProvisioningSource source, Guid id, string? version, CancellationToken token);
}

public sealed class ScimGroupService(IProvisioningGroupStore store, ScimOptions options) : IScimGroupService
{
    public async Task<ScimGroupResponse> GetAsync(ProvisioningSource source, Guid id, CancellationToken token) =>
        Response(await store.GetAsync(source, id, token));
    public async Task<ScimListResponse<ScimGroupResponse>> ListAsync(ProvisioningSource source, string? filter, int startIndex, int count, CancellationToken token)
    {
        if (startIndex < 1 || count < 0) throw new ScimProtocolException(400, "startIndex must be at least 1; count must be non-negative.", "invalidValue");
        var result = await store.ListAsync(source, ScimGroupProtocol.Filter(filter), startIndex, Math.Min(count, ScimUserProtocol.MaximumPageSize), token);
        return new([ScimSchemas.List], result.TotalResults, startIndex, result.Groups.Count, result.Groups.Select(Response).ToArray());
    }
    public async Task<ScimGroupResponse> CreateAsync(ProvisioningSource source, JsonElement body, CancellationToken token) =>
        Response(await store.CreateAsync(source, ScimGroupProtocol.Read(body), token));
    public async Task<ScimGroupResponse> ReplaceAsync(ProvisioningSource source, Guid id, JsonElement body, string? version, CancellationToken token)
    {
        var profile = ScimGroupProtocol.Read(body);
        var current = await store.GetAsync(source, id, token);
        if (profile.ExternalId != current.Profile.ExternalId) throw new ScimProtocolException(400, "externalId cannot be changed.", "mutability");
        return Response(await store.ReplaceAsync(source, id, profile, version ?? current.Version, token));
    }
    public async Task<ScimGroupResponse> PatchAsync(ProvisioningSource source, Guid id, JsonElement body, string? version, CancellationToken token)
    {
        var current = await store.GetAsync(source, id, token);
        return Response(await store.ReplaceAsync(source, id, ScimGroupProtocol.Patch(current.Profile, body), version ?? current.Version, token));
    }
    public Task DeleteAsync(ProvisioningSource source, Guid id, string? version, CancellationToken token) => store.DeleteAsync(source, id, version, token);
    private ScimGroupResponse Response(ProvisioningGroup group) => new([ScimSchemas.Group], group.Id.ToString("D"),
        group.Profile.ExternalId.ToString("D"), group.Profile.DisplayName,
        group.Profile.Members.Select(id => new ScimGroupMemberResponse(id.ToString("D"))).ToArray(),
        new("Group", $"{options.BaseUrl.TrimEnd('/')}/Groups/{group.Id:D}", LastModified: group.Modified, Version: group.Version));
}
