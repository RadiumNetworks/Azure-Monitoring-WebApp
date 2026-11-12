using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

public sealed class ScimApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static async Task<ScimUserResponse> CreateAsync(ScimTestHost host, Guid? external = null, string name = "user@contoso.test")
    {
        using var response = await host.Client.PostAsJsonAsync("/scim/v2/Users", ScimProtocolTests.User(external, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<ScimUserResponse>(JsonOptions))!;
        Assert.Equal(user.Meta.Location, response.Headers.Location!.AbsoluteUri);
        Assert.Equal(user.Meta.Version, response.Headers.ETag!.ToString());
        return user;
    }

    [Theory]
    [InlineData("open")]
    [InlineData("sql")]
    [InlineData("entra")]
    public async Task InteractiveAccessNeverAuthorizesMachineProvisioning(string authentication)
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: authentication);
        host.Client.DefaultRequestHeaders.Authorization = null;
        host.Client.DefaultRequestHeaders.Add("X-Test-Interactive", "true");
        using var response = await host.Client.GetAsync("/scim/v2/Users");
        await ErrorAsync(response, 401);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(0, host.ContextsCreated);
    }

    [Theory]
    [InlineData(false, true, 404)]
    [InlineData(true, false, 403)]
    public async Task DisabledOrInsecureEndpointNeverTouchesTheDirectory(bool enabled, bool https, int status)
    {
        await using var host = await ScimTestHost.CreateAsync(enabled, https);
        using var response = await host.Client.GetAsync("/scim/v2/Users");
        await ErrorAsync(response, status);
        Assert.Equal(0, host.ContextsCreated);
    }

    [Fact]
    public async Task WrongTokensRotationDisabledConnectionsAndTenantMismatchFailClosed()
    {
        await using var host = await ScimTestHost.CreateAsync();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", new string('x', 64));
        using (var bad = await host.Client.GetAsync("/scim/v2/Users")) await ErrorAsync(bad, 401);
        Assert.Equal(0, host.ContextsCreated);
        var replacement = new string('r', 64);
        host.Options.TokenHashes = [ScimTestHost.Hash(host.Token), ScimTestHost.Hash(replacement)];
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", replacement);
        using (var rotated = await host.Client.GetAsync("/scim/v2/Users")) Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        host.Options.TokenHashes = [ScimTestHost.Hash(replacement)];
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token);
        using (var retired = await host.Client.GetAsync("/scim/v2/Users")) await ErrorAsync(retired, 401);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", replacement);
        var tenant = host.Options.TenantId;
        host.Options.TenantId = Guid.NewGuid();
        using (var wrongTenant = await host.Client.GetAsync("/scim/v2/Users")) await ErrorAsync(wrongTenant, 403);
        host.Options.TenantId = tenant;
        await using var context = host.CreateDbContext();
        var source = await context.ScimConnections.SingleAsync();
        source.IsEnabled = false;
        source.Revision++;
        await context.SaveChangesAsync();
        using var disabled = await host.Client.GetAsync("/scim/v2/Users");
        await ErrorAsync(disabled, 403);
        Assert.False((await context.ScimConnections.AsNoTracking().SingleAsync()).IsEnabled);
    }

    [Fact]
    public async Task FullHttpLifecycleIsAtomicVersionedAndPreservesApplicationOwnership()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var created = await CreateAsync(host);
        using (var loaded = await host.Client.GetAsync("/scim/v2/Users/" + created.Id))
            Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        using (var duplicate = await host.Client.PostAsJsonAsync("/scim/v2/Users",
            ScimProtocolTests.User(Guid.Parse(created.ExternalId)))) await ErrorAsync(duplicate, 409, "uniqueness");
        using (var duplicateName = await host.Client.PostAsJsonAsync("/scim/v2/Users",
            ScimProtocolTests.User(userName: "USER@CONTOSO.TEST"))) await ErrorAsync(duplicateName, 409, "uniqueness");
        var patch = ScimProtocolTests.Patch("""
            [{"op":"replace","path":"userName","value":"renamed@contoso.test"},
             {"op":"replace","path":"name.givenName","value":"Changed"},
             {"op":"remove","path":"name.familyName"},
             {"op":"replace","path":"emails[type eq \"work\"].value","value":"new@contoso.test"},
             {"op":"replace","path":"active","value":false}]
            """);
        using var changedResponse = await host.Client.PatchAsJsonAsync("/scim/v2/Users/" + created.Id, patch);
        Assert.Equal(HttpStatusCode.OK, changedResponse.StatusCode);
        var changed = (await changedResponse.Content.ReadFromJsonAsync<ScimUserResponse>())!;
        Assert.Equal(("renamed@contoso.test", "Changed", null, "new@contoso.test", false),
            (changed.UserName, changed.Name.GivenName, changed.Name.FamilyName, changed.Emails.Single().Value, changed.Active));
        Assert.NotEqual(created.Meta.Version, changed.Meta.Version);
        using (var stale = new HttpRequestMessage(HttpMethod.Patch, "/scim/v2/Users/" + created.Id)
        {
            Content = JsonContent.Create(ScimProtocolTests.Patch("""[{"op":"replace","path":"active","value":true}]"""))
        })
        {
            stale.Headers.TryAddWithoutValidation("If-Match", created.Meta.Version);
            using var rejected = await host.Client.SendAsync(stale);
            await ErrorAsync(rejected, 412);
        }
        using (var invalid = await host.Client.PatchAsJsonAsync("/scim/v2/Users/" + created.Id,
            ScimProtocolTests.Patch("""[{"op":"replace","path":"active","value":true},{"op":"replace","path":"roles","value":["Admin"]}]""")))
            await ErrorAsync(invalid, 400, "invalidPath");
        var unchanged = (await host.Client.GetFromJsonAsync<ScimUserResponse>("/scim/v2/Users/" + created.Id))!;
        Assert.Equal(changed.Meta.Version, unchanged.Meta.Version);
        Assert.False(unchanged.Active);
        await using (var context = host.CreateDbContext())
        {
            context.AnalysisDashboards.Add(new() { OwnerKey = "historical-owner", DocumentJson = """{"original":true}""", Revision = 7 });
            await context.SaveChangesAsync();
        }
        using (var deleted = await host.Client.DeleteAsync("/scim/v2/Users/" + created.Id))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var again = await host.Client.DeleteAsync("/scim/v2/Users/" + created.Id))
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        using (var absent = await host.Client.GetAsync("/scim/v2/Users/" + created.Id)) await ErrorAsync(absent, 404);
        var empty = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimUserResponse>>("/scim/v2/Users"))!;
        Assert.Equal(0, empty.TotalResults);
        var restored = await CreateAsync(host, Guid.Parse(created.ExternalId), "restored@contoso.test");
        Assert.Equal(created.Id, restored.Id);
        await using var verify = host.CreateDbContext();
        Assert.Equal(1, await verify.DirectoryUsers.CountAsync());
        Assert.Equal(1, await verify.EntraUserIdentities.CountAsync());
        Assert.Equal(1, await verify.ScimUserStates.CountAsync());
        Assert.Empty(await verify.AuthenticationUsers.ToArrayAsync());
        Assert.Empty(await verify.UserTeamMemberships.ToArrayAsync());
        var dashboard = await verify.AnalysisDashboards.SingleAsync();
        Assert.Equal(("historical-owner", """{"original":true}""", 7), (dashboard.OwnerKey, dashboard.DocumentJson, dashboard.Revision));
    }

    [Fact]
    public async Task ExistingEntraLoginIsEnrichedWithoutChangingUserIdLocalOverrideLockOrCutover()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var external = Guid.NewGuid();
        var directory = new IdentityDirectoryService(host, new() { Enabled = true }, new() { Type = "entra" });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new("tid", host.Options.TenantId.ToString()), new("oid", external.ToString()), new("preferred_username", "before@contoso.test"),
            new(ClaimTypes.Role, "Operator"), new(ClaimTypes.NameIdentifier, external.ToString())
        ], AppServiceAuthenticationHandler.SchemeName));
        var learned = await directory.ResolveAsync(principal);
        var id = DirectoryIdentity.UserId(learned)!.Value;
        await using (var context = host.CreateDbContext())
        {
            var user = (await context.DirectoryUsers.FindAsync(id))!;
            user.LocalDisplayName = "Local display";
            user.IsEnabled = false;
            user.UsesStableIdentity = true;
            user.Revision++;
            await context.SaveChangesAsync();
        }
        var provisioned = await CreateAsync(host, external);
        Assert.Equal(id.ToString("D"), provisioned.Id);
        Assert.Equal("Source display", provisioned.DisplayName);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ResolveAsync(principal));
        await using var verify = host.CreateDbContext();
        var stored = await verify.DirectoryUsers.SingleAsync();
        Assert.Equal("Local display", stored.LocalDisplayName);
        Assert.False(stored.IsEnabled);
        Assert.True(stored.UsesStableIdentity);
        Assert.Equal(DirectoryUserOrigins.EntraLogin, stored.Origin);
    }

    [Fact]
    public async Task PutIsFullReplacementAndCannotChangeTheStableExternalIdentity()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await CreateAsync(host);
        using (var wrong = await host.Client.PutAsJsonAsync("/scim/v2/Users/" + user.Id, ScimProtocolTests.User()))
            await ErrorAsync(wrong, 400, "mutability");
        using var replaced = await host.Client.PutAsJsonAsync("/scim/v2/Users/" + user.Id, new
        {
            schemas = new[] { ScimSchemas.User }, externalId = user.ExternalId, userName = "replacement@contoso.test"
        });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var result = (await replaced.Content.ReadFromJsonAsync<ScimUserResponse>())!;
        Assert.Empty(result.DisplayName);
        Assert.Null(result.Name.GivenName);
        Assert.Null(result.Name.FamilyName);
        Assert.Empty(result.Emails);
        Assert.True(result.Active);
    }

    [Fact]
    public async Task MatchingPaginationAndActualMaximumPageSizeAreCorrect()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var first = await CreateAsync(host);
        await using (var context = host.CreateDbContext())
        {
            for (var index = 0; index < 100; index++)
            {
                var user = new DirectoryUser { UserName = $"bulk-{index}@contoso.test", Origin = DirectoryUserOrigins.Scim };
                var external = Guid.NewGuid();
                context.DirectoryUsers.Add(user);
                context.EntraUserIdentities.Add(new() { UserId = user.Id, TenantId = host.Options.TenantId, ObjectId = external });
                context.ScimUserStates.Add(new()
                {
                    UserId = user.Id, TenantId = host.Options.TenantId, ExternalId = external, ConnectionId = host.Options.ConnectionId
                });
            }
            await context.SaveChangesAsync();
        }
        var capped = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimUserResponse>>("/scim/v2/Users?count=1000"))!;
        Assert.Equal((101, 100, 100), (capped.TotalResults, capped.ItemsPerPage, capped.Resources.Count));
        var page = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimUserResponse>>("/scim/v2/Users?startIndex=2&count=1"))!;
        Assert.Equal((101, 2, 1, 1), (page.TotalResults, page.StartIndex, page.ItemsPerPage, page.Resources.Count));
        var countOnly = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimUserResponse>>("/scim/v2/Users?count=0"))!;
        Assert.Equal(101, countOnly.TotalResults);
        Assert.Empty(countOnly.Resources);
        foreach (var filter in new[] { $"externalId eq \"{first.ExternalId}\"", "userName eq \"USER@CONTOSO.TEST\"" })
        {
            var matched = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimUserResponse>>(
                "/scim/v2/Users?filter=" + Uri.EscapeDataString(filter)))!;
            Assert.Equal(first.Id, Assert.Single(matched.Resources).Id);
        }
        var missing = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimUserResponse>>(
            "/scim/v2/Users?filter=" + Uri.EscapeDataString("userName eq \"unknown@contoso.test\"")))!;
        Assert.Equal(0, missing.TotalResults);
        Assert.Empty(missing.Resources);
    }

    [Theory]
    [InlineData("?startIndex=0")]
    [InlineData("?count=-1")]
    [InlineData("?count=1&count=2")]
    [InlineData("?sortBy=userName")]
    [InlineData("?filter=active%20eq%20true")]
    public async Task InvalidQueriesReturnScimErrorsNotUnfilteredData(string query)
    {
        await using var host = await ScimTestHost.CreateAsync();
        using var response = await host.Client.GetAsync("/scim/v2/Users" + query);
        await ErrorAsync(response, 400);
    }

    [Theory]
    [InlineData("{", "application/scim+json", 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task MalformedPayloadsUseScimErrorMediaType(string body, string contentType, int status)
    {
        await using var host = await ScimTestHost.CreateAsync();
        using var response = await host.Client.PostAsync("/scim/v2/Users", new StringContent(body, Encoding.UTF8, contentType));
        await ErrorAsync(response, status);
    }

    [Fact]
    public async Task OversizedPayloadAndUnknownResourcesAreRejectedAndDiscoveryIsHonest()
    {
        await using var host = await ScimTestHost.CreateAsync();
        using (var oversized = await host.Client.PostAsync("/scim/v2/Users",
            new StringContent(new string(' ', 65537), Encoding.UTF8, "application/scim+json"))) await ErrorAsync(oversized, 413);
        using (var chunked = new HttpRequestMessage(HttpMethod.Post, "/scim/v2/Users")
        {
            Content = new StringContent(new string(' ', 65537), Encoding.UTF8, "application/scim+json")
        })
        {
            chunked.Headers.TransferEncodingChunked = true;
            using var oversized = await host.Client.SendAsync(chunked);
            await ErrorAsync(oversized, 413);
        }
        using (var unknown = await host.Client.GetAsync("/scim/v2/Unsupported")) await ErrorAsync(unknown, 404);
        var config = (await host.Client.GetFromJsonAsync<ScimServiceProviderResponse>("/scim/v2/ServiceProviderConfig"))!;
        Assert.True(config.Patch.Supported);
        Assert.True(config.ETag.Supported);
        Assert.False(config.Bulk.Supported);
        Assert.False(config.Sort.Supported);
        var types = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimResourceTypeResponse>>("/scim/v2/ResourceTypes"))!;
        Assert.Equal(new[] { "Group", "User" }, types.Resources.Select(type => type.Name).Order().ToArray());
        Assert.Equal("User", (await host.Client.GetFromJsonAsync<ScimResourceTypeResponse>("/scim/v2/ResourceTypes/User"))!.Name);
        var schemas = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimSchemaResponse>>("/scim/v2/Schemas"))!;
        Assert.Equal(new[] { ScimSchemas.Group, ScimSchemas.User }, schemas.Resources.Select(schema => schema.Id).Order().ToArray());
        Assert.Equal(ScimSchemas.User, (await host.Client.GetFromJsonAsync<ScimSchemaResponse>("/scim/v2/Schemas/" + ScimSchemas.User))!.Id);
        using var openApi = await host.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/scim/v2/Users", out var users));
        Assert.True(users.TryGetProperty("post", out var post));
        Assert.True(post.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(paths.GetProperty("/scim/v2/Users/{id}").GetProperty("delete").GetProperty("responses").TryGetProperty("204", out _));
    }

    private static async Task ErrorAsync(HttpResponseMessage response, int status, string? type = null)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/scim+json", response.Content.Headers.ContentType!.MediaType);
        var error = (await response.Content.ReadFromJsonAsync<ScimErrorResponse>())!;
        Assert.Equal([ScimSchemas.Error], error.Schemas);
        Assert.Equal(status.ToString(), error.Status);
        if (type is not null) Assert.Equal(type, error.ScimType);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }
}
