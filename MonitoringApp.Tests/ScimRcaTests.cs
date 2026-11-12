using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class ScimRcaTests
{
    internal static RcaService Service(ScimTestHost host, bool enabled = true, string mode = "entra")
    {
        var authentication = new ApplicationAuthenticationOptions { Type = mode };
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        return new(host, host.App.Services.GetRequiredService<IAuthorizationService>(),
            new(new HttpContextAccessor(), new(host, new() { Enabled = enabled }, authentication)),
            authentication, TimeProvider.System, new(presenter), presenter);
    }

    private static async Task<RootCauseAnalysis> CaseAsync(ScimTestHost host, string assignee = "")
    {
        var item = new RootCauseAnalysis
        {
            Id = Guid.NewGuid(), CategoryName = "Directory case", AssignedTo = assignee,
            CreatedAt = DateTimeOffset.UtcNow, Revision = 1
        };
        await using var context = host.CreateDbContext();
        context.RootCauseAnalyses.Add(item);
        await context.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task ProvisionedUsersAreAssignableBeforeLoginAndProfileChangesNeverChangeTheAssignment()
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: "entra");
        await AssertStableAssignmentAsync(host);
    }

    internal static async Task AssertStableAssignmentAsync(ScimTestHost host)
    {
        var provisioned = await ScimGroupTests.UserAsync(host);
        var id = Guid.Parse(provisioned.Id);
        var key = DirectoryIdentity.OwnerKey(id);
        var actor = ScimGroupTests.Admin(host);
        var service = Service(host);
        var option = Assert.Single(await service.GetAssigneeOptionsAsync(actor), option => option.Key == key);
        Assert.Contains("Source display", option.Label);
        var item = await CaseAsync(host);
        await service.SaveAsync(actor, item.Id, item.Revision, "", key, "Open", "");
        var saved = (await service.ListAsync()).Single(savedCase => savedCase.Id == item.Id);
        Assert.Equal(key, saved.AssignedTo);
        using var updated = await host.Client.PutAsJsonAsync("/scim/v2/Users/" + id, new
        {
            schemas = new[] { ScimSchemas.User }, externalId = provisioned.ExternalId,
            userName = "renamed-" + id + "@example.test", displayName = "Renamed person",
            name = new { givenName = "New", familyName = "Name" },
            emails = new[] { new { value = "renamed-" + id + "@example.test", primary = true } }, active = true
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        host.Options.Enabled = false;
        saved = (await service.ListAsync()).Single(item => item.AssignedTo == key);
        Assert.Equal(key, saved.AssignedTo);
        Assert.Contains("Renamed person", saved.AssigneeDisplayName);
        Assert.Contains(await service.GetAssigneeOptionsAsync(actor), option => option.Key == key && option.Label.Contains("Renamed person"));
        Assert.Contains(await service.GetOpenNoticesAsync(), notice => notice.Id == saved.Id && notice.AssigneeLabel.Contains("Renamed person"));
        var change = Assert.Single(await service.GetChangesAsync(saved.Id), change => change.Field == "AssignedTo");
        Assert.Equal(key, change.After);
        Assert.Contains("Renamed person", change.AfterDisplayName);
        Assert.DoesNotContain("AssigneeDisplayName", JsonSerializer.Serialize(saved));
        Assert.DoesNotContain("AfterDisplayName", JsonSerializer.Serialize(change));
        await using var check = host.CreateDbContext();
        Assert.False((await check.DirectoryUsers.SingleAsync(user => user.Id == id)).UsesStableIdentity);
        Assert.False(await check.SqlUserIdentities.AnyAsync(binding => binding.UserId == id));
        Assert.Equal(key, (await check.RootCauseAnalyses.SingleAsync(row => row.Id == saved.Id)).AssignedTo);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("source")]
    [InlineData("deleted")]
    [InlineData("orphan")]
    [InlineData("sql-missing")]
    [InlineData("sql-invalid-role")]
    public async Task UnavailableUsersCannotBeNewlyAssignedThroughAnIdNameOrEmailButExistingAssignmentsSurvive(string kind)
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: "entra");
        var source = await ScimGroupTests.UserAsync(host);
        var id = Guid.Parse(source.Id);
        var key = DirectoryIdentity.OwnerKey(id);
        var service = Service(host);
        var actor = ScimGroupTests.Admin(host);
        var assigned = await CaseAsync(host, key);
        var fresh = await CaseAsync(host);
        string userName, email;
        await using (var context = host.CreateDbContext())
        {
            var user = await context.DirectoryUsers.SingleAsync(user => user.Id == id);
            userName = user.UserName;
            email = user.Email!;
            var state = await context.ScimUserStates.SingleAsync(state => state.UserId == id);
            if (kind == "local") user.IsEnabled = false;
            if (kind == "source") state.IsActive = false;
            if (kind == "deleted") state.IsDeleted = true;
            if (kind == "orphan")
            {
                context.ScimUserStates.Remove(state);
                context.EntraUserIdentities.Remove(await context.EntraUserIdentities.SingleAsync(binding => binding.UserId == id));
            }
            if (kind.StartsWith("sql", StringComparison.Ordinal))
            {
                user.Origin = DirectoryUserOrigins.Sql;
                if (kind == "sql-invalid-role")
                {
                    context.SqlUserIdentities.Add(new() { UserId = id, Username = userName });
                    context.AuthenticationUsers.Add(new() { Username = userName, Role = "invalid", PasswordHash = "synthetic" });
                }
            }
            await context.SaveChangesAsync();
        }
        var options = await service.GetAssigneeOptionsAsync(actor);
        Assert.DoesNotContain(options, option => option.Key == key || option.Key.Equals(userName, StringComparison.OrdinalIgnoreCase) || option.Key.Equals(email, StringComparison.OrdinalIgnoreCase));
        foreach (var value in new[] { key, userName, email })
            Assert.Equal(RcaValidationCode.AssigneeInvalid, (await Assert.ThrowsAsync<RcaValidationException>(() =>
                service.SaveAsync(actor, fresh.Id, fresh.Revision, "", value, "Open", ""))).Code);
        await service.SaveAsync(actor, assigned.Id, assigned.Revision, "Retained", key.ToUpperInvariant(), "Open", "Existing case");
        var retained = (await service.ListAsync()).Single(item => item.Id == assigned.Id);
        Assert.Equal(key, retained.AssignedTo);
        Assert.NotEmpty(retained.AssigneeDisplayName!);
        Assert.Equal("", (await service.ListAsync()).Single(item => item.Id == fresh.Id).AssignedTo);
        await service.SaveAsync(actor, retained.Id, retained.Revision, retained.Analysis, "", "Open", "");
        Assert.Empty((await service.ListAsync()).Single(item => item.Id == retained.Id).AssignedTo);
    }

    [Fact]
    public async Task UnknownIdsAreRejectedAndIdenticalEmailsDoNotMergeUsersOrRewriteLegacyAssignments()
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: "entra");
        var first = await ScimGroupTests.UserAsync(host);
        var second = await ScimGroupTests.UserAsync(host);
        var service = Service(host);
        var actor = ScimGroupTests.Admin(host);
        var item = await CaseAsync(host, "legacy-operator");
        var options = await service.GetAssigneeOptionsAsync(actor);
        Assert.Contains(options, option => option.Key == DirectoryIdentity.OwnerKey(Guid.Parse(first.Id)));
        Assert.Contains(options, option => option.Key == DirectoryIdentity.OwnerKey(Guid.Parse(second.Id)));
        await using (var context = host.CreateDbContext())
        {
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var known = new DirectoryUser { Origin = DirectoryUserOrigins.EntraLogin, DisplayName = "Ambiguous user" };
                context.DirectoryUsers.Add(known);
                context.EntraUserIdentities.Add(new() { UserId = known.Id, TenantId = host.Options.TenantId, ObjectId = Guid.NewGuid() });
            }
            await context.SaveChangesAsync();
        }
        var ambiguous = (await service.GetAssigneeOptionsAsync(actor)).Where(option => option.Label.StartsWith("Ambiguous user [", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, ambiguous.Length);
        Assert.Equal(2, ambiguous.Select(option => option.Label).Distinct().Count());
        await service.SaveAsync(actor, item.Id, item.Revision, "Preserved legacy", item.AssignedTo, "Open", "");
        var saved = Assert.Single(await service.ListAsync());
        Assert.Equal("legacy-operator", saved.AssignedTo);
        Assert.Null(saved.AssigneeDisplayName);
        await Assert.ThrowsAsync<RcaValidationException>(() => service.SaveAsync(actor, saved.Id, saved.Revision, "",
            DirectoryIdentity.OwnerKey(Guid.NewGuid()), "Open", ""));
        await service.SaveAsync(actor, saved.Id, saved.Revision, saved.Analysis, "external@example.test", "Open", "");
        Assert.Equal("external@example.test", Assert.Single(await service.ListAsync()).AssignedTo);
    }

    [Fact]
    public async Task ConfirmedAliasesOfDisabledUsersAreNotAnEmailBackdoorAndReaderCannotListOptions()
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: "entra");
        var source = await ScimGroupTests.UserAsync(host);
        await using (var context = host.CreateDbContext())
        {
            (await context.DirectoryUsers.SingleAsync()).IsEnabled = false;
            context.LegacyUserLinks.Add(new()
            {
                Alias = "old-alias@example.test", TargetUserId = Guid.Parse(source.Id),
                Status = LegacyUserLinkStatuses.Confirmed, VerificationNote = "Synthetic verified alias",
                ApprovedByUserId = Guid.Parse(source.Id), ApprovedAt = DateTimeOffset.UtcNow
            });
            context.LogbookEntries.Add(new() { Id = Guid.NewGuid(), User = "old-alias@example.test" });
            await context.SaveChangesAsync();
        }
        var service = Service(host);
        var reader = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role, "Reader")], AppServiceAuthenticationHandler.SchemeName));
        var before = host.ContextsCreated;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAssigneeOptionsAsync(reader));
        Assert.Equal(before, host.ContextsCreated);
        Assert.DoesNotContain(await service.GetAssigneesAsync(ScimGroupTests.Admin(host)), value => value == "old-alias@example.test");
        var item = await CaseAsync(host);
        await Assert.ThrowsAsync<RcaValidationException>(() => service.SaveAsync(ScimGroupTests.Admin(host),
            item.Id, item.Revision, "", "old-alias@example.test", "Open", ""));
    }

    [Fact]
    public async Task SharedReadLabelsPreserveRawAuditValuesSystemActorsAndPublicationOwnership()
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: "entra", orderedAuditTimestamps: true);
        await AssertReadLabelsAsync(host);
    }

    internal static async Task AssertReadLabelsAsync(ScimTestHost host)
    {
        var source = await ScimGroupTests.UserAsync(host);
        var key = DirectoryIdentity.OwnerKey(Guid.Parse(source.Id));
        Guid jobId, publicationId;
        await using (var context = host.CreateDbContext())
        {
            var user = await context.DirectoryUsers.SingleAsync(user => user.Id == Guid.Parse(source.Id));
            user.LocalDisplayName = "<Local & name>";
            user.IsEnabled = false;
            var job = new AgentAnalysisJob { Id = Guid.NewGuid(), CreatedBy = key, IdempotencyKey = Guid.NewGuid().ToString() };
            jobId = job.Id;
            publicationId = Guid.NewGuid();
            context.AgentAnalysisJobs.Add(job);
            context.AgentAnalysisReviewEvents.Add(new() { Id = Guid.NewGuid(), AgentAnalysisJobId = job.Id, Actor = key });
            context.LogbookEntries.AddRange(new LogbookEntry { Id = Guid.NewGuid(), User = key },
                new LogbookEntry { Id = Guid.NewGuid(), User = "System" });
            context.AnalysisDashboardTemplates.Add(new()
            {
                Id = publicationId, UpdatedBy = key, Published = true, DocumentJson = """{"name":"Published"}"""
            });
            await context.SaveChangesAsync();
        }
        var authentication = new ApplicationAuthenticationOptions { Type = "entra" };
        var directory = new IdentityDirectoryOptions { Enabled = true };
        var logbook = new LogbookStore(host, directory, authentication);
        var entries = await logbook.GetAllAsync();
        Assert.Contains("<Local & name>", entries.Single(entry => entry.User == key).UserDisplayName);
        Assert.Null(entries.Single(entry => entry.User == "System").UserDisplayName);
        Assert.All(await new LogbookStore(host).GetAllAsync(), entry => Assert.Null(entry.UserDisplayName));
        var review = Assert.Single(await new AgentAnalysisReviewStore(host, TimeProvider.System, directory, authentication).ListAsync(jobId));
        Assert.Equal(key, review.Actor);
        Assert.Contains("<Local & name>", review.ActorDisplayName);
        var publishing = new AnalysisDashboardPublishing(host, host.App.Services.GetRequiredService<IAuthorizationService>(),
            new(new HttpContextAccessor()), authentication, directory);
        var publication = (await publishing.LoadAsync(publicationId, ScimGroupTests.Admin(host))).Publication;
        Assert.Equal(key, publication.Owner);
        Assert.Contains("<Local & name>", publication.OwnerDisplayName);
        Assert.Equal(key, Assert.Single(await publishing.ListAsync(ScimGroupTests.Admin(host))).Owner);
        Assert.DoesNotContain("DisplayName", JsonSerializer.Serialize(review));
        Assert.DoesNotContain("DisplayName", JsonSerializer.Serialize(publication));
        await using var check = host.CreateDbContext();
        Assert.Equal(key, (await check.AgentAnalysisReviewEvents.SingleAsync()).Actor);
        Assert.Equal(key, (await check.AnalysisDashboardTemplates.SingleAsync(item => item.Id == publicationId)).UpdatedBy);
    }

    [Fact]
    public async Task FailedAssignmentAuditRollsBackCaseRevisionAndAssignee()
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: "entra");
        var source = await ScimGroupTests.UserAsync(host);
        var item = await CaseAsync(host);
        await using var context = host.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectAssignmentAudit BEFORE INSERT ON RcaChanges
            BEGIN SELECT RAISE(ABORT, 'Synthetic audit failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Service(host).SaveAsync(ScimGroupTests.Admin(host), item.Id,
            item.Revision, "Must roll back", DirectoryIdentity.OwnerKey(Guid.Parse(source.Id)), "Open", ""));
        var unchanged = await context.RootCauseAnalyses.AsNoTracking().SingleAsync();
        Assert.Equal(item.Revision, unchanged.Revision);
        Assert.Empty(unchanged.AssignedTo);
        Assert.Empty(unchanged.Analysis);
        Assert.Empty(await context.RcaChanges.ToArrayAsync());
    }

    [Fact]
    public async Task StableAuditActorsDoNotRewriteLegacyHistoryAndOffModeNeedsNoDirectoryForNewAssignments()
    {
        await using var host = await ScimTestHost.CreateAsync(authentication: "entra");
        var actor = ScimGroupTests.Admin(host);
        var directory = new IdentityDirectoryService(host, new() { Enabled = true }, new() { Type = "entra" });
        var actorId = DirectoryIdentity.UserId(await directory.ResolveAsync(actor))!.Value;
        var key = DirectoryIdentity.OwnerKey(actorId);
        var item = await CaseAsync(host, "legacy@example.test");
        await using (var context = host.CreateDbContext())
        {
            (await context.DirectoryUsers.SingleAsync()).UsesStableIdentity = true;
            context.RcaChanges.Add(new()
            {
                Id = Guid.NewGuid(), RcaId = item.Id, Actor = "Historical name", Field = "Comment", After = "Legacy comment"
            });
            context.AuthenticationUsers.Add(new() { Username = "unbound-local", PasswordHash = "synthetic", Role = "Reader" });
            await context.SaveChangesAsync();
        }
        var service = Service(host);
        await service.SaveAsync(actor, item.Id, item.Revision, key, key, "Open", "New comment");
        var changes = await service.GetChangesAsync(item.Id);
        Assert.Contains(changes, change => change.Actor == "Historical name" && change.ActorDisplayName is null && change.After == "Legacy comment");
        Assert.All(changes.Where(change => change.Actor == key), change => Assert.NotNull(change.ActorDisplayName));
        Assert.Null(Assert.Single(changes, change => change.Field == "Analysis").AfterDisplayName);
        Assert.Contains(await service.GetAssigneesAsync(actor), value => value == "unbound-local");
        var off = Service(host, enabled: false);
        Assert.DoesNotContain(await off.GetAssigneesAsync(actor), value => value == key);
        var saved = Assert.Single(await off.ListAsync());
        Assert.Null(saved.AssigneeDisplayName);
        await off.SaveAsync(actor, saved.Id, saved.Revision, saved.Analysis, "unbound-local", "Open", "");
        Assert.Equal("unbound-local", Assert.Single(await off.ListAsync()).AssignedTo);
        Assert.False(Service(host, mode: "open").DirectoryAssigneesEnabled);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task LocalizedRcaPageUsesStableOptionValuesAndEncodesNamesWithoutExposingIdsAsLabels(string culture, bool reader)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using var host = await ScimTestHost.CreateAsync(authentication: "entra");
            var source = await ScimGroupTests.UserAsync(host);
            var key = DirectoryIdentity.OwnerKey(Guid.Parse(source.Id));
            await using (var context = host.CreateDbContext())
            {
                (await context.DirectoryUsers.SingleAsync()).LocalDisplayName = "<Selected & user>";
                await context.SaveChangesAsync();
            }
            var item = await CaseAsync(host, key);
            var capture = new Capture();
            var services = new ServiceCollection().AddLogging();
            services.AddUiLocalization();
            services.AddCascadingAuthenticationState();
            services.AddSingleton(Service(host));
            services.AddSingleton(host.App.Services.GetRequiredService<IAuthorizationService>());
            services.AddSingleton(new RcaDisplayOptions());
            services.AddSingleton<IComponentActivator>(capture);
            var principal = reader ? new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role, "Reader")],
                AppServiceAuthenticationHandler.SchemeName)) : ScimGroupTests.Admin(host);
            services.AddSingleton<AuthenticationStateProvider>(new State(principal));
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new PageRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(SelectedPage), ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SelectedPage.SelectedCaseId)] = item.Id
                }));
                await root.QuiescenceTask;
                var html = WebUtility.HtmlDecode(root.ToHtmlString());
                Assert.Contains("value=\"" + key + "\"", html);
                Assert.Contains("<Selected & user>", html);
                Assert.DoesNotContain("<Selected & user>", root.ToHtmlString());
                Assert.Contains(culture == "de-DE" ? "vor ihrer ersten Anmeldung" : "before their first login", html);
                Assert.DoesNotContain("Rca.DirectoryAssigneeHelp", html);
                if (reader)
                {
                    Assert.DoesNotContain("no longer available for new assignments", html);
                    Assert.DoesNotContain("nicht mehr für neue Zuweisungen", html);
                    return;
                }
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var page = capture.Page!;
                typeof(Rca).GetField("busy", flags)!.SetValue(page, true);
                await (Task)typeof(Rca).GetMethod("SaveAsync", flags)!.Invoke(page, null)!;
                Assert.Empty(await Service(host).GetChangesAsync(item.Id));
                typeof(Rca).GetField("busy", flags)!.SetValue(page, false);
                typeof(Rca).GetProperty("DirectoryAssignee", flags)!.SetValue(page, "");
                Assert.Empty((string)typeof(Rca).GetField("assignedTo", flags)!.GetValue(page)!);
                await (Task)typeof(Rca).GetMethod("SaveAsync", flags)!.Invoke(page, null)!;
                Assert.Empty((await Service(host).ListAsync()).Single(row => row.Id == item.Id).AssignedTo);
                typeof(Rca).GetProperty("DirectoryAssignee", flags)!.SetValue(page, key);
                await (Task)typeof(Rca).GetMethod("SaveAsync", flags)!.Invoke(page, null)!;
                Assert.Equal(key, (await Service(host).ListAsync()).Single(row => row.Id == item.Id).AssignedTo);
            });
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private sealed class State(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class Capture : IComponentActivator
    {
        public Rca? Page { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            if (componentType == typeof(SelectedPage)) return Page = new SelectedPage();
            return (IComponent)(Activator.CreateInstance(componentType) ?? throw new InvalidOperationException("Component activation failed."));
        }
    }

    private sealed class SelectedPage : Rca
    {
        [Parameter] public Guid SelectedCaseId { get; set; }
        protected override Task OnParametersSetAsync()
        {
            CaseId = SelectedCaseId;
            return base.OnParametersSetAsync();
        }
    }

    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type type, int? parentId, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type) :
                throw new NotSupportedException($"Unsupported test render mode {mode.GetType().Name}.");
    }
}
