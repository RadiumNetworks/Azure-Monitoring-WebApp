using System.Text.Json;

namespace MonitoringApp;

public interface IScimUserService
{
    Task<ScimUserResponse> GetAsync(ProvisioningSource source, Guid id, CancellationToken cancellationToken);
    Task<ScimListResponse<ScimUserResponse>> ListAsync(ProvisioningSource source, string? filter, int startIndex, int count, CancellationToken cancellationToken);
    Task<ScimUserResponse> CreateAsync(ProvisioningSource source, JsonElement body, CancellationToken cancellationToken);
    Task<ScimUserResponse> ReplaceAsync(ProvisioningSource source, Guid id, JsonElement body, string? version, CancellationToken cancellationToken);
    Task<ScimUserResponse> PatchAsync(ProvisioningSource source, Guid id, JsonElement body, string? version, CancellationToken cancellationToken);
    Task DeleteAsync(ProvisioningSource source, Guid id, string? version, CancellationToken cancellationToken);
}

public sealed class ScimUserService(IProvisioningUserStore store, ScimOptions options) : IScimUserService
{
    public async Task<ScimUserResponse> GetAsync(ProvisioningSource source, Guid id, CancellationToken cancellationToken) =>
        Response(await store.GetAsync(source, id, cancellationToken));

    public async Task<ScimListResponse<ScimUserResponse>> ListAsync(ProvisioningSource source, string? filter,
        int startIndex, int count, CancellationToken cancellationToken)
    {
        if (startIndex < 1 || count < 0)
            throw new ScimProtocolException(400, "startIndex must be at least 1; count must be non-negative.", "invalidValue");
        count = Math.Min(count, ScimUserProtocol.MaximumPageSize);
        var result = await store.ListAsync(source, ScimUserProtocol.Filter(filter), startIndex, count, cancellationToken);
        return new([ScimSchemas.List], result.TotalResults, startIndex, result.Users.Count, result.Users.Select(Response).ToArray());
    }

    public async Task<ScimUserResponse> CreateAsync(ProvisioningSource source, JsonElement body, CancellationToken cancellationToken) =>
        Response(await store.CreateAsync(source, ScimUserProtocol.Read(body), cancellationToken));

    public async Task<ScimUserResponse> ReplaceAsync(ProvisioningSource source, Guid id, JsonElement body,
        string? version, CancellationToken cancellationToken)
    {
        var profile = ScimUserProtocol.Read(body);
        var current = await store.GetAsync(source, id, cancellationToken);
        if (profile.ExternalId != current.Profile.ExternalId)
            throw new ScimProtocolException(400, "externalId cannot be changed.", "mutability");
        return Response(await store.ReplaceAsync(source, id, profile, version ?? current.Version, cancellationToken));
    }

    public async Task<ScimUserResponse> PatchAsync(ProvisioningSource source, Guid id, JsonElement body,
        string? version, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(source, id, cancellationToken);
        var profile = ScimUserProtocol.Patch(current.Profile, body);
        return Response(await store.ReplaceAsync(source, id, profile, version ?? current.Version, cancellationToken));
    }

    public Task DeleteAsync(ProvisioningSource source, Guid id, string? version, CancellationToken cancellationToken) =>
        store.DeleteAsync(source, id, version, cancellationToken);

    private ScimUserResponse Response(ProvisioningUser user) => new(
        [ScimSchemas.User], user.Id.ToString("D"), user.Profile.ExternalId.ToString("D"),
        user.Profile.UserName, user.Profile.DisplayName,
        new(user.Profile.GivenName, user.Profile.FamilyName),
        user.Profile.Email is { } email ? [new(email)] : [], user.Profile.Active,
        new("User", $"{options.BaseUrl.TrimEnd('/')}/Users/{user.Id:D}", user.Created, user.Modified, user.Version));
}
