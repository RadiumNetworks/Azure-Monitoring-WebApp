using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed partial class AnalysisDashboardPublishingTests
{
    private const string HistoricalAlias = "old-alice@example.com";
    private const string HistoricalSibling = "admin-alice@example.com";

    [Fact]
    public async Task HistoricalMainCanEditAndRepublishAliasDashboardsWithoutTransferringOwnership()
    {
        await using var fixture = await Fixture.CreateAsync(directoryEnabled: true);
        var (main, alias, collection) = await SeedHistoricalDashboardsAsync(fixture);
        var owners = await fixture.Repository.ListOwnersAsync(main.UserName);
        Assert.Equal(main.UserName, owners[0].OwnerKey);
        Assert.Equal(new[] { HistoricalAlias, HistoricalSibling }.Order(),
            owners.Skip(1).Select(owner => owner.OwnerKey).Order());
        var loaded = await fixture.Repository.LoadForOwnerAsync(main.UserName, alias.UserName);
        Assert.Equal(collection.ActiveDashboardId, loaded.Collection!.ActiveDashboardId);
        var originalMain = (await fixture.Repository.LoadAsync(main.UserName)).Collection!.Serialize();
        loaded.Collection.RenameActive("Recovered <dashboard> &");
        var saved = await fixture.Repository.SaveForOwnerAsync(main.UserName, alias.UserName, loaded.Collection, loaded.Revision);
        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, saved.Revision, Alice,
            sourceOwnerKey: alias.UserName);
        Assert.Equal(alias.UserName, publication.Owner);
        Assert.Equal(collection.ActiveDashboardId, publication.Id);
        Assert.Equal(2, publication.Version);
        Assert.Equal("Recovered <dashboard> &", (await fixture.Publishing.LoadAsync(publication.Id, Operator)).Document.Name);
        Assert.Equal(originalMain, (await fixture.Repository.LoadAsync(main.UserName)).Collection!.Serialize());
        await fixture.Publishing.WithdrawAsync(publication.Id, Alice);
        Assert.DoesNotContain(await fixture.Publishing.ListAsync(Operator), item => item.Id == publication.Id);
        loaded = await fixture.Repository.LoadForOwnerAsync(main.UserName, alias.UserName);
        publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, loaded.Revision, Alice,
            sourceOwnerKey: alias.UserName);
        Assert.Equal(3, publication.Version);
        Assert.Equal(alias.UserName, publication.Owner);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(3, await context.AnalysisDashboards.CountAsync());
        Assert.Equal(1, await context.AnalysisDashboardTemplates.CountAsync());
        Assert.Empty(await context.LegacyUserLinks.ToArrayAsync());
        Assert.False(await context.DirectoryUsers.AnyAsync(user => user.UsesStableIdentity));
    }

    [Theory]
    [InlineData(HistoricalAlias, "alice@example.com")]
    [InlineData(HistoricalAlias, HistoricalSibling)]
    [InlineData(HistoricalSibling, HistoricalAlias)]
    [InlineData("bob@example.com", HistoricalAlias)]
    [InlineData("bob@example.com", "alice@example.com")]
    public async Task HistoricalAliasAccessIsMainOnlyAndNeverReciprocal(string actor, string owner)
    {
        await using var fixture = await Fixture.CreateAsync(directoryEnabled: true);
        var (_, _, collection) = await SeedHistoricalDashboardsAsync(fixture);
        Assert.Equal(actor, Assert.Single(await fixture.Repository.ListOwnersAsync(actor)).OwnerKey);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Repository.LoadForOwnerAsync(actor, owner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Repository.SaveForOwnerAsync(actor, owner, collection, 1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 2, User(actor, SqlAuthenticationRoles.Admin),
                sourceOwnerKey: owner));
        if (actor != HistoricalAlias)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, User(actor, SqlAuthenticationRoles.Admin)));
    }

    [Fact]
    public async Task HistoricalMainRetainsRoleAndSourceCollectionChecksAndOptimisticConcurrency()
    {
        await using var fixture = await Fixture.CreateAsync(directoryEnabled: true);
        var (main, alias, collection) = await SeedHistoricalDashboardsAsync(fixture);
        var loaded = await fixture.Repository.LoadForOwnerAsync(main.UserName, alias.UserName);
        var reader = User(main.UserName, SqlAuthenticationRoles.Operator);
        Assert.False(await fixture.Publishing.CanPublishAsync(reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, loaded.Revision, reader, sourceOwnerKey: alias.UserName));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice));
        var competing = loaded.Collection!.Copy();
        competing.RenameActive("Alias session update");
        await fixture.Repository.SaveAsync(alias.UserName, competing, loaded.Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            fixture.Repository.SaveForOwnerAsync(main.UserName, alias.UserName, loaded.Collection, loaded.Revision));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, loaded.Revision, Alice, sourceOwnerKey: alias.UserName));
        Assert.Equal("Alias session update", (await fixture.Repository.LoadAsync(alias.UserName)).Collection!.Active.Document.Name);
        Assert.Equal(1, (await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator)).Publication.Version);
    }

    [Theory]
    [InlineData("detach")]
    [InlineData("main-disabled")]
    [InlineData("alias-disabled")]
    [InlineData("main-stable")]
    [InlineData("alias-stable")]
    [InlineData("main-entra")]
    [InlineData("alias-entra")]
    [InlineData("main-sql")]
    [InlineData("alias-sql")]
    [InlineData("alias-confirmed")]
    [InlineData("main-renamed")]
    [InlineData("alias-renamed")]
    public async Task HistoricalAccessIsRecheckedForEveryReadSavePublishAndWithdrawal(string change)
    {
        await using var fixture = await Fixture.CreateAsync(directoryEnabled: true);
        var (main, alias, collection) = await SeedHistoricalDashboardsAsync(fixture);
        var loaded = await fixture.Repository.LoadForOwnerAsync(main.UserName, alias.UserName);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var targetId = change.StartsWith("main-", StringComparison.Ordinal) ? main.Id : alias.Id;
            var target = await context.DirectoryUsers.SingleAsync(user => user.Id == targetId);
            if (change == "detach")
                context.HistoricalUserAliases.Remove(await context.HistoricalUserAliases.SingleAsync(link => link.AliasUserId == alias.Id));
            else if (change.EndsWith("-disabled", StringComparison.Ordinal)) target.IsEnabled = false;
            else if (change.EndsWith("-stable", StringComparison.Ordinal)) target.UsesStableIdentity = true;
            else if (change.EndsWith("-entra", StringComparison.Ordinal))
                context.EntraUserIdentities.Add(new() { UserId = targetId, TenantId = Guid.NewGuid(), ObjectId = Guid.NewGuid() });
            else if (change.EndsWith("-sql", StringComparison.Ordinal))
                context.AuthenticationUsers.Add(new() { Username = target.UserName, PasswordHash = "not-a-login", Role = SqlAuthenticationRoles.Admin });
            else if (change == "alias-confirmed")
                context.LegacyUserLinks.Add(new()
                {
                    Alias = alias.UserName, TargetUserId = main.Id, Status = LegacyUserLinkStatuses.Confirmed,
                    ApprovedByUserId = main.Id, ApprovedAt = DateTimeOffset.UtcNow, VerificationNote = "Verified test assignment"
                });
            else target.UserName = "changed-original-key@example.com";
            await context.SaveChangesAsync();
        }
        Assert.DoesNotContain(await fixture.Repository.ListOwnersAsync(main.UserName), owner => owner.OwnerKey == alias.UserName);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Repository.LoadForOwnerAsync(main.UserName, alias.UserName));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Repository.SaveForOwnerAsync(main.UserName, alias.UserName, loaded.Collection!, loaded.Revision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, loaded.Revision, Alice, sourceOwnerKey: alias.UserName));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice));
        await using var check = fixture.Factory.CreateDbContext();
        var source = await check.AnalysisDashboards.SingleAsync(row => row.OwnerKey == alias.UserName);
        Assert.Equal(loaded.Revision, source.Revision);
        Assert.Equal(loaded.Collection!.Serialize(), source.DocumentJson);
        Assert.True((await check.AnalysisDashboardTemplates.SingleAsync()).Published);
    }

    [Theory]
    [InlineData("open-mode-operator")]
    [InlineData("open-access")]
    [InlineData("different-login@example.com")]
    public async Task SharedOpenActorsAndMatchingDisplayOrContactDataDoNotGrantAliasAccess(string actor)
    {
        await using var fixture = await Fixture.CreateAsync(directoryEnabled: true);
        var (main, alias, _) = await SeedHistoricalDashboardsAsync(fixture);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var profile = await context.DirectoryUsers.SingleAsync(user => user.Id == main.Id);
            profile.UserName = actor.StartsWith("open-", StringComparison.Ordinal) ? actor : "another-original-key@example.com";
            profile.LocalDisplayName = actor;
            profile.Email = actor;
            await context.SaveChangesAsync();
        }
        Assert.Single(await fixture.Repository.ListOwnersAsync(actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Repository.LoadForOwnerAsync(actor, alias.UserName));
    }

    [Fact]
    public async Task DisabledDirectoryDoesNotEnableHistoricalDashboardAccess()
    {
        await using var fixture = await Fixture.CreateAsync(directoryEnabled: true);
        var (main, alias, _) = await SeedHistoricalDashboardsAsync(fixture);
        var disabled = new AnalysisDashboardRepository(fixture.Factory, new() { Enabled = false });
        Assert.Single(await disabled.ListOwnersAsync(main.UserName));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disabled.LoadForOwnerAsync(main.UserName, alias.UserName));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MainAccountCanSelectEditUpdateAndWithdrawAliasPublicationsInMyDashboards(string culture)
    {
        using var display = new UiSelection(culture);
        await using var page = await BrowserPageFixture.CreateAsync(aliases: true);
        Assert.Contains(page.Text["AnalysisDashboards.Owner"], page.Decoded);
        Assert.Contains(HistoricalAlias, page.Decoded);
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = HistoricalAlias });
        Assert.Equal(HistoricalAlias, page.Get<string>("dashboardOwnerKey"));
        Assert.NotNull(page.Get<PublishedAnalysisDashboard?>("OwnPublication"));
        Assert.Contains(page.Text["AnalysisPublications.Update"], page.Decoded);
        var id = page.Get<AnalysisDashboardCollection>("dashboardCollection").ActiveDashboardId;
        await page.Invoke("RenameDashboardAsync", "Recovered alias");
        await page.Invoke("PublishDashboardAsync");
        var publication = await page.Storage.Publishing.LoadAsync(id, Operator);
        Assert.Equal(2, publication.Publication.Version);
        Assert.Equal(HistoricalAlias, publication.Publication.Owner);
        Assert.Equal("Recovered alias", publication.Document.Name);
        await page.Invoke("WithdrawDashboardAsync");
        Assert.DoesNotContain(await page.Storage.Publishing.ListAsync(Operator), item => item.Id == id);
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = "alice@example.com" });
        Assert.Equal("alice@example.com", page.Get<string>("dashboardOwnerKey"));
        Assert.Equal("AD dashboard", page.Dashboard.Document.Name);
        Assert.Equal("Recovered alias", (await page.Storage.Repository.LoadAsync(HistoricalAlias)).Collection!.Active.Document.Name);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("publish")]
    [InlineData("run")]
    [InlineData("detail")]
    public async Task AnOpenAliasEditorCannotSavePublishOrRunAfterDetachingAndCanReturnToItsOwnAccount(string action)
    {
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, queryClient: client, aliases: true);
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = HistoricalAlias });
        var before = await page.Storage.Repository.LoadAsync(HistoricalAlias);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            var aliasId = await context.DirectoryUsers.Where(user => user.UserName == HistoricalAlias).Select(user => user.Id).SingleAsync();
            context.HistoricalUserAliases.Remove(await context.HistoricalUserAliases.SingleAsync(link => link.AliasUserId == aliasId));
            await context.SaveChangesAsync();
        }
        await page.Set("selectedQueryId", page.Get<IEnumerable<AnalysisQueryDefinition>>("availableQueries").First().Id);
        await page.Invoke(action switch
        {
            "save" => "SaveDashboardAsync", "publish" => "PublishDashboardAsync",
            "run" => "RunDashboardAsync", _ => "RunAsync"
        });
        Assert.Equal(page.Text["AnalysisDashboards.AccessDenied"], page.Get<string>("errorMessage"));
        Assert.True(page.Get<bool>("dashboardLoadFailed"));
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        Assert.Equal(0, client.Calls);
        var after = await page.Storage.Repository.LoadAsync(HistoricalAlias);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Collection!.Serialize(), after.Collection!.Serialize());
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = "alice@example.com" });
        Assert.False(page.Get<bool>("dashboardLoadFailed"));
        Assert.Equal("alice@example.com", page.Get<string>("dashboardOwnerKey"));
        Assert.DoesNotContain(page.Get<IReadOnlyList<AnalysisDashboardOwner>>("dashboardOwners"), owner => owner.OwnerKey == HistoricalAlias);
    }

    private static async Task<(DirectoryUser Main, DirectoryUser Alias, AnalysisDashboardCollection Collection)>
        SeedHistoricalDashboardsAsync(Fixture fixture)
    {
        var main = new DirectoryUser { UserName = "alice@example.com", LocalDisplayName = "Main <display>" };
        var alias = new DirectoryUser { UserName = HistoricalAlias, LocalDisplayName = "Alias <display>" };
        var sibling = new DirectoryUser { UserName = HistoricalSibling };
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.DirectoryUsers.AddRange(main, alias, sibling);
            context.HistoricalUserAliases.AddRange(
                new() { AliasUserId = alias.Id, MainUserId = main.Id },
                new() { AliasUserId = sibling.Id, MainUserId = main.Id });
            await context.SaveChangesAsync();
        }
        if ((await fixture.Repository.LoadAsync(main.UserName)).Collection is null)
            await fixture.Repository.SaveAsync(main.UserName, CreateDashboard(), 0);
        var collection = CreateDashboard();
        collection.RenameActive("Historical dashboard");
        await fixture.Repository.SaveAsync(alias.UserName, collection, 0);
        await fixture.Repository.SaveAsync(sibling.UserName, CreateDashboard(), 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, User(alias.UserName, SqlAuthenticationRoles.Admin));
        return (main, alias, collection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingAnAliasDuringAQueryDiscardsTheLateDashboardOrDetailResponse(bool detail)
    {
        var client = new DashboardQueryClient { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, queryClient: client, aliases: true);
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = HistoricalAlias });
        await page.Set("selectedQueryId", page.Get<IEnumerable<AnalysisQueryDefinition>>("availableQueries").First().Id);
        var pending = page.Invoke(detail ? "RunAsync" : "RunDashboardAsync");
        try
        {
            await client.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var context = page.Storage.Factory.CreateDbContext();
            var aliasId = await context.DirectoryUsers.Where(user => user.UserName == HistoricalAlias).Select(user => user.Id).SingleAsync();
            context.HistoricalUserAliases.Remove(await context.HistoricalUserAliases.SingleAsync(link => link.AliasUserId == aliasId));
            await context.SaveChangesAsync();
        }
        finally
        {
            client.Release.TrySetResult();
            await pending;
        }
        Assert.Equal(1, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        Assert.Null(page.Get<AnalysisQueryResponse?>("result"));
        Assert.Equal(page.Text["AnalysisDashboards.AccessDenied"], page.Get<string>("errorMessage"));
        Assert.True(page.Get<bool>("dashboardLoadFailed"));
    }

    [Fact]
    public async Task RefreshDiscoversNewAliasCollectionsAndKeepsARevokedSelectionExplicitUntilReturningToOwnAccount()
    {
        await using var page = await BrowserPageFixture.CreateAsync(aliases: true);
        const string addedOwner = "another-alice@example.com";
        var added = new DirectoryUser { UserName = addedOwner };
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            var mainId = await context.DirectoryUsers.Where(user => user.UserName == "alice@example.com").Select(user => user.Id).SingleAsync();
            context.DirectoryUsers.Add(added);
            context.HistoricalUserAliases.Add(new() { AliasUserId = added.Id, MainUserId = mainId });
            await context.SaveChangesAsync();
        }
        await page.Storage.Repository.SaveAsync(addedOwner, CreateDashboard(), 0);
        Assert.DoesNotContain(page.Get<IReadOnlyList<AnalysisDashboardOwner>>("dashboardOwners"), owner => owner.OwnerKey == addedOwner);
        await page.Invoke("ReloadPublicationsAsync");
        Assert.Contains(page.Get<IReadOnlyList<AnalysisDashboardOwner>>("dashboardOwners"), owner => owner.OwnerKey == addedOwner);
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = addedOwner });
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.HistoricalUserAliases.Remove(await context.HistoricalUserAliases.SingleAsync(link => link.AliasUserId == added.Id));
            await context.SaveChangesAsync();
        }
        await page.Invoke("ReloadPublicationsAsync");
        Assert.True(page.Get<bool>("dashboardLoadFailed"));
        Assert.Equal(addedOwner, page.Get<string>("dashboardOwnerKey"));
        Assert.Contains(page.Text.Format("AnalysisDashboards.UnavailableOwner", addedOwner), page.Decoded);
        await page.Invoke("ChangeDashboardOwnerAsync", new ChangeEventArgs { Value = "alice@example.com" });
        Assert.False(page.Get<bool>("dashboardLoadFailed"));
        Assert.Equal("alice@example.com", page.Get<string>("dashboardOwnerKey"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedRolesCannotBypassLowerQueryTimeLimitsButSessionNarrowingCanRecover(bool workspaceWide)
    {
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, workspaceWide: workspaceWide);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            var query = await context.AnalysisQueryDefinitions.Include(item => item.Versions).SingleAsync();
            query.MaxTimeRangeMinutes = 60;
            query.Versions.Single(item => item.Id == query.PublishedVersionId).SnapshotJson =
                JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query));
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunAsync");
        Assert.Equal(0, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Single(page.Get<Dictionary<string, string>>("errors"));
        await page.Invoke("ExecutionSettingsChanged", ((int?)60, (int?)30));
        await page.Invoke("RunAsync");
        Assert.Equal(2, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, string>>("errors"));
        Assert.Single(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Null((await page.Storage.Publishing.LoadAsync(page.Component.DashboardId, Operator)).Document.TimeRangeMinutesOverride);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PrivateRoleRuleCanBePublishedOpenedAndCopiedWithoutMaterializingMembers(string culture)
    {
        using var display = new UiSelection(culture);
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, new DashboardQueryClient());
        await page.Invoke("PublishDashboardAsync");
        var publication = Assert.Single(await page.Storage.Publishing.ListAsync(Alice));
        var published = await page.Storage.Publishing.LoadAsync(publication.Id, Operator);
        Assert.Equal(["Domain Controller", "File Server"], published.Document.InventoryRoles);
        Assert.Equal("FIXED", Assert.Single(published.Document.Systems).Computer);
        await page.Invoke("OpenPublishedDashboardAsync", publication.Id);
        Assert.True(page.Get<bool>("publishedUsesInventoryRoles"));
        var filters = Regex.Match(page.Html, "<section class=\"analysis-controls.*?</section>", RegexOptions.Singleline).Value;
        Assert.Contains(page.Text["Filters.AllWorkspaces"], filters);
        Assert.Single(Regex.Matches(filters, @"<select\b"));
        await page.Invoke("DuplicateDashboardAsync");
        var copy = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.OpenActive();
        Assert.Equal(AnalysisDashboardSelectionMode.InventoryRoles, copy.SelectionMode);
        Assert.Equal(published.Document.InventoryRoles, copy.InventoryRoles);
        Assert.Equal("FIXED", Assert.Single(copy.Systems).Computer);
        Assert.Equal(1, (await page.Storage.Publishing.LoadAsync(publication.Id, Operator)).Publication.Version);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedRoleDashboardResolvesOnOpenAndRunWithoutRepublishingOrApplyingLegacyFilters(string culture)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client);
        var original = await page.Storage.Publishing.LoadAsync(page.Component.DashboardId, Operator);
        var savedJson = JsonSerializer.Serialize(original.Document);
        Assert.Empty(original.Document.Systems);
        Assert.Equal(3, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems.Count);
        Assert.Contains("DC02", page.Decoded);
        Assert.Equal(0, client.Calls);
        await page.SetParameters(new()
        {
            ["Domain"] = "ignored.example", ["Site"] = "ignored-site", ["Role"] = "Other",
            ["FilteredSystems"] = Array.Empty<ComputerInventoryEntry>()
        });
        await page.Invoke("RunAsync");
        Assert.Equal(["DC01", "DC02"], RoleRequestSystems(client.Requests.Single(request => request.Workspace == "a").Kql));
        Assert.Equal(["DC01"], RoleRequestSystems(client.Requests.Single(request => request.Workspace == "b").Kql));
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.Remove(await context.ComputerInventory.SingleAsync(system =>
                system.Computer == "DC01" && system.SubscriptionId == "11111111-1111-1111-1111-111111111111"));
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "DC03", Role = "Domain Controller"
            });
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunAsync");
        Assert.Equal(4, client.Calls);
        Assert.Equal(["DC02", "DC03"], RoleRequestSystems(client.Requests.Last(request => request.Workspace == "a").Kql));
        Assert.Contains("DC02", page.Decoded);
        Assert.Contains("DC03", page.Decoded);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            (await context.ComputerInventory.SingleAsync(system => system.Computer == "DC02")).Role = "Other";
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunAsync");
        Assert.Equal(6, client.Calls);
        Assert.Equal(["DC03"], RoleRequestSystems(client.Requests.Last(request => request.Workspace == "a").Kql));
        Assert.DoesNotContain("DC02", page.Decoded);
        Assert.Contains("DC03", page.Decoded);
        var current = await page.Storage.Publishing.LoadAsync(page.Component.DashboardId, Operator);
        Assert.Equal(original.Publication.Version, current.Publication.Version);
        Assert.Equal(savedJson, JsonSerializer.Serialize(current.Document));
        Assert.Single(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
    }

    [Theory]
    [InlineData("en-US", "empty")]
    [InlineData("de-DE", "empty")]
    [InlineData("en-US", "inventory")]
    [InlineData("de-DE", "inventory")]
    [InlineData("en-US", "routing")]
    [InlineData("de-DE", "routing")]
    [InlineData("en-US", "workspace")]
    [InlineData("de-DE", "workspace")]
    public async Task PublishedRoleResolutionClearsOldResultsAndNeverFallsBack(string culture, string failure)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client);
        await page.Invoke("RunAsync");
        Assert.Single(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        if (failure == "inventory")
            page.Executions.NextFailure = new IOException("PRIVATE_INVENTORY_DETAIL");
        else if (failure == "workspace")
            await page.SetParameters(new() { ["WorkspaceKey"] = "unconfigured" });
        else
        {
            await using var context = page.Storage.Factory.CreateDbContext();
            if (failure == "empty")
                foreach (var system in await context.ComputerInventory.ToArrayAsync()) system.Role = "Other";
            else
                context.ComputerInventory.Add(new() { SubscriptionId = "unconfigured", Computer = "UNROUTED", Role = "Domain Controller" });
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunAsync");
        Assert.Equal(2, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.DoesNotContain("PRIVATE_INVENTORY_DETAIL", page.Decoded);
        if (failure == "empty")
        {
            Assert.Equal(AnalysisDashboardInventoryStatus.Empty, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Status);
            Assert.Contains(page.Text["Analysis.RoleSelection.Empty"], page.Decoded);
            Assert.Null(page.Get<string?>("errorMessage"));
        }
        else
        {
            Assert.Null(page.Get<AnalysisDashboardInventorySelection?>("roleSelection"));
            Assert.Contains(page.Text["Analysis.RoleSelection.CountUnavailable"], page.Decoded);
            Assert.NotNull(page.Get<string?>("errorMessage"));
        }
    }

    [Theory]
    [InlineData(97, 100, false)]
    [InlineData(98, 101, true)]
    public async Task PublishedRoleLimitIsExactAndWorkspaceNarrowingCanRecover(int added, int total, bool exceedsLimit)
    {
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.AddRange(Enumerable.Range(0, added).Select(index => new ComputerInventoryEntry
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = $"ROLE-{index}", Role = "Domain Controller"
            }));
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunAsync");
        if (exceedsLimit)
        {
            Assert.Equal(0, client.Calls);
            Assert.Contains(page.Text["Analysis.Validation.DashboardSystemsLimitExceeded"], page.Decoded);
            Assert.Null(page.Get<AnalysisDashboardInventorySelection?>("roleSelection"));
        }
        else
        {
            Assert.Equal(total, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems.Count);
            Assert.Equal(total, client.Requests.Sum(request => RoleRequestSystems(request.Kql).Length));
        }
        var before = client.Calls;
        await page.SetParameters(new() { ["WorkspaceKey"] = "b" });
        await page.Invoke("RunAsync");
        Assert.Equal(before + 1, client.Calls);
        Assert.Equal("b", client.Requests.Last().Workspace);
        Assert.Single(page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems);
        Assert.Null(page.Get<string?>("errorMessage"));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task PublishedWorkspaceWideRoleWidgetsAreLabelledAndKeepTheirScope(string culture, bool workspaceWide)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, workspaceWide: workspaceWide);
        Assert.Equal(workspaceWide, page.Decoded.Contains(page.Text["Analysis.RoleSelection.WorkspaceWide"], StringComparison.Ordinal));
        await page.Invoke("RunAsync");
        Assert.Equal(2, client.Calls);
        Assert.Single(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Equal(workspaceWide, page.Decoded.Contains(page.Text["Analysis.RoleSelection.WorkspaceWide"], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedWorkspaceChangesDiscardLateResponsesIncludingReturningToTheOriginalScope(bool returnToOriginal)
    {
        var client = new DashboardQueryClient { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, twoWidgets: true);
        await page.SetParameters(new() { ["WorkspaceKey"] = "a" });
        var run = page.Invoke("RunAsync");
        await client.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.SetParameters(new() { ["WorkspaceKey"] = "b" });
        if (returnToOriginal) await page.SetParameters(new() { ["WorkspaceKey"] = "a" });
        client.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Empty(page.Get<Dictionary<string, string>>("errors"));
        client.Release = null;
        await page.Invoke("RunAsync");
        Assert.Equal(3, client.Calls);
        Assert.Equal(2, page.Get<Dictionary<string, AnalysisQueryResponse>>("results").Count);
        Assert.All(client.Requests.Skip(1), request => Assert.Equal(returnToOriginal ? "a" : "b", request.Workspace));
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("republish")]
    [InlineData("reader")]
    public async Task PublishedRoleBatchesPreserveWithdrawalVersionAndReaderGuards(string change)
    {
        var client = new DashboardQueryClient { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, twoWidgets: true);
        await page.SetParameters(new() { ["WorkspaceKey"] = "a" });
        var run = page.Invoke("RunAsync");
        await client.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "withdraw")
            await page.Storage.Publishing.WithdrawAsync(page.Component.DashboardId, Alice);
        else if (change == "reader")
            page.SetAuthentication(Task.FromResult(new AuthenticationState(Operator)));
        else
        {
            var stored = await page.Storage.Repository.LoadAsync("alice@example.com");
            stored.Collection!.Active.Document.InventoryRoles = ["File Server"];
            await page.Storage.Repository.SaveAsync("alice@example.com", stored.Collection, stored.Revision);
            await page.Storage.Publishing.PublishAsync(page.Component.DashboardId, stored.Revision + 1, Alice);
        }
        client.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Empty(page.Get<Dictionary<string, string>>("errors"));
        if (change == "republish")
        {
            Assert.Equal(2, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Publication.Version);
            Assert.Equal(["File Server"], page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Document.InventoryRoles);
        }
        else
            Assert.Null(page.Get<PublishedAnalysisDashboardSnapshot?>("snapshot"));
    }

    [Fact]
    public async Task PublishedRoleAutoRefreshUsesNewMembershipAndPausedRefreshStillDetectsWithdrawal()
    {
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client);
        page.Set("autoRefresh", false);
        await page.Invoke("OnAfterRenderAsync", true);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            foreach (var system in await context.ComputerInventory.ToArrayAsync()) system.Role = "Other";
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "DC03", Role = "Domain Controller"
            });
            await context.SaveChangesAsync();
        }
        var gate = page.Reads.Block();
        page.Clock.Advance(TimeSpan.FromSeconds(30));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await page.Invoke("ReloadAsync");
        Assert.Equal(0, client.Calls);
        Assert.Equal(3, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems.Count);
        page.Set("autoRefresh", true);
        page.Clock.Advance(TimeSpan.FromSeconds(30));
        await client.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.DrainExecution();
        Assert.Equal(["DC03"], RoleRequestSystems(Assert.Single(client.Requests).Kql));
        Assert.Equal(1, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Publication.Version);
        page.Set("autoRefresh", false);
        await page.Storage.Publishing.WithdrawAsync(page.Component.DashboardId, Alice);
        gate = page.Reads.Block();
        page.Clock.Advance(TimeSpan.FromSeconds(30));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await page.Invoke("ReloadAsync");
        Assert.Null(page.Get<PublishedAnalysisDashboardSnapshot?>("snapshot"));
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task PublishedRoleMembershipIsResolvedOncePerBatchAndChangesOnlyOnTheNextRun()
    {
        var client = new DashboardQueryClient { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, twoWidgets: true);
        await page.SetParameters(new() { ["WorkspaceKey"] = "a" });
        var reads = page.Executions.Requests;
        var run = page.Invoke("RunAsync");
        await client.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            (await context.ComputerInventory.SingleAsync(system => system.Computer == "DC02")).Role = "Other";
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "DC03", Role = "Domain Controller"
            });
            await context.SaveChangesAsync();
        }
        client.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reads + 3, page.Executions.Requests);
        Assert.Equal(2, client.Calls);
        Assert.All(client.Requests, request => Assert.Equal(["DC01", "DC02"], RoleRequestSystems(request.Kql)));
        Assert.Equal(["DC01", "DC02"], page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems
            .Select(system => system.Computer).Order());
        client.Release = null;
        await page.Invoke("RunAsync");
        Assert.Equal(4, client.Calls);
        Assert.All(client.Requests.Skip(2), request => Assert.Equal(["DC01", "DC03"], RoleRequestSystems(request.Kql)));
        Assert.Equal(1, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Publication.Version);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedUnknownRoleWithInactiveFixedSystemsStaysEmptyAndRetainsSessionOverrides(string culture)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client);
        var stored = await page.Storage.Repository.LoadAsync("alice@example.com");
        stored.Collection!.Active.Document.InventoryRoles = ["Future <role> &"];
        stored.Collection.Active.Document.Systems = [new()
        {
            SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "UNSELECTED"
        }];
        await page.Storage.Repository.SaveAsync("alice@example.com", stored.Collection, stored.Revision);
        await page.Storage.Publishing.PublishAsync(page.Component.DashboardId, stored.Revision + 1, Alice);
        await page.Invoke("ReloadAsync");
        await page.Invoke("ExecutionSettingsChanged", ((int?)60, (int?)30));
        await page.Invoke("RunAsync");
        Assert.Equal(0, client.Calls);
        Assert.Contains("Future <role> &", page.Decoded);
        Assert.Contains(page.Text["Analysis.RoleSelection.Empty"], page.Decoded);
        Assert.DoesNotContain("UNSELECTED", page.Decoded);
        Assert.Null(page.Get<string?>("errorMessage"));
        Assert.Equal(60, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Document.TimeRangeMinutesOverride);
        var persisted = await page.Storage.Publishing.LoadAsync(page.Component.DashboardId, Operator);
        Assert.Null(persisted.Document.TimeRangeMinutesOverride);
        Assert.Equal(["Future <role> &"], persisted.Document.InventoryRoles);
    }

    [Fact]
    public async Task PublishedRoleReaderCannotExpandQueryWorkspacePermissions()
    {
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client,
            authentication: Task.FromResult(new AuthenticationState(Operator)));
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            var assignment = await context.AnalysisQueryWorkspaces.SingleAsync(item => item.WorkspaceKey == "b");
            assignment.Enabled = false;
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunAsync");
        Assert.Equal(0, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Single(page.Get<Dictionary<string, string>>("errors"));
        await page.SetParameters(new() { ["WorkspaceKey"] = "a" });
        await page.Invoke("RunAsync");
        Assert.Equal("a", Assert.Single(client.Requests).Workspace);
        Assert.Single(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
    }

    [Fact]
    public async Task PublishedRoleAutoRunWaitsForAnInProgressLoadWithoutLosingTheRequest()
    {
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, autoRun: true);
        var gate = page.Reads.Block();
        var reload = page.Invoke("ReloadAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Invoke("OnAfterRenderAsync", true);
        Assert.False(page.Get<bool>("wasAutoRunEnabled"));
        Assert.Equal(0, client.Calls);
        gate.Release.SetResult();
        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Invoke("OnAfterRenderAsync", false);
        Assert.Equal(2, client.Calls);
        await page.Invoke("OnAfterRenderAsync", false);
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, page.Clock.TimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedRoleDisposalDrainsPendingInventoryResolutionWithoutLateResults(bool noncooperative)
    {
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client);
        var gate = page.Executions.Block(noncooperative);
        var run = page.Invoke("RunAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = page.DisposePage();
        if (noncooperative)
        {
            Assert.False(disposal.IsCompleted);
            gate.Release.SetResult();
        }
        await Task.WhenAll(disposal, run).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Null(page.Get<AnalysisDashboardInventorySelection?>("roleSelection"));
        Assert.Null(page.Get<string?>("errorMessage"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PrivateRoleDashboardUsesFreshMembershipForWidgetsDetailsAndPreviewsWithoutSaving(string culture)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, client);
        var original = JsonSerializer.Serialize(page.Dashboard.Document);
        Assert.Equal(3, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems.Count);
        Assert.Contains(page.Text["Analysis.RoleSelection.Snapshot"].Split("{0}")[0], page.Decoded);
        await page.Set("domainFilter", "ignored.example");
        await page.Set("siteFilter", "ignored-site");
        await page.Set("roleFilter", "ignored-role");
        await page.Invoke("RunDashboardAsync");
        Assert.Equal(2, client.Calls);
        Assert.Equal(["DC01", "FILE01"], RoleRequestSystems(client.Requests.Single(request => request.Workspace == "a").Kql));
        Assert.Equal(["DC01"], RoleRequestSystems(client.Requests.Single(request => request.Workspace == "b").Kql));
        Assert.DoesNotContain("Original <cached>", page.Decoded);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.Remove(await context.ComputerInventory.SingleAsync(system =>
                system.Computer == "DC01" && system.SubscriptionId == "11111111-1111-1111-1111-111111111111"));
            (await context.ComputerInventory.SingleAsync(system => system.Computer == "FILE01")).Role = "Other";
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "DC03", Role = "domaincontrollers"
            });
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunDashboardAsync");
        Assert.Equal(["DC03"], RoleRequestSystems(client.Requests.Last(request => request.Workspace == "a").Kql));
        Assert.Equal(2, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems.Count);
        Assert.Equal(original, JsonSerializer.Serialize(page.Dashboard.Document));
        await page.Set("selectedQueryId", page.Dashboard.Document.Widgets.Single().QueryDefinitionId);
        await page.Invoke("QueryChanged");
        Assert.Equal(2, page.Get<IReadOnlyList<AnalysisQueryExecutionPreview>>("executionPreviews").Count);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "DETAIL-NEW", Role = "Domain Controller"
            });
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunAsync");
        Assert.NotNull(page.Get<AnalysisQueryResponse?>("result"));
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        Assert.Equal(["DC03", "DETAIL-NEW"], RoleRequestSystems(client.Requests.Last(request => request.Workspace == "a").Kql));
        Assert.Equal(1, (await page.Storage.Repository.LoadAsync("alice@example.com")).Revision);
        await page.Invoke("SaveDashboardAsync");
        var stored = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.OpenActive();
        Assert.Equal(["Domain Controller", "File Server"], stored.InventoryRoles);
        Assert.Equal("FIXED", Assert.Single(stored.Systems).Computer);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task EmptyRoleDraftCannotSaveCopyPublishOrRunButAnUnmatchedRoleCanBeSaved(string culture)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, client);
        await page.Invoke("InventoryRoleChangedAsync", "Domain Controller", new ChangeEventArgs { Value = false });
        await page.Invoke("InventoryRoleChangedAsync", "File Server", new ChangeEventArgs { Value = false });
        Assert.False(page.Get<bool>("CanRun"));
        Assert.False(page.Get<bool>("CanRunDashboard"));
        Assert.Contains(page.Text["Analysis.Validation.DashboardRolesRequired"], page.Decoded);
        await page.Invoke("SaveDashboardAsync");
        await page.Invoke("DuplicateDashboardAsync");
        await page.Invoke("RunDashboardAsync");
        await page.Invoke("PublishDashboardAsync");
        Assert.Equal(0, client.Calls);
        var unchanged = await page.Storage.Repository.LoadAsync("alice@example.com");
        Assert.Equal(1, unchanged.Revision);
        Assert.Single(unchanged.Collection!.Dashboards);
        Assert.Contains(page.Text["Analysis.Validation.DashboardRolesRequired"], page.Decoded);
        Assert.Empty(await page.Storage.Publishing.ListAsync(Alice));
        await page.Invoke("SelectionModeChangedAsync", new ChangeEventArgs { Value = "FixedSystems" });
        Assert.Equal(AnalysisDashboardSelectionMode.FixedSystems, page.Dashboard.Document.SelectionMode);
        await page.Invoke("SelectionModeChangedAsync", new ChangeEventArgs { Value = "InventoryRoles" });
        Assert.False(page.Get<bool>("CanRunDashboard"));
        await page.Invoke("InventoryRoleChangedAsync", "Future <role> &", new ChangeEventArgs { Value = true });
        Assert.Contains("Future &lt;role&gt; &amp;", page.Html);
        Assert.Equal(AnalysisDashboardInventoryStatus.Empty, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Status);
        await page.Invoke("SaveDashboardAsync");
        await page.Invoke("DuplicateDashboardAsync");
        var copied = (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!;
        Assert.Equal(2, copied.Dashboards.Count);
        Assert.Equal(["Future <role> &"], copied.OpenActive().InventoryRoles);
        Assert.Equal("FIXED", Assert.Single(copied.OpenActive().Systems).Computer);
        await page.Invoke("LoadWorkspaceAsync");
        Assert.Contains("Future <role> &", page.Get<IEnumerable<string>>("AvailableInventoryRoles"));
        Assert.False(page.Get<bool>("dashboardLoadFailed"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task EmptyMembershipAndInventoryFailureClearResultsAndNeverUseTheInactiveFixedList(string culture)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, client);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            foreach (var system in await context.ComputerInventory.ToArrayAsync())
                system.Role = "Other";
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunDashboardAsync");
        Assert.Equal(0, client.Calls);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        Assert.Contains(page.Text["Analysis.RoleSelection.Empty"], page.Decoded);
        Assert.True(page.Get<bool>("CanRunDashboard"));
        await page.Invoke("SaveDashboardAsync");
        await using (var context = page.Storage.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"ComputerInventory\"");
        await page.Invoke("RunDashboardAsync");
        Assert.Null(page.Get<AnalysisDashboardInventorySelection?>("roleSelection"));
        Assert.NotNull(page.Get<string?>("roleSelectionError"));
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        Assert.Equal(0, client.Calls);
        Assert.Equal(["Domain Controller", "File Server"],
            (await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.OpenActive().InventoryRoles);
    }

    [Fact]
    public async Task RoleLimitIsExplicitAndWorkspaceNarrowingCanRecoverWithoutChangingTheRule()
    {
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, client);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.AddRange(Enumerable.Range(1, 98).Select(index => new ComputerInventoryEntry
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = $"EXTRA{index}", Role = "Domain Controller"
            }));
            await context.SaveChangesAsync();
        }
        await page.Invoke("RunDashboardAsync");
        Assert.Equal(0, client.Calls);
        Assert.Contains(page.Text["Analysis.Validation.DashboardSystemsLimitExceeded"], page.Decoded);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        await page.Set("workspaceFilter", "b");
        await page.Invoke("WorkspaceChangedAsync");
        Assert.Equal(1, client.Calls);
        Assert.Equal("b", Assert.Single(client.Requests).Workspace);
        Assert.Single(page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems);
        Assert.Equal(["Domain Controller", "File Server"], page.Dashboard.Document.InventoryRoles);
        Assert.Equal(1, (await page.Storage.Repository.LoadAsync("alice@example.com")).Revision);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ModeUndoRedoPreservesFixedSelectionAndWorkspaceWideWidgetsAreLabelled(string culture)
    {
        using var display = new UiSelection(culture);
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, new DashboardQueryClient());
        var query = Assert.Single(page.Get<IReadOnlyList<AnalysisQueryDefinition>>("availableQueries"));
        query.Scope = AnalysisQueryScopes.Workspace;
        await page.Set("selectedQueryId", query.Id);
        await page.Invoke("QueryChanged");
        Assert.Contains(page.Text["Analysis.RoleSelection.WorkspaceWide"], page.Decoded);
        Assert.DoesNotContain("<span>" + page.Text["Filters.Domain"] + "</span>", page.Decoded);
        await page.Invoke("SelectionModeChangedAsync", new ChangeEventArgs { Value = "FixedSystems" });
        Assert.Equal(AnalysisDashboardSelectionMode.FixedSystems, page.Dashboard.Document.SelectionMode);
        Assert.Contains(page.Text["Analysis.SelectVisible"], page.Decoded);
        Assert.Single(page.Get<HashSet<string>>("selectedSystems"));
        await page.Invoke("UndoDashboard");
        Assert.Equal(AnalysisDashboardSelectionMode.InventoryRoles, page.Dashboard.Document.SelectionMode);
        Assert.Equal(3, page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems.Count);
        await page.Invoke("RedoDashboard");
        Assert.Equal(AnalysisDashboardSelectionMode.FixedSystems, page.Dashboard.Document.SelectionMode);
        Assert.Equal("FIXED", Assert.Single(page.Dashboard.Document.Systems).Computer);
    }

    [Fact]
    public async Task ResponsesFromAnEarlierRoleScopeAreDiscarded()
    {
        var client = new DashboardQueryClient { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, client);
        var running = page.Invoke("RunDashboardAsync");
        try
        {
            await client.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.Set("workspaceFilter", "b");
            await page.Invoke("DashboardFiltersChangedAsync");
        }
        finally
        {
            client.Release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults"));
        Assert.Null(page.Get<AnalysisQueryResponse?>("result"));
        Assert.Single(page.Get<AnalysisDashboardInventorySelection>("roleSelection").Systems);
    }

    [Fact]
    public async Task AutomaticPrivateRefreshReevaluatesRoleMembershipWithoutSaving()
    {
        var client = new DashboardQueryClient();
        await using var page = await BrowserPageFixture.CreateAsync(roles: true, client);
        page.Dashboard.UpdateWorkspace(document => document.AutoRefreshEnabled = true);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            (await context.ComputerInventory.SingleAsync(system => system.Computer == "FILE01")).Role = "Other";
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "AUTO-NEW", Role = "Domain Controller"
            });
            await context.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var loop = page.Invoke("RefreshClockAsync", cancellation.Token);
        try { await client.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        finally
        {
            await cancellation.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(["AUTO-NEW", "DC01"], RoleRequestSystems(client.Requests.Single(request => request.Workspace == "a").Kql));
        Assert.Equal(1, (await page.Storage.Repository.LoadAsync("alice@example.com")).Revision);
    }

    private static string[] RoleRequestSystems(string kql) =>
        JsonSerializer.Deserialize<string[]>(Regex.Match(kql, @"let _SelectedSystems = dynamic\((.*?)\);").Groups[1].Value)!
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public async Task RoleRuleIsPersistedAndPublishedWithoutMaterializingMembersAndFreshResolutionNeedsNoRepublish()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string subscription = "11111111-1111-1111-1111-111111111111";
        var collection = CreateDashboard();
        collection.Active.Document.SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles;
        collection.Active.Document.InventoryRoles = ["Domain Controller"];
        var inactiveFixed = Assert.Single(collection.Active.Document.Systems).Computer;
        var options = new CrossTenantLogAnalyticsOptions
        {
            Workspaces = [new() { Key = "workspace", SubscriptionIds = [subscription] }]
        };
        var runner = new AnalysisQueryRunner(options, new DashboardSecret(), new TargetWorkspaceResolver(options));
        var execution = new AnalysisQueryExecutionService(fixture.Factory, runner);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.AddRange(
                new() { SubscriptionId = subscription, Computer = "ROLE01", Role = "Domain Controller" },
                new() { SubscriptionId = subscription, Computer = "ROLE02", Role = "Domain Controller" });
            await context.SaveChangesAsync();
        }
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var persisted = (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.OpenActive();
        var snapshot = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal(AnalysisDashboardSelectionMode.InventoryRoles, persisted.SelectionMode);
        Assert.Equal(["Domain Controller"], snapshot.Document.InventoryRoles);
        Assert.Equal(inactiveFixed, Assert.Single(snapshot.Document.Systems).Computer);
        var first = await execution.ResolveDashboardSelectionAsync(snapshot.Document);
        Assert.Equal(["ROLE01", "ROLE02"], first.Systems.Select(system => system.Computer).Order());
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.Remove(await context.ComputerInventory.SingleAsync(system => system.Computer == "ROLE01"));
            context.ComputerInventory.Add(new() { SubscriptionId = subscription, Computer = "ROLE03", Role = "domaincontrollers" });
            await context.SaveChangesAsync();
        }
        var second = await execution.ResolveDashboardSelectionAsync(snapshot.Document, "WORKSPACE");
        Assert.Equal(["ROLE02", "ROLE03"], second.Systems.Select(system => system.Computer).Order());
        Assert.Equal(["ROLE01", "ROLE02"], first.Systems.Select(system => system.Computer).Order());
        var reloaded = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal(publication.Version, reloaded.Publication.Version);
        Assert.Equal(inactiveFixed, Assert.Single(reloaded.Document.Systems).Computer);
        Assert.Equal(["Domain Controller"], reloaded.Document.InventoryRoles);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            foreach (var system in await context.ComputerInventory.ToArrayAsync())
                system.Role = "Other";
            await context.SaveChangesAsync();
        }
        Assert.Equal(AnalysisDashboardInventoryStatus.Empty,
            (await execution.ResolveDashboardSelectionAsync(reloaded.Document)).Status);
        await fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator));
    }

    [Fact]
    public async Task InventoryReadFailureAndCancellationAreNotReportedAsEmptyRoleMembership()
    {
        await using var fixture = await Fixture.CreateAsync();
        var options = new CrossTenantLogAnalyticsOptions();
        var runner = new AnalysisQueryRunner(options, new DashboardSecret(), new TargetWorkspaceResolver(options));
        var execution = new AnalysisQueryExecutionService(fixture.Factory, runner);
        var document = new AnalysisDashboardDocument
        {
            SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles, InventoryRoles = ["Future Role"]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            execution.ResolveDashboardSelectionAsync(document, cancellationToken: cancellation.Token));
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"ComputerInventory\"");
        await Assert.ThrowsAsync<SqliteException>(() => execution.ResolveDashboardSelectionAsync(document));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task PrivateAndPublishedRoleViewsSupportAnEmptyMembershipWithoutUsingFixedSystems(string culture, bool published)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        collection.Active.Document.SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles;
        collection.Active.Document.InventoryRoles = ["Domain Controller"];
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var options = new CrossTenantLogAnalyticsOptions { Enabled = true };
        var client = new DashboardQueryClient();
        var runner = new AnalysisQueryRunner(options, new DashboardSecret(), new TargetWorkspaceResolver(options), client);
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton(fixture.Publishing).AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner)).AddSingleton(runner)
            .AddSingleton(new AlertStore(fixture.Factory, new(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder()),
                presenter, new(presenter), Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton(new AnalysisDashboardState()).AddSingleton(fixture.Repository).AddSingleton(new DashboardFilterOptions())
            .AddSingleton<IJSRuntime>(new UnusedJavaScript())
            .AddSingleton(new ApplicationAuthenticationOptions()).AddSingleton(options).AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent(0, published ? typeof(PublishedAnalysisDashboardView) : typeof(Components.Pages.Analysis));
                        if (published)
                        {
                            builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                            builder.AddAttribute(2, "AutoRunOnOpen", true);
                        }
                        builder.CloseComponent();
                    })
                }));
            await root.QuiescenceTask;
            return root;
        });
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        var text = services.GetRequiredService<UiText>();
        Assert.DoesNotContain(text["Analysis.Validation.DashboardSelectionNotAvailable"], html);
        Assert.Contains(text["Analysis.RoleSelection.Empty"], html);
        if (!published)
            Assert.Contains(text["Analysis.RoleSelection.Dynamic"], html);
        else
            Assert.Contains("LDAP latency", html);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task PrivateAndPublishedDashboardsSkipRetiredSystemsWithoutChangingSavedSelections(bool published, int remaining)
    {
        using var selection = new UiSelection("en-US");
        await using var fixture = await Fixture.CreateAsync();
        const string subscription = "11111111-1111-1111-1111-111111111111";
        var collection = CreateDashboard();
        var document = collection.Active.Document;
        document.Systems[0].SubscriptionId = subscription;
        document.Systems.Add(new() { SubscriptionId = subscription, Computer = "DC02" });
        var widget = Assert.Single(document.Widgets);
        var query = new AnalysisQueryDefinition
        {
            Id = widget.QueryDefinitionId, Key = "inventory-test", Name = "Inventory test", Category = "Test",
            VisualizationType = AnalysisQueryVisualizationTypes.Stat,
            Kql = "print Series = 'DC01', Value = 1 | where Series in~ (_SelectedSystems)"
        };
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(), Version = 1,
            SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
        };
        query.Versions.Add(version);
        query.PublishedVersionId = version.Id;
        query.Workspaces.Add(new() { WorkspaceKey = "workspace", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded });
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.AnalysisQueryDefinitions.Add(query);
            for (var index = 1; index <= remaining; index++)
                context.ComputerInventory.Add(new() { SubscriptionId = subscription, Computer = $"DC0{index}" });
            context.ComputerInventory.Add(new() { SubscriptionId = subscription, Computer = "NOT-SELECTED" });
            await context.SaveChangesAsync();
        }
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await using var before = fixture.Factory.CreateDbContext();
        var storedDocument = (await before.AnalysisDashboards.SingleAsync()).DocumentJson;
        var publishedDocument = (await before.AnalysisDashboardTemplates.SingleAsync()).DocumentJson;
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true, Workspaces = [new() { Key = "workspace", WorkspaceId = Guid.NewGuid().ToString(), SubscriptionIds = [subscription] }]
        };
        var client = new DashboardQueryClient();
        var runner = new AnalysisQueryRunner(options, new DashboardSecret(), new TargetWorkspaceResolver(options), client);
        var execution = new AnalysisQueryExecutionService(fixture.Factory, runner);
        if (remaining < 2)
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.ExecuteAsync(
                document.CreateExecutionRequest(widget, document.Systems.Select(system => new AnalysisQuerySystem(system.SubscriptionId, system.Computer)).ToArray())));
        Assert.Equal(0, client.Calls);
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var components = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(components).AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner)).AddSingleton(execution).AddSingleton(runner)
            .AddSingleton(new AlertStore(fixture.Factory, new(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder()),
                presenter, new(presenter), Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
            .AddSingleton(new ApplicationAuthenticationOptions()).AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton(new AnalysisDashboardState()).AddSingleton(fixture.Repository).AddSingleton(new DashboardFilterOptions())
            .AddSingleton(options).AddSingleton(TimeProvider.System).AddSingleton<IJSRuntime>(new UnusedJavaScript())
            .BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent(0, published ? typeof(PublishedAnalysisDashboardView) : typeof(Components.Pages.Analysis));
                        if (published) builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                        builder.CloseComponent();
                    })
                }));
            await root.QuiescenceTask;
            return root;
        });
        var component = published ? (IComponent)components.PublishedView! : components.AnalysisPage!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cachedResults = (Dictionary<string, AnalysisQueryResponse>)component.GetType()
            .GetField(published ? "results" : "widgetResults", flags)!.GetValue(component)!;
        cachedResults[widget.Id] = CachedResult("STALE_BEFORE_RETIREMENT");
        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)component.GetType().GetMethod(published ? "RunAsync" : "RunDashboardAsync", flags)!.Invoke(component, [])!);
        Assert.Equal(remaining == 0 ? 0 : 1, client.Calls);
        var errors = (Dictionary<string, string>)component.GetType().GetField(published ? "errors" : "widgetErrors", flags)!.GetValue(component)!;
        Assert.Empty(errors);
        var results = (Dictionary<string, AnalysisQueryResponse>)component.GetType().GetField(published ? "results" : "widgetResults", flags)!.GetValue(component)!;
        Assert.Equal(remaining == 0 ? 0 : 1, results.Count);
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.DoesNotContain("STALE_BEFORE_RETIREMENT", html);
        Assert.Equal(remaining < 2, html.Contains("saved selection is unchanged", StringComparison.Ordinal));
        Assert.Equal(remaining == 0, html.Contains(services.GetRequiredService<UiText>()["Analysis.DashboardAllSystemsMissing"], StringComparison.Ordinal));
        if (remaining > 0)
        {
            var selected = JsonSerializer.Deserialize<string[]>(Regex.Match(client.Kql, @"let _SelectedSystems = dynamic\((.*?)\);").Groups[1].Value)!;
            Assert.Equal(remaining, selected.Length);
            Assert.DoesNotContain("NOT-SELECTED", selected);
        }
        await using var after = fixture.Factory.CreateDbContext();
        Assert.Equal(storedDocument, (await after.AnalysisDashboards.SingleAsync()).DocumentJson);
        Assert.Equal(publishedDocument, (await after.AnalysisDashboardTemplates.SingleAsync()).DocumentJson);
    }

    private sealed class DashboardSecret : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) => Task.FromResult("test-secret");
    }

    private sealed class DashboardQueryClient : IAnalysisLogAnalyticsQuery
    {
        public int Calls { get; private set; }
        public string Kql { get; private set; } = "";
        public List<(string Workspace, string Kql)> Requests { get; } = [];
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; set; }
        public async Task<Azure.Monitor.Query.Logs.Models.LogsQueryResult> QueryAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
            string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Calls++;
            Kql = kql;
            Requests.Add((workspace.Key, kql));
            FirstCall.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            return System.ClientModel.Primitives.ModelReaderWriter.Read<Azure.Monitor.Query.Logs.Models.LogsQueryResult>(
                BinaryData.FromString("""{"tables":[{"name":"PrimaryResult","columns":[{"name":"Series","type":"string"},{"name":"Value","type":"real"}],"rows":[["DC01",1]]}]}"""))!;
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ActualLibraryProjectsNamesDescriptionsOptionsAndHiddenEntriesButNotWidgetTitles(string culture, bool trusted)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var fixture = await BrowserPageFixture.CreateAsync();
            var entry = QueryTemplateCatalog.GetActive("kql/ad/ad-processor");
            var query = AnalysisQueryTemplates.Parse(QueryTemplateCatalog.ReadPackagedTemplate(entry));
            query.StandardTextProvenanceJson = trusted ? StandardQueryTextProvenance.Create(entry) : null;
            var widget = Assert.Single(fixture.Dashboard.Document.Widgets);
            query.Id = widget.QueryDefinitionId;
            widget.Title = query.Name;
            var original = JsonSerializer.Serialize(query);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            fixture.Component.GetType().GetField("availableQueries", flags)!.SetValue(fixture.Component, new[] { query });
            fixture.Component.GetType().GetField("selectedQueryId", flags)!.SetValue(fixture.Component, query.Id);
            var dashboard = JsonSerializer.Serialize(fixture.Dashboard.Document);
            await fixture.Invoke("QueryChanged");
            var expected = trusted ? fixture.Text[entry.NameResourceKey] : query.Name;
            var description = trusted ? fixture.Text[entry.DescriptionResourceKey!] : query.Description;
            var html = fixture.Decoded;
            Assert.Contains($"<h3>{expected}</h3>", html);
            Assert.Contains($"title=\"{description}\"", html);
            Assert.Contains(fixture.Text.Format("Analysis.PlaceQueryLabel", expected), html);
            Assert.Contains(fixture.Text.Format("Analysis.RemoveFromLibraryLabel", expected), html);
            Assert.Contains(fixture.Text.Format("Analysis.QueryOption", query.Category, expected, query.Version), html);
            Assert.Contains($"<strong>{widget.Title}</strong>", html);
            Assert.Equal(dashboard, JsonSerializer.Serialize(fixture.Dashboard.Document));
            fixture.Dashboard.Document.HiddenQueryIds.Add(query.Id);
            await fixture.Invoke("QueryChanged");
            html = fixture.Decoded;
            Assert.Contains($"<span>{expected}</span>", html);
            Assert.Contains(fixture.Text.Format("Analysis.OpenedQueryOption", expected), html);
            Assert.Equal(original, JsonSerializer.Serialize(query));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static readonly ClaimsPrincipal Alice = User("alice@example.com", SqlAuthenticationRoles.Admin);
    private static readonly ClaimsPrincipal Bob = User("bob@example.com", SqlAuthenticationRoles.Admin);
    private static readonly ClaimsPrincipal Operator = User("operator@example.com", SqlAuthenticationRoles.Operator);

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("""{"widgets":42}""")]
    public async Task InvalidPublishedJsonRemainsAnExplicitErrorWithoutChangingThePublication(string json)
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            (await context.AnalysisDashboardTemplates.SingleAsync()).DocumentJson = json;
            await context.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Publishing.LoadAsync(publication.Id, Operator));
        Assert.Equal("The published dashboard is invalid.", error.Message);
        await using var verify = fixture.Factory.CreateDbContext();
        var stored = await verify.AnalysisDashboardTemplates.SingleAsync();
        Assert.Equal(json, stored.DocumentJson);
        Assert.True(stored.Published);
        Assert.Equal(1, stored.Version);
        Assert.Equal(2, (await verify.AnalysisDashboards.SingleAsync()).Revision);
    }

    [Fact]
    public async Task FailedPublicationRollsBackBothTemplateAndSourceRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailPublication BEFORE INSERT ON AnalysisDashboardTemplates
                BEGIN SELECT RAISE(ABORT, 'Publication failed'); END;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice));
        Assert.Empty(await fixture.Publishing.ListAsync(Operator));
        var personal = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal(1, personal.Revision);
        Assert.Equal(collection.Serialize(), personal.Collection!.Serialize());
    }

    [Fact]
    public async Task PublishesSavedSnapshotWithoutSharingPersonalLibraryPreferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        var saved = await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        Assert.Empty(await fixture.Publishing.ListAsync(Operator));

        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, saved.Revision, Alice);
        Assert.Equal(1, publication.Version);
        Assert.Equal("alice@example.com", publication.Owner);
        var snapshot = await fixture.Publishing.LoadAsync(publication.Id, Operator);
        Assert.Equal("AD dashboard", snapshot.Document.Name);
        Assert.Equal("DC01", Assert.Single(snapshot.Document.Systems).Computer);
        Assert.Equal(240, Assert.Single(snapshot.Document.Widgets).TimeRangeMinutes);
        Assert.Empty(snapshot.Document.HiddenQueryIds);
        Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.HiddenQueryIds);
        Assert.Null((await fixture.Repository.LoadAsync("operator@example.com")).Collection);
        Assert.Single(await fixture.Publishing.ListAsync(Bob));

        snapshot.Document.Widgets.Clear();
        Assert.Single((await fixture.Publishing.LoadAsync(publication.Id, Operator)).Document.Widgets);
    }

    [Fact]
    public async Task OperatorCanKeepAnIndependentPrivateCopyOfAnotherUsersPublishedDashboard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", original, 0);
        var publication = await fixture.Publishing.PublishAsync(original.ActiveDashboardId, 1, Alice);
        var snapshot = await fixture.Publishing.LoadAsync(publication.Id, Operator);
        var personal = AnalysisDashboardCollection.Create();
        personal.AddCopy(snapshot.Document);
        await fixture.Repository.SaveAsync("operator@example.com", personal, 0);
        var restored = (await fixture.Repository.LoadAsync("operator@example.com")).Collection!;
        Assert.NotEqual(publication.Id, restored.ActiveDashboardId);
        Assert.Equal("AD dashboard (copy)", restored.Active.Document.Name);
        restored.Active.Document.Widgets[0].Title = "Operators private customization";
        await fixture.Repository.SaveAsync("operator@example.com", restored, 1);
        await fixture.Publishing.WithdrawAsync(publication.Id, Alice);
        Assert.Equal("Operators private customization",
            (await fixture.Repository.LoadAsync("operator@example.com")).Collection!.Active.Document.Widgets[0].Title);
        Assert.Equal("LDAP latency",
            (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Widgets[0].Title);
        Assert.Empty(await fixture.Publishing.ListAsync(Operator));
    }

    [Fact]
    public async Task PersonalEditsRemainPrivateUntilRepublishedAndRevisionIsProtected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var stored = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal(2, stored.Revision);
        stored.Collection!.RenameActive("Private edit");
        stored.Collection.Active.Document.Systems[0].Computer = "DC02";
        await fixture.Repository.SaveAsync("alice@example.com", stored.Collection, stored.Revision);
        var original = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal("AD dashboard", original.Document.Name);
        Assert.Equal("DC01", original.Document.Systems[0].Computer);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 2, Alice));
        var republished = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 3, Alice);
        Assert.Equal(2, republished.Version);
        var updated = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal("Private edit", updated.Document.Name);
        Assert.Equal("DC02", updated.Document.Systems[0].Computer);
        Assert.Equal(4, (await fixture.Repository.LoadAsync("alice@example.com")).Revision);
    }

    [Theory]
    [InlineData("en-US", false, null, null, null, null, "")]
    [InlineData("de-DE", false, null, null, null, null, "")]
    [InlineData("en-US", true, null, null, null, null, "second-configured")]
    [InlineData("de-DE", true, null, null, null, null, "second-configured")]
    [InlineData("en-US", true, "first-configured", " ZULU.EXAMPLE. ", " North ", " Domain Controller ", "first-configured")]
    [InlineData("de-DE", true, "first-configured", " ZULU.EXAMPLE. ", " North ", " Domain Controller ", "first-configured")]
    [InlineData("en-US", true, "", "", "", "", "")]
    [InlineData("de-DE", true, "", "", "", "", "")]
    public async Task AnalysisPageDefaultsToCompactViewWithExistingDashboardsSystemsAndWidgets(
        string culture, bool configuredWorkspaces, string? defaultWorkspace, string? defaultDomain, string? defaultSite, string? defaultRole, string expectedWorkspace)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        var publication = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            (await context.AnalysisDashboardTemplates.SingleAsync()).Version = 7;
            context.ComputerInventory.AddRange(
                new() { SubscriptionId = "sub", Computer = "DC01", Domain = "zulu.example", Site = "North", Role = "Domain Controller" },
                new() { SubscriptionId = "other-sub", Computer = "DC02", Domain = "alpha.example", Site = "South", Role = "SQL" });
            var query = ActiveDirectoryAnalysisQueries.Create().First();
            query.Id = Guid.NewGuid();
            query.Versions.Add(new()
            {
                Id = Guid.NewGuid(), Version = 4,
                SnapshotJson = System.Text.Json.JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
            });
            query.PublishedVersionId = query.Versions.Single().Id;
            context.AnalysisQueryDefinitions.Add(query);
            await context.SaveChangesAsync();
        }
        var options = new CrossTenantLogAnalyticsOptions
        {
            Workspaces = configuredWorkspaces
                ? [
                    new()
                    {
                        Key = "first-configured", DisplayName = "Zulu <workspace> & value", Domains = ["zulu.example"],
                        DomainDisplayNames = new() { ["zulu.example"] = "Original <domain> & value" }
                    },
                    new() { Key = "second-configured", DisplayName = "alpha <workspace> & value", Domains = ["alpha.example"] }
                ]
                : []
        };
        var originalOptions = JsonSerializer.Serialize(options);
        var originalStored = await fixture.Repository.LoadAsync("alice@example.com");
        var originalJson = originalStored.Collection!.Serialize();
        var components = new CapturingComponentActivator();
        var defaults = new DashboardFilterOptions
        {
            Workspace = defaultWorkspace, Domain = defaultDomain, Site = defaultSite, Role = defaultRole
        };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var configuration = new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder());
        await using var services = new ServiceCollection().AddLogging()
            .AddUiLocalization()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(runner)
            .AddSingleton(new AlertStore(fixture.Factory, configuration, presenter, new AlertRuleEvaluator(presenter),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton(new AnalysisDashboardState())
            .AddSingleton(fixture.Repository)
            .AddSingleton(defaults)
            .AddSingleton(options)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new UnusedJavaScript())
            .BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        RenderFragment child = builder =>
        {
            builder.OpenComponent<MonitoringApp.Components.Pages.Analysis>(0);
            builder.CloseComponent();
        };
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                    ["ChildContent"] = child
                }));
            await rendered.QuiescenceTask;
            return rendered;
        });
        var rawHtml = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        var html = WebUtility.HtmlDecode(rawHtml);
        var text = services.GetRequiredService<UiText>();
        var page = Assert.IsType<MonitoringApp.Components.Pages.Analysis>(components.AnalysisPage);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        T Value<T>(string name) => (T)(page.GetType().GetField(name, flags)?.GetValue(page)
            ?? page.GetType().GetProperty(name, flags)!.GetValue(page))!;
        Assert.Contains($"{publication.Name} · v7", html);
        Assert.DoesNotContain($"{publication.Name} · {publication.Owner}", html);
        Assert.Contains("<h1>Dashboards</h1>", html);
        Assert.Contains("<p class=\"eyebrow\">Live Log Analytics</p>", html);
        Assert.Contains(culture == "de-DE"
            ? "Freigegebene, versionierte Abfragen für automatisch zugeordnete Arbeitsbereiche ausführen."
            : "Run approved, versioned queries against automatically routed workspaces.", html);
        Assert.Contains($"aria-label=\"{(culture == "de-DE" ? "Systemauswahl und Dashboardfilter" : "System selection and dashboard filters")}\"", html);
        foreach (var label in culture == "de-DE"
            ? new[] { "Arbeitsbereich", "Domäne", "Standort", "Rolle" }
            : new[] { "Workspace", "Domain", "Site", "Role" })
            Assert.Equal(1, html.Split($"<span>{label}</span>", StringSplitOptions.None).Length - 1);
        Assert.Contains(culture == "de-DE" ? "Live Log Analytics ist deaktiviert." : "Live Log Analytics is disabled.", html);
        Assert.Contains(culture == "de-DE"
            ? "Vor dem Ausführen von Analyseabfragen die validierte mandantenübergreifende Konfiguration aktivieren."
            : "Enable the validated cross-tenant configuration before running analysis queries.", html);
        var filters = Regex.Match(rawHtml, "<section class=\"analysis-controls\".*?</section>", RegexOptions.Singleline).Value;
        var selects = Regex.Matches(filters, "<select[^>]*>(.*?)</select>", RegexOptions.Singleline);
        Assert.Equal(4, selects.Count);
        var allLabels = culture == "de-DE"
            ? new[] { "Alle Arbeitsbereiche", "Alle Domänen", "Alle Standorte", "Alle Rollen" }
            : new[] { "All workspaces", "All domains", "All sites", "All roles" };
        var originals = new[]
        {
            options.Workspaces.OrderBy(workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(workspace => (Value: workspace.Key, Label: workspace.DisplayName)).ToArray(),
            Value<IEnumerable<(string Value, string Label)>>("AvailableDomains").ToArray(),
            Value<IEnumerable<ComputerInventoryEntry>>("FilteredByDomain").Select(system => system.Site)
                .OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order()
                .Select(value => (Value: value, Label: value)).ToArray(),
            Value<IEnumerable<ComputerInventoryEntry>>("FilteredBySite").Select(system => system.Role)
                .OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order()
                .Select(value => (Value: value, Label: value)).ToArray()
        };
        for (var index = 0; index < selects.Count; index++)
        {
            Assert.DoesNotContain("disabled", selects[index].Value);
            var actual = Regex.Matches(selects[index].Groups[1].Value, "<option\\b([^>]*)>(.*?)</option>", RegexOptions.Singleline)
                .Select(option =>
                {
                    var value = Regex.Match(option.Groups[1].Value, "\\bvalue(?:=\"([^\"]*)\")?(?=\\s|$)");
                    Assert.True(value.Success, option.Value);
                    return (Value: WebUtility.HtmlDecode(value.Groups[1].Value), Label: WebUtility.HtmlDecode(option.Groups[2].Value));
                }).ToArray();
            Assert.Equal(("", allLabels[index]), actual[0]);
            Assert.Equal(originals[index], actual.Skip(1));
        }
        if (configuredWorkspaces)
        {
            Assert.Contains("&lt;workspace&gt; &amp; value", rawHtml);
            if (expectedWorkspace != "second-configured") Assert.Contains("&lt;domain&gt; &amp; value", filters);
        }
        page.GetType().GetField("isDashboardRunning", flags)!.SetValue(page, true);
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
        var busyHtml = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        var busyFilters = Regex.Match(busyHtml, "<section class=\"analysis-controls\".*?</section>", RegexOptions.Singleline).Value;
        Assert.All(Regex.Matches(busyFilters, "<select[^>]*>"), select => Assert.Contains("disabled", select.Value));
        var busyHeading = Regex.Match(busyHtml, "<section class=\"query-heading[^\"]*\".*?</section>", RegexOptions.Singleline).Value;
        Assert.Contains("disabled", Regex.Match(busyHeading, "<input[^>]*>").Value);
        var busyPicker = Regex.Match(busyHtml, "<section class=\"analysis-system-picker.*?</section>", RegexOptions.Singleline).Value;
        Assert.All(Regex.Matches(busyPicker, "<input[^>]*>|<button[^>]*>"),
            control => Assert.Contains("disabled", control.Value));
        page.GetType().GetField("isDashboardRunning", flags)!.SetValue(page, false);
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
        var selectedSystems = Value<HashSet<string>>("selectedSystems");
        var originalSelection = selectedSystems.ToArray();
        var visibleSystems = Value<IEnumerable<ComputerInventoryEntry>>("VisibleSystems").ToArray();
        foreach (var count in new[] { 0, 1, 99, 100 })
        {
            selectedSystems.Clear();
            if (count > 0) selectedSystems.UnionWith(originalSelection);
            for (var index = selectedSystems.Count; index < count; index++)
                selectedSystems.Add($"original-subscription\u001fOriginal synthetic computer {index}");
            foreach (var editing in new[] { false, true })
            {
                page.GetType().GetField("editMode", flags)!.SetValue(page, editing);
                await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
                var selectionHtml = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
                var picker = Regex.Match(selectionHtml, "<section class=\"analysis-system-picker.*?</section>", RegexOptions.Singleline).Value;
                var decodedPicker = WebUtility.HtmlDecode(picker);
                Assert.Contains($"<h2 id=\"analysis-systems\">{(culture == "de-DE" ? "Systeme" : "Systems")}</h2>", decodedPicker);
                Assert.Contains(culture == "de-DE" ? $"{count} / 100 ausgewählt" : $"{count} / 100 selected", decodedPicker);
                Assert.Contains(culture == "de-DE" ? "Sichtbare auswählen" : "Select visible", decodedPicker);
                Assert.Contains(culture == "de-DE" ? ">Leeren</button>" : ">Clear</button>", decodedPicker);
                Assert.Contains("aria-labelledby=\"analysis-systems\"", picker);
                Assert.Contains("data-analysis-panel=\"systems\"", picker);
                var inputs = Regex.Matches(picker, "<input[^>]*>");
                Assert.Equal(visibleSystems.Length, inputs.Count);
                for (var index = 0; index < inputs.Count; index++)
                {
                    var system = visibleSystems[index];
                    var isSelected = selectedSystems.Contains($"{system.SubscriptionId}\u001f{system.Computer}");
                    Assert.Equal(isSelected, inputs[index].Value.Contains("checked"));
                    Assert.Equal(count == 100 && !isSelected, inputs[index].Value.Contains("disabled"));
                    Assert.Contains($">{system.Computer}</strong>", decodedPicker);
                    Assert.Contains($"{system.Domain} · {system.Site} · {system.Role}", decodedPicker);
                }
                Assert.Equal(editing, picker.Contains("data-analysis-panel-resize"));
                if (editing)
                {
                    Assert.Contains(culture == "de-DE" ? "aria-label=\"Größe der Systemauswahl ändern\"" : "aria-label=\"Resize Systems\"", decodedPicker);
                    Assert.Contains(culture == "de-DE"
                        ? "title=\"Zum Ändern der Größe der Systemauswahl ziehen; mit den Pfeiltasten fein anpassen\""
                        : "title=\"Drag to resize Systems; use arrow keys for fine adjustments\"", decodedPicker);
                }
            }
        }
        selectedSystems.Clear();
        selectedSystems.UnionWith(originalSelection);
        var dashboard = Value<AnalysisDashboardState>("Dashboard");
        var originalDashboard = JsonSerializer.Serialize(dashboard.Document);
        var originalQueries = Value<IReadOnlyList<AnalysisQueryDefinition>>("availableQueries");
        AnalysisQueryDefinition[] libraryQueries =
        [
            new() { Id = dashboard.Document.Widgets[0].QueryDefinitionId, Name = "Original <query A> & value",
                Category = "Original <category> &", Description = "Original <description> &", Unit = "Original <unit> &", Version = 7, Kql = "Original KQL A" },
            new() { Id = Guid.NewGuid(), Name = "Original <query B> & value",
                Category = "Original <category> &", Description = "Original <description> &", Unit = "Original <unit> &", Version = 9, Kql = "Original KQL B" }
        ];
        var originalLibrary = JsonSerializer.Serialize(libraryQueries);
        page.GetType().GetField("availableQueries", flags)!.SetValue(page, libraryQueries);
        page.GetType().GetField("editMode", flags)!.SetValue(page, true);
        foreach (var widgetCount in new[] { 1, 12 })
        {
            while (dashboard.Document.Widgets.Count < widgetCount)
                dashboard.Document.Widgets.Add(new() { Id = Guid.NewGuid().ToString("N"), QueryDefinitionId = Guid.NewGuid(),
                    Title = "Original widget", Column = 1, Row = 1, ColumnSpan = 1, RowSpan = 1 });
            foreach (var hiddenCount in new[] { 0, 1, 2 })
            {
                dashboard.Document.HiddenQueryIds.Clear();
                dashboard.Document.HiddenQueryIds.AddRange(libraryQueries.Take(hiddenCount).Select(query => query.Id));
                await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
                var libraryHtml = Regex.Match(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString),
                    "<section class=\"analysis-query-library.*?</section>", RegexOptions.Singleline).Value;
                var decoded = WebUtility.HtmlDecode(libraryHtml);
                Assert.Contains(culture == "de-DE" ? "Abfragebibliothek" : "Query library", decoded);
                Assert.Contains(culture == "de-DE"
                    ? "Eine Abfrage auf das Dashboard ziehen. Erneut ziehen, um sie zu verschieben. Mit der Eingabetaste wird sie an der ersten freien Position hinzugefügt."
                    : "Drag a query onto the dashboard. Drag again to reposition it. Enter adds it to the first free position.", decoded);
                Assert.Contains(culture == "de-DE" ? "Bibliothek aktualisieren" : "Refresh library", decoded);
                Assert.Contains(culture == "de-DE"
                    ? "title=\"Zum Ändern der Größe der Abfragebibliothek ziehen; mit den Pfeiltasten fein anpassen\""
                    : "title=\"Drag to resize Query library; use arrow keys for fine adjustments\"", decoded);
                Assert.Contains("data-analysis-panel=\"queries\"", libraryHtml);
                var articles = Regex.Matches(libraryHtml, "<article\\b([^>]*)>(.*?)</article>", RegexOptions.Singleline);
                Assert.Equal(2 - hiddenCount, articles.Count);
                foreach (Match article in articles)
                {
                    var id = Guid.Parse(Regex.Match(article.Groups[1].Value, "data-analysis-query-id=\"([^\"]+)\"").Groups[1].Value);
                    var query = libraryQueries.Single(query => query.Id == id);
                    var placed = query.Id == libraryQueries[0].Id;
                    var canPlace = placed || widgetCount < 12;
                    Assert.Contains($"draggable=\"{(canPlace ? "true" : "false")}\"", article.Value);
                    Assert.Contains($"tabindex=\"{(canPlace ? 0 : -1)}\"", article.Value);
                    Assert.Equal(!canPlace, article.Groups[1].Value.Contains("aria-disabled"));
                    var articleText = WebUtility.HtmlDecode(article.Value);
                    Assert.Contains(culture == "de-DE"
                        ? $"aria-label=\"{query.Name} auf das Dashboard ziehen oder die Eingabetaste drücken\""
                        : $"aria-label=\"Drag {query.Name} onto the dashboard or press Enter\"", articleText);
                    Assert.Contains($"{query.Unit} · v{query.Version}" + (placed ? culture == "de-DE" ? " · Auf dem Dashboard" : " · On dashboard" : ""), articleText);
                    Assert.Contains(culture == "de-DE"
                        ? $"aria-label=\"{query.Name} aus meiner Bibliothek entfernen\""
                        : $"aria-label=\"Remove {query.Name} from my library\"", decoded);
                    Assert.Contains("Original &lt;query", article.Value);
                }
                Assert.Equal(hiddenCount > 0, libraryHtml.Contains("class=\"analysis-hidden-queries\""));
                if (hiddenCount > 0)
                {
                    Assert.Contains(culture == "de-DE" ? $"Aus meiner Bibliothek entfernt ({hiddenCount})" : $"Removed from my library ({hiddenCount})", decoded);
                    Assert.Contains(culture == "de-DE" ? ">Wiederherstellen</button>" : ">Restore</button>", decoded);
                    foreach (var query in libraryQueries.Take(hiddenCount)) Assert.Contains(query.Name, decoded);
                }
                Assert.Equal(originalLibrary, JsonSerializer.Serialize(libraryQueries));
            }
        }
        dashboard.Load(JsonSerializer.Deserialize<AnalysisDashboardDocument>(originalDashboard)!);
        dashboard.Document.Widgets[0].Title = "Original <widget> & value";
        dashboard.Document.Widgets[0].TimeRangeMinutes = 10080;
        var originalWidgets = dashboard.Document.Widgets.ToArray();
        page.GetType().GetProperty("Options", flags)!.SetValue(page,
            new CrossTenantLogAnalyticsOptions { Enabled = true, Workspaces = options.Workspaces });
        foreach (var editing in new[] { false, true })
        foreach (var gridEditing in new[] { false, true })
        foreach (var running in new[] { false, true })
        foreach (var hasWidgets in new[] { false, true })
        {
            page.GetType().GetField("editMode", flags)!.SetValue(page, editing);
            page.GetType().GetField("dashboardEditMode", flags)!.SetValue(page, gridEditing);
            page.GetType().GetField("isDashboardRunning", flags)!.SetValue(page, running);
            dashboard.Document.Widgets.Clear();
            if (hasWidgets) dashboard.Document.Widgets.AddRange(originalWidgets);
            await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
            var gridHtml = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            var heading = WebUtility.HtmlDecode(Regex.Match(gridHtml,
                "<section class=\"query-heading[^\"]*\".*?</section>", RegexOptions.Singleline).Value);
            Assert.Equal(editing, heading.Contains(culture == "de-DE" ? "Raster bearbeiten" : "Edit grid"));
            if (editing)
            {
                Assert.Contains(culture == "de-DE" ? "Automatisch aktualisieren (30 Sekunden)" : "Auto-refresh (30 seconds)", heading);
                var buttons = Regex.Matches(heading, "<button\\b([^>]*)>(.*?)</button>", RegexOptions.Singleline);
                Assert.Equal(4, buttons.Count);
                Assert.Contains(running
                    ? culture == "de-DE" ? "Dashboard wird ausgeführt..." : "Running dashboard..."
                    : culture == "de-DE" ? "Dashboard ausführen" : "Run dashboard", buttons[0].Value);
                Assert.Equal(running || !hasWidgets || originalSelection.Length == 0, buttons[0].Groups[1].Value.Contains("disabled"));
                Assert.Contains(culture == "de-DE" ? "Rückgängig" : "Undo", buttons[1].Value);
                Assert.Contains(culture == "de-DE" ? "Wiederholen" : "Redo", buttons[2].Value);
                Assert.Contains("disabled", buttons[1].Groups[1].Value);
                Assert.Contains("disabled", buttons[2].Groups[1].Value);
                Assert.Contains(culture == "de-DE" ? "Dashboard speichern" : "Save dashboard", buttons[3].Value);
                Assert.DoesNotContain("disabled", buttons[3].Groups[1].Value);
            }
            var canEditGrid = editing && gridEditing;
            var gridControls = Regex.Match(gridHtml, "<fieldset class=\"analysis-controls\"[^>]*>");
            Assert.Equal(canEditGrid, gridControls.Success);
            if (canEditGrid)
            {
                Assert.Equal(running, gridControls.Value.Contains("disabled"));
                Assert.Contains(culture == "de-DE" ? "Dashboardrastergröße" : "Dashboard grid size",
                    WebUtility.HtmlDecode(gridControls.Value));
            }
            var canvas = Regex.Match(gridHtml, "<section class=\"analysis-dashboard-canvas\".*?</section>", RegexOptions.Singleline).Value;
            var decodedCanvas = WebUtility.HtmlDecode(canvas);
            Assert.Contains(culture == "de-DE" ? "aria-label=\"Analyse-Dashboard\"" : "aria-label=\"Analysis dashboard\"", decodedCanvas);
            Assert.Equal(canEditGrid && hasWidgets, canvas.Contains("data-inbox-drag-handle"));
            Assert.Equal(canEditGrid && hasWidgets, canvas.Contains("data-inbox-resize-handle"));
            Assert.Equal(editing && hasWidgets, canvas.Contains("class=\"inbox-widget-menu\""));
            if (!hasWidgets)
                Assert.Contains(editing
                    ? culture == "de-DE" ? "Eine freigegebene Abfrage aus der Bibliothek auf eine Dashboardzelle ziehen." : "Drag a published query from the library onto a dashboard cell."
                    : culture == "de-DE" ? "Dieses Dashboard hat keine Widgets. Zum Hinzufügen von Abfragen den Bearbeitungsmodus aktivieren." : "This dashboard has no widgets. Enable Edit to add queries.", decodedCanvas);
            else
            {
                Assert.Contains("Original &lt;widget&gt; &amp; value", canvas);
                Assert.Contains(culture == "de-DE" ? "<small>7 Tage ·" : "<small>7 days ·", decodedCanvas);
                if (editing) Assert.Contains(culture == "de-DE" ? ">Öffnen</button>" : ">Open</button>", decodedCanvas);
                if (canEditGrid)
                {
                    Assert.Contains(culture == "de-DE" ? "title=\"Kopfzeile zum Verschieben ziehen\"" : "title=\"Drag header to move\"", decodedCanvas);
                    Assert.Contains(culture == "de-DE" ? ">Entfernen</button>" : ">Remove</button>", decodedCanvas);
                    Assert.Contains(culture == "de-DE" ? "title=\"Ziehen, um die Größe von Original <widget> & value zu ändern\"" : "title=\"Drag to resize Original <widget> & value\"", decodedCanvas);
                }
            }
        }
        libraryQueries[1].Scope = AnalysisQueryScopes.Workspace;
        var originalExecutionQueries = JsonSerializer.Serialize(libraryQueries);
        AnalysisQueryExecutionPreview[] previews =
        [
            new("Original <workspace-key> &", "Original <workspace-name> &",
                ["Original <system A> &", "Original system B"], "Original KQL <value> &\n| project Computer")
        ];
        var originalPreviews = JsonSerializer.Serialize(previews);
        var originalPreviewValues = Value<IReadOnlyList<AnalysisQueryExecutionPreview>>("executionPreviews");
        var originalPreviewError = page.GetType().GetField("previewError", flags)!.GetValue(page);
        var originalOpenedWidget = page.GetType().GetField("openedWidgetId", flags)!.GetValue(page);
        var originalQueryId = Value<Guid>("selectedQueryId");
        page.GetType().GetField("editMode", flags)!.SetValue(page, true);
        foreach (var queryState in new[] { 0, 1, 2, 3 })
        foreach (var runningState in new[] { 0, 1, 2 })
        foreach (var hasSelection in new[] { false, true })
        {
            var selected = queryState == 0 ? null : libraryQueries[queryState == 1 ? 0 : 1];
            var hidden = queryState == 3;
            var opened = queryState >= 2;
            dashboard.Document.HiddenQueryIds.Clear();
            if (hidden) dashboard.Document.HiddenQueryIds.Add(libraryQueries[1].Id);
            selectedSystems.Clear();
            if (hasSelection) selectedSystems.Add("Original subscription\u001fOriginal <system> &");
            page.GetType().GetField("selectedQueryId", flags)!.SetValue(page, selected?.Id ?? Guid.Empty);
            page.GetType().GetField("openedWidgetId", flags)!.SetValue(page, opened ? originalWidgets[0].Id : null);
            page.GetType().GetField("isRunning", flags)!.SetValue(page, runningState == 1);
            page.GetType().GetField("isDashboardRunning", flags)!.SetValue(page, runningState == 2);
            page.GetType().GetField("executionPreviews", flags)!.SetValue(page, queryState >= 2 ? previews : Array.Empty<AnalysisQueryExecutionPreview>());
            page.GetType().GetField("previewError", flags)!.SetValue(page, hidden ? "Original <preview error> &" : null);
            await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
            var executionHtml = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            var controls = Regex.Match(executionHtml,
                $"<section class=\"analysis-controls\" aria-label=\"{text["Analysis.QueryControlsLabel"]}\".*?</section>",
                RegexOptions.Singleline).Value;
            var decodedControls = WebUtility.HtmlDecode(controls);
            Assert.Contains(culture == "de-DE" ? "<span>Freigegebene Abfrage</span>" : "<span>Published query</span>", decodedControls);
            var queryOptions = Regex.Matches(Regex.Match(controls, "<select[^>]*>.*?</select>", RegexOptions.Singleline).Value,
                "<option\\b[^>]*value=\"([^\"]+)\"[^>]*>(.*?)</option>", RegexOptions.Singleline);
            Assert.Equal(3, queryOptions.Count);
            Assert.Equal(Guid.Empty.ToString(), queryOptions[0].Groups[1].Value);
            Assert.Equal(culture == "de-DE" ? "Abfrage auswählen" : "Select a query", WebUtility.HtmlDecode(queryOptions[0].Groups[2].Value));
            for (var index = 0; index < libraryQueries.Length; index++)
            {
                var query = libraryQueries[index];
                Assert.Equal(query.Id.ToString(), queryOptions[index + 1].Groups[1].Value);
                var expected = hidden && index == 1
                    ? query.Name + (culture == "de-DE" ? " · Aus dem Dashboard geöffnet" : " · Opened from dashboard")
                    : $"{query.Category} · {query.Name} · v{query.Version}" +
                        (index == 1 ? culture == "de-DE" ? " · Arbeitsbereichsweit" : " · Workspace-wide" : "");
                Assert.Equal(expected, WebUtility.HtmlDecode(queryOptions[index + 1].Groups[2].Value));
            }
            Assert.Contains("Original &lt;query", controls);
            Assert.Equal(queryState >= 2, decodedControls.Contains(culture == "de-DE"
                ? "Arbeitsbereichsweite Abfrage: Ausgewählte Systeme bestimmen nur die Arbeitsbereiche. Ergebnisse können alle Systeme/Domänen in diesen Arbeitsbereichen enthalten."
                : "Workspace-wide query: selected systems choose the workspaces only. Results can include all systems/domains in those workspaces."));
            var actionButtons = Regex.Matches(decodedControls, "<button\\b([^>]*)>(.*?)</button>", RegexOptions.Singleline);
            Assert.Equal(opened ? 2 : 1, actionButtons.Count);
            Assert.Contains(runningState == 1
                ? culture == "de-DE" ? "Wird ausgeführt..." : "Running..."
                : culture == "de-DE" ? "Abfrage ausführen" : "Run query", actionButtons[0].Value);
            Assert.Equal(selected is null || !hasSelection || runningState != 0, actionButtons[0].Groups[1].Value.Contains("disabled"));
            if (opened)
            {
                Assert.Contains(culture == "de-DE" ? "Einstellungen auf das geöffnete Widget anwenden" : "Apply settings to opened widget", actionButtons[1].Value);
                Assert.Equal(runningState != 0, actionButtons[1].Groups[1].Value.Contains("disabled"));
            }
            var previewHtml = Regex.Match(executionHtml,
                "<section class=\"analysis-execution-preview\".*?</section>", RegexOptions.Singleline).Value;
            Assert.Equal(selected is not null, previewHtml.Length > 0);
            if (selected is not null)
            {
                var decodedPreview = WebUtility.HtmlDecode(previewHtml);
                Assert.Contains(culture == "de-DE" ? ">Ausführungsvorschau</h2>" : ">Execution preview</h2>", decodedPreview);
                Assert.Contains(culture == "de-DE"
                    ? "Effektiver, serverseitig erzeugter Abfrageplan; Anmeldeinformationen und Azure-Ressourcen-IDs werden nicht angezeigt."
                    : "Effective server-generated query plan; credentials and Azure resource IDs are not displayed.", decodedPreview);
                if (hidden)
                {
                    Assert.Contains("Original &lt;preview error&gt; &amp;", previewHtml);
                    Assert.DoesNotContain("analysis-execution-preview-card", previewHtml);
                }
                else if (queryState == 1)
                    Assert.Contains(culture == "de-DE"
                        ? "Mindestens ein System auswählen, um die Arbeitsbereichszuordnung und das effektive KQL anzuzeigen."
                        : "Select at least one system to see workspace routing and effective KQL.", decodedPreview);
                else
                {
                    Assert.Contains(culture == "de-DE" ? "<span>Arbeitsbereich</span>" : "<span>Workspace</span>", decodedPreview);
                    Assert.Contains(culture == "de-DE" ? "<strong>Systeme:</strong>" : "<strong>Systems:</strong>", decodedPreview);
                    Assert.Contains(culture == "de-DE" ? "<summary>Effektives KQL</summary>" : "<summary>Effective KQL</summary>", decodedPreview);
                    Assert.Contains(previews[0].WorkspaceKey, decodedPreview);
                    Assert.Contains(previews[0].WorkspaceDisplayName, decodedPreview);
                    Assert.Contains(string.Join(", ", previews[0].Systems), decodedPreview);
                    Assert.Contains(previews[0].EffectiveKql, decodedPreview);
                    Assert.Contains("Original &lt;workspace-name&gt; &amp;", previewHtml);
                    Assert.Contains("Original KQL &lt;value&gt; &amp;", previewHtml);
                }
            }
            Assert.Equal(originalExecutionQueries, JsonSerializer.Serialize(libraryQueries));
            Assert.Equal(originalPreviews, JsonSerializer.Serialize(previews));
        }
        selectedSystems.Clear();
        selectedSystems.UnionWith(originalSelection);
        page.GetType().GetField("selectedQueryId", flags)!.SetValue(page, originalQueryId);
        page.GetType().GetField("openedWidgetId", flags)!.SetValue(page, originalOpenedWidget);
        page.GetType().GetField("previewError", flags)!.SetValue(page, originalPreviewError);
        page.GetType().GetField("executionPreviews", flags)!.SetValue(page, originalPreviewValues);
        page.GetType().GetField("isRunning", flags)!.SetValue(page, false);
        page.GetType().GetField("isDashboardRunning", flags)!.SetValue(page, false);
        var originalPublications = Value<IReadOnlyList<PublishedAnalysisDashboard>>("publishedDashboards");
        var originalPublishPermission = Value<bool>("canPublishDashboard");
        var owner = Value<string>("dashboardOwnerKey");
        foreach (var publicationState in new[] { 0, 1, 2 })
        foreach (var editing in new[] { false, true })
        foreach (var permitted in new[] { false, true })
        foreach (var busy in new[] { false, true })
        {
            PublishedAnalysisDashboard[] publications = publicationState == 0 ? [] :
                [publication with { Name = "Original <shared dashboard> &", Version = 17,
                    Owner = publicationState == 1 ? owner : "Original <other owner> &" }];
            var originalPublicationValues = JsonSerializer.Serialize(publications);
            page.GetType().GetField("publishedDashboards", flags)!.SetValue(page, publications);
            page.GetType().GetField("editMode", flags)!.SetValue(page, editing);
            page.GetType().GetField("canPublishDashboard", flags)!.SetValue(page, permitted);
            page.GetType().GetField("isPublishingDashboard", flags)!.SetValue(page, busy);
            page.GetType().GetField("confirmWithdrawDashboard", flags)!.SetValue(page, publicationState == 1);
            await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
            var publicationHtml = Regex.Match(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString),
                "<section class=\"analysis-dashboard-tabs\".*?</section>", RegexOptions.Singleline).Value;
            var decoded = WebUtility.HtmlDecode(publicationHtml);
            Assert.Contains(culture == "de-DE" ? "aria-label=\"Geteilte Dashboards\"" : "aria-label=\"Shared dashboards\"", decoded);
            Assert.Contains(culture == "de-DE" ? ">Meine Dashboards</button>" : ">My dashboards</button>", decoded);
            Assert.Contains(culture == "de-DE" ? ">Dashboard duplizieren</button>" : ">Duplicate dashboard</button>", decoded);
            Assert.Equal(editing, decoded.Contains(culture == "de-DE" ? "Veröffentlichte Dashboards aktualisieren" : "Refresh published dashboards"));
            if (publicationState != 0)
            {
                Assert.Contains("Original &lt;shared dashboard&gt; &amp;", publicationHtml);
                Assert.Contains("Original <shared dashboard> & · v17", decoded);
                Assert.DoesNotContain("Original <other owner> &", decoded);
            }
            var publishVisible = editing && permitted;
            Assert.Equal(publishVisible, decoded.Contains(publicationState == 1
                ? culture == "de-DE" ? "Aktualisiertes Dashboard veröffentlichen" : "Publish updated dashboard"
                : culture == "de-DE" ? "Dashboard für alle veröffentlichen" : "Publish dashboard for everyone"));
            var withdrawalVisible = publishVisible && publicationState == 1;
            Assert.Equal(withdrawalVisible, decoded.Contains("role=\"alertdialog\""));
            Assert.Equal(withdrawalVisible, decoded.Contains(culture == "de-DE"
                ? "Veröffentlicht v17 · persönliche Änderungen bleiben bis zur erneuten Veröffentlichung privat."
                : "Published v17 · personal edits remain private until republished."));
            if (withdrawalVisible)
            {
                Assert.Contains(culture == "de-DE" ? "aria-label=\"Dashboard zurückziehen\"" : "aria-label=\"Withdraw dashboard\"", decoded);
                Assert.Contains(culture == "de-DE"
                    ? "Dieses Dashboard für alle zurückziehen? Ihr persönliches Dashboard bleibt erhalten."
                    : "Withdraw this dashboard for everyone? Your personal dashboard will be retained.", decoded);
                Assert.Contains(culture == "de-DE" ? ">Abbrechen</button>" : ">Cancel</button>", decoded);
                Assert.Contains(culture == "de-DE" ? ">Zurückziehen bestätigen</button>" : ">Confirm withdrawal</button>", decoded);
            }
            Assert.All(Regex.Matches(publicationHtml, "<button\\b[^>]*>").Cast<Match>(),
                button => Assert.Equal(busy, button.Value.Contains("disabled")));
            Assert.Equal(originalPublicationValues, JsonSerializer.Serialize(publications));
        }
        page.GetType().GetField("isPublishingDashboard", flags)!.SetValue(page, false);
        page.GetType().GetField("publishedDashboards", flags)!.SetValue(page, originalPublications);
        page.GetType().GetField("canPublishDashboard", flags)!.SetValue(page, true);
        page.GetType().GetField("editMode", flags)!.SetValue(page, true);
        page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, publication.Id);
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
        var viewingPublication = WebUtility.HtmlDecode(Regex.Match(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString),
            "<section class=\"analysis-dashboard-tabs\".*?</section>", RegexOptions.Singleline).Value);
        Assert.DoesNotContain(text["AnalysisPublications.Update"], viewingPublication);
        Assert.DoesNotContain(text["AnalysisPublications.Publish"], viewingPublication);
        Assert.DoesNotContain("role=\"alertdialog\"", viewingPublication);
        page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, null);
        page.GetType().GetField("canPublishDashboard", flags)!.SetValue(page, originalPublishPermission);
        page.GetType().GetField("confirmWithdrawDashboard", flags)!.SetValue(page, false);
        page.GetType().GetProperty("Options", flags)!.SetValue(page, options);
        dashboard.Load(JsonSerializer.Deserialize<AnalysisDashboardDocument>(originalDashboard)!);
        page.GetType().GetField("isDashboardRunning", flags)!.SetValue(page, false);
        page.GetType().GetField("dashboardEditMode", flags)!.SetValue(page, true);
        page.GetType().GetField("availableQueries", flags)!.SetValue(page, originalQueries);
        page.GetType().GetField("editMode", flags)!.SetValue(page, false);
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
        var storedAfterDisplay = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal(originalStored.Revision, storedAfterDisplay.Revision);
        Assert.Equal(originalJson, storedAfterDisplay.Collection!.Serialize());
        Assert.Equal(originalOptions, JsonSerializer.Serialize(options));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        Assert.DoesNotContain("Applies to system-based widgets", html);
        Assert.Contains(culture == "de-DE" ? "<span>Bearbeiten</span>" : "<span>Edit</span>", html);
        Assert.Contains(text["AnalysisDashboards.SelectDashboard"], html);
        Assert.Contains(text["AnalysisPublications.Duplicate"], html);
        Assert.Contains(text["Analysis.Systems"], html);
        Assert.Contains("LDAP latency", html);
        Assert.DoesNotContain(text.Format("AnalysisPublications.OwnNotice", 7), html);
        Assert.DoesNotContain(text["Analysis.QueryLibrary"], html);
        Assert.DoesNotContain(text["Analysis.PublishedQuery"], html);
        Assert.DoesNotContain(text["Analysis.ExecutionPreview"], html);
        Assert.DoesNotContain(text["AnalysisDashboards.Create"], html);
        Assert.DoesNotContain(text["Analysis.RunDashboard"], html);
        Assert.DoesNotContain(text["Analysis.SaveDashboard"], html);
        Assert.DoesNotContain(text["AnalysisPublications.Update"], html);
        Assert.DoesNotContain("data-inbox-drag-handle", html);
        Assert.DoesNotContain("data-inbox-resize-handle", html);
        Assert.DoesNotContain("data-analysis-panel-resize", html);
        Assert.DoesNotContain("@publication.Version", html);
        Assert.DoesNotContain("@OwnPublication.Version", html);
        Assert.DoesNotContain("@query.Version", html);
        var workspaceField = page.GetType().GetField("workspaceFilter", flags)!;
        Assert.Equal(expectedWorkspace, workspaceField.GetValue(page));
        Assert.Equal(CrossTenantLogAnalyticsOptions.NormalizeDomain(defaultDomain), page.GetType().GetField("domainFilter", flags)!.GetValue(page));
        Assert.Equal(defaultSite?.Trim() ?? "", page.GetType().GetField("siteFilter", flags)!.GetValue(page));
        Assert.Equal(defaultRole?.Trim() ?? "", page.GetType().GetField("roleFilter", flags)!.GetValue(page));
        page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, publication.Id);
        await renderer.Dispatcher.InvokeAsync(() =>
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
        var filteredSystems = Assert.IsAssignableFrom<IReadOnlyList<ComputerInventoryEntry>>(components.PublishedView!.FilteredSystems);
        Assert.Equal(configuredWorkspaces && defaultWorkspace != "" ? 1 : 2, filteredSystems.Count);
        if (configuredWorkspaces && defaultWorkspace != "")
            Assert.Equal(defaultWorkspace is null ? "DC02" : "DC01", Assert.Single(filteredSystems).Computer);
        workspaceField.SetValue(page, "");
        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)page.GetType().GetMethod("WorkspaceChangedAsync", flags)!.Invoke(page, [])!);
        Assert.Equal("", workspaceField.GetValue(page));

        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
        Assert.Null(page.GetType().GetField("publicationError", flags)!.GetValue(page));
        Assert.Null(page.GetType().GetField("selectedPublishedDashboard", flags)!.GetValue(page));
        Assert.True((bool)page.GetType().GetField("editMode", flags)!.GetValue(page)!);
        var copied = (await fixture.Repository.LoadAsync("alice@example.com")).Collection!;
        Assert.Equal(2, copied.Dashboards.Count);
        Assert.NotEqual(publication.Id, copied.ActiveDashboardId);
        Assert.Equal("AD dashboard (copy)", copied.Active.Document.Name);
        Assert.Equal("DC01", Assert.Single(copied.Active.Document.Systems).Computer);
        Assert.Equal("LDAP latency", Assert.Single(copied.Active.Document.Widgets).Title);
        Assert.Null(page.GetType().GetProperty("OwnPublication", flags)!.GetValue(page));
        Assert.Single(await fixture.Publishing.ListAsync(Alice));
        var source = await fixture.Publishing.LoadAsync(publication.Id, Alice);
        Assert.Equal("AD dashboard", source.Document.Name);
        Assert.Equal(7, source.Publication.Version);
        var state = (AnalysisDashboardState)page.GetType().GetProperty("Dashboard", flags)!.GetValue(page)!;
        state.UpdateWorkspace(document =>
        {
            document.Name = "Edited personal copy";
            document.Widgets[0].Title = "Private widget edit";
        });
        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
        var personalCopies = (await fixture.Repository.LoadAsync("alice@example.com")).Collection!;
        Assert.Equal(3, personalCopies.Dashboards.Count);
        Assert.Equal("Edited personal copy (copy)", personalCopies.Active.Document.Name);
        Assert.Equal("Private widget edit", personalCopies.Active.Document.Widgets[0].Title);
        Assert.Equal("Edited personal copy", personalCopies.Dashboards.Single(entry => entry.Id == copied.ActiveDashboardId).Document.Name);
        Assert.Equal("LDAP latency", (await fixture.Publishing.LoadAsync(publication.Id, Alice)).Document.Widgets[0].Title);
        Assert.Single(await fixture.Publishing.ListAsync(Alice));

        if (!configuredWorkspaces)
        {
            var current = await fixture.Repository.LoadAsync("alice@example.com");
            await fixture.Repository.SaveAsync("alice@example.com", current.Collection!, current.Revision);
            page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, publication.Id);
            await renderer.Dispatcher.InvokeAsync(async () =>
                await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
            Assert.Equal(services.GetRequiredService<UiText>()["Analysis.Errors.CopyConflict"],
                page.GetType().GetField("publicationError", flags)!.GetValue(page));
            Assert.Equal(publication.Id, page.GetType().GetField("selectedPublishedDashboard", flags)!.GetValue(page));
            Assert.Equal(personalCopies.ActiveDashboardId,
                ((AnalysisDashboardCollection)page.GetType().GetField("dashboardCollection", flags)!.GetValue(page)!).ActiveDashboardId);
            Assert.Equal(3, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
            await fixture.Publishing.WithdrawAsync(publication.Id, Alice);
            page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, publication.Id);
            await renderer.Dispatcher.InvokeAsync(async () =>
                await (Task)page.GetType().GetMethod("DuplicateDashboardAsync", flags)!.Invoke(page, [])!);
            Assert.Equal(services.GetRequiredService<UiText>()["Analysis.Errors.PublicationUnavailable"],
                page.GetType().GetField("publicationError", flags)!.GetValue(page));
            Assert.Equal(3, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task StatusMessagesFollowActualSaveUndoPublicationAndCopyOutcomes(string culture, bool stored)
    {
        using var selection = new UiSelection(culture);
        var saves = new DashboardSaveInterceptor();
        var log = new AnalysisLog<MonitoringApp.Components.Pages.Analysis>();
        await using var fixture = await Fixture.CreateAsync(interceptor: saves);
        if (stored) await fixture.Repository.SaveAsync("alice@example.com", CreateDashboard(), 0);
        var options = new CrossTenantLogAnalyticsOptions { Enabled = true };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var components = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<ILogger<MonitoringApp.Components.Pages.Analysis>>(log)
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(runner)
            .AddSingleton(new AlertStore(fixture.Factory,
                new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder()),
                presenter, new AlertRuleEvaluator(presenter), Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton(new AnalysisDashboardState()).AddSingleton(fixture.Repository)
            .AddSingleton(new DashboardFilterOptions()).AddSingleton(options).AddSingleton(TimeProvider.System)
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new UnusedJavaScript()).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent<MonitoringApp.Components.Pages.Analysis>(0);
                        builder.CloseComponent();
                    })
                }));
            await root.QuiescenceTask;
            return root;
        });
        var page = Assert.IsType<MonitoringApp.Components.Pages.Analysis>(components.AnalysisPage);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        page.GetType().GetField("editMode", flags)!.SetValue(page, true);
        async Task<string> Invoke(string method, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (page.GetType().GetMethod(method, flags | System.Reflection.BindingFlags.Public)!.Invoke(page, arguments) is Task task)
                    await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        }
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []));
        var initial = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains(stored
            ? culture == "de-DE" ? "Dashboard wiederhergestellt · Revision 1" : "Dashboard restored · revision 1"
            : culture == "de-DE" ? "Dashboard nicht gespeichert" : "Dashboard not saved", initial);
        var from = new DateTimeOffset(2026, 11, 6, 12, 34, 56, TimeSpan.Zero);
        var result = new AnalysisQueryResponse(Guid.NewGuid(), "Original <result> &", 2, "ms",
            from, from.AddHours(1), from.AddHours(1), 1234, [], []);
        var resultSnapshot = JsonSerializer.Serialize(result);
        await renderer.Dispatcher.InvokeAsync(() => page.GetType().GetField("result", flags)!.SetValue(page, result));
        var resultHtml = await Invoke("StateHasChanged");
        Assert.Contains(culture == "de-DE"
            ? "Version 2 · 2026-11-06 12:34:56Z bis 2026-11-06 13:34:56Z · 1234 ms"
            : "Version 2 · 2026-11-06 12:34:56Z to 2026-11-06 13:34:56Z · 1234 ms", resultHtml);
        Assert.Contains("Original <result> &", resultHtml);
        Assert.Contains("Original &lt;result&gt; &amp;", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Equal(resultSnapshot, JsonSerializer.Serialize(result));
        await renderer.Dispatcher.InvokeAsync(() => page.GetType().GetField("result", flags)!.SetValue(page, null));
        var state = services.GetRequiredService<AnalysisDashboardState>();
        state.UpdateWorkspace(document => document.Name = "Original <dashboard> &");
        var widget = state.Document.Widgets.FirstOrDefault() ?? state.AddWidget(Guid.NewGuid(), "Original widget", 240);
        var beforeEdits = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Contains(culture == "de-DE" ? "Nicht gespeicherte Widgetänderungen" : "Unsaved widget changes",
            await Invoke("RemoveWidget", widget.Id));
        Assert.Empty(state.Document.Widgets);
        Assert.Contains(culture == "de-DE" ? "Nicht gespeicherte Rückgängig-Aktion" : "Unsaved undo", await Invoke("UndoDashboard"));
        Assert.Single(state.Document.Widgets);
        Assert.Contains(culture == "de-DE" ? "Nicht gespeicherte Wiederholen-Aktion" : "Unsaved redo", await Invoke("RedoDashboard"));
        Assert.Empty(state.Document.Widgets);
        await Invoke("UndoDashboard");
        var afterEdits = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal(beforeEdits.Revision, afterEdits.Revision);
        Assert.Equal(beforeEdits.Collection?.Serialize(), afterEdits.Collection?.Serialize());
        var savedHtml = await Invoke("SaveDashboardAsync");
        var saved = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal("Original <dashboard> &", saved.Collection!.Active.Document.Name);
        Assert.Contains(culture == "de-DE" ? $"Dashboard gespeichert · Revision {saved.Revision}" : $"Dashboard saved · revision {saved.Revision}", savedHtml);
        async Task CheckErrorPhases()
        {
            var text = services.GetRequiredService<UiText>();
            object? Get(string field) => page.GetType().GetField(field, flags)!.GetValue(page);
            Task Set(string field, object? value) => renderer.Dispatcher.InvokeAsync(() =>
                page.GetType().GetField(field, flags)!.SetValue(page, value));
            void AssertError(string key)
            {
                Assert.Equal(text[key], Get("errorMessage"));
                Assert.False(Assert.IsType<bool>(Get("isSavingDashboard")));
                Assert.False(Assert.IsType<bool>(Get("isChangingDashboard")));
            }
            async Task Recover()
            {
                var recovered = await fixture.Repository.LoadAsync("alice@example.com");
                await Invoke("ActivatePersonalDashboard", recovered.Collection!, recovered.Revision);
                state.UpdateWorkspace(document => document.Name = "Original <dashboard> &");
                await Invoke("SaveDashboardAsync");
                Assert.Null(Get("errorMessage"));
            }
            var workspaceBefore = JsonSerializer.Serialize(state.Document);
            foreach (var table in new[] { "AnalysisDashboards", "ComputerInventory" })
            {
                var collectionBefore = Get("dashboardCollection");
                var inventoryBefore = Get("inventory");
                var attemptsBefore = saves.Attempts;
                await using (var context = fixture.Factory.CreateDbContext())
                    await context.Database.ExecuteSqlRawAsync(table == "AnalysisDashboards"
                        ? "ALTER TABLE \"AnalysisDashboards\" RENAME TO \"UnavailableWorkspaceData\";"
                        : "ALTER TABLE \"ComputerInventory\" RENAME TO \"UnavailableWorkspaceData\";");
                try
                {
                    var failedWorkspace = await Invoke("OnInitializedAsync");
                    Assert.True(Assert.IsType<bool>(Get("dashboardLoadFailed")));
                    Assert.False(Assert.IsType<bool>(Get("isLoadingDashboard")));
                    Assert.Contains(text["Analysis.Errors.WorkspaceLoadFailed"], failedWorkspace);
                    Assert.IsType<SqliteException>(log.Entries[^1].Exception);
                    Assert.DoesNotContain(log.Entries[^1].Exception!.Message, failedWorkspace);
                    Assert.Same(collectionBefore, Get("dashboardCollection"));
                    Assert.Same(inventoryBefore, Get("inventory"));
                    Assert.Equal(workspaceBefore, JsonSerializer.Serialize(state.Document));
                    await Invoke("SaveDashboardAsync");
                    Assert.Equal(attemptsBefore, saves.Attempts);
                }
                finally
                {
                    await using var context = fixture.Factory.CreateDbContext();
                    await context.Database.ExecuteSqlRawAsync(table == "AnalysisDashboards"
                        ? "ALTER TABLE \"UnavailableWorkspaceData\" RENAME TO \"AnalysisDashboards\";"
                        : "ALTER TABLE \"UnavailableWorkspaceData\" RENAME TO \"ComputerInventory\";");
                }
                await Invoke("LoadWorkspaceAsync");
                Assert.False(Assert.IsType<bool>(Get("dashboardLoadFailed")));
                Assert.Equal(workspaceBefore, JsonSerializer.Serialize(state.Document));
                Assert.Equal(attemptsBefore, saves.Attempts);
            }
            string originalJson;
            await using (var context = fixture.Factory.CreateDbContext())
                originalJson = (await context.AnalysisDashboards.SingleAsync()).DocumentJson;
            foreach (var corrupt in new[] { "{", "null", """{"dashboards":[]}""" })
            {
                await using (var context = fixture.Factory.CreateDbContext())
                    await context.AnalysisDashboards.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DocumentJson, corrupt));
                try
                {
                    var failedWorkspace = await Invoke("LoadWorkspaceAsync");
                    Assert.True(Assert.IsType<bool>(Get("dashboardLoadFailed")));
                    Assert.Contains(text["Analysis.Errors.WorkspaceLoadFailed"], failedWorkspace);
                    Assert.IsType<InvalidOperationException>(log.Entries[^1].Exception);
                    Assert.Equal(workspaceBefore, JsonSerializer.Serialize(state.Document));
                    var attemptsBefore = saves.Attempts;
                    await Invoke("SaveDashboardAsync");
                    Assert.Equal(attemptsBefore, saves.Attempts);
                    await using var verification = fixture.Factory.CreateDbContext();
                    Assert.Equal(corrupt, (await verification.AnalysisDashboards.SingleAsync()).DocumentJson);
                }
                finally
                {
                    await using var context = fixture.Factory.CreateDbContext();
                    await context.AnalysisDashboards.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DocumentJson, originalJson));
                }
                await Invoke("LoadWorkspaceAsync");
                Assert.False(Assert.IsType<bool>(Get("dashboardLoadFailed")));
            }
            await Set("editMode", true);
            var queries = Get("availableQueries");
            await using (var context = fixture.Factory.CreateDbContext())
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AnalysisQueryDefinitions\" RENAME TO \"UnavailableQueries\";");
            try
            {
                var failedLibrary = await Invoke("RefreshLibraryAsync");
                Assert.Equal(text["Analysis.Errors.LibraryLoadFailed"], Get("libraryLoadError"));
                Assert.Contains(text["Analysis.Errors.LibraryLoadFailed"], failedLibrary);
                Assert.Same(queries, Get("availableQueries"));
                Assert.False(Assert.IsType<bool>(Get("isRefreshingLibrary")));
                Assert.IsType<SqliteException>(log.Entries[^1].Exception);
                Assert.DoesNotContain(log.Entries[^1].Exception!.Message, failedLibrary);
                var failedInitialLibrary = await Invoke("OnInitializedAsync");
                Assert.Equal(text["Analysis.Errors.LibraryLoadFailed"], Get("libraryLoadError"));
                Assert.Contains(text["Navigation.Dashboards"], failedInitialLibrary);
                await Invoke("SaveDashboardAsync");
                Assert.Null(Get("errorMessage"));
                Assert.Equal(text["Analysis.Errors.LibraryLoadFailed"], Get("libraryLoadError"));
            }
            finally
            {
                await using var context = fixture.Factory.CreateDbContext();
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableQueries\" RENAME TO \"AnalysisQueryDefinitions\";");
            }
            await Invoke("RefreshLibraryAsync");
            Assert.Null(Get("libraryLoadError"));
            var publicationQueries = ActiveDirectoryAnalysisQueries.Create().Take(2).ToArray();
            var publicationVersions = publicationQueries.Select(query =>
            {
                query.Id = Guid.NewGuid();
                var version = new AnalysisQueryVersion
                {
                    Id = Guid.NewGuid(), QueryDefinitionId = query.Id, Version = 1,
                    SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query)),
                    PublishedBy = "original-publisher"
                };
                query.PublishedVersionId = version.Id;
                return version;
            }).ToArray();
            await using (var context = fixture.Factory.CreateDbContext())
            {
                context.AnalysisQueryDefinitions.AddRange(publicationQueries);
                context.AnalysisQueryVersions.AddRange(publicationVersions);
                await context.SaveChangesAsync();
            }
            try
            {
                await Invoke("RefreshLibraryAsync");
                Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<AnalysisQueryDefinition>>(Get("availableQueries")).Count);
                var cachedLibrary = Get("availableQueries");
                var documentBeforeRead = JsonSerializer.Serialize(state.Document);
                var selectedBeforeRead = Get("selectedQueryId");
                foreach (var fault in new[] { "missing-version", "null", "json", "array" })
                {
                    var source = publicationQueries[0];
                    var version = publicationVersions[0];
                    var corrupt = fault switch
                    {
                        "null" => "null", "json" => "{\n\"name\":\"private JSON diagnostic\",",
                        "array" => "[]", _ => version.SnapshotJson
                    };
                    var pointer = fault == "missing-version" ? Guid.NewGuid() : version.Id;
                    await using (var context = fixture.Factory.CreateDbContext())
                    {
                        await context.AnalysisQueryDefinitions.Where(item => item.Id == source.Id)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PublishedVersionId, pointer));
                        await context.AnalysisQueryVersions.Where(item => item.Id == version.Id)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SnapshotJson, corrupt));
                    }
                    var attemptsBeforeRead = saves.Attempts;
                    try
                    {
                        var expected = text[fault == "missing-version" ? "Analysis.Publication.VersionUnavailable" : "Analysis.Publication.SnapshotInvalid"];
                        var failedRead = await Invoke("RefreshLibraryAsync");
                        Assert.Equal(expected, Get("libraryLoadError"));
                        Assert.Contains(expected, failedRead);
                        Assert.Same(cachedLibrary, Get("availableQueries"));
                        Assert.Equal(documentBeforeRead, JsonSerializer.Serialize(state.Document));
                        Assert.Equal(selectedBeforeRead, Get("selectedQueryId"));
                        Assert.False(Assert.IsType<bool>(Get("isRefreshingLibrary")));
                        Assert.Equal(LogLevel.Error, log.Entries[^1].Level);
                        Assert.DoesNotContain("private JSON diagnostic", failedRead);
                        var execution = services.GetRequiredService<AnalysisQueryExecutionService>();
                        var error = await Record.ExceptionAsync(() => execution.ExecuteAsync(
                            new(source.Id, [new("subscription", "DC01")], 60)));
                        if (fault is "json" or "array")
                        {
                            Assert.IsType<AnalysisQuerySnapshotJsonException>(log.Entries[^1].Exception);
                            Assert.IsType<AnalysisQuerySnapshotJsonException>(error);
                        }
                        else
                        {
                            var code = fault == "null" ? AnalysisQueryPublicationCode.SnapshotInvalid : AnalysisQueryPublicationCode.VersionUnavailable;
                            Assert.Equal(code, Assert.IsType<AnalysisQueryPublicationException>(log.Entries[^1].Exception).Code);
                            Assert.Equal(code, Assert.IsType<AnalysisQueryPublicationException>(error).Code);
                        }
                        Assert.Equal(attemptsBeforeRead, saves.Attempts);
                        await using var verification = fixture.Factory.CreateDbContext();
                        var storedQuery = await verification.AnalysisQueryDefinitions.SingleAsync(item => item.Id == source.Id);
                        var storedVersion = await verification.AnalysisQueryVersions.SingleAsync(item => item.Id == version.Id);
                        Assert.Equal(pointer, storedQuery.PublishedVersionId);
                        Assert.Equal(source.Status, storedQuery.Status);
                        Assert.Equal(source.UpdatedAt, storedQuery.UpdatedAt);
                        Assert.Equal(source.UpdatedBy, storedQuery.UpdatedBy);
                        Assert.Equal(source.RowVersion, storedQuery.RowVersion);
                        Assert.Equal(corrupt, storedVersion.SnapshotJson);
                        Assert.Equal(version.PublishedBy, storedVersion.PublishedBy);
                    }
                    finally
                    {
                        await using var context = fixture.Factory.CreateDbContext();
                        await context.AnalysisQueryDefinitions.Where(item => item.Id == source.Id)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PublishedVersionId, version.Id));
                        await context.AnalysisQueryVersions.Where(item => item.Id == version.Id)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SnapshotJson, version.SnapshotJson));
                    }
                    await Invoke("RefreshLibraryAsync");
                    Assert.Null(Get("libraryLoadError"));
                    Assert.Equal(attemptsBeforeRead, saves.Attempts);
                    cachedLibrary = Get("availableQueries");
                }
            }
            finally
            {
                var ids = publicationQueries.Select(query => query.Id).ToArray();
                await using var context = fixture.Factory.CreateDbContext();
                await context.AnalysisQueryDefinitions.Where(query => ids.Contains(query.Id)).ExecuteDeleteAsync();
            }
            await Invoke("RefreshLibraryAsync");
            Assert.Null(Get("libraryLoadError"));
            var selectedBefore = Assert.IsType<HashSet<string>>(Get("selectedSystems")).ToArray();
            var inventoryBeforeExecution = Get("inventory");
            var queryIdBefore = Get("selectedQueryId");
            var minutesBefore = Get("timeRangeMinutes");
            var selected = Assert.IsType<HashSet<string>>(Get("selectedSystems"));
            var missingQuery = ActiveDirectoryAnalysisQueries.Create().First();
            missingQuery.Id = Guid.NewGuid();
            try
            {
                await Set("availableQueries", new[] { missingQuery });
                await Set("selectedQueryId", missingQuery.Id);
                await Set("inventory", new ComputerInventoryEntry[] { new() { SubscriptionId = "sub", Computer = "DC01" } });
                selected.Clear();
                selected.Add("sub\u001fDC01");
                var failedDetail = await Invoke("RunAsync");
                AssertError("Analysis.Errors.QueryUnavailable");
                Assert.Contains(text["Analysis.Errors.QueryUnavailable"], failedDetail);
                Assert.IsType<KeyNotFoundException>(log.Entries[^1].Exception);
                Assert.DoesNotContain(log.Entries[^1].Exception!.Message, failedDetail);
                await Set("timeRangeMinutes", 4);
                var failedPreview = await Invoke("RefreshExecutionPreview");
                var validation = Assert.IsType<AnalysisValidationException>(log.Entries[^1].Exception);
                Assert.Equal(AnalysisValidationCode.TimeRangeInvalid, validation.Code);
                var expectedPreview = text.Format("Analysis.Validation.TimeRangeInvalid", missingQuery.MaxTimeRangeMinutes);
                Assert.Equal(expectedPreview, Get("previewError"));
                Assert.Contains(expectedPreview, failedPreview);
                if (culture == "de-DE") Assert.DoesNotContain(validation.Message, failedPreview);
            }
            finally
            {
                selected.Clear();
                foreach (var key in selectedBefore) selected.Add(key);
                await Set("availableQueries", queries);
                await Set("inventory", inventoryBeforeExecution);
                await Set("selectedQueryId", queryIdBefore);
                await Set("timeRangeMinutes", minutesBefore);
                await Set("previewError", null);
            }
            var widgetsBefore = state.Document.Widgets.ToArray();
            try
            {
                await Set("availableQueries", new[] { missingQuery });
                state.Document.Widgets.Clear();
                for (var index = 0; index < 11; index++)
                    state.AddWidget(Guid.NewGuid(), $"Custom widget {index}", 60, 0, index);
                var atEleven = saves.Attempts;
                await Invoke("PlaceDashboardQuery", missingQuery.Id.ToString(), 1, 1);
                Assert.Equal(12, state.Document.Widgets.Count);
                Assert.Equal(atEleven + 1, saves.Attempts);
                await Invoke("PlaceDashboardQuery", missingQuery.Id.ToString(), 2, 2);
                Assert.Equal(12, state.Document.Widgets.Count);
                Assert.Equal(atEleven + 2, saves.Attempts);
                var additionalQuery = ActiveDirectoryAnalysisQueries.Create().First();
                additionalQuery.Id = Guid.NewGuid();
                await Set("availableQueries", new[] { missingQuery, additionalQuery });
                var atLimit = saves.Attempts;
                var limit = await Invoke("PlaceDashboardQuery", additionalQuery.Id.ToString(), 0, 0);
                AssertError("Analysis.Validation.WidgetLimit");
                Assert.Contains(text["Analysis.Validation.WidgetLimit"], limit);
                await Invoke("PlaceDashboardQuery", Guid.NewGuid().ToString(), 0, 0);
                AssertError("Analysis.Errors.QueryUnavailable");
                var invalidPanel = await Invoke("CommitAnalysisPanelResize", "<Unknown panel>", 100, 100);
                AssertError("Analysis.Validation.PanelMissing");
                Assert.Contains(text["Analysis.Validation.PanelMissing"], invalidPanel);
                Assert.DoesNotContain("<Unknown panel>", invalidPanel);
                Assert.Equal(atLimit, saves.Attempts);
                Assert.Equal(12, state.Document.Widgets.Count);
            }
            finally
            {
                state.Document.Widgets.Clear();
                state.Document.Widgets.AddRange(widgetsBefore);
                await Set("availableQueries", queries);
                await Invoke("SaveDashboardAsync");
            }
            foreach (var name in new[] { "", " ", new string('x', 257) })
            {
                var beforeInvalidName = saves.Attempts;
                var beforeName = state.Document.Name;
                var invalidName = await Invoke("RenameDashboardAsync", name);
                AssertError("Analysis.Validation.DashboardNameInvalid");
                Assert.Contains(text["Analysis.Validation.DashboardNameInvalid"], invalidName);
                Assert.Equal(AnalysisValidationCode.DashboardNameInvalid,
                    Assert.IsType<AnalysisValidationException>(log.Entries[^1].Exception).Code);
                Assert.Equal(beforeInvalidName + 1, saves.Attempts);
                Assert.Equal(beforeName, state.Document.Name);
                Assert.Equal(beforeName, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Name);
            }
            var beforeOverrides = JsonSerializer.Serialize(state.Document);
            var overrideAttempts = saves.Attempts;
            foreach (var settings in new (int? TimeRangeMinutes, int? BinMinutes)[]
            {
                (0, null), (5, null), (43201, null), (null, 0), (null, 5), (null, 1440)
            })
            {
                var invalid = await Invoke("DashboardExecutionSettingsChangedAsync", settings);
                var key = settings.TimeRangeMinutes.HasValue
                    ? "Analysis.Validation.DashboardTimeRangeInvalid" : "Analysis.Validation.DashboardIntervalInvalid";
                AssertError(key);
                Assert.Contains(text[key], invalid);
                Assert.IsType<AnalysisValidationException>(log.Entries[^1].Exception);
                Assert.Equal(LogLevel.Warning, log.Entries[^1].Level);
                Assert.Equal(beforeOverrides, JsonSerializer.Serialize(state.Document));
                Assert.Equal(overrideAttempts, saves.Attempts);
            }
            await Invoke("SaveDashboardAsync");
            Assert.Null(Get("errorMessage"));
            var beforeGuards = saves.Attempts;
            await Set("isSavingDashboard", true);
            try
            {
                Assert.Contains(text["Analysis.Errors.SaveInProgress"], await Invoke("SaveDashboardAsync"));
                Assert.Equal(beforeGuards, saves.Attempts);
            }
            finally { await Set("isSavingDashboard", false); }
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, Guid.NewGuid());
                try
                {
                    var save = Assert.IsType<Task<bool>>(page.GetType().GetMethod("TrySaveDashboardAsync", flags)!.Invoke(page, []));
                    Assert.False(await save);
                }
                finally { page.GetType().GetField("selectedPublishedDashboard", flags)!.SetValue(page, null); }
            });
            Assert.Equal(text["Analysis.Errors.PublishedReadOnly"], Get("publicationError"));
            Assert.Equal(beforeGuards, saves.Attempts);
            await Set("publicationError", null);
            await Invoke("SaveDashboardAsync");
            Assert.Null(Get("errorMessage"));

            var current = await fixture.Repository.LoadAsync("alice@example.com");
            await Set("dashboardRevision", current.Revision - 1);
            var stale = await Invoke("SaveDashboardAsync");
            AssertError("Analysis.Errors.SavedElsewhere");
            Assert.Contains(text["Analysis.Errors.SavedElsewhere"], stale);
            Assert.IsType<DbUpdateConcurrencyException>(log.Entries[^1].Exception);
            Assert.Equal(current.Revision, (await fixture.Repository.LoadAsync("alice@example.com")).Revision);
            await Recover();

            await using (var context = fixture.Factory.CreateDbContext())
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER FailAnalysisSave BEFORE UPDATE ON AnalysisDashboards BEGIN SELECT RAISE(ABORT, 'Sensitive dashboard provider failure'); END;");
            var beforeProvider = (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Serialize();
            try
            {
                var providerFailure = await Invoke("SaveDashboardAsync");
                AssertError("Analysis.Errors.SaveUnconfirmed");
                Assert.Contains(text["Analysis.Errors.SaveUnconfirmed"], providerFailure);
                Assert.IsType<SqliteException>(Assert.IsType<DbUpdateException>(log.Entries[^1].Exception).InnerException);
                Assert.DoesNotContain("Sensitive dashboard provider failure", providerFailure);
                Assert.Equal(beforeProvider, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Serialize());
            }
            finally
            {
                await using var context = fixture.Factory.CreateDbContext();
                await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailAnalysisSave;");
            }
            await Recover();
            foreach (var afterCommit in new[] { false, true })
            {
                current = await fixture.Repository.LoadAsync("alice@example.com");
                state.UpdateWorkspace(document => document.Name = "Original attempted <save> &");
                var draft = JsonSerializer.Serialize(state.Document);
                var attempts = saves.Attempts;
                saves.FailAt = attempts + 1;
                saves.AfterCommit = afterCommit;
                try { await Invoke("SaveDashboardAsync"); }
                finally { saves.FailAt = 0; }
                AssertError("Analysis.Errors.SaveUnconfirmed");
                Assert.Same(saves.Failure, log.Entries[^1].Exception);
                Assert.Equal(attempts + 1, saves.Attempts);
                Assert.Equal(current.Revision, Get("dashboardRevision"));
                Assert.Equal(draft, JsonSerializer.Serialize(state.Document));
                var outcome = await fixture.Repository.LoadAsync("alice@example.com");
                Assert.Equal(current.Revision + (afterCommit ? 1 : 0), outcome.Revision);
                Assert.Equal(afterCommit ? "Original attempted <save> &" : current.Collection!.Active.Document.Name,
                    outcome.Collection!.Active.Document.Name);
                Assert.DoesNotContain(saves.Failure.Message, await Invoke("StateHasChanged"));
                await Recover();
            }
            foreach (var phase in new[] { "concurrency", "before", "after" })
            {
                current = await fixture.Repository.LoadAsync("alice@example.com");
                var draft = JsonSerializer.Serialize(state.Document);
                var attempts = saves.Attempts;
                saves.Failure = phase == "concurrency"
                    ? new DbUpdateConcurrencyException("Sensitive collection concurrency detail.")
                    : new InvalidOperationException("Sensitive collection <change> & detail.");
                saves.FailAt = attempts + 2;
                saves.AfterCommit = phase == "after";
                string failedChange;
                try { failedChange = await Invoke("RenameDashboardAsync", "Original pending <rename> &"); }
                finally { saves.FailAt = 0; }
                var key = phase == "concurrency" ? "Analysis.Errors.CollectionChanged" : "Analysis.Errors.ChangeFailed";
                AssertError(key);
                Assert.Contains(text[key], failedChange);
                Assert.Same(saves.Failure, log.Entries[^1].Exception);
                Assert.DoesNotContain(saves.Failure.Message, failedChange);
                Assert.Equal(attempts + 2, saves.Attempts);
                Assert.Equal(draft, JsonSerializer.Serialize(state.Document));
                var outcome = await fixture.Repository.LoadAsync("alice@example.com");
                Assert.Equal(current.Revision + (phase == "after" ? 2 : 1), outcome.Revision);
                Assert.Equal(phase == "after" ? "Original pending <rename> &" : current.Collection!.Active.Document.Name,
                    outcome.Collection!.Active.Document.Name);
                await Recover();
            }
        }
        await CheckErrorPhases();
        async Task CheckPublicationPhases()
        {
            var text = services.GetRequiredService<UiText>();
            object? Get(string field) => page.GetType().GetField(field, flags)!.GetValue(page);
            Task Set(string name, object? value) => renderer.Dispatcher.InvokeAsync(() =>
            {
                var field = page.GetType().GetField(name, flags);
                if (field is not null) field.SetValue(page, value);
                else page.GetType().GetProperty(name, flags)!.SetValue(page, value);
            });
            var baseline = (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Copy();
            async Task Reset()
            {
                saves.FailAt = 0;
                saves.AfterPublicationWrite = null;
                await using (var context = fixture.Factory.CreateDbContext())
                    await context.AnalysisDashboardTemplates.ExecuteDeleteAsync();
                var latest = await fixture.Repository.LoadAsync("alice@example.com");
                await fixture.Repository.SaveAsync("alice@example.com", baseline, latest.Revision);
                await Invoke("LoadWorkspaceAsync");
                await Invoke("ReloadPublicationsAsync");
                await Set("editMode", true);
            }
            foreach (var action in new[] { "publish", "withdraw", "copy" })
            foreach (var afterCommit in new[] { false, true })
            {
                await Reset();
                if (action == "withdraw")
                {
                    var source = await fixture.Repository.LoadAsync("alice@example.com");
                    await fixture.Publishing.PublishAsync(source.Collection!.ActiveDashboardId, source.Revision, Alice);
                    await Invoke("LoadWorkspaceAsync");
                    await Invoke("ReloadPublicationsAsync");
                }
                var current = await fixture.Repository.LoadAsync("alice@example.com");
                var attempts = saves.Attempts;
                saves.Failure = new InvalidOperationException("Sensitive publication <failure> &");
                saves.FailAt = attempts + (action == "withdraw" ? 1 : 2);
                saves.AfterCommit = afterCommit;
                var method = action == "publish" ? "PublishDashboardAsync"
                    : action == "withdraw" ? "WithdrawDashboardAsync" : "DuplicateDashboardAsync";
                string failed;
                try { failed = await Invoke(method); }
                finally { saves.FailAt = 0; }
                var key = action == "publish" ? "Analysis.Errors.PublishUnconfirmed"
                    : action == "withdraw" ? "Analysis.Errors.WithdrawUnconfirmed" : "Analysis.Errors.CopyFailed";
                Assert.Equal(text[key], Get("publicationError"));
                Assert.Contains(text[key], failed);
                Assert.DoesNotContain(saves.Failure.Message, failed);
                Assert.Same(saves.Failure, log.Entries[^1].Exception);
                Assert.False(Assert.IsType<bool>(Get("isPublishingDashboard")));
                Assert.False(Assert.IsType<bool>(Get("isChangingDashboard")));
                Assert.Equal(attempts + (action == "withdraw" ? 1 : 2), saves.Attempts);
                Assert.Null(Get("publicationMessage"));
                if (action == "publish")
                    Assert.Equal(afterCommit ? 1 : 0, (await fixture.Publishing.ListAsync(Alice)).Count);
                else if (action == "withdraw")
                    Assert.Equal(afterCommit ? 0 : 1, (await fixture.Publishing.ListAsync(Alice)).Count);
                else
                {
                    Assert.Single(Assert.IsType<AnalysisDashboardCollection>(Get("dashboardCollection")).Dashboards);
                    Assert.Equal(afterCommit ? 2 : 1,
                        (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
                }
            }
            foreach (var action in new[] { "PublishDashboardAsync", "DuplicateDashboardAsync" })
            {
                await Reset();
                var attempts = saves.Attempts;
                saves.Failure = new DbUpdateConcurrencyException("Sensitive publication concurrency detail.");
                saves.FailAt = attempts + 2;
                saves.AfterCommit = false;
                try { await Invoke(action); }
                finally { saves.FailAt = 0; }
                Assert.Equal(text[action == "PublishDashboardAsync" ? "Analysis.Errors.PublicationConflict" : "Analysis.Errors.CopyConflict"],
                    Get("publicationError"));
                Assert.Same(saves.Failure, log.Entries[^1].Exception);
            }
            await Reset();
            await Set("AuthenticationStateTask", Task.FromResult(new AuthenticationState(Operator)));
            try
            {
                await Invoke("PublishDashboardAsync");
                Assert.Equal(text["Analysis.Errors.PublicationNotAllowed"], Get("publicationError"));
                Assert.IsType<UnauthorizedAccessException>(log.Entries[^1].Exception);
            }
            finally { await Set("AuthenticationStateTask", Task.FromResult(new AuthenticationState(Alice))); }
            await Invoke("ReloadPublicationsAsync");
            Assert.Equal(text["Analysis.Errors.PublicationNotAllowed"], Get("publicationError"));
            await Set("publicationError", null);
            saves.AfterPublicationWrite = context => context.Database.ExecuteSqlRaw(
                "ALTER TABLE \"AnalysisDashboardTemplates\" RENAME TO \"UnavailablePublications\";");
            try
            {
                var confirmedWithLoadFailure = await Invoke("PublishDashboardAsync");
                Assert.Null(Get("publicationError"));
                Assert.NotNull(Get("publicationMessage"));
                Assert.Equal(text["Analysis.Errors.PublicationLoadFailed"], Get("publicationLoadError"));
                Assert.Contains(text["Analysis.Errors.PublicationLoadFailed"], confirmedWithLoadFailure);
                Assert.IsType<SqliteException>(log.Entries[^1].Exception);
            }
            finally
            {
                saves.AfterPublicationWrite = null;
                await using var context = fixture.Factory.CreateDbContext();
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"UnavailablePublications\" RENAME TO \"AnalysisDashboardTemplates\";");
            }
            await Invoke("ReloadPublicationsAsync");
            Assert.Null(Get("publicationLoadError"));
            Assert.Single(await fixture.Publishing.ListAsync(Alice));
            await Reset();
            await Set("publicationMessage", null);
            await Set("publicationError", null);
        }
        await CheckPublicationPhases();
        var publicationHtml = await Invoke("PublishDashboardAsync");
        var publication = Assert.Single(await fixture.Publishing.ListAsync(Alice));
        Assert.Equal("Original <dashboard> &", publication.Name);
        Assert.Equal(1, publication.Version);
        Assert.Contains(culture == "de-DE" ? "Original <dashboard> & v1 für alle veröffentlicht." : "Published Original <dashboard> & v1 for everyone.", publicationHtml);
        Assert.Contains("Original &lt;dashboard&gt; &amp;", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Null(page.GetType().GetField("publicationError", flags)!.GetValue(page));
        Assert.Contains(culture == "de-DE" ? "Dashboard zurückgezogen. Es ist jetzt nur für Sie sichtbar." : "Dashboard withdrawn. It is now private to you.",
            await Invoke("WithdrawDashboardAsync"));
        Assert.Empty(await fixture.Publishing.ListAsync(Alice));
        Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Widgets);
        var copyHtml = await Invoke("DuplicateDashboardAsync");
        var copied = await fixture.Repository.LoadAsync("alice@example.com");
        var name = copied.Collection!.Active.Document.Name;
        Assert.Equal("Original <dashboard> & (copy)", name);
        Assert.Equal(2, copied.Collection.Dashboards.Count);
        Assert.Contains(culture == "de-DE"
            ? $"Persönliche Kopie '{name}' erstellt. Änderungen wirken sich nicht auf das ursprüngliche Dashboard aus."
            : $"Personal copy '{name}' created. Changes do not affect the original dashboard.", copyHtml);
        Assert.Contains(culture == "de-DE" ? $"{name} · gespeicherte Revision {copied.Revision}" : $"{name} · saved revision {copied.Revision}", copyHtml);
        Assert.Null(page.GetType().GetField("publicationError", flags)!.GetValue(page));
        Assert.Null(page.GetType().GetField("errorMessage", flags)!.GetValue(page));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task CompactPublishedViewKeepsSystemsAndWidgetsWithoutExecutionControls(string culture, bool showControls)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var options = new CrossTenantLogAnalyticsOptions { Enabled = false };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var components = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(options).AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Operator)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent<PublishedAnalysisDashboardView>(0);
                        builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                        builder.AddAttribute(2, "ShowControls", showControls);
                        builder.AddAttribute(3, "AutoRunOnOpen", true);
                        builder.CloseComponent();
                    })
                }))));
        var rawHtml = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
        var html = WebUtility.HtmlDecode(rawHtml);
        var text = services.GetRequiredService<UiText>();
        Assert.Contains("LDAP latency", html);
        Assert.Contains("DC01", html);
        Assert.Contains(culture == "de-DE" ? "aria-label=\"Veröffentlichtes Dashboard\"" : "aria-label=\"Published dashboard\"", html);
        Assert.Contains(culture == "de-DE" ? "Veröffentlicht von alice@example.com · Version 1 · schreibgeschützt" : "Published by alice@example.com · version 1 · read-only", html);
        Assert.Contains(culture == "de-DE" ? "Ausgewählte Systeme: DC01" : "Selected systems: DC01", html);
        Assert.Contains(text["Analysis.Disabled"], html);
        Assert.Equal(showControls, html.Contains(text["Analysis.RunDashboard"]));
        Assert.Equal(showControls, html.Contains(text["Analysis.AutoRefresh"]));
        Assert.Equal(showControls, html.Contains(text["AnalysisExecution.TimeRange"]));
        Assert.Equal(showControls, html.Contains(text["AnalysisExecution.AveragingInterval"]));
        Assert.Equal(showControls, html.Contains(culture == "de-DE"
            ? "Veröffentlichte Version geladen. Das Dashboard ausführen, um aktuelle Daten abzurufen."
            : "Published version loaded. Run the dashboard to retrieve current data."));
        if (showControls)
        {
            Assert.Contains("disabled", Regex.Match(rawHtml, "<button[^>]*>").Value);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var view = Assert.IsType<PublishedAnalysisDashboardView>(components.PublishedView);
            view.GetType().GetField("running", flags)!.SetValue(view, true);
            await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(view, []));
            var runningHtml = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
            Assert.Contains(text["Analysis.RunningDashboard"], runningHtml);
            view.GetType().GetField("running", flags)!.SetValue(view, false);
            var snapshotBefore = view.GetType().GetField("snapshot", flags)!.GetValue(view);
            Assert.NotNull(snapshotBefore);
            var beforeSettings = JsonSerializer.Serialize(snapshotBefore);
            foreach (var settings in new (int? TimeRangeMinutes, int? BinMinutes)[]
            {
                (0, null), (5, null), (43201, null), (null, 0), (null, 5), (null, 1440)
            })
            {
                await renderer.Dispatcher.InvokeAsync(() => components.ExecutionControls!.Changed.InvokeAsync(settings));
                var key = settings.TimeRangeMinutes.HasValue
                    ? "Analysis.Validation.DashboardTimeRangeInvalid" : "Analysis.Validation.DashboardIntervalInvalid";
                Assert.Contains(text[key], WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
                Assert.Same(snapshotBefore, view.GetType().GetField("snapshot", flags)!.GetValue(view));
                Assert.Equal(beforeSettings, JsonSerializer.Serialize(snapshotBefore));
            }
            await renderer.Dispatcher.InvokeAsync(() => components.ExecutionControls!.Changed.InvokeAsync(((int?)60, (int?)15)));
            Assert.Null(view.GetType().GetField("errorMessage", flags)!.GetValue(view));
            Assert.Contains(culture == "de-DE"
                ? "Sitzungseinstellungen geändert. Das Dashboard ausführen, um aktuelle Daten abzurufen."
                : "Session settings changed. Run the dashboard to retrieve current data.",
                WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
        }
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedSystemListTracksFilteredInventoryWithoutChangingThePublishedSelection(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        collection.Active.Document.Systems.AddRange(
        [
            new() { SubscriptionId = "sub", Computer = "DC02" },
            new() { SubscriptionId = "other-sub", Computer = "DC01" },
            new() { SubscriptionId = "sub", Computer = "Missing" },
            new() { SubscriptionId = "sub", Computer = "None" }
        ]);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var options = new CrossTenantLogAnalyticsOptions();
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var components = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(options).AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
            await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Operator)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent<PublishedAnalysisDashboardView>(0);
                        builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                        builder.AddAttribute(2, "ShowControls", false);
                        builder.AddAttribute(3, "FilteredSystems", new ComputerInventoryEntry[]
                        {
                            new() { SubscriptionId = "SUB", Computer = "dc01" },
                            new() { SubscriptionId = "sub", Computer = "Unselected" }
                        });
                        builder.CloseComponent();
                    })
                })));
        var text = services.GetRequiredService<UiText>();
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains(text.Format("AnalysisPublications.SelectedSystems", "DC01") + "</p>", html);
        Assert.DoesNotContain("DC02", html);
        Assert.DoesNotContain("Unselected", html);
        Assert.DoesNotContain("Missing", html);

        await ApplyFilterAsync([new() { SubscriptionId = "sub", Computer = "DC02" }]);
        html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains(text.Format("AnalysisPublications.SelectedSystems", "DC02") + "</p>", html);
        Assert.DoesNotContain("DC01", html);
        await ApplyFilterAsync([]);
        Assert.Contains(text.Format("AnalysisPublications.SelectedSystems", culture == "de-DE" ? "Keine" : "None") + "</p>",
            WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
        await ApplyFilterAsync([new() { SubscriptionId = "sub", Computer = "None" }]);
        Assert.Contains(text.Format("AnalysisPublications.SelectedSystems", "None") + "</p>",
            WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
        await ApplyFilterAsync(
        [
            new() { SubscriptionId = "sub", Computer = "DC01" },
            new() { SubscriptionId = "sub", Computer = "DC02" },
            new() { SubscriptionId = "other-sub", Computer = "DC01" }
        ]);
        Assert.Contains(text.Format("AnalysisPublications.SelectedSystems", "DC01, DC02, DC01") + "</p>",
            WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
        Assert.Equal(5, (await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator)).Document.Systems.Count);
        Assert.Equal(5, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Systems.Count);

        Task ApplyFilterAsync(ComputerInventoryEntry[] systems) => renderer.Dispatcher.InvokeAsync(() =>
            components.PublishedView!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["FilteredSystems"] = systems
            })));
    }

    [Fact]
    public async Task PublishesExecutionDefaultsWhileViewerChangesRemainSessionLocal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        collection.Active.Document.TimeRangeMinutesOverride = 10080;
        collection.Active.Document.BinMinutesOverride = 30;
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var snapshot = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal(10080, snapshot.Document.TimeRangeMinutesOverride);
        Assert.Equal(30, snapshot.Document.BinMinutesOverride);
        snapshot.Document.TimeRangeMinutesOverride = 60;
        snapshot.Document.BinMinutesOverride = null;
        var widget = Assert.Single(snapshot.Document.Widgets);
        Assert.Equal(60, snapshot.Document.CreateExecutionRequest(widget, []).TimeRangeMinutes);
        Assert.Equal(240, widget.TimeRangeMinutes);

        var fresh = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        Assert.Equal(10080, fresh.Document.TimeRangeMinutesOverride);
        Assert.Equal(30, fresh.Document.BinMinutesOverride);
        var personal = await fixture.Repository.LoadAsync("alice@example.com");
        Assert.Equal(10080, personal.Collection!.Active.Document.TimeRangeMinutesOverride);
        Assert.Equal(30, personal.Collection.Active.Document.BinMinutesOverride);
    }

    [Fact]
    public async Task WithdrawalRevokesFreshReadsRetainsPersonalOriginalAndAllowsRepublishing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
        await fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice);
        Assert.Empty(await fixture.Publishing.ListAsync(Operator));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator));
        Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Widgets);
        var republished = await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 2, Alice);
        Assert.Equal(2, republished.Version);
        Assert.Single(await fixture.Publishing.ListAsync(Operator));
    }

    [Fact]
    public async Task AdminCannotPublishOrWithdrawAnotherCreatorsDashboard()
    {
        await using var fixture = await Fixture.CreateAsync();
        var aliceCollection = CreateDashboard();
        var bobCollection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", aliceCollection, 0);
        await fixture.Repository.SaveAsync("bob@example.com", bobCollection, 0);
        await fixture.Publishing.PublishAsync(aliceCollection.ActiveDashboardId, 1, Alice);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(aliceCollection.ActiveDashboardId, 1, Bob));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.WithdrawAsync(aliceCollection.ActiveDashboardId, Bob));
        Assert.Single(await fixture.Publishing.ListAsync(Operator));
    }

    [Fact]
    public async Task OperatorCannotPublishOrWithdrawEvenTheirOwnDashboardAndReaderCannotView()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("operator@example.com", collection, 0);
        Assert.False(await fixture.Publishing.CanPublishAsync(Operator));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Operator));
        var promoted = User("operator@example.com", SqlAuthenticationRoles.Admin);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, promoted);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Operator));
        var reader = User("reader@example.com", SqlAuthenticationRoles.Reader);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.ListAsync(reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.LoadAsync(collection.ActiveDashboardId, reader));
    }

    [Fact]
    public async Task SameNamesAndVersionsAreIndependentAcrossCreators()
    {
        await using var fixture = await Fixture.CreateAsync();
        var aliceCollection = CreateDashboard();
        var bobCollection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", aliceCollection, 0);
        await fixture.Repository.SaveAsync("bob@example.com", bobCollection, 0);
        await fixture.Publishing.PublishAsync(aliceCollection.ActiveDashboardId, 1, Alice);
        await fixture.Publishing.PublishAsync(bobCollection.ActiveDashboardId, 1, Bob);
        var publications = await fixture.Publishing.ListAsync(Operator);
        Assert.Equal(2, publications.Count);
        Assert.All(publications, item => { Assert.Equal(1, item.Version); Assert.Equal("AD dashboard", item.Name); });
        await fixture.Publishing.WithdrawAsync(aliceCollection.ActiveDashboardId, Alice);
        Assert.Equal("bob@example.com", Assert.Single(await fixture.Publishing.ListAsync(Operator)).Owner);
    }

    [Fact]
    public async Task CannotDeletePublishedSourceUntilWithdrawn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        var publishedId = collection.ActiveDashboardId;
        collection.Add("Other");
        collection.Select(publishedId);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(publishedId, 1, Alice);
        collection.DeleteActive();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Repository.SaveAsync("alice@example.com", collection, 2));
        Assert.Contains("Withdraw", exception.Message);
        Assert.Equal(2, (await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards.Count);
        await fixture.Publishing.WithdrawAsync(publishedId, Alice);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 2);
        Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publishing.PublishAsync(publishedId, 3, Alice));
    }

    [Fact]
    public async Task OpenModeKeepsExistingAdminPolicyButStillChecksCreator()
    {
        await using var fixture = await Fixture.CreateAsync(open: true);
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        var aliceWithoutRole = User("alice@example.com");
        Assert.True(await fixture.Publishing.CanPublishAsync(aliceWithoutRole));
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, aliceWithoutRole);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, User("bob@example.com")));
    }

    [Fact]
    public void MigrationAllowsIndependentDashboardNamesAndModelSnapshotMatches()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;").Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261019142000_NormalizeDomainControllerRole", "20261020170000_AllowSharedDashboardNames");
        Assert.Contains("DROP INDEX [IX_AnalysisDashboardTemplates_Name_Version]", script);
        Assert.Contains("CREATE INDEX [IX_AnalysisDashboardTemplates_Name_Version]", script);
        Assert.DoesNotContain("CREATE UNIQUE INDEX", script);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ReadOnlyGridPreservesLayoutAndEscapesNamesWithoutEditOrDragControls(string culture, bool empty)
    {
        using var selection = new UiSelection(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var document = CreateDashboard().Active.Document;
        document.Widgets[0].Title = "<LDAP latency>";
        if (empty) document.Widgets.Clear();
        var original = JsonSerializer.Serialize(document);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<PublishedAnalysisDashboardGrid>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Document"] = document }))).ToHtmlString());
        var decoded = WebUtility.HtmlDecode(html);
        Assert.Contains(culture == "de-DE" ? "Schreibgeschütztes veröffentlichtes Dashboard" : "Read-only published dashboard", decoded);
        if (empty)
            Assert.Contains(culture == "de-DE" ? "Dieses veröffentlichte Dashboard hat keine Widgets." : "This published dashboard has no widgets.", decoded);
        else Assert.Contains("&lt;LDAP latency&gt;", html);
        Assert.Contains("--columns:4;--rows:12;--row-height:88px", html);
        if (!empty) Assert.Contains("grid-column:2 / span 2;grid-row:3 / span 4", html);
        Assert.DoesNotContain("data-inbox-drag-handle", html);
        Assert.DoesNotContain("data-inbox-widget-id", html);
        Assert.DoesNotContain("data-inbox-resize-handle", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("draggable", html);
        Assert.Equal(original, JsonSerializer.Serialize(document));
    }

    [Theory]
    [InlineData("en-US", false, false, false, false)]
    [InlineData("de-DE", false, false, false, false)]
    [InlineData("en-US", true, false, false, false)]
    [InlineData("de-DE", true, false, false, false)]
    [InlineData("en-US", true, true, false, false)]
    [InlineData("de-DE", true, true, false, false)]
    [InlineData("en-US", false, false, true, false)]
    [InlineData("de-DE", false, false, true, false)]
    [InlineData("en-US", true, false, true, false)]
    [InlineData("de-DE", true, false, true, false)]
    [InlineData("en-US", false, false, false, true)]
    [InlineData("de-DE", false, false, false, true)]
    [InlineData("en-US", true, false, false, true)]
    [InlineData("de-DE", true, false, false, true)]
    public async Task RefreshesRenderEachWidgetBeforeStartingTheNextAndRespectWithdrawal(
        string culture, bool published, bool withdraw, bool timeout, bool single)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        var lower = collection.Active.Document.Widgets[0];
        lower.Row = 7;
        var upper = new AnalysisDashboardWidget
        {
            QueryDefinitionId = Guid.NewGuid(), Title = "Upper widget", Row = 1, Column = 1,
            TimeRangeMinutes = 240
        };
        collection.Active.Document.Widgets.Add(upper);
        if (single) collection.Active.Document.Widgets.Remove(lower);
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "DC01" });
            foreach (var widget in collection.Active.Document.Widgets)
            {
                var query = ActiveDirectoryAnalysisQueries.Create().First();
                query.Id = widget.QueryDefinitionId;
                query.Key = widget.QueryDefinitionId.ToString("N");
                var version = new AnalysisQueryVersion
                {
                    Id = Guid.NewGuid(), Version = 1,
                    SnapshotJson = System.Text.Json.JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
                };
                query.Versions.Add(version);
                query.PublishedVersionId = version.Id;
                context.AnalysisQueryDefinitions.Add(query);
            }
            await context.SaveChangesAsync();
        }
        var options = new CrossTenantLogAnalyticsOptions { Enabled = true };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var gated = new GatedDbContextFactory(fixture.Factory, timeout);
        var components = new CapturingComponentActivator();
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var configuration = new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder());
        var personalLog = new AnalysisLog<MonitoringApp.Components.Pages.Analysis>();
        var publishedLog = new AnalysisLog<PublishedAnalysisDashboardView>();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<ILogger<MonitoringApp.Components.Pages.Analysis>>(personalLog)
            .AddSingleton<ILogger<PublishedAnalysisDashboardView>>(publishedLog)
            .AddUiLocalization()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(gated, runner))
            .AddSingleton(runner)
            .AddSingleton(new AlertStore(fixture.Factory, configuration, presenter, new AlertRuleEvaluator(presenter),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
            .AddSingleton(new AnalysisDashboardState())
            .AddSingleton(fixture.Repository)
            .AddSingleton(new DashboardFilterOptions())
            .AddSingleton(options)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new UnusedJavaScript())
            .BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent(0, published ? typeof(PublishedAnalysisDashboardView) : typeof(MonitoringApp.Components.Pages.Analysis));
                        if (published) builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                        builder.CloseComponent();
                    })
                }));
            await root.QuiescenceTask;
            return root;
        });
        var component = published ? (IComponent)components.PublishedView! : components.AnalysisPage!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var results = (Dictionary<string, AnalysisQueryResponse>)component.GetType()
            .GetField(published ? "results" : "widgetResults", flags)!.GetValue(component)!;
        results[upper.Id] = CachedResult("OLD_UPPER");
        if (!single) results[lower.Id] = CachedResult("OLD_LOWER");
        Task refresh = Task.CompletedTask;
        try
        {
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                refresh = (Task)component.GetType().GetMethod(published ? "RunAsync" : "RunDashboardAsync", flags)!
                    .Invoke(component, [])!;
            });
            await gated.Started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gated.Started[1].Task.IsCompleted);
            var before = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            Assert.Contains("OLD_UPPER", before);
            if (!single) Assert.Contains("OLD_LOWER", before);

            if (withdraw)
            {
                await fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice);
                gated.Release[0].TrySetResult();
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(gated.Started[1].Task.IsCompleted);
                var withdrawn = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
                Assert.DoesNotContain("OLD_UPPER", withdrawn);
                Assert.DoesNotContain("OLD_LOWER", withdrawn);
                Assert.Contains(services.GetRequiredService<UiText>()["Analysis.Errors.PublicationUnavailable"],
                    WebUtility.HtmlDecode(withdrawn));
                return;
            }

            gated.Release[0].TrySetResult();
            if (!single) await gated.Started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            else await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            var during = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            Assert.DoesNotContain("OLD_UPPER", during);
            if (!single) Assert.Contains("OLD_LOWER", during);
            var errors = (Dictionary<string, string>)component.GetType()
                .GetField(published ? "errors" : "widgetErrors", flags)!.GetValue(component)!;
            Assert.True(errors.ContainsKey(upper.Id));
            var failureKey = "Analysis.Errors.ExecutionFailed";
            Assert.Equal(services.GetRequiredService<UiText>()[failureKey], errors[upper.Id]);
            var entries = published ? publishedLog.Entries : personalLog.Entries;
            Assert.Single(entries);
            Assert.NotNull(entries[0].Exception);
            if (timeout) Assert.Contains("timed out", entries[0].Exception!.Message);
            if (entries[0].Exception is not AnalysisValidationException || culture == "de-DE")
                Assert.DoesNotContain(entries[0].Exception!.Message, WebUtility.HtmlDecode(during));
            Assert.False(errors.ContainsKey(lower.Id));
            Assert.Contains(errors[upper.Id], WebUtility.HtmlDecode(during));
            Assert.Equal(single ? 1 : 2, gated.RequestCount);

            gated.Release[1].TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(single ? 1 : 2, errors.Count);
            Assert.False((bool)component.GetType().GetField(published ? "running" : "isDashboardRunning", flags)!.GetValue(component)!);
            var status = Assert.IsType<string>(component.GetType().GetField(published ? "status" : "dashboardStatus", flags)!.GetValue(component));
            var timestamp = Regex.Match(status, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z").Value;
            var utc = DateTimeOffset.ParseExact(timestamp, "u", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            Assert.Equal(TimeSpan.Zero, utc.Offset);
            Assert.InRange(utc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
            Assert.Equal(services.GetRequiredService<UiText>().Format(single
                ? "AnalysisStatus.RefreshFailures.One" : "AnalysisStatus.RefreshFailures.Other", single ? 1 : 2, utc), status);
            Assert.Equal(single ? 1 : 2, entries.Count);
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            foreach (var release in gated.Release) release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }

    }

    private static AnalysisQueryResponse CachedResult(string marker)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), marker, 1, "ms", now.AddHours(-1), now, now, 0, [], [],
            AnalysisQueryVisualizationTypes.Table,
            [new("Cached workspace", [new("Value", "string")], [[marker]])]);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task OpenViewReloadsRepublishedSnapshotAndRemovesWithdrawnContentEvenWithQueryRefreshOff(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        var collection = CreateDashboard();
        await fixture.Repository.SaveAsync("alice@example.com", collection, 0);
        await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
        var options = new CrossTenantLogAnalyticsOptions { Enabled = false };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        var clock = new AnalysisRefreshLoopTests.ManualTimeProvider();
        var components = new CapturingComponentActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IComponentActivator>(components)
            .AddSingleton(fixture.Publishing)
            .AddSingleton(new AnalysisQueryRegistry(fixture.Factory, options, runner))
            .AddSingleton(new AnalysisQueryExecutionService(fixture.Factory, runner))
            .AddSingleton(new ApplicationAuthenticationOptions())
            .AddSingleton(options).AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();
        await using (var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()))
        {
            RenderFragment child = builder =>
            {
                builder.OpenComponent<PublishedAnalysisDashboardView>(0);
                builder.AddAttribute(1, "DashboardId", collection.ActiveDashboardId);
                builder.CloseComponent();
            };
            var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = Task.FromResult(new AuthenticationState(Operator)),
                        ["ChildContent"] = child
                    })));
            var initial = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            Assert.Contains("AD dashboard", initial);
            Assert.Contains("DC01", initial);
            var text = services.GetRequiredService<UiText>();
            Assert.Contains(text["Analysis.Disabled"], WebUtility.HtmlDecode(initial));
            await renderer.Dispatcher.InvokeAsync(() =>
                (Task)typeof(PublishedAnalysisDashboardView).GetMethod("OnAfterRenderAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(components.PublishedView, [true])!);
            Assert.Equal(TimeSpan.FromSeconds(30), clock.Period);
            Assert.NotNull(components.ExecutionControls);
            await renderer.Dispatcher.InvokeAsync(() =>
                components.ExecutionControls.Changed.InvokeAsync(((int?)60, (int?)15)));
            var sessionHtml = System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
            Assert.Contains("<small>1h · 15 min · SourceDSA</small>", sessionHtml);
            var originalPublication = await fixture.Publishing.LoadAsync(collection.ActiveDashboardId, Operator);
            Assert.Null(originalPublication.Document.TimeRangeMinutesOverride);
            Assert.Null(originalPublication.Document.BinMinutesOverride);
            var stored = await fixture.Repository.LoadAsync("alice@example.com");
            stored.Collection!.RenameActive("Republished dashboard");
            stored.Collection.Active.Document.TimeRangeMinutesOverride = 10080;
            stored.Collection.Active.Document.BinMinutesOverride = 30;
            await fixture.Repository.SaveAsync("alice@example.com", stored.Collection, stored.Revision);
            await fixture.Publishing.PublishAsync(collection.ActiveDashboardId, 3, Alice);
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.DoesNotContain("Republished dashboard", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForHtmlAsync("Republished dashboard");
            Assert.Contains(text.Format("AnalysisPublications.PublishedBy", originalPublication.Publication.Owner, 2),
                WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
            Assert.Equal(10080, components.ExecutionControls.TimeRangeMinutes);
            Assert.Equal(30, components.ExecutionControls.BinMinutes);
            await fixture.Publishing.WithdrawAsync(collection.ActiveDashboardId, Alice);
            clock.Advance(TimeSpan.FromSeconds(30));
            var withdrawn = await WaitForHtmlAsync(text["Analysis.Errors.PublicationUnavailable"]);
            Assert.DoesNotContain("DC01", withdrawn);
            Assert.DoesNotContain("LDAP latency", withdrawn);
            Assert.DoesNotContain(text["AnalysisPublications.ReadOnlyCanvas"], WebUtility.HtmlDecode(withdrawn));
            Assert.Single((await fixture.Repository.LoadAsync("alice@example.com")).Collection!.Active.Document.Widgets);

            async Task<string> WaitForHtmlAsync(string expected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var html = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
                    if (WebUtility.HtmlDecode(html).Contains(expected, StringComparison.Ordinal))
                        return html;
                    await Task.Delay(10, timeout.Token);
                }
            }
        }
        Assert.True(clock.Disposed);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task AnalysisBrowserFailuresPreserveActualPageResultsAndPersistenceAndRecoverExplicitly(string culture, bool importing)
    {
        using var selection = new UiSelection(culture);
        foreach (var failure in new Exception[]
        {
            new JSException("PRIVATE_BROWSER_DETAIL"),
            new JSDisconnectedException("PRIVATE_BROWSER_DETAIL"),
            new TaskCanceledException("PRIVATE_BROWSER_DETAIL"),
            new InvalidOperationException("PRIVATE_BROWSER_DETAIL")
        })
        {
            await using var page = await BrowserPageFixture.CreateAsync();
            var original = JsonSerializer.Serialize(page.Dashboard.Document);
            var stored = await page.Storage.Repository.LoadAsync("alice@example.com");
            var response = page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults").Single().Value;
            page.Browser.ImportFailure = importing ? failure : null;
            page.Browser.InitializeFailure = importing ? null : failure;
            await page.Invoke("OnAfterRenderAsync", true);
            Assert.Contains(page.Text[importing ? "Analysis.Browser.ImportFailed" : "Analysis.Browser.InitializeFailed"], page.Decoded);
            Assert.Contains(page.Text["Analysis.Browser.Retry"], page.Decoded);
            Assert.DoesNotContain("PRIVATE_BROWSER_DETAIL", page.Html);
            Assert.Contains("Original &lt;cached&gt; &amp; value", page.Html);
            Assert.False(page.Get<bool>("dashboardBrowserInitialized"));
            Assert.Same(response, page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults").Single().Value);
            Assert.Contains(page.Log.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
            await page.AssertBrowserCallbacksBlocked(original);
            var calls = page.Browser.Calls.Count;
            await page.Invoke("OnAfterRenderAsync", false);
            Assert.Equal(calls, page.Browser.Calls.Count);
            var timer = page.Get<Task>("refreshTask");
            page.Browser.ImportFailure = null;
            page.Browser.InitializeFailure = null;
            await page.Invoke("RetryDashboardBrowserAsync");
            Assert.True(page.Get<bool>("dashboardBrowserInitialized"));
            Assert.Null(page.Get<string?>("dashboardBrowserError"));
            Assert.Same(timer, page.Get<Task>("refreshTask"));
            Assert.True(page.Get<bool>("initialDashboardRunStarted"));
            Assert.Equal(original, JsonSerializer.Serialize(page.Dashboard.Document));
            var reloaded = await page.Storage.Repository.LoadAsync("alice@example.com");
            Assert.Equal(stored.Revision, reloaded.Revision);
            Assert.Equal(stored.Collection!.Serialize(), reloaded.Collection!.Serialize());
            Assert.False(page.Browser.DuplicateHandlers);
            Assert.All(page.Browser.Calls.Where(call => call.Name == "import"),
                call => Assert.Equal("./js/analysis-dashboard.js", Assert.Single(call.Arguments)));
            var module = Assert.Single(page.Browser.Modules, module => module.HandlersActive);
            Assert.Same(page.Component, module.Reference!.Value);
            Assert.IsType<ElementReference>(module.Root);
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task AnalysisBrowserRetryRequiresSuccessfulHandlerAndModuleCleanupBeforeNewImport(string culture, bool moduleRelease)
    {
        using var selection = new UiSelection(culture);
        await using var page = await BrowserPageFixture.CreateAsync();
        var original = JsonSerializer.Serialize(page.Dashboard.Document);
        page.Browser.InitializeFailure = new JSException("PRIVATE_INIT_DETAIL");
        await page.Invoke("OnAfterRenderAsync", true);
        var oldModule = Assert.Single(page.Browser.Modules);
        var oldReference = oldModule.Reference!;
        page.Browser.InitializeFailure = null;
        var failure = new InvalidOperationException("PRIVATE_CLEANUP_DETAIL");
        if (moduleRelease) page.Browser.ReleaseFailure = failure;
        else page.Browser.CleanupFailure = failure;
        await page.Invoke("RetryDashboardBrowserAsync");
        Assert.Contains(page.Text["Analysis.Browser.CleanupFailed"], page.Decoded);
        Assert.Contains(page.Log.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
        Assert.Single(page.Browser.Modules);
        Assert.Same(page.Component, oldReference.Value);
        Assert.False(page.Get<bool>("dashboardBrowserInitialized"));
        await page.AssertBrowserCallbacksBlocked(original);
        page.Browser.ReleaseFailure = null;
        page.Browser.CleanupFailure = null;
        await page.Invoke("RetryDashboardBrowserAsync");
        Assert.True(page.Get<bool>("dashboardBrowserInitialized"));
        Assert.True(oldModule.Released);
        Assert.Throws<ObjectDisposedException>(() => oldReference.Value);
        Assert.False(page.Browser.DuplicateHandlers);
        Assert.Single(page.Browser.Modules, module => module.HandlersActive);
        Assert.Equal(original, JsonSerializer.Serialize(page.Dashboard.Document));
    }

    [Theory]
    [InlineData("en-US", "import")]
    [InlineData("de-DE", "import")]
    [InlineData("en-US", "initialize")]
    [InlineData("de-DE", "initialize")]
    public async Task PendingAnalysisBrowserSetupBlocksOverlapsAndCallbacksWithoutLosingEdits(string culture, string operation)
    {
        using var selection = new UiSelection(culture);
        await using var page = await BrowserPageFixture.CreateAsync();
        var original = JsonSerializer.Serialize(page.Dashboard.Document);
        page.Browser.BlockOperation = operation;
        var pending = page.Invoke("OnAfterRenderAsync", true);
        await page.Browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var calls = page.Browser.Calls.Count;
            await page.Invoke("RetryDashboardBrowserAsync");
            await page.Invoke("OnAfterRenderAsync", false);
            await page.AssertBrowserCallbacksBlocked(original);
            Assert.Equal(calls, page.Browser.Calls.Count);
            Assert.True(page.Get<bool>("isInitializingBrowser"));
            page.Dashboard.UpdateWorkspace(document => document.Name = "Edited while loading");
        }
        finally
        {
            page.Browser.Release.SetResult();
            await pending;
        }
        Assert.True(page.Get<bool>("dashboardBrowserInitialized"));
        Assert.Equal("Edited while loading", page.Dashboard.Document.Name);
        Assert.Single(page.Browser.Modules);
        var callsAfterSuccess = page.Browser.Calls.Count;
        await page.Invoke("OnAfterRenderAsync", false);
        Assert.Equal(callsAfterSuccess, page.Browser.Calls.Count);
    }

    [Theory]
    [InlineData("en-US", "import")]
    [InlineData("de-DE", "import")]
    [InlineData("en-US", "initialize")]
    [InlineData("de-DE", "initialize")]
    public async Task DisposingAnalysisCancelsBrowserSetupAndReleasesPartialReferencesOnce(string culture, string operation)
    {
        using var selection = new UiSelection(culture);
        await using var page = await BrowserPageFixture.CreateAsync();
        var original = JsonSerializer.Serialize(page.Dashboard.Document);
        page.Browser.BlockOperation = operation;
        var pending = page.Invoke("OnAfterRenderAsync", true);
        await page.Browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reference = page.Browser.Modules.SingleOrDefault()?.Reference;
        await page.DisposePage();
        await pending;
        Assert.Null(page.Get<string?>("dashboardBrowserError"));
        Assert.Empty(page.Log.Entries);
        Assert.False(page.Get<bool>("dashboardBrowserInitialized"));
        Assert.Equal(original, JsonSerializer.Serialize(page.Dashboard.Document));
        Assert.All(page.Browser.Modules, module => Assert.True(module.Released));
        if (reference is not null) Assert.Throws<ObjectDisposedException>(() => reference.Value);
        var calls = page.Browser.Calls.Count;
        await page.Invoke("OnAfterRenderAsync", false);
        await page.Invoke("RetryDashboardBrowserAsync");
        await page.DisposePage();
        Assert.Equal(calls, page.Browser.Calls.Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task AnalysisLateImportAfterCancellationIsOwnedAndReleasedWithoutInitialization(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var page = await BrowserPageFixture.CreateAsync();
        page.Browser.BlockOperation = "import";
        page.Browser.IgnoreCancellation = true;
        var pending = page.Invoke("OnAfterRenderAsync", true);
        await page.Browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposing = page.DisposePage();
        page.Browser.Release.SetResult();
        await Task.WhenAll(pending, disposing);
        var module = Assert.Single(page.Browser.Modules);
        Assert.True(module.Released);
        Assert.Null(module.Reference);
        Assert.DoesNotContain(page.Browser.Calls, call => call.Name == "initialize");
        Assert.Null(page.Get<string?>("dashboardBrowserError"));
        Assert.Empty(page.Log.Entries);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task AnalysisTeardownAttemptsEveryCleanupPhaseAndDisposesCallbackDespiteFailures(string culture)
    {
        using var selection = new UiSelection(culture);
        foreach (var disconnected in new[] { false, true })
        {
            await using var page = await BrowserPageFixture.CreateAsync();
            await page.Invoke("OnAfterRenderAsync", true);
            var module = Assert.Single(page.Browser.Modules);
            var reference = module.Reference!;
            var failure = disconnected ? (Exception)new JSDisconnectedException("PRIVATE_CLEANUP_DETAIL")
                : new InvalidOperationException("PRIVATE_CLEANUP_DETAIL");
            page.Browser.CleanupFailure = failure;
            page.Browser.ReleaseFailure = failure;
            await page.DisposePage();
            Assert.Contains(page.Browser.Calls, call => call.Name == "module-release");
            Assert.Throws<ObjectDisposedException>(() => reference.Value);
            if (disconnected) Assert.Empty(page.Log.Entries);
            else Assert.Equal(2, page.Log.Entries.Count(entry => ReferenceEquals(entry.Exception, failure)));
            Assert.DoesNotContain("PRIVATE_CLEANUP_DETAIL", page.Html);
        }
    }

    private sealed class BrowserPageFixture(Fixture storage, ServiceProvider services, StaticPageRenderer renderer,
        CapturingComponentActivator components, BrowserRuntime browser, AnalysisLog<MonitoringApp.Components.Pages.Analysis> log,
        Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root) : IAsyncDisposable
    {
        private const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        public Fixture Storage => storage;
        public MonitoringApp.Components.Pages.Analysis Component => components.AnalysisPage!;
        public BrowserRuntime Browser => browser;
        public AnalysisLog<MonitoringApp.Components.Pages.Analysis> Log => log;
        public AnalysisDashboardState Dashboard => services.GetRequiredService<AnalysisDashboardState>();
        public UiText Text => services.GetRequiredService<UiText>();
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public T Get<T>(string name) => (T)(Component.GetType().GetField(name, Flags)?.GetValue(Component)
            ?? Component.GetType().GetProperty(name, Flags)?.GetValue(Component))!;
        public Task Set(string name, object value) => renderer.Dispatcher.InvokeAsync(() =>
            Component.GetType().GetField(name, Flags)!.SetValue(Component, value));
        public Task Invoke(string name, params object[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = Component.GetType().GetMethod(name, Flags)!.Invoke(Component, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Component, null);
        });
        public Task DisposePage() => renderer.Dispatcher.InvokeAsync(async () =>
            await Component.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        public async Task AssertBrowserCallbacksBlocked(string original)
        {
            var widget = Dashboard.Document.Widgets.Single();
            await Invoke("CommitDashboardMove", widget.Id, 5, 5);
            await Invoke("CommitDashboardResize", widget.Id, 3, 3);
            await Invoke("PlaceDashboardQuery", widget.QueryDefinitionId.ToString(), 4, 4);
            await Invoke("CommitAnalysisPanelResize", "systems", 700, 800);
            Assert.Equal(original, JsonSerializer.Serialize(Dashboard.Document));
            Assert.DoesNotContain("data-inbox-drag-handle", Html);
        }
        public static async Task<BrowserPageFixture> CreateAsync(bool roles = false, IAnalysisLogAnalyticsQuery? queryClient = null,
            bool aliases = false)
        {
            var storage = await Fixture.CreateAsync(directoryEnabled: aliases);
            var collection = CreateDashboard();
            const string subA = "11111111-1111-1111-1111-111111111111";
            const string subB = "22222222-2222-2222-2222-222222222222";
            if (roles)
            {
                collection.Active.Document.SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles;
                collection.Active.Document.InventoryRoles = ["Domain Controller", "File Server"];
                collection.Active.Document.Systems = [new() { SubscriptionId = subA, Computer = "FIXED" }];
                var query = new AnalysisQueryDefinition
                {
                    Id = collection.Active.Document.Widgets.Single().QueryDefinitionId,
                    Key = "role-editor-test", Name = "Role query", Category = "Test",
                    Kql = "print Series = 'DC01', Value = 1 | where Series in~ (_SelectedSystems)",
                    VisualizationType = AnalysisQueryVisualizationTypes.Stat
                };
                var version = new AnalysisQueryVersion
                {
                    Id = Guid.NewGuid(), Version = 1,
                    SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
                };
                query.Versions.Add(version);
                query.PublishedVersionId = version.Id;
                query.Workspaces.Add(new() { WorkspaceKey = "a", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded });
                query.Workspaces.Add(new() { WorkspaceKey = "b", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded });
                await using var context = storage.Factory.CreateDbContext();
                context.AnalysisQueryDefinitions.Add(query);
                context.ComputerInventory.AddRange(
                    new() { SubscriptionId = subA, Computer = "DC01", Role = "Domain Controller" },
                    new() { SubscriptionId = subA, Computer = "FILE01", Role = "File Server" },
                    new() { SubscriptionId = subA, Computer = "FIXED", Role = "Other" },
                    new() { SubscriptionId = subB, Computer = "DC01", Role = "domaincontrollers" });
                await context.SaveChangesAsync();
            }
            await storage.Repository.SaveAsync("alice@example.com", collection, 0);
            if (aliases)
            {
                var (_, _, historical) = await SeedHistoricalDashboardsAsync(storage);
                if (roles)
                {
                    historical.Active.Document.Systems = [new() { SubscriptionId = subA, Computer = "DC01" }];
                    historical.Active.Document.Widgets.Single().QueryDefinitionId = collection.Active.Document.Widgets.Single().QueryDefinitionId;
                    var stored = await storage.Repository.LoadAsync(HistoricalAlias);
                    await storage.Repository.SaveAsync(HistoricalAlias, historical, stored.Revision);
                }
            }
            var options = new CrossTenantLogAnalyticsOptions
            {
                Enabled = roles,
                Workspaces = roles ? [
                    new() { Key = "a", DisplayName = "Workspace A", WorkspaceId = Guid.NewGuid().ToString(), SubscriptionIds = [subA] },
                    new() { Key = "b", DisplayName = "Workspace B", WorkspaceId = Guid.NewGuid().ToString(), SubscriptionIds = [subB] }] : []
            };
            ILogAnalyticsSecretProvider secret = roles ? new DashboardSecret() : new UnusedSecretProvider();
            var runner = new AnalysisQueryRunner(options, secret, new TargetWorkspaceResolver(options), queryClient);
            var components = new CapturingComponentActivator();
            var browser = new BrowserRuntime();
            var log = new AnalysisLog<MonitoringApp.Components.Pages.Analysis>();
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            var configuration = new DatabaseConfigurationStatus(true, null, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder());
            var services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton<IComponentActivator>(components)
                .AddSingleton<ILogger<MonitoringApp.Components.Pages.Analysis>>(log)
                .AddSingleton(storage.Publishing)
                .AddSingleton(new AnalysisQueryRegistry(storage.Factory, options, runner))
                .AddSingleton(new AnalysisQueryExecutionService(storage.Factory, runner))
                .AddSingleton(runner)
                .AddSingleton(new AlertStore(storage.Factory, configuration, presenter, new AlertRuleEvaluator(presenter),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance))
                .AddSingleton(new ApplicationAuthenticationOptions())
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
                .AddSingleton(new AnalysisDashboardState())
                .AddSingleton(storage.Repository)
                .AddSingleton(new DashboardFilterOptions { Workspace = roles ? string.Empty : null })
                .AddSingleton(options)
                .AddSingleton(TimeProvider.System)
                .AddSingleton<IJSRuntime>(browser).BuildServiceProvider();
            var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = Task.FromResult(new AuthenticationState(Alice)),
                        ["ChildContent"] = (RenderFragment)(builder =>
                        {
                            builder.OpenComponent<MonitoringApp.Components.Pages.Analysis>(0);
                            builder.CloseComponent();
                        })
                    }));
                await root.QuiescenceTask;
                return root;
            });
            var page = new BrowserPageFixture(storage, services, renderer, components, browser, log, root);
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                components.AnalysisPage!.GetType().GetField("editMode", Flags)!.SetValue(components.AnalysisPage, true);
                var now = DateTimeOffset.UtcNow;
                page.Get<Dictionary<string, AnalysisQueryResponse>>("widgetResults")
                    [page.Dashboard.Document.Widgets.Single().Id] = new(Guid.NewGuid(), "Original query", 1, "ms",
                        now.AddHours(-1), now, now, 0, [], [], AnalysisQueryVisualizationTypes.Table,
                        [new("Original workspace", [new("Value", "string")], [["Original <cached> & value"]])]);
                typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(page.Component, null);
            });
            return page;
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await services.DisposeAsync();
            await storage.DisposeAsync();
        }
    }

    private sealed class BrowserRuntime : IJSRuntime
    {
        public List<(string Name, object?[] Arguments)> Calls { get; } = [];
        public List<BrowserModule> Modules { get; } = [];
        public Exception? ImportFailure { get; set; }
        public Exception? InitializeFailure { get; set; }
        public Exception? CleanupFailure { get; set; }
        public Exception? ReleaseFailure { get; set; }
        public string? BlockOperation { get; set; }
        public bool IgnoreCancellation { get; set; }
        public bool DuplicateHandlers { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(string operation, CancellationToken cancellationToken)
        {
            if (BlockOperation != operation) return;
            BlockOperation = null;
            Started.SetResult();
            await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            Calls.Add((identifier, args ?? []));
            await WaitAsync(identifier, cancellationToken);
            if (ImportFailure is { } failure) throw failure;
            var module = new BrowserModule(this);
            Modules.Add(module);
            return Assert.IsAssignableFrom<TValue>(module);
        }
    }

    private sealed class BrowserModule(BrowserRuntime browser) : IJSObjectReference
    {
        public bool HandlersActive { get; private set; }
        public bool Released { get; private set; }
        public DotNetObjectReference<MonitoringApp.Components.Pages.Analysis>? Reference { get; private set; }
        public object? Root { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            browser.Calls.Add((identifier, args ?? []));
            if (identifier == "initialize")
            {
                browser.DuplicateHandlers |= browser.Modules.Any(module => module.HandlersActive);
                Reference = Assert.IsType<DotNetObjectReference<MonitoringApp.Components.Pages.Analysis>>(args![0]);
                Root = args[1];
                HandlersActive = true;
                await browser.WaitAsync(identifier, cancellationToken);
                if (browser.InitializeFailure is { } failure) throw failure;
            }
            else
            {
                Assert.Equal("dispose", identifier);
                if (browser.CleanupFailure is { } failure) throw failure;
                HandlersActive = false;
            }
            return default!;
        }
        public ValueTask DisposeAsync()
        {
            browser.Calls.Add(("module-release", []));
            if (browser.ReleaseFailure is { } failure) throw failure;
            Released = true;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("en-US", "read")]
    [InlineData("de-DE", "read")]
    [InlineData("en-US", "identity")]
    [InlineData("de-DE", "identity")]
    [InlineData("en-US", "missing")]
    [InlineData("de-DE", "missing")]
    [InlineData("en-US", "denied")]
    [InlineData("de-DE", "denied")]
    public async Task PublishedReadFailureOffersLocalizedExplicitRecoveryWithoutControls(string culture, string kind)
    {
        using var selection = new UiSelection(culture);
        Exception failure = kind switch
        {
            "missing" => new KeyNotFoundException("Private publication detail."),
            "denied" or "identity" => new UnauthorizedAccessException("Private authorization detail."),
            _ => new IOException("Private storage detail.")
        };
        await using var page = await PublishedViewFixture.CreateAsync(
            initialFailure: kind == "identity" ? null : failure,
            authentication: kind == "identity" ? Task.FromException<AuthenticationState>(failure) : null,
            controls: false, autoRun: true);
        var key = kind switch
        {
            "missing" => "Analysis.Errors.PublicationUnavailable",
            "denied" => "Analysis.Errors.PublicationNotAllowed",
            _ => "Analysis.Errors.PublishedReadFailed"
        };
        Assert.Null(page.Get<PublishedAnalysisDashboardSnapshot?>("snapshot"));
        Assert.Contains(page.Text[key], page.Decoded);
        Assert.Contains(page.Text["AnalysisPublications.Reload"], page.Decoded);
        Assert.DoesNotContain("AD dashboard", page.Decoded);
        Assert.DoesNotContain("Private", page.Decoded);
        Assert.Contains(page.Log.Entries, entry => ReferenceEquals(entry.Exception, failure));
        Assert.Equal(0, page.Clock.TimerCount);

        page.SetAuthentication(Task.FromResult(new AuthenticationState(Alice)));
        await page.Invoke("ReloadViewAsync");
        Assert.Contains("AD dashboard", page.Decoded);
        Assert.Null(page.Get<string?>("errorMessage"));
        Assert.True(page.Get<bool>("autoRefresh"));
        Assert.Equal(0, page.Executions.Requests);
        page.Set("autoRefresh", false);
        await page.Invoke("ReloadViewAsync");
        Assert.False(page.Get<bool>("autoRefresh"));
        Assert.Equal(0, page.Clock.TimerCount);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedReloadSerializesReadsAndBlocksRunAndSessionChanges(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync(enabled: true);
        await page.Invoke("OnAfterRenderAsync", true);
        await page.Invoke("ExecutionSettingsChanged", ((int?)60, (int?)15));
        page.Set("errorMessage", page.Text["Analysis.Errors.PublishedReadFailed"]);
        var gate = page.Reads.Block();
        var reload = page.Invoke("ReloadViewAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requests = page.Reads.Requests;
        Assert.True(page.Get<bool>("isReloading"));
        Assert.Contains(page.Text["AnalysisPublications.Reloading"], page.Decoded);
        page.Clock.Advance(TimeSpan.FromSeconds(30));
        await page.Invoke("ReloadViewAsync");
        await page.Invoke("RunAsync");
        await page.Invoke("ExecutionSettingsChanged", ((int?)240, (int?)30));
        Assert.Equal(requests, page.Reads.Requests);
        Assert.Equal(0, page.Executions.Requests);
        Assert.Equal(60, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Document.TimeRangeMinutesOverride);
        var queued = page.Invoke("ReloadAsync");
        Assert.False(queued.IsCompleted);
        Assert.Equal(requests, page.Reads.Requests);
        gate.Release.SetResult();
        await Task.WhenAll(reload, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(requests + 1, page.Reads.Requests);
        Assert.False(page.Get<bool>("isReloading"));
        Assert.Equal(60, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Document.TimeRangeMinutesOverride);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedReadDiscardsTheCapturedReaderAfterAuthenticationTaskChanges(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync();
        var gate = page.Reads.Block();
        var reload = page.Invoke("ReloadViewAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.SetAuthentication(Task.FromResult(new AuthenticationState(Operator)));
        gate.Release.SetResult();
        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(page.Get<PublishedAnalysisDashboardSnapshot?>("snapshot"));
        Assert.Null(page.Get<string?>("status"));
        Assert.Contains(page.Text["Analysis.Errors.PublishedReaderChanged"], page.Decoded);
        Assert.DoesNotContain("AD dashboard", page.Decoded);
        Assert.Contains(page.Log.Entries, entry => entry.Level == LogLevel.Warning);
        await page.Invoke("ReloadViewAsync");
        Assert.Contains("AD dashboard", page.Decoded);
        Assert.Null(page.Get<string?>("errorMessage"));
    }

    [Theory]
    [InlineData("en-US", false, false)]
    [InlineData("de-DE", false, false)]
    [InlineData("en-US", true, false)]
    [InlineData("de-DE", true, false)]
    [InlineData("en-US", true, true)]
    [InlineData("de-DE", true, true)]
    public async Task PublishedDisposalCancelsAndDrainsReadsWithoutLateProjection(string culture, bool noncooperative, bool lateFailure)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync();
        await page.Invoke("OnAfterRenderAsync", true);
        var before = page.Get<PublishedAnalysisDashboardSnapshot>("snapshot");
        var status = page.Get<string?>("status");
        var failure = new IOException("Private late read detail.");
        if (lateFailure) page.Reads.NextFailure = failure;
        var gate = page.Reads.Block(noncooperative);
        var reload = page.Invoke("ReloadAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = page.Invoke("ReloadAsync");
        var first = page.DisposePage();
        var ownedDisposal = page.Get<Task>("disposeTask");
        var second = page.DisposePage();
        Assert.Same(ownedDisposal, page.Get<Task>("disposeTask"));
        if (noncooperative)
        {
            Assert.False(first.IsCompleted);
            gate.Release.SetResult();
        }
        await Task.WhenAll(first, second, reload, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(page.Clock.Disposed);
        Assert.False(page.Get<bool>("isReloading"));
        Assert.Same(before, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot"));
        Assert.Equal(status, page.Get<string?>("status"));
        Assert.Null(page.Get<string?>("errorMessage"));
        if (lateFailure)
            Assert.Contains(page.Log.Entries, entry => ReferenceEquals(entry.Exception, failure));
        Assert.Throws<ObjectDisposedException>(() => page.Get<CancellationTokenSource>("cancellation").Token);
        var requests = page.Reads.Requests;
        await page.Invoke("ReloadViewAsync");
        await page.Invoke("RunAsync");
        await page.Invoke("ExecutionSettingsChanged", ((int?)120, (int?)10));
        Assert.Equal(requests, page.Reads.Requests);
        Assert.Same(before, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedPendingAuthenticationDoesNotBlockDisposal(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync();
        var pending = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.SetAuthentication(pending.Task);
        var requests = page.Reads.Requests;
        var reload = page.Invoke("ReloadAsync");
        Assert.True(page.Get<bool>("isReloading"));
        await page.DisposePage().WaitAsync(TimeSpan.FromSeconds(5));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        pending.SetResult(new AuthenticationState(Alice));
        Assert.Equal(requests, page.Reads.Requests);
        Assert.Null(page.Get<string?>("errorMessage"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedChangedPendingIdentityCannotStartAnOldReaderDatabaseRead(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync();
        var pending = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.SetAuthentication(pending.Task);
        var requests = page.Reads.Requests;
        var reload = page.Invoke("ReloadAsync");
        page.SetAuthentication(Task.FromResult(new AuthenticationState(Operator)));
        pending.SetResult(new AuthenticationState(Alice));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(requests, page.Reads.Requests);
        Assert.Null(page.Get<PublishedAnalysisDashboardSnapshot?>("snapshot"));
        Assert.Contains(page.Text["Analysis.Errors.PublishedReaderChanged"], page.Decoded);
        await page.Invoke("ReloadViewAsync");
        Assert.Equal(requests + 1, page.Reads.Requests);
        Assert.Contains("AD dashboard", page.Decoded);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedDisposalAwaitsNoncooperativeExecutionWithoutLateWidgetOrStatusWrites(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync(enabled: true);
        await page.Invoke("OnAfterRenderAsync", true);
        var snapshot = page.Get<PublishedAnalysisDashboardSnapshot>("snapshot");
        var status = page.Get<string?>("status");
        var failure = new IOException("Private late execution detail.");
        page.Executions.NextFailure = failure;
        var gate = page.Executions.Block(noncooperative: true);
        var execution = page.Invoke("RunAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requests = page.Reads.Requests;
        var disposal = page.DisposePage();
        Assert.False(disposal.IsCompleted);
        gate.Release.SetResult();
        await Task.WhenAll(execution, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(requests, page.Reads.Requests);
        Assert.Equal(snapshot.Publication.Version, page.Get<PublishedAnalysisDashboardSnapshot>("snapshot").Publication.Version);
        Assert.Equal(status, page.Get<string?>("status"));
        Assert.Null(page.Get<string?>("errorMessage"));
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Empty(page.Get<Dictionary<string, string>>("errors"));
        Assert.False(page.Get<bool>("running"));
        Assert.True(page.Clock.Disposed);
        Assert.Contains(page.Log.Entries, entry => ReferenceEquals(entry.Exception, failure));
        Assert.Throws<ObjectDisposedException>(() => page.Get<CancellationTokenSource>("cancellation").Token);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task PublishedTeardownLogsFailuresAndStillDrainsTheOwnedRead(string culture, bool callback)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync();
        var failure = new IOException("Private teardown detail.");
        using var registration = callback
            ? page.Get<CancellationTokenSource>("cancellation").Token.Register(() => throw failure)
            : default;
        if (callback) await page.Invoke("OnAfterRenderAsync", true);
        else page.Set("refreshTask", Task.FromException(failure));
        var gate = page.Reads.Block(noncooperative: true);
        var reload = page.Invoke("ReloadAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = page.DisposePage();
        Assert.False(disposal.IsCompleted);
        gate.Release.SetResult();
        await Task.WhenAll(disposal, reload).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(page.Log.Entries, entry => ReferenceEquals(entry.Exception, failure)
            || entry.Exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(failure));
        if (callback) Assert.True(page.Clock.Disposed);
        Assert.Throws<ObjectDisposedException>(() => page.Get<CancellationTokenSource>("cancellation").Token);
        Assert.Null(page.Get<string?>("errorMessage"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedExecutionCannotReuseAdminQueryIdsAfterReaderChanges(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var page = await PublishedViewFixture.CreateAsync(enabled: true, twoWidgets: true);
        page.Executions.NextFailure = new IOException("Private delayed query detail.");
        var gate = page.Executions.Block();
        var execution = page.Invoke("RunAsync");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.SetAuthentication(Task.FromResult(new AuthenticationState(Operator)));
        gate.Release.SetResult();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, page.Executions.Requests);
        Assert.Empty(page.Get<Dictionary<string, AnalysisQueryResponse>>("results"));
        Assert.Empty(page.Get<Dictionary<string, string>>("errors"));
        Assert.Null(page.Get<PublishedAnalysisDashboardSnapshot?>("snapshot"));
        Assert.Null(page.Get<string?>("status"));
        Assert.Contains(page.Text["Analysis.Errors.PublishedReaderChanged"], page.Decoded);
        Assert.DoesNotContain("Private", page.Decoded);
        await page.Invoke("ReloadViewAsync");
        await page.Invoke("RunAsync");
        Assert.Equal(2, page.Executions.Requests);
        Assert.All(page.Get<Dictionary<string, string>>("errors").Values,
            message => Assert.Equal(page.Text["Analysis.Errors.QueryUnavailableOrDenied"], message));
        Assert.Equal(2, page.Get<Dictionary<string, string>>("errors").Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PublishedExplicitRecoveryAutoRunsOnceAndStartsOnlyOneInteractiveTimer(string culture)
    {
        using var selection = new UiSelection(culture);
        var failure = new IOException("Private identity detail.");
        await using var page = await PublishedViewFixture.CreateAsync(enabled: true, controls: false, autoRun: true,
            authentication: Task.FromException<AuthenticationState>(failure));
        Assert.Equal(0, page.Clock.TimerCount);
        await page.Invoke("OnAfterRenderAsync", true);
        Assert.Equal(1, page.Clock.TimerCount);
        Assert.Equal(0, page.Executions.Requests);
        page.SetAuthentication(Task.FromResult(new AuthenticationState(Alice)));
        page.Executions.NextFailure = new IOException("Private query detail.");
        await page.Invoke("ReloadViewAsync");
        Assert.Equal(1, page.Executions.Requests);
        Assert.Contains("AD dashboard", page.Decoded);
        Assert.DoesNotContain("Private", page.Decoded);
        await page.Invoke("OnAfterRenderAsync", false);
        await page.Invoke("OnAfterRenderAsync", true);
        Assert.Equal(1, page.Executions.Requests);
        Assert.Equal(1, page.Clock.TimerCount);
        await page.SetAutoRun(false);
        await page.Invoke("OnAfterRenderAsync", false);
        await page.SetAutoRun(true);
        page.Executions.NextFailure = new IOException("Private query detail.");
        await page.Invoke("OnAfterRenderAsync", false);
        Assert.Equal(2, page.Executions.Requests);
        Assert.Equal(1, page.Clock.TimerCount);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task DirectDashboardRouteShowsOnlyWorkspaceAndWidgetsWithoutChangingTheStoredDocument(string culture, bool roles)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: roles, queryClient: client, direct: true);
        var stored = JsonSerializer.Serialize((await page.Storage.Publishing.LoadAsync(page.Component.DashboardId, Operator)).Document);
        Assert.False(page.Component.ShowControls);
        Assert.False(page.Component.ShowDetails);
        Assert.True(page.Component.AutoRunOnOpen);
        Assert.Contains("LDAP latency", page.Decoded);
        Assert.Single(Regex.Matches(page.Decoded, "<select\\b"));
        Assert.Contains(page.Text["Filters.Workspace"], page.Decoded);
        Assert.DoesNotContain(page.Text["Filters.Domain"], page.Decoded);
        Assert.DoesNotContain(page.Text["Filters.Site"], page.Decoded);
        Assert.DoesNotContain("alice@example.com", page.Decoded);
        Assert.DoesNotContain("AD dashboard", page.Decoded);
        Assert.DoesNotContain(page.Text["AnalysisPublications.SelectedSystems"].Split("{0}")[0], page.Decoded);
        Assert.DoesNotContain(page.Text["Analysis.RoleSelection.FiltersHelp"], page.Decoded);
        Assert.DoesNotContain(page.Text["Analysis.RunDashboard"], page.Decoded);
        Assert.DoesNotContain(page.Text["Analysis.AutoRefresh"], page.Decoded);
        Assert.DoesNotContain(page.Text["AnalysisExecution.TimeRange"], page.Decoded);
        Assert.DoesNotContain("analysis-dashboard-tabs", page.Decoded);
        Assert.DoesNotContain("admin-section-heading", page.Decoded);
        await page.Invoke("OnAfterRenderAsync", true);
        Assert.Equal(roles ? 2 : 0, client.Calls);
        Assert.Equal(stored, JsonSerializer.Serialize((await page.Storage.Publishing.LoadAsync(page.Component.DashboardId, Operator)).Document));
        if (!roles) Assert.Contains(page.Text["Analysis.Disabled"], page.Decoded);
    }

    [Fact]
    public async Task DirectWorkspaceSelectionRecreatesTheReaderAndRunsOnlyTheSelectedWorkspace()
    {
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, direct: true);
        await page.Invoke("OnAfterRenderAsync", true);
        Assert.Equal(2, client.Calls);
        foreach (var workspace in new[] { "a", "b", "" })
        {
            var old = page.Component;
            await page.SetWorkspace(workspace);
            Assert.NotSame(old, page.Component);
            Assert.Equal(workspace, page.Component.WorkspaceKey);
            Assert.Null(page.Component.Domain);
            Assert.Null(page.Component.Site);
            Assert.Null(page.Component.Role);
            Assert.Null(page.Component.FilteredSystems);
            var previousCalls = client.Calls;
            await page.Invoke("OnAfterRenderAsync", true);
            Assert.Equal(workspace.Length == 0 ? 2 : 1, client.Calls - previousCalls);
            if (workspace.Length > 0) Assert.Equal(workspace, client.Requests.Last().Workspace);
        }
        Assert.Single((await page.Storage.Repository.LoadAsync("alice@example.com")).Collection!.Dashboards);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DirectReaderKeepsEmptyRoleAndWithdrawalWarningsVisible(string culture)
    {
        using var display = new UiSelection(culture);
        var client = new DashboardQueryClient();
        await using var page = await PublishedViewFixture.CreateAsync(roles: true, queryClient: client, direct: true);
        await using (var context = page.Storage.Factory.CreateDbContext())
        {
            context.ComputerInventory.RemoveRange(await context.ComputerInventory.ToArrayAsync());
            await context.SaveChangesAsync();
        }
        await page.Invoke("OnAfterRenderAsync", true);
        Assert.Equal(0, client.Calls);
        Assert.Contains(page.Text["Analysis.RoleSelection.Empty"], page.Decoded);
        var id = page.Component.DashboardId;
        await page.Storage.Publishing.WithdrawAsync(id, Alice);
        await page.Invoke("ReloadAsync");
        Assert.Contains(page.Text["Analysis.Errors.PublicationUnavailable"], page.Decoded);
        Assert.Contains("role=\"alert\"", page.Decoded);
        Assert.Contains(page.Text["AnalysisPublications.Reload"], page.Decoded);
        Assert.DoesNotContain("LDAP latency", page.Decoded);
    }

    [Fact]
    public void DirectRouteRetainsOperatorAuthorization()
    {
        var type = typeof(MonitoringApp.Components.Pages.PublishedDashboard);
        Assert.Equal("/analysis/published/{DashboardId:guid}",
            Assert.Single(type.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
        Assert.Equal("OperatorAccess",
            Assert.Single(type.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Policy);
    }

    private sealed class PublishedViewFixture(Fixture storage, ServiceProvider services, StaticPageRenderer renderer,
        CapturingComponentActivator components, Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root)
        : IAsyncDisposable
    {
        private const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        public PublishedAnalysisDashboardView Component => components.PublishedView!;
        public MonitoringApp.Components.Pages.PublishedDashboard Page => components.PublishedDashboardPage!;
        public Fixture Storage => storage;
        public ControlledPublishedFactory Reads => services.GetRequiredService<ControlledPublishedFactory>();
        public ControlledPublishedFactory Executions { get; init; } = default!;
        public AnalysisLog<PublishedAnalysisDashboardView> Log => services.GetRequiredService<AnalysisLog<PublishedAnalysisDashboardView>>();
        public AnalysisRefreshLoopTests.ManualTimeProvider Clock => services.GetRequiredService<AnalysisRefreshLoopTests.ManualTimeProvider>();
        public UiText Text => services.GetRequiredService<UiText>();
        public string Decoded => WebUtility.HtmlDecode(renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult());
        public T Get<T>(string name) => (T)Component.GetType().GetField(name, Flags)!.GetValue(Component)!;
        public void Set(string name, object? value) => Component.GetType().GetField(name, Flags)!.SetValue(Component, value);
        public void SetAuthentication(Task<AuthenticationState> authentication) =>
            Component.GetType().GetProperty("AuthenticationStateTask", Flags)!.SetValue(Component, authentication);
        public Task SetAutoRun(bool value) => renderer.Dispatcher.InvokeAsync(() =>
            Component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AutoRunOnOpen"] = value })));
        public Task SetParameters(Dictionary<string, object?> values) => renderer.Dispatcher.InvokeAsync(() =>
            Component.SetParametersAsync(ParameterView.FromDictionary(values)));
        public Task SetWorkspace(string value) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(MonitoringApp.Components.Pages.PublishedDashboard).GetField("workspaceKey", Flags)!.SetValue(Page, value);
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Page, null);
            await root.QuiescenceTask;
        });
        public Task DrainExecution() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (Get<Task?>("executionTask") is { } task) await task;
        });
        public Task Invoke(string name, params object[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = Component.GetType().GetMethod(name, Flags)!.Invoke(Component, arguments);
            if (result is Task task) await task;
            if (!Get<bool>("disposed")) typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(Component, null);
        });
        public Task DisposePage() => renderer.Dispatcher.InvokeAsync(() => Component.DisposeAsync().AsTask());
        public static async Task<PublishedViewFixture> CreateAsync(Exception? initialFailure = null,
            Task<AuthenticationState>? authentication = null, bool controls = true, bool enabled = false,
            bool autoRun = false, bool twoWidgets = false, bool roles = false,
            DashboardQueryClient? queryClient = null, bool workspaceWide = false, bool direct = false)
        {
            var storage = await Fixture.CreateAsync();
            var collection = CreateDashboard();
            collection.Active.Document.AutoRefreshEnabled = true;
            if (roles)
            {
                enabled = true;
                collection.Active.Document.SelectionMode = AnalysisDashboardSelectionMode.InventoryRoles;
                collection.Active.Document.InventoryRoles = ["Domain Controller"];
                collection.Active.Document.Systems.Clear();
            }
            if (twoWidgets) collection.Active.Document.Widgets.Add(new() { QueryDefinitionId = Guid.NewGuid(), Row = 5 });
            await storage.Repository.SaveAsync("alice@example.com", collection, 0);
            var publication = await storage.Publishing.PublishAsync(collection.ActiveDashboardId, 1, Alice);
            if (enabled)
            {
                await using var context = storage.Factory.CreateDbContext();
                if (roles)
                    context.ComputerInventory.AddRange(
                        new() { SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "DC01", Role = "Domain Controller" },
                        new() { SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "DC02", Role = "domaincontrollers" },
                        new() { SubscriptionId = "22222222-2222-2222-2222-222222222222", Computer = "DC01", Role = "Domain Controller" },
                        new() { SubscriptionId = "11111111-1111-1111-1111-111111111111", Computer = "UNSELECTED", Role = "Other" });
                else
                    context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "DC01" });
                foreach (var widget in collection.Active.Document.Widgets)
                {
                    var query = ActiveDirectoryAnalysisQueries.Create().First();
                    if (roles)
                    {
                        query.Kql = "print Series = 'DC01', Value = 1" +
                            (workspaceWide ? " | where now() >= ago(_TimePeriod)" : " | where Series in~ (_SelectedSystems)");
                        query.Scope = workspaceWide ? AnalysisQueryScopes.Workspace : AnalysisQueryScopes.SelectedSystems;
                        query.VisualizationType = AnalysisQueryVisualizationTypes.Stat;
                    }
                    query.Id = widget.QueryDefinitionId;
                    query.Key = widget.QueryDefinitionId.ToString("N");
                    query.MinimumRole = roles ? SqlAuthenticationRoles.Operator : SqlAuthenticationRoles.Admin;
                    var version = new AnalysisQueryVersion
                    {
                        Id = Guid.NewGuid(), Version = 1,
                        SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
                    };
                    query.Versions.Add(version);
                    query.PublishedVersionId = version.Id;
                    if (roles)
                    {
                        query.Workspaces.Add(new() { WorkspaceKey = "a", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded });
                        query.Workspaces.Add(new() { WorkspaceKey = "b", Enabled = true, LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded });
                    }
                    context.AnalysisQueryDefinitions.Add(query);
                }
                await context.SaveChangesAsync();
            }
            var reads = new ControlledPublishedFactory(storage.Factory) { NextFailure = initialFailure };
            var executions = new ControlledPublishedFactory(storage.Factory);
            var options = new CrossTenantLogAnalyticsOptions
            {
                Enabled = enabled,
                Workspaces = roles ? [
                    new() { Key = "a", DisplayName = "Workspace A", WorkspaceId = Guid.NewGuid().ToString(), SubscriptionIds = ["11111111-1111-1111-1111-111111111111"] },
                    new() { Key = "b", DisplayName = "Workspace B", WorkspaceId = Guid.NewGuid().ToString(), SubscriptionIds = ["22222222-2222-2222-2222-222222222222"] }] : []
            };
            ILogAnalyticsSecretProvider secret = roles ? new DashboardSecret() : new UnusedSecretProvider();
            var runner = new AnalysisQueryRunner(options, secret, new TargetWorkspaceResolver(options), queryClient);
            var components = new CapturingComponentActivator();
            var log = new AnalysisLog<PublishedAnalysisDashboardView>();
            var clock = new AnalysisRefreshLoopTests.ManualTimeProvider();
            var services = new ServiceCollection().AddLogging().AddUiLocalization()
                .AddSingleton<IComponentActivator>(components).AddSingleton(log)
                .AddSingleton<ILogger<PublishedAnalysisDashboardView>>(log)
                .AddSingleton(reads)
                .AddSingleton(storage.CreatePublishing(reads))
                .AddSingleton(new AnalysisQueryRegistry(storage.Factory, options, runner))
                .AddSingleton(new AnalysisQueryExecutionService(executions, runner))
                .AddSingleton(options).AddSingleton(new ApplicationAuthenticationOptions())
                .AddSingleton(clock).AddSingleton<TimeProvider>(clock).BuildServiceProvider();
            var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(CascadingValue<Task<AuthenticationState>>),
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        ["Value"] = authentication ?? Task.FromResult(new AuthenticationState(Alice)),
                        ["ChildContent"] = (RenderFragment)(builder =>
                        {
                            builder.OpenComponent(0, direct ? typeof(MonitoringApp.Components.Pages.PublishedDashboard) : typeof(PublishedAnalysisDashboardView));
                            builder.AddAttribute(1, "DashboardId", publication.Id);
                            if (!direct)
                            {
                                builder.AddAttribute(2, "ShowControls", controls);
                                builder.AddAttribute(3, "AutoRunOnOpen", autoRun);
                            }
                            builder.CloseComponent();
                        })
                    }));
                await root.QuiescenceTask;
                return root;
            });
            return new(storage, services, renderer, components, root) { Executions = executions };
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await services.DisposeAsync();
            await storage.DisposeAsync();
        }
    }

    private sealed class ControlledPublishedFactory(IDbContextFactory<AlertDbContext> inner) : IDbContextFactory<AlertDbContext>
    {
        private int requests;
        private Func<CancellationToken, Task>? beforeNext;
        public int Requests => Volatile.Read(ref requests);
        public Exception? NextFailure { get; set; }
        public (TaskCompletionSource Started, TaskCompletionSource Release) Block(bool noncooperative = false)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeNext = async token =>
            {
                started.SetResult();
                if (noncooperative) await release.Task;
                else await release.Task.WaitAsync(token);
            };
            return (started, release);
        }
        public AlertDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref requests);
            var wait = beforeNext;
            beforeNext = null;
            var failure = NextFailure;
            NextFailure = null;
            if (wait is not null) await wait(cancellationToken);
            if (failure is not null) throw failure;
            return await inner.CreateDbContextAsync(cancellationToken);
        }
    }

    private static AnalysisDashboardCollection CreateDashboard()
    {
        var collection = AnalysisDashboardCollection.Create();
        collection.RenameActive("AD dashboard");
        collection.HiddenQueryIds.Add(Guid.NewGuid());
        collection.Active.Document.HiddenQueryIds.Add(Guid.NewGuid());
        collection.Active.Document.Systems.Add(new() { SubscriptionId = "sub", Computer = "DC01" });
        collection.Active.Document.Widgets.Add(new() { QueryDefinitionId = Guid.NewGuid(), Title = "LDAP latency", TimeRangeMinutes = 240, Column = 2, Row = 3 });
        return collection;
    }

    private static ClaimsPrincipal User(string name, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        if (role is not null)
            claims.Add(new(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "test"));
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previousUi = CultureInfo.CurrentUICulture;
        private readonly CultureInfo previousFormat = CultureInfo.CurrentCulture;
        public UiSelection(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }

    private sealed class DashboardSaveInterceptor : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public int FailAt { get; set; }
        public bool AfterCommit { get; set; }
        public Action<DbContext>? AfterPublicationWrite { get; set; }
        public Exception Failure { get; set; } = new InvalidOperationException("Sensitive dashboard <storage> & detail.");
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<AnalysisDashboard>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified) ||
                data.Context.ChangeTracker.Entries<AnalysisDashboardTemplate>()
                    .Any(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                Attempts++;
                if (Attempts == FailAt && !AfterCommit) throw Failure;
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result,
            CancellationToken cancellationToken = default)
        {
            if ((data.Context!.ChangeTracker.Entries<AnalysisDashboard>().Any() ||
                data.Context.ChangeTracker.Entries<AnalysisDashboardTemplate>().Any()) && Attempts == FailAt && AfterCommit)
                throw Failure;
            if (data.Context.ChangeTracker.Entries<AnalysisDashboardTemplate>().Any())
                AfterPublicationWrite?.Invoke(data.Context);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AnalysisLog<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, exception));
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, TestDbContextFactory factory,
        bool directoryEnabled) : IAsyncDisposable
    {
        public IDbContextFactory<AlertDbContext> Factory => factory;
        public AnalysisDashboardRepository Repository { get; } = new(factory, new() { Enabled = directoryEnabled });
        public AnalysisDashboardPublishing Publishing { get; } = new(factory,
            services.GetRequiredService<IAuthorizationService>(), new CurrentUserIdentity(new HttpContextAccessor()),
            services.GetRequiredService<ApplicationAuthenticationOptions>(), new() { Enabled = directoryEnabled });
        public AnalysisDashboardPublishing CreatePublishing(IDbContextFactory<AlertDbContext> contextFactory) => new(contextFactory,
            services.GetRequiredService<IAuthorizationService>(), new CurrentUserIdentity(new HttpContextAccessor()),
            services.GetRequiredService<ApplicationAuthenticationOptions>(), new() { Enabled = directoryEnabled });

        public static async Task<Fixture> CreateAsync(bool open = false, SaveChangesInterceptor? interceptor = null,
            bool directoryEnabled = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var factory = new TestDbContextFactory(options.Options);
            await using var context = factory.CreateDbContext();
            string[] identityTables = ["DirectoryUsers", "HistoricalUserAliases", "SqlUserIdentities",
                "EntraUserIdentities", "ScimUserStates", "ScimConnections", "AuthenticationUsers", "LegacyUserLinks"];
            var statements = context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement =>
                    statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisDashboards\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisDashboardTemplates\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisQueryDefinitions\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisQueryWorkspaces\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"AnalysisQueryVersions\" ", StringComparison.Ordinal)
                    || statement.TrimStart().StartsWith("CREATE TABLE \"ComputerInventory\" ", StringComparison.Ordinal)
                    || statement.Contains(" ON \"AnalysisDashboardTemplates\" ", StringComparison.Ordinal)
                    || directoryEnabled && identityTables.Any(table =>
                        statement.TrimStart().StartsWith($"CREATE TABLE \"{table}\" ", StringComparison.Ordinal) ||
                        statement.Contains($" ON \"{table}\" ", StringComparison.Ordinal)));
            foreach (var statement in statements)
                await context.Database.ExecuteSqlRawAsync(statement
                    .Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            var services = new ServiceCollection().AddLogging().AddSingleton(new ApplicationAuthenticationOptions
            {
                Type = open ? ApplicationAuthenticationOptions.Open : ApplicationAuthenticationOptions.Sql
            }).AddAuthorization(options =>
            {
                options.AddPolicy("AdminAccess", policy => policy.RequireAssertion(context => open || context.User.IsInRole(SqlAuthenticationRoles.Admin)));
                options.AddPolicy("OperatorAccess", policy => policy.RequireAssertion(context => open
                    || context.User.IsInRole(SqlAuthenticationRoles.Operator) || context.User.IsInRole(SqlAuthenticationRoles.Admin)));
            }).BuildServiceProvider();
            return new(connection, services, factory, directoryEnabled);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class CapturingComponentActivator : IComponentActivator
    {
        public AnalysisDashboardExecutionControls? ExecutionControls { get; private set; }
        public MonitoringApp.Components.Pages.Analysis? AnalysisPage { get; private set; }
        public PublishedAnalysisDashboardView? PublishedView { get; private set; }
        public MonitoringApp.Components.Pages.PublishedDashboard? PublishedDashboardPage { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AnalysisDashboardExecutionControls controls)
                ExecutionControls = controls;
            if (component is MonitoringApp.Components.Pages.Analysis page)
                AnalysisPage = page;
            if (component is PublishedAnalysisDashboardView view)
                PublishedView = view;
            if (component is MonitoringApp.Components.Pages.PublishedDashboard publishedPage)
                PublishedDashboardPage = publishedPage;
            return component;
        }
    }

    private sealed class GatedDbContextFactory(IDbContextFactory<AlertDbContext> inner, bool timeoutFirst = false) : IDbContextFactory<AlertDbContext>
    {
        private int inventoryReads;
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public TaskCompletionSource[] Started { get; } =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public TaskCompletionSource[] Release { get; } =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];

        public AlertDbContext CreateDbContext() => inner.CreateDbContext();

        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref inventoryReads) == 1)
                return await inner.CreateDbContextAsync(cancellationToken);
            var index = Interlocked.Increment(ref requestCount) - 1;
            Started[index].TrySetResult();
            await Release[index].Task.WaitAsync(cancellationToken);
            if (index == 0 && timeoutFirst)
                throw new TaskCanceledException("The first widget timed out.");
            return await inner.CreateDbContextAsync(cancellationToken);
        }
    }

    private sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode
                ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Render mode {renderMode.GetType().Name} is not supported in static page tests.");
    }

    private sealed class UnusedJavaScript : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static HTML rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static HTML rendering must not invoke JavaScript.");
    }

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Read-only rendering must not access Key Vault.");
    }
}
