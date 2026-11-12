using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class ScimGroupTests
{
    private static readonly Guid First = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("10000000-0000-0000-0000-000000000002");
    internal static JsonElement Body(Guid external, string name, params string[] members) => JsonSerializer.SerializeToElement(new
    {
        schemas = new[] { ScimSchemas.Group }, externalId = external, displayName = name,
        members = members.Select(value => new { value, type = "User" })
    });
    internal static async Task<ScimUserResponse> UserAsync(ScimTestHost host)
    {
        using var response = await host.Client.PostAsJsonAsync("/scim/v2/Users", ScimProtocolTests.User(Guid.NewGuid(), Guid.NewGuid() + "@contoso.test"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScimUserResponse>())!;
    }
    internal static async Task<ScimGroupResponse> GroupAsync(ScimTestHost host, string name, params string[] members)
    {
        using var response = await host.Client.PostAsJsonAsync("/scim/v2/Groups", Body(Guid.NewGuid(), name, members));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var group = (await response.Content.ReadFromJsonAsync<ScimGroupResponse>())!;
        Assert.Equal(group.Meta.Location, response.Headers.Location!.AbsoluteUri);
        Assert.Equal(group.Meta.Version, response.Headers.ETag!.ToString());
        return group;
    }
    private static JsonElement Patch(string operations) => ScimProtocolTests.Patch(operations);
    internal static ClaimsPrincipal Admin(ScimTestHost host) => new(new ClaimsIdentity([
        new("tid", host.Options.TenantId.ToString()), new("oid", "20000000-0000-0000-0000-000000000001"),
        new("preferred_username", "group-admin@example.test"), new("name", "Group administrator"),
        new(ClaimTypes.Name, "Group administrator"), new(ClaimTypes.Role, "Admin")
    ], AppServiceAuthenticationHandler.SchemeName));
    internal static UserAdministrationService Administration(ScimTestHost host) => new(host,
        new(host, new() { Enabled = true }, new() { Type = "entra" }), new() { Type = "entra" },
        new(host, new SqlPasswordHasher(), new() { Enabled = true }), NullLogger<UserAdministrationService>.Instance);

    [Theory]
    [InlineData("""[{"op":"add","path":"members","value":[{"value":"10000000-0000-0000-0000-000000000002"}]}]""", 2)]
    [InlineData("""[{"op":"add","value":{"members":[{"value":"10000000-0000-0000-0000-000000000001"}]}}]""", 1)]
    [InlineData("""[{"op":"remove","path":"members[value eq \"10000000-0000-0000-0000-000000000001\"]"}]""", 0)]
    [InlineData("""[{"op":"Remove","path":"members","value":[{"value":"10000000-0000-0000-0000-000000000001"}]}]""", 0)]
    [InlineData("""[{"op":"remove","path":"members"}]""", 0)]
    [InlineData("""[{"op":"replace","path":"members","value":[]}]""", 0)]
    [InlineData("""[{"op":"replace","value":{"displayName":"Renamed"}}]""", 1)]
    [InlineData("""[{"op":"add","path":"urn:ietf:params:scim:schemas:core:2.0:Group:members","value":[{"value":"10000000-0000-0000-0000-000000000002"}]}]""", 2)]
    public void ProtocolSupportsEntraMemberOperationsWithoutReplacingUnmentionedMembers(string operations, int count)
    {
        var original = new ProvisioningGroupProfile(Guid.NewGuid(), "Original", [First]);
        var result = ScimGroupProtocol.Patch(original, Patch(operations));
        Assert.Equal(count, result.Members.Count);
        Assert.Equal([First], original.Members);
        Assert.Equal(original.ExternalId, result.ExternalId);
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"externalId","value":"10000000-0000-0000-0000-000000000002"}]""")]
    [InlineData("""[{"op":"remove","path":"displayName"}]""")]
    [InlineData("""[{"op":"add","path":"members","value":[{"value":"10000000-0000-0000-0000-000000000002","type":"Group"}]}]""")]
    [InlineData("""[{"op":"replace","path":"active","value":false}]""")]
    [InlineData("""[{"op":"add","path":"members","value":[{"value":"invalid"}]}]""")]
    [InlineData("""[{"op":"add","path":"members","value":[{"value":"10000000-0000-0000-0000-000000000002","VALUE":"10000000-0000-0000-0000-000000000001"}]}]""")]
    [InlineData("""[{"op":"replace","path":"members[type eq \"Group\"]","value":[]}]""")]
    public void InvalidNestedOrAmbiguousOperationsAreRejected(string operations)
    {
        Assert.Throws<ScimProtocolException>(() => ScimGroupProtocol.Patch(new(Guid.NewGuid(), "Original", [First]), Patch(operations)));
    }

    [Theory]
    [InlineData("displayName co \"x\"")]
    [InlineData("userName eq \"x\"")]
    [InlineData("externalId eq \"not-a-uuid\"")]
    [InlineData("displayName eq \"x\" or displayName eq \"y\"")]
    public void UnsupportedFiltersNeverFallBackToListingAllGroups(string filter) =>
        Assert.Equal("invalidFilter", Assert.Throws<ScimProtocolException>(() => ScimGroupProtocol.Filter(filter)).ScimType);

    [Theory]
    [InlineData(false, true, true, 404)]
    [InlineData(true, false, true, 403)]
    [InlineData(true, true, false, 401)]
    public async Task GroupsRequireTheirOwnMachineAuthenticationBeforeAnyDirectoryAccess(bool enabled, bool https, bool bearer, int status)
    {
        await using var host = await ScimTestHost.CreateAsync(enabled, https);
        if (!bearer)
        {
            host.Client.DefaultRequestHeaders.Authorization = null;
            host.Client.DefaultRequestHeaders.Add("X-Test-Interactive", "true");
        }
        using var response = await host.Client.GetAsync("/scim/v2/Groups");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, host.ContextsCreated);
    }

    [Fact]
    public async Task DiscoveryMatchingPaginationAndDuplicateIdentityHaveAccurateOutputShapes()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var group = await GroupAsync(host, "Same display", user.Id, user.Id);
        Assert.Single(group.Members);
        await GroupAsync(host, "Same display");
        using (var duplicate = await host.Client.PostAsJsonAsync("/scim/v2/Groups", Body(Guid.Parse(group.ExternalId), "Different")))
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var exact = await host.Client.GetFromJsonAsync<ScimListResponse<ScimGroupResponse>>(
            "/scim/v2/Groups?filter=" + Uri.EscapeDataString($"externalId eq \"{group.ExternalId}\""));
        Assert.Equal(group.Id, Assert.Single(exact!.Resources).Id);
        var count = await host.Client.GetFromJsonAsync<ScimListResponse<ScimGroupResponse>>("/scim/v2/Groups?count=0&startIndex=2");
        Assert.Equal(2, count!.TotalResults);
        Assert.Equal(2, count.StartIndex);
        Assert.Equal(0, count.ItemsPerPage);
        Assert.Empty(count.Resources);
        var names = await host.Client.GetFromJsonAsync<ScimListResponse<ScimGroupResponse>>(
            "/scim/v2/Groups?filter=" + Uri.EscapeDataString("displayName eq \"same display\"") + "&count=1");
        Assert.Equal(2, names!.TotalResults);
        Assert.Single(names.Resources);
        Assert.Equal(ScimSchemas.Group, (await host.Client.GetFromJsonAsync<ScimResourceTypeResponse>("/scim/v2/ResourceTypes/Group"))!.Schema);
        var schema = await host.Client.GetFromJsonAsync<ScimSchemaResponse>("/scim/v2/Schemas/" + ScimSchemas.Group);
        Assert.Contains(schema!.Attributes, attribute => attribute.Name == "members" && attribute.MultiValued);
        await using var context = host.CreateDbContext();
        Assert.Empty(await context.UserTeams.ToArrayAsync());
        Assert.Empty(await context.UserTeamGroupMappings.ToArrayAsync());
        Assert.Empty(await context.UserTeamMemberships.ToArrayAsync());
    }

    [Fact]
    public async Task GroupPaginationCapsAtExactlyOneHundredAndCanRetrieveTheRemainingResources()
    {
        await using var host = await ScimTestHost.CreateAsync();
        await GroupAsync(host, "First");
        await using (var context = host.CreateDbContext())
        {
            var source = await context.ScimConnections.SingleAsync();
            context.ScimGroups.AddRange(Enumerable.Range(1, 106).Select(index => new ScimGroup
            {
                ConnectionId = source.Id, ExternalId = Guid.NewGuid(), DisplayName = "Group " + index
            }));
            await context.SaveChangesAsync();
        }
        var first = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimGroupResponse>>("/scim/v2/Groups?count=1000"))!;
        var remaining = (await host.Client.GetFromJsonAsync<ScimListResponse<ScimGroupResponse>>("/scim/v2/Groups?count=1000&startIndex=101"))!;
        Assert.Equal(107, first.TotalResults);
        Assert.Equal(100, first.ItemsPerPage);
        Assert.Equal(100, first.Resources.Count);
        Assert.Equal(107, remaining.TotalResults);
        Assert.Equal(101, remaining.StartIndex);
        Assert.Equal(7, remaining.ItemsPerPage);
        Assert.Equal(7, remaining.Resources.Count);
        Assert.Equal(107, first.Resources.Concat(remaining.Resources).Select(group => group.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("duplicate", "DuplicateMapping")]
    [InlineData("pair", "MappingIdentity")]
    [InlineData("disabled-team", "MappingSource")]
    [InlineData("inactive-group", "MappingSource")]
    [InlineData("deleted-group", "MappingSource")]
    public async Task InvalidMappingChangesPreserveConfigurationProjectionAndAudit(string kind, string error)
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var group = await GroupAsync(host, "Source", user.Id);
        var service = Administration(host);
        var actor = Admin(host);
        await service.SaveTeamAsync(actor, null, null, "Operations", true);
        var team = (await service.GetSnapshotAsync(actor)).Teams.Single();
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), false);
        var mapping = (await service.GetSnapshotAsync(actor)).Mappings.Single();
        var targetTeam = team.Id;
        if (kind == "pair")
        {
            await service.SaveTeamAsync(actor, null, null, "Another team", true);
            targetTeam = (await service.GetSnapshotAsync(actor)).Teams.Single(team => team.Name == "Another team").Id;
        }
        await using var context = host.CreateDbContext();
        if (kind == "disabled-team") (await context.UserTeams.SingleAsync(team => team.Id == targetTeam)).IsEnabled = false;
        if (kind == "inactive-group") (await context.ScimGroups.SingleAsync()).IsActive = false;
        if (kind == "deleted-group") (await context.ScimGroups.SingleAsync()).IsDeleted = true;
        await context.SaveChangesAsync();
        var auditCount = await context.IdentityAdministrationRecords.CountAsync();
        var exception = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            service.SaveGroupMappingAsync(actor, kind == "duplicate" ? null : mapping.Id,
                kind == "duplicate" ? null : mapping.Revision, targetTeam, mapping.GroupId, true));
        Assert.Equal("UserDirectory.Errors." + error, exception.ResourceKey);
        var unchanged = (await service.GetSnapshotAsync(actor)).Mappings.Single();
        Assert.Equal(mapping, unchanged);
        Assert.True((await service.GetSnapshotAsync(actor)).Users.Single(account => account.Id == Guid.Parse(user.Id)).Unassigned);
        Assert.Empty(await context.UserTeamMemberships.ToArrayAsync());
        Assert.Equal(auditCount, await context.IdentityAdministrationRecords.CountAsync());
    }

    [Fact]
    public async Task FullGroupReplacementWithOmittedMembersClearsOnlyThisGroupsDerivedSources()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var group = await GroupAsync(host, "Original", user.Id);
        var service = Administration(host);
        var actor = Admin(host);
        await service.SaveTeamAsync(actor, null, null, "Operations", true);
        var snapshot = await service.GetSnapshotAsync(actor);
        var team = snapshot.Teams.Single();
        var account = snapshot.Users.Single(account => account.Id == Guid.Parse(user.Id));
        await service.SaveUserAsync(actor, new(account.Key, account.Version, null, true, [team.Id]));
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true);
        using var replaced = await host.Client.PutAsJsonAsync("/scim/v2/Groups/" + group.Id, new
        {
            schemas = new[] { ScimSchemas.Group }, externalId = group.ExternalId, displayName = "Replaced"
        });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var updated = (await replaced.Content.ReadFromJsonAsync<ScimGroupResponse>())!;
        Assert.Equal("Replaced", updated.DisplayName);
        Assert.Empty(updated.Members);
        Assert.NotEqual(group.Meta.Version, updated.Meta.Version);
        Assert.True(Assert.Single((await service.GetSnapshotAsync(actor)).Users.Single(account => account.Id == Guid.Parse(user.Id)).TeamSources).Manual);
        await using var context = host.CreateDbContext();
        Assert.True((await context.ScimGroupMembers.SingleAsync()).IsDeleted);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("sql")]
    [InlineData("known-entra")]
    [InlineData("foreign")]
    [InlineData("deleted")]
    public async Task MembersMustBeProvisionedUsersInExactlyThisSource(string kind)
    {
        await using var host = await ScimTestHost.CreateAsync();
        var group = await GroupAsync(host, "Original");
        var memberId = Guid.NewGuid();
        if (kind == "deleted")
        {
            var user = await UserAsync(host);
            memberId = Guid.Parse(user.Id);
            using var deleted = await host.Client.DeleteAsync("/scim/v2/Users/" + user.Id);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        if (kind is "sql" or "known-entra")
        {
            await using var context = host.CreateDbContext();
            context.DirectoryUsers.Add(new() { Id = memberId, Origin = kind == "sql" ? "Sql" : "EntraLogin" });
            if (kind == "sql")
            {
                context.AuthenticationUsers.Add(new() { Username = "same@example.test", Role = "Admin", PasswordHash = "synthetic" });
                context.SqlUserIdentities.Add(new() { UserId = memberId, Username = "same@example.test" });
            }
            else context.EntraUserIdentities.Add(new() { UserId = memberId, TenantId = host.Options.TenantId, ObjectId = Guid.NewGuid() });
            await context.SaveChangesAsync();
        }
        if (kind == "foreign")
        {
            var source = new ProvisioningSource(Guid.NewGuid(), Guid.NewGuid(), "Foreign source");
            var store = new ProvisioningUserStore(host, NullLogger<ProvisioningUserStore>.Instance);
            await store.EnsureSourceAsync(source, default);
            var user = await store.CreateAsync(source, new(Guid.NewGuid(), "same@example.test", "Foreign", null, null, null, true), default);
            memberId = user.Id;
        }
        using var response = await host.Client.PutAsJsonAsync("/scim/v2/Groups/" + group.Id, Body(Guid.Parse(group.ExternalId), "Must roll back", memberId.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalidValue", (await response.Content.ReadFromJsonAsync<ScimErrorResponse>())!.ScimType);
        var unchanged = (await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + group.Id))!;
        Assert.Equal("Original", unchanged.DisplayName);
        Assert.Equal(group.Meta.Version, unchanged.Meta.Version);
        Assert.Empty(unchanged.Members);
    }

    [Fact]
    public async Task IncrementalChangesAndMappingTogglesPreserveManualAndOtherGroupSources()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var otherUser = await UserAsync(host);
        var first = await GroupAsync(host, "First group", user.Id);
        var second = await GroupAsync(host, "Second group", user.Id);
        var service = Administration(host);
        var actor = Admin(host);
        await service.SaveTeamAsync(actor, null, null, "Operations", true);
        var snapshot = await service.GetSnapshotAsync(actor);
        var team = snapshot.Teams.Single();
        var account = snapshot.Users.Single(entry => entry.Id == Guid.Parse(user.Id));
        await service.SaveUserAsync(actor, new(account.Key, account.Version, null, true, [team.Id]));
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(first.Id), true);
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(second.Id), true);
        snapshot = await service.GetSnapshotAsync(actor);
        Assert.Equal(3, snapshot.Users.Single(entry => entry.Id == account.Id).TeamSources.Count);
        using (var added = await host.Client.PatchAsJsonAsync("/scim/v2/Groups/" + first.Id,
            Patch($$"""[{"op":"add","path":"members","value":[{"value":"{{otherUser.Id}}"}]}]""")))
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var current = (await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + first.Id))!;
        Assert.Equal(2, current.Members.Count);
        using (var removed = await host.Client.PatchAsJsonAsync("/scim/v2/Groups/" + first.Id,
            Patch($$"""[{"op":"remove","path":"members[value eq \"{{user.Id}}\"]"}]""")))
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        snapshot = await service.GetSnapshotAsync(actor);
        Assert.Equal(2, snapshot.Users.Single(entry => entry.Id == account.Id).TeamSources.Count);
        var mapping = snapshot.Mappings.Single(row => row.GroupId == Guid.Parse(second.Id));
        await service.SaveGroupMappingAsync(actor, mapping.Id, mapping.Revision, mapping.TeamId, mapping.GroupId, false);
        snapshot = await service.GetSnapshotAsync(actor);
        Assert.True(Assert.Single(snapshot.Users.Single(entry => entry.Id == account.Id).TeamSources).Manual);
        await service.SaveGroupMappingAsync(actor, mapping.Id, mapping.Revision + 1, mapping.TeamId, mapping.GroupId, true);
        Assert.Equal(2, (await service.GetSnapshotAsync(actor)).Users.Single(entry => entry.Id == account.Id).TeamSources.Count);
        await using var context = host.CreateDbContext();
        Assert.Equal(3, await context.ScimGroupMembers.CountAsync());
        Assert.Single(await context.ScimGroupMembers.Where(member => member.IsDeleted).ToArrayAsync());
        Assert.Contains(await context.IdentityAdministrationRecords.ToArrayAsync(), record => record.Action == "GroupMappingUpdated");
    }

    [Fact]
    public async Task GroupDeleteRetainsIdentityAndMappingsAndReprovisioningReusesBoth()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var group = await GroupAsync(host, "Original", user.Id);
        var service = Administration(host);
        var actor = Admin(host);
        await service.SaveTeamAsync(actor, null, null, "Operations", true);
        var team = (await service.GetSnapshotAsync(actor)).Teams.Single();
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true);
        using (var deleted = await host.Client.DeleteAsync("/scim/v2/Groups/" + group.Id)) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var absent = await host.Client.GetAsync("/scim/v2/Groups/" + group.Id)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var snapshot = await service.GetSnapshotAsync(actor);
        Assert.True(snapshot.Groups.Single().IsDeleted);
        Assert.Single(snapshot.Mappings);
        Assert.True(snapshot.Users.Single(entry => entry.Id == Guid.Parse(user.Id)).Unassigned);
        using var restored = await host.Client.PostAsJsonAsync("/scim/v2/Groups", Body(Guid.Parse(group.ExternalId), "Restored", user.Id));
        Assert.Equal(HttpStatusCode.Created, restored.StatusCode);
        Assert.Equal(group.Id, (await restored.Content.ReadFromJsonAsync<ScimGroupResponse>())!.Id);
        Assert.False((await service.GetSnapshotAsync(actor)).Users.Single(entry => entry.Id == Guid.Parse(user.Id)).Unassigned);
    }

    [Fact]
    public async Task UserDisablePreservesGroupsButDeleteRemovesScimReferencesAndOnlyDerivedTeams()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var group = await GroupAsync(host, "Members", user.Id);
        var service = Administration(host);
        var actor = Admin(host);
        await service.SaveTeamAsync(actor, null, null, "Operations", true);
        var snapshot = await service.GetSnapshotAsync(actor);
        var team = snapshot.Teams.Single();
        var account = snapshot.Users.Single(entry => entry.Id == Guid.Parse(user.Id));
        await service.SaveUserAsync(actor, new(account.Key, account.Version, null, true, [team.Id]));
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true);
        using (var inactive = await host.Client.PatchAsJsonAsync("/scim/v2/Users/" + user.Id, Patch("""[{"op":"replace","path":"active","value":false}]""")))
            Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
        Assert.Single((await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + group.Id))!.Members);
        using (var deleted = await host.Client.DeleteAsync("/scim/v2/Users/" + user.Id)) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var updated = (await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + group.Id))!;
        Assert.Empty(updated.Members);
        Assert.NotEqual(group.Meta.Version, updated.Meta.Version);
        var retained = (await service.GetSnapshotAsync(actor)).Users.Single(entry => entry.Id == account.Id);
        Assert.True(retained.ScimDeleted);
        Assert.True(Assert.Single(retained.TeamSources).Manual);
        await using var context = host.CreateDbContext();
        Assert.True((await context.ScimGroupMembers.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task StaleAndInvalidPatchesAreAtomicAndProjectionFailureRollsBackSourceMembers()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var group = await GroupAsync(host, "Original");
        var service = Administration(host);
        var actor = Admin(host);
        await service.SaveTeamAsync(actor, null, null, "Operations", true);
        var team = (await service.GetSnapshotAsync(actor)).Teams.Single();
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true);
        using (var invalid = await host.Client.PatchAsJsonAsync("/scim/v2/Groups/" + group.Id,
            Patch("""[{"op":"replace","path":"displayName","value":"Bad change"},{"op":"replace","path":"roles","value":[]} ]""")))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await using var context = host.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectDerivedMembership BEFORE INSERT ON UserTeamMemberships
            WHEN NEW.GroupMappingId IS NOT NULL BEGIN SELECT RAISE(ABORT, 'Synthetic projection failure'); END;
            """);
        using (var failed = await host.Client.PatchAsJsonAsync("/scim/v2/Groups/" + group.Id,
            Patch($$"""[{"op":"add","path":"members","value":[{"value":"{{user.Id}}"}]}]""")))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var unchanged = (await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + group.Id))!;
        Assert.Equal(group.Meta.Version, unchanged.Meta.Version);
        Assert.Empty(unchanged.Members);
        Assert.Empty(await context.ScimGroupMembers.ToArrayAsync());
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectDerivedMembership;");
        using (var renamed = await host.Client.PatchAsJsonAsync("/scim/v2/Groups/" + group.Id,
            Patch("""[{"op":"replace","path":"displayName","value":"Renamed"}]"""))) Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/scim/v2/Groups/" + group.Id)
        {
            Content = JsonContent.Create(Body(Guid.Parse(group.ExternalId), "Stale", user.Id))
        };
        request.Headers.TryAddWithoutValidation("If-Match", group.Meta.Version);
        using var stale = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("Renamed", (await host.Client.GetFromJsonAsync<ScimGroupResponse>("/scim/v2/Groups/" + group.Id))!.DisplayName);
    }

    [Fact]
    public async Task MappingAuditFailureRollsBackProjectionAndDisabledProvisioningDoesNotRemoveAssignments()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await UserAsync(host);
        var group = await GroupAsync(host, "Source", user.Id);
        var service = Administration(host);
        var actor = Admin(host);
        await service.SaveTeamAsync(actor, null, null, "Operations", true);
        var team = (await service.GetSnapshotAsync(actor)).Teams.Single();
        await using var context = host.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectMappingAudit BEFORE INSERT ON IdentityAdministrationRecords
            BEGIN SELECT RAISE(ABORT, 'Synthetic audit failure'); END;
            """);
        var error = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true));
        Assert.Equal("UserDirectory.Errors.Persistence", error.ResourceKey);
        Assert.Empty(await context.UserTeamGroupMappings.ToArrayAsync());
        Assert.Empty(await context.UserTeamMemberships.ToArrayAsync());
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectMappingAudit;");
        await service.SaveGroupMappingAsync(actor, null, null, team.Id, Guid.Parse(group.Id), true);
        host.Options.Enabled = false;
        var connection = await context.ScimConnections.SingleAsync();
        connection.IsEnabled = false;
        connection.Revision++;
        await context.SaveChangesAsync();
        Assert.False((await service.GetSnapshotAsync(actor)).Users.Single(entry => entry.Id == Guid.Parse(user.Id)).Unassigned);
        var mapping = (await service.GetSnapshotAsync(actor)).Mappings.Single();
        var stale = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            service.SaveGroupMappingAsync(actor, mapping.Id, mapping.Revision - 1, team.Id, mapping.GroupId, false));
        Assert.Equal("UserDirectory.Errors.Stale", stale.ResourceKey);
        await service.SaveGroupMappingAsync(actor, mapping.Id, mapping.Revision, team.Id, mapping.GroupId, false);
        Assert.True((await service.GetSnapshotAsync(actor)).Users.Single(entry => entry.Id == Guid.Parse(user.Id)).Unassigned);
    }
}
