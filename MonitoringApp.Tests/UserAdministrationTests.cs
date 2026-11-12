using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class UserAdministrationTests
{
    private static string Password => AuthenticationTestData.Cases.Password;
    private static readonly Guid ActorObject = Guid.Parse("12000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task UnifiedInventoryShowsUnlearnedSqlWithoutBackfillAndDistinguishesActualProviders()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Directory.ResolveAsync(fixture.Entra(Guid.NewGuid(), "Known"));
        await fixture.CreateScimAsync();
        var snapshot = await fixture.SnapshotAsync();
        Assert.Equal(6, snapshot.Users.Count);
        Assert.Equal(3, snapshot.Users.Count(user => user.Provider == "Sql" && user.Id is null));
        Assert.Single(snapshot.Users, user => user.Provider == "Scim");
        Assert.Equal(2, snapshot.Users.Count(user => user.Provider == "Entra"));
        Assert.All(snapshot.Users, user => Assert.True(user.Unassigned));
        Assert.All(snapshot.Users, user => Assert.False(user.UsesStableIdentity));
        await using var context = fixture.Host.CreateDbContext();
        Assert.Equal(3, await context.DirectoryUsers.CountAsync());
        Assert.Empty(await context.SqlUserIdentities.ToArrayAsync());
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("PasswordHash", json);
        Assert.DoesNotContain(Password, json);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("entra")]
    [InlineData("sql")]
    public async Task HistoricalDisplayGroupsUseOnlyTheMainProfileAndDetachingRestoresAllAliasData(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode);
        var (main, alias) = await GroupingProfilesAsync(fixture);
        var originalAlias = alias;
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(main) with { HistoricalAliasKeys = [alias.Key] });
        var snapshot = await fixture.SnapshotAsync();
        main = snapshot.Users.Single(user => user.Key == main.Key);
        alias = snapshot.Users.Single(user => user.Key == alias.Key);
        Assert.Equal(main.Key, alias.HistoricalMainKey);
        Assert.True(alias.HistoricalMainAvailable);
        Assert.False(alias.CanEditProfile);
        Assert.Equal(alias.Key, Assert.Single(main.HistoricalAliases).Key);
        Assert.Equal(originalAlias.DisplayName, alias.DisplayName);
        Assert.Equal(originalAlias.LocalDisplayName, alias.LocalDisplayName);
        Assert.Equal(originalAlias.GivenName, alias.GivenName);
        Assert.Equal(originalAlias.FamilyName, alias.FamilyName);
        Assert.Equal(originalAlias.Email, alias.Email);
        Assert.Equal(main.Email, snapshot.Users.Single(user => user.Key == main.Key).Email);

        var service = ScimRcaTests.Service(fixture.Host, mode: mode);
        var item = Assert.Single(await service.ListAsync(), item => item.AssignedTo == alias.UserName);
        Assert.Contains("Main <display>", item.AssigneeDisplayName);
        Assert.Contains(alias.UserName, item.AssigneeDisplayName);
        Assert.DoesNotContain(await service.GetAssigneesAsync(fixture.Actor),
            key => key.Equals(alias.UserName, StringComparison.OrdinalIgnoreCase));
        var options = await service.GetAssigneeOptionsAsync(fixture.Actor);
        Assert.DoesNotContain(options, option => option.Key.Equals(alias.UserName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options, option => option.Key == main.UserName && option.Label.Contains("Main <display>"));
        Assert.Contains(await ScimRcaTests.Service(fixture.Host, enabled: false, mode: mode).GetAssigneesAsync(fixture.Actor),
            key => key.Equals(alias.UserName, StringComparison.OrdinalIgnoreCase));
        await service.SaveAsync(fixture.Actor, item.Id, item.Revision, "Retained alias assignment", item.AssignedTo, item.Status, "");
        Assert.Equal(alias.UserName, (await service.ListAsync()).Single(row => row.Id == item.Id).AssignedTo);
        var stale = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveUserAsync(fixture.Actor, Edit(originalAlias)));
        Assert.Equal("UserDirectory.Errors.Stale", stale.ResourceKey);
        Assert.Equal("UserDirectory.Errors.HistoricalGrouped",
            (await Assert.ThrowsAsync<UserAdministrationException>(() =>
                fixture.Service.SaveUserAsync(fixture.Actor, Edit(alias)))).ResourceKey);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(main) with { LocalDisplayName = "Changed main" });
        Assert.Contains("Changed main", (await service.ListAsync()).Single(item => item.AssignedTo == alias.UserName).AssigneeDisplayName);
        await fixture.Service.DetachHistoricalAliasAsync(fixture.Actor, alias.Key, alias.Version);
        snapshot = await fixture.SnapshotAsync();
        var separated = snapshot.Users.Single(user => user.Key == alias.Key);
        Assert.Null(separated.HistoricalMainKey);
        Assert.True(separated.CanEditProfile);
        Assert.Equal(originalAlias.LocalDisplayName, separated.LocalDisplayName);
        Assert.Equal(originalAlias.DisplayName, separated.DisplayName);
        Assert.Equal(originalAlias.GivenName, separated.GivenName);
        Assert.Equal(originalAlias.FamilyName, separated.FamilyName);
        Assert.Equal(originalAlias.Email, separated.Email);
        Assert.Contains("Alias <original>", (await service.ListAsync()).Single(item => item.AssignedTo == alias.UserName).AssigneeDisplayName);
        Assert.Contains(await service.GetAssigneeOptionsAsync(fixture.Actor),
            option => option.Key == alias.UserName && option.Label.Contains("Alias <original>"));
        Assert.Empty(snapshot.Users.Single(user => user.Key == main.Key).HistoricalAliases);
        await using var context = fixture.Host.CreateDbContext();
        Assert.Empty(await context.HistoricalUserAliases.ToArrayAsync());
        Assert.Empty(await context.LegacyUserLinks.ToArrayAsync());
        Assert.Empty(await context.IdentityMigrationRecords.ToArrayAsync());
        Assert.False(await context.DirectoryUsers.AnyAsync(user => user.Origin == DirectoryUserOrigins.Legacy && user.UsesStableIdentity));
        Assert.Equal(3, await context.AuthenticationUsers.CountAsync());
        Assert.Equal(2, await context.RootCauseAnalyses.CountAsync());
        Assert.Equal("historical-admin@example.test", (await context.RootCauseAnalyses.SingleAsync(row => row.Id == item.Id)).AssignedTo);
        Assert.Contains(await context.IdentityAdministrationRecords.ToArrayAsync(), audit =>
            audit.Action == "HistoricalAliasDetached" && audit.IsOpenAuthentication == (mode == "open"));
    }

    [Fact]
    public async Task HistoricalGroupingRejectsSelfReferencesVerifiedSourcesChainsAndMovingAnExistingAlias()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (main, alias) = await GroupingProfilesAsync(fixture);
        var scimId = Guid.Parse(await fixture.CreateScimAsync());
        foreach (var key in new[] { main.Key, "sql:reader", DirectoryIdentity.OwnerKey(scimId), "" })
        {
            var failure = await Assert.ThrowsAsync<UserAdministrationException>(() =>
                fixture.Service.SaveUserAsync(fixture.Actor, Edit(main) with { HistoricalAliasKeys = [key] }));
            Assert.Equal("UserDirectory.Errors.HistoricalAliases", failure.ResourceKey);
        }
        var sql = (await fixture.SnapshotAsync()).Users.Single(user => user.SqlUsername == "reader");
        Assert.Equal("UserDirectory.Errors.SourceReadOnly",
            (await Assert.ThrowsAsync<UserAdministrationException>(() => fixture.Service.SaveUserAsync(fixture.Actor,
                Edit(sql) with { HistoricalAliasKeys = [alias.Key] }))).ResourceKey);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(main) with { HistoricalAliasKeys = [alias.Key] });
        var snapshot = await fixture.SnapshotAsync();
        var groupedMain = snapshot.Users.Single(user => user.Key == main.Key);
        var groupedAlias = snapshot.Users.Single(user => user.Key == alias.Key);
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.LogbookEntries.Add(new() { Id = Guid.NewGuid(), User = "another-main@example.test" });
            await context.SaveChangesAsync();
        }
        var another = (await fixture.SnapshotAsync()).Users.Single(user => user.UserName == "another-main@example.test");
        foreach (var key in new[] { groupedMain.Key, groupedAlias.Key })
            Assert.Equal("UserDirectory.Errors.HistoricalAliases",
                (await Assert.ThrowsAsync<UserAdministrationException>(() => fixture.Service.SaveUserAsync(fixture.Actor,
                    Edit(another) with { HistoricalAliasKeys = [key] }))).ResourceKey);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(groupedMain) with { HistoricalAliasKeys = [] });
        snapshot = await fixture.SnapshotAsync();
        Assert.Null(snapshot.Users.Single(user => user.Key == alias.Key).HistoricalMainKey);
        Assert.Equal("Alias <original>", snapshot.Users.Single(user => user.Key == alias.Key).LocalDisplayName);
        Assert.Equal("UserDirectory.Errors.Stale", (await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveUserAsync(fixture.Actor, Edit(groupedMain) with { HistoricalAliasKeys = [another.Key] }))).ResourceKey);
    }

    [Fact]
    public async Task ChangedMainSourceMakesGroupingExplicitlyUnavailableAndAliasCanStillBeSeparated()
    {
        await using var fixture = await Fixture.CreateAsync("open");
        var (main, alias) = await GroupingProfilesAsync(fixture);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(main) with { HistoricalAliasKeys = [alias.Key] });
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.AuthenticationUsers.Add(new() { Username = main.UserName, Role = "Reader", PasswordHash = "synthetic" });
            await context.SaveChangesAsync();
        }
        var snapshot = await fixture.SnapshotAsync();
        alias = snapshot.Users.Single(user => user.Key == alias.Key);
        Assert.False(alias.HistoricalMainAvailable);
        Assert.NotNull(alias.HistoricalMainKey);
        Assert.Contains("Alias <original>", (await ScimRcaTests.Service(fixture.Host, mode: "open").ListAsync())
            .Single(item => item.AssignedTo == alias.UserName).AssigneeDisplayName);
        await using var ui = await UserUi.CreateAsync(fixture);
        Assert.Contains(ui.Text["UserDirectory.Aliases.UnavailableMain"], ui.Decoded);
        await ui.Invoke("DetachAliasAsync", alias);
        Assert.Null((await fixture.SnapshotAsync()).Users.Single(user => user.Key == alias.Key).HistoricalMainKey);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task HistoricalGroupingUiOffersOnlyIndependentHistoricalProfilesAndSupportsReversibleSelection(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var fixture = await Fixture.CreateAsync("open");
            var (main, alias) = await GroupingProfilesAsync(fixture);
            await using var ui = await UserUi.CreateAsync(fixture);
            await ui.Invoke("BeginEdit", main);
            Assert.Contains(ui.Text["UserDirectory.Aliases.Title"], ui.Decoded);
            Assert.Contains(ui.Text["UserDirectory.Aliases.Help"], ui.Decoded);
            var candidates = typeof(UserDirectoryPanel).GetProperty("HistoricalAliasCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(typeof(UserUi).GetProperty("Panel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ui));
            Assert.Equal(alias.Key, Assert.Single(Assert.IsAssignableFrom<IEnumerable<UserAdministrationHistoricalAlias>>(candidates)).Key);
            ui.Set("busy", true);
            await ui.Invoke("SetHistoricalAlias", alias.Key, new ChangeEventArgs { Value = true });
            Assert.Empty(ui.Get<HashSet<string>>("historicalAliasKeys"));
            ui.Set("busy", false);
            await ui.Invoke("SetHistoricalAlias", alias.Key, new ChangeEventArgs { Value = true });
            await ui.Invoke("SaveAsync");
            var snapshot = await fixture.SnapshotAsync();
            main = snapshot.Users.Single(user => user.Key == main.Key);
            Assert.Contains(ui.Text["UserDirectory.Aliases.Main"], ui.Decoded);
            Assert.Contains("Alias &lt;original&gt;", ui.Html);
            Assert.DoesNotContain("<original>", ui.Html);
            Assert.Equal(1, HeadingCount(ui.Html, "Main &lt;display&gt;"));
            Assert.Equal(0, HeadingCount(ui.Html, "Alias &lt;original&gt;"));
            ui.Set("search", alias.UserName);
            await ui.Invoke("RefreshAsync");
            Assert.Equal(1, HeadingCount(ui.Html, "Main &lt;display&gt;"));
            await ui.Invoke("BeginEdit", main);
            await ui.Invoke("SetHistoricalAlias", alias.Key, new ChangeEventArgs { Value = false });
            await ui.Invoke("SaveAsync");
            Assert.Null((await fixture.SnapshotAsync()).Users.Single(user => user.Key == alias.Key).HistoricalMainKey);
            Assert.Equal(1, HeadingCount(ui.Html, "Alias &lt;original&gt;"));
            Assert.DoesNotContain("UserDirectory.", ui.Decoded);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static int HeadingCount(string value, string content) =>
        System.Text.RegularExpressions.Regex.Matches(value, "<h3\\b[^>]*>" +
            System.Text.RegularExpressions.Regex.Escape(content) + "</h3>").Count;

    private static async Task<(UserAdministrationUser Main, UserAdministrationUser Alias)> GroupingProfilesAsync(Fixture fixture)
    {
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.RootCauseAnalyses.AddRange(
                new RootCauseAnalysis { Id = Guid.NewGuid(), AssignedTo = "historical-main@example.test", Revision = 1 },
                new RootCauseAnalysis { Id = Guid.NewGuid(), AssignedTo = "historical-admin@example.test", Revision = 1 });
            await context.SaveChangesAsync();
        }
        var snapshot = await fixture.SnapshotAsync();
        foreach (var user in snapshot.Users.Where(user => user.IsHistorical))
        {
            var main = user.UserName == "historical-main@example.test";
            await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with
            {
                LocalDisplayName = main ? "Main <display>" : "Alias <original>",
                Profile = new(main ? "Main source" : "Alias source", main ? "Main first" : "Alias first",
                    main ? "Main last" : "Alias last", main ? "main-contact@example.test" : "alias-contact@example.test")
            });
        }
        snapshot = await fixture.SnapshotAsync();
        return (snapshot.Users.Single(user => user.UserName == "historical-main@example.test"),
            snapshot.Users.Single(user => user.UserName == "historical-admin@example.test"));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("entra")]
    [InlineData("sql")]
    public async Task HistoricalProfilesAreDiscoveredWithoutBackfillAndOnlyDecorateOriginalReferences(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode, orderedAuditTimestamps: true);
        const string alias = "historical@example.test";
        var caseId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var publicationId = Guid.NewGuid();
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.RootCauseAnalyses.Add(new() { Id = caseId, AssignedTo = alias, Revision = 1, IsActive = true });
            context.RcaChanges.Add(new()
            {
                Id = Guid.NewGuid(), RcaId = caseId, Actor = alias, Field = "AssignedTo", Before = alias, After = alias
            });
            context.LogbookEntries.Add(new() { Id = Guid.NewGuid(), User = alias.ToUpperInvariant(), Comment = "Historical entry" });
            context.AgentAnalysisJobs.Add(new() { Id = jobId, CreatedBy = alias, IdempotencyKey = "historical-profile-test" });
            context.AgentAnalysisReviewEvents.Add(new() { Id = Guid.NewGuid(), AgentAnalysisJobId = jobId, Actor = alias });
            context.AnalysisDashboardTemplates.Add(new()
            {
                Id = publicationId, UpdatedBy = alias, Published = true, DocumentJson = """{"name":"Historical dashboard"}"""
            });
            await context.SaveChangesAsync();
        }
        var discovered = (await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical);
        Assert.Null(discovered.Id);
        Assert.Equal(alias, discovered.UserName, ignoreCase: true);
        Assert.Equal("Legacy", discovered.Provider);
        Assert.True(discovered.CanEditProfile);
        Assert.False(discovered.CanManageSql);
        await using (var context = fixture.Host.CreateDbContext())
        {
            Assert.False(await context.DirectoryUsers.AnyAsync(user => user.Origin == DirectoryUserOrigins.Legacy));
            Assert.Empty(await context.IdentityAdministrationRecords.ToArrayAsync());
        }
        var request = Edit(discovered) with
        {
            Profile = new("Historical display", "First", "Last", "contact-only@example.test"),
            LocalDisplayName = "Local <display>"
        };
        await fixture.Service.SaveUserAsync(fixture.Actor, request);
        var saved = (await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical);
        Assert.Equal(discovered.Key, saved.Key);
        Assert.NotNull(saved.Id);
        Assert.False(saved.UsesStableIdentity);
        Assert.Equal("Historical display", saved.DisplayName);
        Assert.Equal("First", saved.GivenName);
        Assert.Equal("Last", saved.FamilyName);
        Assert.Equal("contact-only@example.test", saved.Email);
        Assert.Equal(discovered.UserName, saved.UserName);
        Assert.NotEqual(discovered.Version, saved.Version);
        Assert.Equal("UserDirectory.Errors.Stale",
            (await Assert.ThrowsAsync<UserAdministrationException>(() => fixture.Service.SaveUserAsync(fixture.Actor, request))).ResourceKey);

        var rca = ScimRcaTests.Service(fixture.Host, mode: mode);
        var listed = Assert.Single(await rca.ListAsync(), item => item.Id == caseId);
        Assert.Equal(alias, listed.AssignedTo);
        Assert.Contains("Local <display>", listed.AssigneeDisplayName);
        Assert.Contains(await rca.GetOpenNoticesAsync(), notice => notice.Id == caseId && notice.AssigneeLabel.Contains("Local <display>"));
        var option = Assert.Single(await rca.GetAssigneeOptionsAsync(fixture.Actor), entry => entry.Key.Equals(alias, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Local <display>", option.Label);
        Assert.DoesNotContain(await rca.GetAssigneeOptionsAsync(fixture.Actor), entry => entry.Key == DirectoryIdentity.OwnerKey(saved.Id!.Value));
        var change = Assert.Single(await rca.GetChangesAsync(caseId));
        Assert.Equal(alias, change.Actor);
        Assert.Contains("Local <display>", change.ActorDisplayName);
        Assert.Contains("Local <display>", change.BeforeDisplayName);
        Assert.Contains("Local <display>", change.AfterDisplayName);
        var log = Assert.Single(await new LogbookStore(fixture.Host, fixture.Options, fixture.Authentication).GetAllAsync());
        Assert.Equal(alias.ToUpperInvariant(), log.User);
        Assert.Contains("Local <display>", log.UserDisplayName);
        var review = Assert.Single(await new AgentAnalysisReviewStore(fixture.Host, TimeProvider.System,
            fixture.Options, fixture.Authentication).ListAsync(jobId));
        Assert.Equal(alias, review.Actor);
        Assert.Contains("Local <display>", review.ActorDisplayName);
        var publications = new AnalysisDashboardPublishing(fixture.Host,
            fixture.Host.App.Services.GetRequiredService<IAuthorizationService>(), new(new HttpContextAccessor(), fixture.Directory),
            fixture.Authentication, fixture.Options);
        Assert.Contains("Local <display>", Assert.Single(await publications.ListAsync(fixture.Actor)).OwnerDisplayName);
        Assert.Contains("Local <display>", (await publications.LoadAsync(publicationId, fixture.Actor)).Publication.OwnerDisplayName);
        Assert.Null((await ScimRcaTests.Service(fixture.Host, enabled: false, mode: mode).ListAsync()).Single().AssigneeDisplayName);

        await using (var context = fixture.Host.CreateDbContext())
        {
            var profile = await context.DirectoryUsers.SingleAsync(user => user.Id == saved.Id);
            Assert.Equal(DirectoryUserOrigins.Legacy, profile.Origin);
            Assert.False(await context.EntraUserIdentities.AnyAsync(identity => identity.UserId == profile.Id));
            Assert.False(await context.SqlUserIdentities.AnyAsync(identity => identity.UserId == profile.Id));
            Assert.False(await context.ScimUserStates.AnyAsync(state => state.UserId == profile.Id));
            Assert.Empty(await context.LegacyUserLinks.ToArrayAsync());
            Assert.Empty(await context.IdentityMigrationRecords.ToArrayAsync());
            var audit = Assert.Single(await context.IdentityAdministrationRecords.ToArrayAsync());
            Assert.Equal("HistoricalProfileUpdated", audit.Action);
            Assert.Equal(mode == "open", audit.IsOpenAuthentication);
            Assert.Equal(mode == "open", audit.ActorUserId is null);
            Assert.Equal(alias, (await context.RootCauseAnalyses.SingleAsync()).AssignedTo);
            Assert.Empty(await context.UserTeamMemberships.ToArrayAsync());
        }
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(saved) with { LocalDisplayName = null, Profile = new("", "First", "Last", null) });
        Assert.Contains("First Last", (await rca.ListAsync()).Single().AssigneeDisplayName);
        Assert.Null((await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical).Email);
    }

    [Fact]
    public async Task DiscoveryCoversOwnersActorsAndAssignmentHistoryButNeverCreatesOrConfirmsIdentities()
    {
        await using var fixture = await Fixture.CreateAsync();
        var caseId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var queryId = Guid.NewGuid();
        var confirmedUser = DirectoryIdentity.UserId(await fixture.Directory.ResolveAsync(fixture.Actor))!.Value;
        var aliases = Enumerable.Range(1, 12).Select(index => $"historical-{index}@example.test").ToArray();
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.AnalysisDashboards.Add(new() { OwnerKey = aliases[0] });
            context.InboxLayouts.Add(new() { OwnerKey = aliases[1] });
            context.AnalysisDashboardTemplates.Add(new() { Id = Guid.NewGuid(), UpdatedBy = aliases[2] });
            context.AgentAnalysisJobs.Add(new() { Id = jobId, CreatedBy = aliases[3], IdempotencyKey = "discovery" });
            context.LogbookEntries.Add(new() { Id = Guid.NewGuid(), User = aliases[4] });
            context.RootCauseAnalyses.Add(new() { Id = caseId, AssignedTo = aliases[5], Revision = 1 });
            context.RcaChanges.Add(new()
            {
                Id = Guid.NewGuid(), RcaId = caseId, Actor = aliases[6], Field = "AssignedTo", Before = aliases[10], After = aliases[11]
            });
            context.AnalysisQueryDefinitions.Add(new() { Id = queryId, Key = "discovery", UpdatedBy = aliases[7] });
            context.AnalysisQueryVersions.Add(new() { Id = Guid.NewGuid(), QueryDefinitionId = queryId, PublishedBy = aliases[8] });
            context.AgentAnalysisReviewEvents.Add(new() { Id = Guid.NewGuid(), AgentAnalysisJobId = jobId, Actor = aliases[9] });
            context.RcaChanges.Add(new()
            {
                Id = Guid.NewGuid(), RcaId = caseId, Actor = "reader", Field = "Analysis",
                Before = "not-a-user@example.test", After = "also-not-a-user@example.test"
            });
            context.LogbookEntries.AddRange(new LogbookEntry { Id = Guid.NewGuid(), User = aliases[0].ToUpperInvariant() },
                new LogbookEntry { Id = Guid.NewGuid(), User = DirectoryIdentity.OwnerKey(confirmedUser) },
                new LogbookEntry { Id = Guid.NewGuid(), User = "confirmed@example.test" }, new LogbookEntry { Id = Guid.NewGuid(), User = " " });
            context.LegacyUserLinks.Add(new()
            {
                Alias = "confirmed@example.test", TargetUserId = confirmedUser, Status = LegacyUserLinkStatuses.Confirmed,
                ApprovedByUserId = confirmedUser, ApprovedAt = DateTimeOffset.UtcNow, VerificationNote = "Synthetic verified history"
            });
            await context.SaveChangesAsync();
        }
        var snapshot = await fixture.SnapshotAsync();
        Assert.Equal(aliases.Order(StringComparer.OrdinalIgnoreCase),
            snapshot.Users.Where(user => user.IsHistorical).Select(user => user.UserName).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.All(snapshot.Users.Where(user => user.IsHistorical), user => Assert.Null(user.Id));
        await using var check = fixture.Host.CreateDbContext();
        Assert.Single(await check.DirectoryUsers.ToArrayAsync());
        Assert.Single(await check.LegacyUserLinks.ToArrayAsync());
        Assert.Empty(await check.IdentityAdministrationRecords.ToArrayAsync());
    }

    [Fact]
    public async Task HistoricalProfileDoesNotMergeWithProvisioningOrBlockExplicitVerifiedOwnershipMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string alias = "source-login@example.test";
        var collection = AnalysisDashboardCollection.Create(new() { Name = "Retained historical owner" });
        await new AnalysisDashboardRepository(fixture.Host).SaveAsync(alias, collection, 0);
        var historical = (await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(historical) with
        {
            LocalDisplayName = "Unverified local name", Profile = new("Historical profile", "Old", "Name", alias)
        });
        var scimId = Guid.Parse(await fixture.CreateScimAsync());
        var snapshot = await fixture.SnapshotAsync();
        var profile = snapshot.Users.Single(user => user.IsHistorical);
        var provisioned = snapshot.Users.Single(user => user.Id == scimId);
        Assert.NotEqual(profile.Id, provisioned.Id);
        Assert.Null(provisioned.LocalDisplayName);
        Assert.Equal("Source display", provisioned.DisplayName);
        string objectAlias;
        await using (var context = fixture.Host.CreateDbContext())
        {
            Assert.Empty(await context.LegacyUserLinks.ToArrayAsync());
            Assert.Equal(alias, (await context.AnalysisDashboards.SingleAsync()).OwnerKey);
            objectAlias = (await context.EntraUserIdentities.SingleAsync(identity => identity.UserId == scimId)).ObjectId.ToString("D");
        }
        Assert.DoesNotContain(await fixture.Directory.ListAsync(fixture.Actor), account => account.Id == profile.Id);
        var migration = new IdentityMigrationService(fixture.Host, fixture.Directory);
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.LogbookEntries.Add(new() { Id = Guid.NewGuid(), User = "additional-history@example.test" });
            await context.SaveChangesAsync();
        }
        var additional = (await fixture.SnapshotAsync()).Users.Single(user => user.UserName == "additional-history@example.test");
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(profile) with { HistoricalAliasKeys = [additional.Key] });
        var blocked = await migration.PreviewAsync(fixture.Actor, scimId, [alias, objectAlias]);
        Assert.Contains("IdentityMigration.Conflicts.HistoricalGroup", blocked.Conflicts);
        Assert.Equal("IdentityMigration.Errors.Conflicts", (await Assert.ThrowsAsync<IdentityMigrationException>(() =>
            migration.CommitAsync(fixture.Actor, scimId, [alias, objectAlias], blocked.Fingerprint, "Synthetic verified proof"))).ResourceKey);
        profile = (await fixture.SnapshotAsync()).Users.Single(user => user.Key == profile.Key);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(profile) with { HistoricalAliasKeys = [] });
        profile = (await fixture.SnapshotAsync()).Users.Single(user => user.Key == profile.Key);
        var preview = await migration.PreviewAsync(fixture.Actor, scimId, [alias, objectAlias]);
        Assert.Empty(preview.Conflicts);
        await migration.CommitAsync(fixture.Actor, scimId, [alias, objectAlias], preview.Fingerprint, "Synthetic verified identity proof");
        snapshot = await fixture.SnapshotAsync();
        Assert.DoesNotContain(snapshot.Users, user => user.IsHistorical && user.UserName == alias);
        Assert.Null(snapshot.Users.Single(user => user.Id == scimId).LocalDisplayName);
        Assert.Equal("UserDirectory.Errors.HistoricalChanged", (await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveUserAsync(fixture.Actor, Edit(profile)))).ResourceKey);
        await using var check = fixture.Host.CreateDbContext();
        Assert.Equal(DirectoryIdentity.OwnerKey(scimId), (await check.AnalysisDashboards.SingleAsync()).OwnerKey);
        Assert.Equal("Unverified local name", (await check.DirectoryUsers.SingleAsync(user => user.Id == profile.Id)).LocalDisplayName);
        Assert.Equal(2, await check.LegacyUserLinks.CountAsync(link => link.Status == LegacyUserLinkStatuses.Confirmed));
    }

    [Fact]
    public async Task HistoricalProfilesCannotGrantLoginTeamsOrStableAssignmentsAndRejectInventedOrChangedSources()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string alias = "old-person@example.test";
        var caseId = Guid.NewGuid();
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.RootCauseAnalyses.Add(new() { Id = caseId, AssignedTo = alias, Revision = 1 });
            await context.SaveChangesAsync();
        }
        var original = (await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical);
        foreach (var request in new[]
        {
            Edit(original) with { SqlRole = "Admin" }, Edit(original) with { NewPassword = Password },
            Edit(original) with { ManualTeamIds = [Guid.NewGuid()] }, Edit(original) with { IsEnabled = false }
        })
            Assert.Equal("UserDirectory.Errors.SourceReadOnly",
                (await Assert.ThrowsAsync<UserAdministrationException>(() => fixture.Service.SaveUserAsync(fixture.Actor, request))).ResourceKey);
        Assert.Equal("UserDirectory.Errors.HistoricalChanged",
            (await Assert.ThrowsAsync<UserAdministrationException>(() => fixture.Service.SaveUserAsync(fixture.Actor,
                Edit(original) with { Key = "historical:invented@example.test" }))).ResourceKey);
        Assert.Equal("UserDirectory.Errors.Profile",
            (await Assert.ThrowsAsync<UserAdministrationException>(() => fixture.Service.SaveUserAsync(fixture.Actor,
                Edit(original) with { Profile = new("Valid", null, null, new string('x', 321)) }))).ResourceKey);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(original) with { Profile = new("Local", null, null, null) });
        var saved = (await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical);
        var rca = ScimRcaTests.Service(fixture.Host);
        var fresh = new RootCauseAnalysis { Id = Guid.NewGuid(), Revision = 1 };
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.RootCauseAnalyses.Add(fresh);
            await context.SaveChangesAsync();
        }
        await rca.SaveAsync(fixture.Actor, fresh.Id, fresh.Revision, "", alias, "Open", "");
        var invalid = await Assert.ThrowsAsync<RcaValidationException>(() =>
            rca.SaveAsync(fixture.Actor, caseId, 1, "", DirectoryIdentity.OwnerKey(saved.Id!.Value), "Open", ""));
        Assert.Equal(RcaValidationCode.AssigneeInvalid, invalid.Code);
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.AuthenticationUsers.Add(new() { Username = alias, Role = "Reader", PasswordHash = "synthetic" });
            await context.SaveChangesAsync();
        }
        Assert.DoesNotContain((await fixture.SnapshotAsync()).Users, user => user.IsHistorical && user.UserName == alias);
        Assert.Equal("UserDirectory.Errors.HistoricalChanged",
            (await Assert.ThrowsAsync<UserAdministrationException>(() => fixture.Service.SaveUserAsync(fixture.Actor, Edit(saved)))).ResourceKey);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task HistoricalProfileUiIsExplicitlyUnverifiedEditableAndEncodedWithoutCredentialControls(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var fixture = await Fixture.CreateAsync("open");
            await using (var context = fixture.Host.CreateDbContext())
            {
                context.RootCauseAnalyses.Add(new() { Id = Guid.NewGuid(), AssignedTo = "old@example.test", Revision = 1 });
                await context.SaveChangesAsync();
            }
            await using var ui = await UserUi.CreateAsync(fixture);
            var user = (await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical);
            await ui.Invoke("BeginEdit", user);
            Assert.Contains(ui.Text["UserDirectory.Unverified"], ui.Decoded);
            Assert.Contains(ui.Text["UserDirectory.HistoricalProfileHint"], ui.Decoded);
            Assert.DoesNotContain("maxlength=\"320\" readonly", ui.Html);
            Assert.DoesNotContain("type=\"password\"", ui.Html);
            Assert.DoesNotContain(ui.Text["UserDirectory.LocalEnabled"], ui.Decoded);
            Assert.DoesNotContain(ui.Text["UserDirectory.ManualTeams"], ui.Decoded);
            ui.Set("displayName", "Name <safe>");
            ui.Set("givenName", "First");
            ui.Set("familyName", "Last");
            ui.Set("email", "contact@example.test");
            await ui.Invoke("SaveAsync");
            Assert.Contains("Name &lt;safe&gt;", ui.Html);
            Assert.DoesNotContain("<safe>", ui.Html);
            Assert.DoesNotContain("UserDirectory.", ui.Decoded);
            var saved = (await fixture.SnapshotAsync()).Users.Single(user => user.IsHistorical);
            Assert.Equal("First", saved.GivenName);
            Assert.Equal("Last", saved.FamilyName);
            Assert.Equal("contact@example.test", saved.Email);
            ui.Set("statusFilter", "historical");
            await ui.Invoke("RefreshAsync");
            Assert.Contains("Name &lt;safe&gt;", ui.Html);
            ui.Set("statusFilter", "active");
            await ui.Invoke("RefreshAsync");
            Assert.DoesNotContain("Name &lt;safe&gt;", ui.Html);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("off")]
    [InlineData("open-off")]
    [InlineData("reader")]
    [InlineData("anonymous")]
    [InlineData("wrong-scheme")]
    [InlineData("disabled")]
    [InlineData("changed-role")]
    public async Task EveryAdministrativeSurfaceRejectsUnauthorizedCallers(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync(scenario == "changed-role" ? "sql" : scenario == "open-off" ? "open" : "entra");
        var actor = fixture.Actor;
        if (scenario is "off" or "open-off") fixture.Options.Enabled = false;
        if (scenario == "reader") actor = fixture.Entra(ActorObject, "Actor", "Reader");
        if (scenario == "anonymous") actor = new(new ClaimsIdentity());
        if (scenario == "wrong-scheme")
            actor = new(new ClaimsIdentity(actor.Claims, "untrusted"));
        if (scenario == "disabled")
        {
            var resolved = await fixture.Directory.ResolveAsync(actor);
            await using var context = fixture.Host.CreateDbContext();
            (await context.DirectoryUsers.FindAsync(DirectoryIdentity.UserId(resolved)))!.IsEnabled = false;
            await context.SaveChangesAsync();
        }
        if (scenario == "changed-role")
        {
            await using var context = fixture.Host.CreateDbContext();
            (await context.AuthenticationUsers.FindAsync("administrator"))!.Role = "Reader";
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.GetSnapshotAsync(actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveTeamAsync(actor, null, null, "Forbidden", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.CreateSqlUserAsync(actor,
            "forbidden", Password, "Reader", new("Forbidden", null, null, null), null, []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveUserAsync(actor,
            new("sql:reader", "", null, true, [])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.DeleteSqlUserAsync(actor, "sql:reader", ""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.DetachHistoricalAliasAsync(actor, "historical:unknown", ""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveGroupMappingAsync(actor, null, null, Guid.NewGuid(), Guid.NewGuid(), true));
        await using var check = fixture.Host.CreateDbContext();
        Assert.Empty(await check.IdentityAdministrationRecords.ToArrayAsync());
        Assert.Equal(3, await check.AuthenticationUsers.CountAsync());
    }

    [Fact]
    public async Task OpenAdministrationUsesExplicitAnonymousAuditWithoutLearningOrTrustingAClaimedIdentity()
    {
        await using var fixture = await Fixture.CreateAsync("open");
        fixture.Host.Options.Enabled = false;
        fixture.Host.Options.TokenHashes = [];
        var caller = fixture.Entra(ActorObject, "Unverified claimed person");
        await fixture.Service.SaveTeamAsync(caller, null, null, "Open team", true);
        var team = (await fixture.Service.GetSnapshotAsync(caller)).Teams.Single();
        await fixture.Service.CreateSqlUserAsync(caller, "open-created", Password, "Reader",
            new("Created in Open", null, null, null), null, [team.Id]);
        var snapshot = await fixture.Service.GetSnapshotAsync(caller);
        var unbound = snapshot.Users.Single(user => user.SqlUsername == "reader");
        await fixture.Service.SaveUserAsync(caller, Edit(unbound) with { IsEnabled = false, ManualTeamIds = [team.Id] });
        var created = (await fixture.Service.GetSnapshotAsync(caller)).Users.Single(user => user.SqlUsername == "open-created");
        await fixture.Service.DeleteSqlUserAsync(caller, created.Key, created.Version);
        await using var context = fixture.Host.CreateDbContext();
        Assert.All(await context.IdentityAdministrationRecords.ToArrayAsync(), audit =>
        {
            Assert.Null(audit.ActorUserId);
            Assert.True(audit.IsOpenAuthentication);
            Assert.DoesNotContain("Unverified claimed person", audit.BeforeJson + audit.AfterJson);
        });
        Assert.Empty(await context.EntraUserIdentities.ToArrayAsync());
        Assert.Empty(await context.ScimConnections.ToArrayAsync());
        Assert.Equal(2, await context.DirectoryUsers.CountAsync());
        var migration = new IdentityMigrationService(fixture.Host, fixture.Directory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => migration.CandidatesAsync(caller));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => migration.PreviewAsync(caller, created.Id!.Value, ["reader"]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            migration.CommitAsync(caller, created.Id!.Value, ["reader"], "", "Not a verified administrator"));
        Assert.Empty(await context.IdentityMigrationRecords.ToArrayAsync());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("sql")]
    [InlineData("entra")]
    public async Task TeamInventoryRolesAreManyToManyNormalizedAndRetainedWhenInventoryChanges(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode);
        await using var context = fixture.Host.CreateDbContext();
        context.ComputerInventory.AddRange(
            new ComputerInventoryEntry { SubscriptionId = "sub", Computer = "one", Role = " Database " },
            new ComputerInventoryEntry { SubscriptionId = "sub", Computer = "two", Role = "database" },
            new ComputerInventoryEntry { SubscriptionId = "sub", Computer = "three", Role = "Web" });
        await context.SaveChangesAsync();
        Assert.Equal(2, (await fixture.SnapshotAsync()).InventoryRoles.Count);
        await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "One", true,
            inventoryRoles: [" database ", "DATABASE", "Web"]);
        await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Two", true, inventoryRoles: ["Database"]);
        var teams = (await fixture.SnapshotAsync()).Teams;
        var first = teams.Single(team => team.Name == "One");
        var second = teams.Single(team => team.Name == "Two");
        Assert.Equal(["DATABASE", "WEB"], first.InventoryRoleKeys);
        Assert.Equal(["DATABASE"], second.InventoryRoleKeys);
        foreach (var entry in await context.ComputerInventory.Where(entry => entry.Computer != "three").ToArrayAsync())
            entry.Role = "Renamed";
        await context.SaveChangesAsync();
        var snapshot = await fixture.SnapshotAsync();
        Assert.False(snapshot.InventoryRoles.Single(role => role.Key == "DATABASE").IsObserved);
        Assert.True(snapshot.InventoryRoles.Single(role => role.Key == "RENAMED").IsObserved);
        await fixture.Service.SaveTeamAsync(fixture.Actor, first.Id, first.Revision, "One renamed", false,
            inventoryRoles: ["DATABASE", "WEB"]);
        var stale = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveTeamAsync(fixture.Actor, first.Id, first.Revision, "Stale", true, inventoryRoles: []));
        Assert.Equal("UserDirectory.Errors.Stale", stale.ResourceKey);
        var missing = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Invalid new assignment", true, inventoryRoles: ["DATABASE"]));
        Assert.Equal("UserDirectory.Errors.InventoryRoles", missing.ResourceKey);
        first = (await fixture.SnapshotAsync()).Teams.Single(team => team.Id == first.Id);
        await fixture.Service.SaveTeamAsync(fixture.Actor, first.Id, first.Revision, first.Name, true);
        first = (await fixture.SnapshotAsync()).Teams.Single(team => team.Id == first.Id);
        Assert.Equal(["DATABASE", "WEB"], first.InventoryRoleKeys);
        await fixture.Service.SaveTeamAsync(fixture.Actor, first.Id, first.Revision, first.Name, true, inventoryRoles: []);
        snapshot = await fixture.SnapshotAsync();
        Assert.Empty(snapshot.Teams.Single(team => team.Id == first.Id).InventoryRoleKeys);
        Assert.Equal(["DATABASE"], snapshot.Teams.Single(team => team.Id == second.Id).InventoryRoleKeys);
        var audit = (await context.IdentityAdministrationRecords.AsNoTracking().ToArrayAsync())
            .Single(record => record.TargetTeamId == first.Id && record.BeforeJson.Contains("\"Revision\":3"));
        Assert.Contains("DATABASE", audit.BeforeJson);
        Assert.Contains("\"InventoryRoleKeys\":[]", audit.AfterJson);
        Assert.Equal(3, await context.ComputerInventory.CountAsync());
        Assert.Empty(await context.UserTeamMemberships.ToArrayAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("overlong")]
    [InlineData(null)]
    public async Task InvalidInventoryRoleSelectionsDoNotCreateTeamsOrAudit(string? role)
    {
        if (role == "overlong") role = new string('r', 257);
        await using var fixture = await Fixture.CreateAsync("open");
        var error = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Invalid", true, inventoryRoles: [role!]));
        Assert.Equal("UserDirectory.Errors.InventoryRoles", error.ResourceKey);
        await using var context = fixture.Host.CreateDbContext();
        Assert.Empty(await context.UserTeams.ToArrayAsync());
        Assert.Empty(await context.UserTeamInventoryRoles.ToArrayAsync());
        Assert.Empty(await context.IdentityAdministrationRecords.ToArrayAsync());
    }

    [Fact]
    public async Task TeamRoleAndAuditChangesRollBackTogether()
    {
        await using var fixture = await Fixture.CreateAsync("open");
        await using var context = fixture.Host.CreateDbContext();
        context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "one", Role = "Database" });
        await context.SaveChangesAsync();
        await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Original", true, inventoryRoles: ["Database"]);
        var team = (await fixture.SnapshotAsync()).Teams.Single();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectTeamRoleAudit BEFORE INSERT ON IdentityAdministrationRecords
            BEGIN SELECT RAISE(ABORT, 'Synthetic role audit failure'); END;
            """);
        await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveTeamAsync(fixture.Actor, team.Id, team.Revision, "Uncommitted", false, inventoryRoles: []));
        var retained = (await fixture.SnapshotAsync()).Teams.Single();
        Assert.Equal(team, retained with { InventoryRoleKeys = team.InventoryRoleKeys });
        Assert.Equal(["DATABASE"], retained.InventoryRoleKeys);
        Assert.Single(await context.IdentityAdministrationRecords.ToArrayAsync());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task OpenUiManagesTeamRolesWithAnonymousAuditAndHidesOwnershipMigration(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var fixture = await Fixture.CreateAsync("open");
        await using var context = fixture.Host.CreateDbContext();
        context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "one", Role = "Role <safe>" });
        await context.SaveChangesAsync();
        await using var ui = await UserUi.CreateAsync(fixture);
        Assert.Contains(ui.Text["UserDirectory.OpenWarning"], ui.Decoded);
        Assert.DoesNotContain(ui.Text["IdentityMigration.Title"], ui.Decoded);
        Assert.DoesNotContain("UserDirectory.", ui.Decoded);
        await ui.Invoke("BeginTeam", (object)null!);
        ui.Set("teamName", "Open UI team");
        await ui.Invoke("SetInventoryRole", "ROLE <SAFE>", new ChangeEventArgs { Value = true });
        ui.Set("busy", true);
        await ui.Invoke("SetInventoryRole", "ROLE <SAFE>", new ChangeEventArgs { Value = false });
        await ui.Invoke("CancelTeam");
        await ui.Invoke("SaveTeamAsync");
        Assert.True(ui.Get<bool>("editingTeam"));
        ui.Set("busy", false);
        Assert.Contains("Role &lt;safe&gt;", ui.Html);
        await ui.Invoke("SaveTeamAsync");
        Assert.Equal(["ROLE <SAFE>"], (await fixture.SnapshotAsync()).Teams.Single().InventoryRoleKeys);
        var audit = await context.IdentityAdministrationRecords.SingleAsync();
        Assert.Null(audit.ActorUserId);
        Assert.True(audit.IsOpenAuthentication);
        Assert.Empty(await context.DirectoryUsers.ToArrayAsync());
    }

    [Fact]
    public async Task SqlCreationAndEditingAreAtomicAndAuditContainsImmutableBeforeStateWithoutCredentials()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Operations", true);
        var team = (await fixture.SnapshotAsync()).Teams.Single();
        await fixture.Service.CreateSqlUserAsync(fixture.Actor, " new-user ", Password, "Reader",
            new("Original display", "First", "Last", "user@example.test"), "Local", [team.Id]);
        var created = (await fixture.SnapshotAsync()).Users.Single(user => user.SqlUsername == "new-user");
        Assert.NotNull(created.Id);
        Assert.Equal("Local", created.EffectiveDisplayName);
        Assert.False(created.Unassigned);
        Assert.True(created.CanEditProfile);
        await using var context = fixture.Host.CreateDbContext();
        var oldHash = (await context.AuthenticationUsers.AsNoTracking().SingleAsync(user => user.Username == "new-user")).PasswordHash;
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(created) with
        {
            Profile = new("Changed display", "Updated", "Last", null), LocalDisplayName = "", SqlRole = "Operator"
        });
        var updated = (await fixture.SnapshotAsync()).Users.Single(user => user.Key == created.Key);
        Assert.Equal("Changed display", updated.EffectiveDisplayName);
        Assert.Null(updated.LocalDisplayName);
        Assert.Equal("Operator", updated.SqlRole);
        Assert.Equal(oldHash, (await context.AuthenticationUsers.AsNoTracking().SingleAsync(user => user.Username == "new-user")).PasswordHash);
        var audit = await context.IdentityAdministrationRecords.AsNoTracking().SingleAsync(record => record.Action == "UserUpdated");
        Assert.Equal(created.Id, audit.TargetUserId);
        Assert.Contains("Original display", audit.BeforeJson);
        Assert.DoesNotContain("Changed display", audit.BeforeJson);
        Assert.Contains("Changed display", audit.AfterJson);
        Assert.Contains("Operator", audit.AfterJson);
        var all = JsonSerializer.Serialize(await context.IdentityAdministrationRecords.AsNoTracking().ToArrayAsync());
        Assert.DoesNotContain(Password, all);
        Assert.DoesNotContain(oldHash, all);
        Assert.DoesNotContain("PasswordHash", all);
        Assert.False(updated.UsesStableIdentity);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("team")]
    public async Task AuditFailureRollsBackCredentialsProfilesMembershipsAndTeamChanges(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = (await fixture.SnapshotAsync()).Users.Single(user => user.SqlUsername == "reader");
        await using var context = fixture.Host.CreateDbContext();
        var before = (await context.AuthenticationUsers.AsNoTracking().SingleAsync(entry => entry.Username == "reader")).PasswordHash;
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectAdministrationAudit BEFORE INSERT ON IdentityAdministrationRecords
            BEGIN SELECT RAISE(ABORT, 'Synthetic audit failure'); END;
            """);
        Task Change() => operation switch
        {
            "save" => fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { SqlRole = "Operator", NewPassword = "Changed-Password-123!" }),
            "create" => fixture.Service.CreateSqlUserAsync(fixture.Actor, "new-user", Password, "Reader", new("New", null, null, null), null, []),
            "delete" => fixture.Service.DeleteSqlUserAsync(fixture.Actor, user.Key, user.Version),
            _ => fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "New team", true)
        };
        var error = await Assert.ThrowsAsync<UserAdministrationException>(Change);
        Assert.Equal("UserDirectory.Errors.Persistence", error.ResourceKey);
        Assert.Equal(3, await context.AuthenticationUsers.CountAsync());
        var unchanged = await context.AuthenticationUsers.AsNoTracking().SingleAsync(entry => entry.Username == "reader");
        Assert.Equal(before, unchanged.PasswordHash);
        Assert.Equal("Reader", unchanged.Role);
        Assert.Empty(await context.SqlUserIdentities.ToArrayAsync());
        Assert.Empty(await context.UserTeams.ToArrayAsync());
        Assert.Empty(await context.UserTeamMemberships.ToArrayAsync());
        Assert.Empty(await context.IdentityAdministrationRecords.ToArrayAsync());
    }

    [Theory]
    [InlineData("profile", true)]
    [InlineData("role", true)]
    [InlineData("password", true)]
    [InlineData("delete", true)]
    [InlineData("profile", false)]
    [InlineData("role", false)]
    [InlineData("password", false)]
    [InlineData("delete", false)]
    public async Task EntraScimProfilesAndCredentialsCannotBeChangedThroughLocalAdministration(string operation, bool scim)
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid? knownId = null;
        if (scim) await fixture.CreateScimAsync();
        else knownId = DirectoryIdentity.UserId(await fixture.Directory.ResolveAsync(fixture.Entra(Guid.NewGuid(), "Source display")));
        var user = (await fixture.SnapshotAsync()).Users.Single(user => scim ? user.IsScim : user.Id == knownId);
        Task Change() => operation == "delete"
            ? fixture.Service.DeleteSqlUserAsync(fixture.Actor, user.Key, user.Version)
            : fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with
            {
                Profile = operation == "profile" ? new("Spoofed", null, null, null) : null,
                SqlRole = operation == "role" ? "Admin" : null,
                NewPassword = operation == "password" ? Password : null
            });
        var error = await Assert.ThrowsAsync<UserAdministrationException>(Change);
        Assert.Equal("UserDirectory.Errors.SourceReadOnly", error.ResourceKey);
        await using var context = fixture.Host.CreateDbContext();
        Assert.Equal("Source display", (await context.DirectoryUsers.SingleAsync(entry => entry.Id == user.Id)).DisplayName);
        Assert.Empty(await context.IdentityAdministrationRecords.ToArrayAsync());
        Assert.Equal(3, await context.AuthenticationUsers.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalOverrideAndEnableNeverReactivateAnInactiveOrDeletedScimSource(bool deleted)
    {
        await using var fixture = await Fixture.CreateAsync();
        var resource = await fixture.CreateScimAsync(active: false);
        if (deleted) Assert.Equal(HttpStatusCode.NoContent, (await fixture.Host.Client.DeleteAsync("/scim/v2/Users/" + resource)).StatusCode);
        var user = (await fixture.SnapshotAsync()).Users.Single(user => user.IsScim);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { LocalDisplayName = "Local display", IsEnabled = true });
        var updated = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key);
        Assert.Equal("Local display", updated.EffectiveDisplayName);
        Assert.False(updated.EffectiveEnabled);
        Assert.False(updated.ScimActive);
        Assert.Equal(deleted, updated.ScimDeleted);
        Assert.NotNull(updated.LastSyncedAt);
        Assert.Equal("Source display", updated.DisplayName);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Directory.ResolveAsync(
            fixture.Entra(updated.ObjectId!.Value, "Source login")));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("password")]
    [InlineData("role")]
    [InlineData("memberships")]
    [InlineData("source")]
    public async Task StaleUserVersionCannotOverwriteAnyRelevantConcurrentChange(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (change == "source") await fixture.CreateScimAsync();
        var user = (await fixture.SnapshotAsync()).Users.Single(entry => change == "source" ? entry.IsScim : entry.SqlUsername == "reader");
        if (change == "profile")
            await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { LocalDisplayName = "Other editor" });
        else
        {
            await using var context = fixture.Host.CreateDbContext();
            if (change == "password") (await context.AuthenticationUsers.FindAsync("reader"))!.PasswordHash = "Concurrent hash";
            if (change == "role") (await context.AuthenticationUsers.FindAsync("reader"))!.Role = "Operator";
            if (change == "source")
            {
                var state = await context.ScimUserStates.SingleAsync();
                state.IsActive = false;
                state.Revision++;
            }
            if (change == "memberships")
            {
                await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user));
                user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key);
                var team = new UserTeam { Name = "Concurrent team" };
                context.UserTeams.Add(team);
                context.UserTeamMemberships.Add(new() { UserId = user.Id!.Value, TeamId = team.Id });
            }
            await context.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { LocalDisplayName = "Stale editor" }));
        Assert.Equal("UserDirectory.Errors.Stale", error.ResourceKey);
        Assert.DoesNotContain((await fixture.SnapshotAsync()).Users, entry => entry.LocalDisplayName == "Stale editor");
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("demote")]
    [InlineData("delete")]
    public async Task LastEffectiveSqlAdminIsProtectedEvenIfAnotherAdminCredentialIsLocallyDisabled(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var users = (await fixture.SnapshotAsync()).Users;
        var other = users.Single(user => user.SqlUsername == "other-admin");
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(other) with { IsEnabled = false });
        var target = (await fixture.SnapshotAsync()).Users.Single(user => user.SqlUsername == "administrator");
        Task Change() => operation == "delete"
            ? fixture.Service.DeleteSqlUserAsync(fixture.Actor, target.Key, target.Version)
            : fixture.Service.SaveUserAsync(fixture.Actor, Edit(target) with
            {
                IsEnabled = operation != "disable", SqlRole = operation == "demote" ? "Reader" : null
            });
        var error = await Assert.ThrowsAsync<UserAdministrationException>(Change);
        Assert.Equal("UserDirectory.Errors.LastEffectiveAdmin", error.ResourceKey);
        var current = (await fixture.SnapshotAsync()).Users.Single(user => user.Key == target.Key);
        Assert.True(current.EffectiveEnabled);
        Assert.Equal("Admin", current.SqlRole);
    }

    [Theory]
    [InlineData("sql")]
    [InlineData("entra")]
    public async Task AdministratorCannotLocallyDisableSelf(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode);
        var resolved = await fixture.Directory.ResolveAsync(fixture.Actor);
        var target = (await fixture.SnapshotAsync()).Users.Single(user => user.Id == DirectoryIdentity.UserId(resolved));
        var error = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveUserAsync(fixture.Actor, Edit(target) with { IsEnabled = false }));
        Assert.Equal("UserDirectory.Errors.SelfDisable", error.ResourceKey);
    }

    [Fact]
    public async Task ReservedHistoricalLoginsCannotBeRecreatedOrCountedAsUsableReplacementAdministrators()
    {
        await using var fixture = await Fixture.CreateAsync();
        var actorId = DirectoryIdentity.UserId(await fixture.Directory.ResolveAsync(fixture.Actor))!.Value;
        await using (var context = fixture.Host.CreateDbContext())
        {
            foreach (var alias in new[] { "other-admin", "reserved-login" })
                context.LegacyUserLinks.Add(new()
                {
                    Alias = alias, TargetUserId = actorId, ApprovedByUserId = actorId, ApprovedAt = DateTimeOffset.UtcNow,
                    Status = LegacyUserLinkStatuses.Confirmed, VerificationNote = "Synthetic verified history"
                });
            await context.SaveChangesAsync();
        }
        var target = (await fixture.SnapshotAsync()).Users.Single(user => user.SqlUsername == "administrator");
        var lastAdmin = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.DeleteSqlUserAsync(fixture.Actor, target.Key, target.Version));
        Assert.Equal("UserDirectory.Errors.LastEffectiveAdmin", lastAdmin.ResourceKey);
        var reserved = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.CreateSqlUserAsync(fixture.Actor, "reserved-login", Password, "Admin",
                new("New account", null, null, null), null, []));
        Assert.Equal("UserDirectory.Errors.ReservedLogin", reserved.ResourceKey);
        await using var check = fixture.Host.CreateDbContext();
        Assert.Equal(3, await check.AuthenticationUsers.CountAsync());
        Assert.Empty(await check.IdentityAdministrationRecords.ToArrayAsync());
    }

    [Fact]
    public async Task TeamsPreserveDerivedProvenanceAndDisabledAssignmentsWhileDeduplicatingEffectiveMemberships()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateScimAsync();
        var user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.IsScim);
        Guid teamId, derivedId;
        await using (var context = fixture.Host.CreateDbContext())
        {
            var team = new UserTeam { Name = "Shared team" };
            var group = new ScimGroup { ConnectionId = fixture.Host.Options.ConnectionId, ExternalId = Guid.NewGuid(), DisplayName = "Source group" };
            var mapping = new UserTeamGroupMapping { TeamId = team.Id, GroupId = group.Id };
            var derived = new UserTeamMembership { UserId = user.Id!.Value, TeamId = team.Id, GroupMappingId = mapping.Id, SourceGroupId = group.Id };
            teamId = team.Id;
            derivedId = derived.Id;
            context.UserTeams.Add(team);
            context.ScimGroups.Add(group);
            context.ScimGroupMembers.Add(new() { GroupId = group.Id, UserId = user.Id.Value, ConnectionId = group.ConnectionId });
            context.UserTeamGroupMappings.Add(mapping);
            context.UserTeamMemberships.AddRange(derived, new UserTeamMembership { UserId = user.Id.Value, TeamId = team.Id });
            await context.SaveChangesAsync();
        }
        user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key);
        Assert.Equal(2, user.TeamSources.Count);
        Assert.Single(user.TeamSources.Select(source => source.TeamId).Distinct());
        Assert.False(user.Unassigned);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { ManualTeamIds = [] });
        user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key);
        var source = Assert.Single(user.TeamSources);
        Assert.False(source.Manual);
        Assert.Equal("Source group", source.GroupName);
        Assert.False(user.Unassigned);
        await using (var context = fixture.Host.CreateDbContext())
            Assert.Equal(derivedId, (await context.UserTeamMemberships.SingleAsync()).Id);

        var teamView = (await fixture.SnapshotAsync()).Teams.Single();
        await fixture.Service.SaveTeamAsync(fixture.Actor, teamId, teamView.Revision, "Renamed team", false);
        user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key);
        Assert.True(user.Unassigned);
        Assert.Equal("Renamed team", user.TeamSources.Single().TeamName);
        var error = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { ManualTeamIds = [teamId] }));
        Assert.Equal("UserDirectory.Errors.Teams", error.ResourceKey);
        await fixture.Service.SaveTeamAsync(fixture.Actor, teamId, teamView.Revision + 1, "Renamed team", true);
        Assert.False((await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key).Unassigned);
    }

    [Fact]
    public async Task TeamNamesAndOptimisticRevisionAreValidatedAndDisabledManualMembershipsCanBeRetainedOrRemoved()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, " Operations ", true);
        var snapshot = await fixture.SnapshotAsync();
        var team = snapshot.Teams.Single();
        var user = snapshot.Users.Single(entry => entry.SqlUsername == "reader");
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { ManualTeamIds = [team.Id, team.Id] });
        await fixture.Service.SaveTeamAsync(fixture.Actor, team.Id, team.Revision, team.Name, false);
        var duplicate = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "operations", true));
        Assert.Equal("UserDirectory.Errors.DuplicateTeam", duplicate.ResourceKey);
        var stale = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            fixture.Service.SaveTeamAsync(fixture.Actor, team.Id, team.Revision, "Stale", true));
        Assert.Equal("UserDirectory.Errors.Stale", stale.ResourceKey);
        user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user));
        user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key);
        Assert.Single(user.TeamSources);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with { ManualTeamIds = [] });
        Assert.Empty((await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == user.Key).TeamSources);
    }

    [Fact]
    public async Task SqlDeletionDisablesButPreservesDirectoryTeamsAndHistoricalDashboardOwnership()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = (await fixture.SnapshotAsync()).Users.Single(entry => entry.SqlUsername == "reader");
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(target) with { LocalDisplayName = "Keep identity" });
        target = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == target.Key);
        await using (var context = fixture.Host.CreateDbContext())
        {
            context.AnalysisDashboards.Add(new() { OwnerKey = "reader", Name = "Keep", DocumentJson = "{}", Revision = 7 });
            await context.SaveChangesAsync();
        }
        await fixture.Service.DeleteSqlUserAsync(fixture.Actor, target.Key, target.Version);
        var retained = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Id == target.Id);
        Assert.Equal("Legacy", retained.Provider);
        Assert.False(retained.EffectiveEnabled);
        Assert.Null(retained.SqlUsername);
        Assert.Equal("Keep identity", retained.LocalDisplayName);
        await using var check = fixture.Host.CreateDbContext();
        Assert.Equal("reader", (await check.AnalysisDashboards.SingleAsync()).OwnerKey);
        Assert.Equal(7, (await check.AnalysisDashboards.SingleAsync()).Revision);
        Assert.Contains(await check.IdentityAdministrationRecords.ToArrayAsync(), record => record.Action == "SqlCredentialDeleted");
    }

    [Fact]
    public async Task SubsequentScimUpdatesPreserveLocalOverrideLocalDisableAndManualTeams()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resource = await fixture.CreateScimAsync();
        await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Manual team", true);
        var snapshot = await fixture.SnapshotAsync();
        var team = snapshot.Teams.Single();
        var user = snapshot.Users.Single(entry => entry.IsScim);
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(user) with
        {
            LocalDisplayName = "Local override", IsEnabled = false, ManualTeamIds = [team.Id]
        });
        var response = await fixture.Host.Client.PutAsJsonAsync("/scim/v2/Users/" + resource, new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" }, externalId = user.ObjectId!.Value.ToString(),
            userName = "changed-source@example.test", displayName = "Changed source", active = true,
            name = new { givenName = "Changed first", familyName = "Changed last" },
            emails = new[] { new { value = "changed@example.test", type = "work", primary = true } }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Id == user.Id);
        Assert.Equal("Local override", updated.EffectiveDisplayName);
        Assert.Equal("Changed source", updated.DisplayName);
        Assert.Equal("Changed first", updated.GivenName);
        Assert.Equal("Changed last", updated.FamilyName);
        Assert.Equal("changed@example.test", updated.Email);
        Assert.Equal("changed-source@example.test", updated.UserName);
        Assert.False(updated.IsEnabled);
        Assert.False(updated.EffectiveEnabled);
        Assert.True(updated.ScimActive);
        Assert.Equal(team.Id, Assert.Single(updated.TeamSources).TeamId);
    }

    [Fact]
    public async Task SqlAndTeamAdministrationRemainAvailableWithoutEnabledScimOrBearerConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Host.Options.Enabled = false;
        fixture.Host.Options.TokenHashes = [];
        await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Without provisioning", true);
        var team = (await fixture.SnapshotAsync()).Teams.Single();
        await fixture.Service.CreateSqlUserAsync(fixture.Actor, "local-only", Password, "Reader",
            new("Local only", null, null, null), null, [team.Id]);
        var snapshot = await fixture.SnapshotAsync();
        var user = snapshot.Users.Single(entry => entry.SqlUsername == "local-only");
        Assert.False(user.Unassigned);
        Assert.Equal("Sql", user.Provider);
        await using var context = fixture.Host.CreateDbContext();
        Assert.Empty(await context.ScimConnections.ToArrayAsync());
        Assert.Empty(await context.ScimUserStates.ToArrayAsync());
    }

    [Fact]
    public async Task PasswordResetUsesExistingHashingWithoutChangingRoleAndLeavesNoCredentialMaterialInAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = (await fixture.SnapshotAsync()).Users.Single(entry => entry.SqlUsername == "reader");
        const string replacement = "Synthetic-Replacement-Password-123!";
        await fixture.Service.SaveUserAsync(fixture.Actor, Edit(target) with { NewPassword = replacement });
        Assert.NotNull(await fixture.Sql.ValidateCredentialsAsync("reader", replacement));
        Assert.Null(await fixture.Sql.ValidateCredentialsAsync("reader", Password));
        var updated = (await fixture.SnapshotAsync()).Users.Single(entry => entry.Key == target.Key);
        Assert.Equal("Reader", updated.SqlRole);
        Assert.NotEqual(target.Version, updated.Version);
        await using var context = fixture.Host.CreateDbContext();
        var record = await context.IdentityAdministrationRecords.SingleAsync();
        Assert.Equal("UserUpdatedWithPasswordReset", record.Action);
        var hash = (await context.AuthenticationUsers.SingleAsync(entry => entry.Username == "reader")).PasswordHash;
        Assert.DoesNotContain(hash, record.BeforeJson + record.AfterJson);
        Assert.DoesNotContain(replacement, record.BeforeJson + record.AfterJson);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task UiReportsCommittedSelfDemotionWithoutBlindRetryAfterAccessIsLost(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var fixture = await Fixture.CreateAsync("sql");
            await using var ui = await UserUi.CreateAsync(fixture);
            var user = (await fixture.SnapshotAsync()).Users.Single(entry => entry.SqlUsername == "administrator");
            await ui.Invoke("BeginEdit", user);
            ui.Set("sqlRole", "Reader");
            await ui.Invoke("SaveAsync");
            Assert.True(ui.Get<bool>("reloadRequired"));
            Assert.Contains(ui.Text["UserDirectory.SavedRefreshFailed"], ui.Decoded);
            await ui.Invoke("SaveAsync");
            await using var context = fixture.Host.CreateDbContext();
            Assert.Equal("Reader", (await context.AuthenticationUsers.SingleAsync(entry => entry.Username == "administrator")).Role);
            Assert.Single(await context.IdentityAdministrationRecords.ToArrayAsync());
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task LocalizedUiPreservesReadonlySourcesFiltersPasswordGuardsAndDrafts(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            await fixture.CreateScimAsync();
            await using var ui = await UserUi.CreateAsync(fixture);
            Assert.Contains(ui.Text["UserDirectory.Title"], ui.Decoded);
            Assert.DoesNotContain("UserDirectory.", ui.Decoded);
            var scim = (await fixture.SnapshotAsync()).Users.Single(user => user.IsScim);
            await ui.Invoke("BeginEdit", scim);
            Assert.Contains("readonly", ui.Html);
            Assert.DoesNotContain("type=\"password\"", ui.Html);
            Assert.Contains(ui.Text["UserDirectory.Sync.Succeeded"], ui.Decoded);
            Assert.Contains(ui.Text["UserDirectory.SourceStatusHint"], ui.Decoded);
            ui.Set("localDisplayName", "Unsaved <draft>");
            ui.Set("busy", true);
            await ui.Invoke("CancelEdit");
            await ui.Invoke("BeginCreate");
            await ui.Invoke("RefreshAsync");
            await ui.Invoke("SaveAsync");
            Assert.Equal(scim.Key, ui.Get<UserAdministrationUser>("editing").Key);
            Assert.Equal("Unsaved <draft>", ui.Get<string>("localDisplayName"));
            ui.Set("busy", false);
            await ui.Invoke("BeginTeam", (object)null!);
            ui.Set("teamName", "UI team <safe>");
            await ui.Invoke("SaveTeamAsync");
            Assert.Equal("Unsaved <draft>", ui.Get<string>("localDisplayName"));
            Assert.Contains("UI team &lt;safe&gt;", ui.Html);
            await ui.Invoke("RefreshAsync");
            Assert.Equal("Unsaved <draft>", ui.Get<string>("localDisplayName"));
            await ui.Invoke("SaveAsync");
            Assert.Contains("Unsaved &lt;draft&gt;", ui.Html);
            Assert.DoesNotContain("<draft>", ui.Html);
            var sql = (await fixture.SnapshotAsync()).Users.Single(user => user.SqlUsername == "reader");
            await ui.Invoke("BeginEdit", sql);
            ui.Set("sqlRole", "Operator");
            foreach (var (password, confirmation) in new[] { ("one-sided", ""), ("", "one-sided"), ("first", "different") })
            {
                ui.Set("password", password);
                ui.Set("passwordConfirmation", confirmation);
                await ui.Invoke("SaveAsync");
                Assert.Contains(ui.Text["AdminUsers.PasswordMismatch"], ui.Decoded);
                Assert.Equal("Reader", (await fixture.SnapshotAsync()).Users.Single(user => user.Key == sql.Key).SqlRole);
            }
            ui.Set("password", "");
            ui.Set("passwordConfirmation", "");
            await ui.Invoke("SaveAsync");
            Assert.Equal("Operator", (await fixture.SnapshotAsync()).Users.Single(user => user.Key == sql.Key).SqlRole);
            ui.Set("providerFilter", "Scim");
            await ui.Invoke("RefreshAsync");
            Assert.Contains("source-login@example.test", ui.Decoded);
            Assert.DoesNotContain("<h3>reader</h3>", ui.Html);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MappingUiIsLocalizedAuditedAndPreservesOpenUserDrafts(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            var userId = await fixture.CreateScimAsync();
            var group = await ScimGroupTests.GroupAsync(fixture.Host, "Source <group>", userId);
            await fixture.Service.SaveTeamAsync(fixture.Actor, null, null, "Team <safe>", true);
            var snapshot = await fixture.SnapshotAsync();
            var team = snapshot.Teams.Single();
            await using var ui = await UserUi.CreateAsync(fixture);
            Assert.Contains(ui.Text["UserDirectory.Mappings"], ui.Decoded);
            await ui.Invoke("BeginMapping", (object)null!);
            ui.Set("mappingGroupId", Guid.Parse(group.Id));
            ui.Set("mappingTeamId", team.Id);
            await ui.Invoke("SaveMappingAsync");
            snapshot = await fixture.SnapshotAsync();
            var mapping = Assert.Single(snapshot.Mappings);
            var user = snapshot.Users.Single(entry => entry.Id == Guid.Parse(userId));
            Assert.False(user.Unassigned);
            await ui.Invoke("BeginEdit", user);
            ui.Set("localDisplayName", "Unsaved user draft");
            await ui.Invoke("BeginMapping", mapping);
            ui.Set("mappingEnabled", false);
            ui.Set("busy", true);
            await ui.Invoke("CancelMapping");
            await ui.Invoke("SaveMappingAsync");
            Assert.True(ui.Get<bool>("editingMapping"));
            ui.Set("busy", false);
            await ui.Invoke("SaveMappingAsync");
            Assert.Equal("Unsaved user draft", ui.Get<string>("localDisplayName"));
            Assert.Contains("Source &lt;group&gt;", ui.Html);
            Assert.DoesNotContain("<group>", ui.Html);
            Assert.DoesNotContain("UserDirectory.", ui.Decoded);
            await ui.Invoke("SaveAsync");
            Assert.Contains(ui.Text["UserDirectory.Errors.Stale"], ui.Decoded);
            await using var context = fixture.Host.CreateDbContext();
            Assert.Equal(2, await context.IdentityAdministrationRecords.CountAsync(record => record.Action.StartsWith("GroupMapping")));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static UserAdministrationEdit Edit(UserAdministrationUser user) =>
        new(user.Key, user.Version, user.LocalDisplayName, user.IsEnabled,
            user.TeamSources.Where(source => source.Manual).Select(source => source.TeamId).ToArray());

    private sealed class Fixture(ScimTestHost host, string mode)
        : IAsyncDisposable
    {
        public ScimTestHost Host => host;
        public IdentityDirectoryOptions Options { get; } = new() { Enabled = true };
        public ApplicationAuthenticationOptions Authentication { get; } = new() { Type = mode };
        public IdentityDirectoryService Directory => new(host, Options, Authentication);
        public SqlAuthenticationService Sql => new(host, new SqlPasswordHasher(), Options);
        public UserAdministrationService Service => new(host, Directory, Authentication, Sql,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UserAdministrationService>.Instance);
        public ClaimsPrincipal Actor => mode == "open" ? new(new ClaimsIdentity()) : mode == "sql"
            ? new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "administrator"), new(ClaimTypes.Name, "administrator"),
                new(ClaimTypes.Role, "Admin")], ApplicationAuthorization.SqlCookieScheme))
            : Entra(ActorObject, "Actor", "Admin");
        public ClaimsPrincipal Entra(Guid objectId, string name, string role = "Operator") =>
            new(new ClaimsIdentity([new("tid", host.Options.TenantId.ToString()), new("oid", objectId.ToString()),
                new("preferred_username", name), new("name", name), new(ClaimTypes.Name, name), new(ClaimTypes.Role, role)],
                AppServiceAuthenticationHandler.SchemeName));
        public Task<UserAdministrationSnapshot> SnapshotAsync() => Service.GetSnapshotAsync(Actor);
        public async Task<string> CreateScimAsync(bool active = true)
        {
            var response = await host.Client.PostAsJsonAsync("/scim/v2/Users", new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" }, externalId = Guid.NewGuid().ToString(),
                userName = "source-login@example.test", displayName = "Source display", active,
                name = new { givenName = "Source first", familyName = "Source last" },
                emails = new[] { new { value = "source@example.test", type = "work", primary = true } }
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        public static async Task<Fixture> CreateAsync(string mode = "entra", bool orderedAuditTimestamps = false)
        {
            var host = await ScimTestHost.CreateAsync(authentication: mode, orderedAuditTimestamps: orderedAuditTimestamps);
            var fixture = new Fixture(host, mode);
            await using var context = host.CreateDbContext();
            var hash = new SqlPasswordHasher().Hash(Password);
            context.AuthenticationUsers.AddRange(
                new SqlAuthenticationUser { Username = "administrator", Role = "Admin", PasswordHash = hash },
                new SqlAuthenticationUser { Username = "other-admin", Role = "Admin", PasswordHash = hash },
                new SqlAuthenticationUser { Username = "reader", Role = "Reader", PasswordHash = hash });
            await context.SaveChangesAsync();
            return fixture;
        }
        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    private sealed class State(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
    private sealed class Capture(IServiceProvider services) : IComponentActivator
    {
        public UserDirectoryPanel Panel { get; private set; } = null!;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is UserDirectoryPanel panel) Panel = panel;
            return component;
        }
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/admin/users");
    }
    private sealed class UserUi(WebApplication app, AsyncServiceScope scope, HtmlRenderer renderer,
        Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root) : IAsyncDisposable
    {
        private UserDirectoryPanel Panel => scope.ServiceProvider.GetRequiredService<Capture>().Panel;
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public void Set(string name, object value) => typeof(UserDirectoryPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Panel, value);
        public T Get<T>(string name) => (T)typeof(UserDirectoryPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Panel)!;
        public Task Invoke(string name, params object[] args) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(UserDirectoryPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Panel, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Panel, null);
        });
        public static async Task<UserUi> CreateAsync(Fixture fixture)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents();
            builder.Services.AddUiLocalization();
            builder.Services.AddSingleton(fixture.Service);
            builder.Services.AddSingleton(fixture.Directory);
            builder.Services.AddSingleton(fixture.Authentication);
            builder.Services.AddSingleton<IDbContextFactory<AlertDbContext>>(fixture.Host);
            builder.Services.AddSingleton<IdentityMigrationService>();
            builder.Services.AddSingleton<AuthenticationStateProvider>(new State(fixture.Actor));
            builder.Services.AddScoped<NavigationManager, Navigation>();
            builder.Services.AddScoped<Capture>();
            builder.Services.AddScoped<IComponentActivator>(services => services.GetRequiredService<Capture>());
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(new AuthenticationState(fixture.Actor)),
                    ["ChildContent"] = (RenderFragment)(builder => { builder.OpenComponent<UserDirectoryPanel>(0); builder.CloseComponent(); })
                })));
            return new(app, scope, renderer, root);
        }
        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
        }
    }
}
