using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class RcaTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IngestionPersistsInitialPromptAtomicallyAndIgnoresDuplicateDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = fixture.Services.GetRequiredService<AlertStore>();
        var payload = JsonSerializer.SerializeToElement(new
        {
            alertId = "ingested", alertRule = "Outage", monitorCondition = "Fired",
            targetResourceName = "DC01", targetSubscriptionId = "sub"
        });
        Assert.True((await store.AddAsync(payload)).Created);
        Assert.False((await store.AddAsync(payload)).Created);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        Assert.Single(await fixture.Service.GetPromptsAsync(rca.Id));
    }

    [Fact]
    public async Task PromptPersistenceFailureRollsBackAlertAndCaseTogether()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailPrompt BEFORE INSERT ON RcaPrompts BEGIN SELECT RAISE(ABORT, 'Prompt failed'); END;");
        var payload = JsonSerializer.SerializeToElement(new
        {
            alertId = "ingested", alertRule = "Outage", monitorCondition = "Fired",
            targetResourceName = "DC01", targetSubscriptionId = "sub"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Services.GetRequiredService<AlertStore>().AddAsync(payload));
        Assert.Empty(await fixture.Service.ListAsync());
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Empty(await check.Alerts.ToArrayAsync());
        Assert.Empty(await check.RcaChanges.ToArrayAsync());
    }

    [Fact]
    public async Task CreatesOneCategoryIncidentAndImmediatePromptForMultipleSystems()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.AddAlertAsync("second", "DC02", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        await using var context = fixture.Factory.CreateDbContext();
        var rca = Assert.Single(await context.RootCauseAnalyses.ToArrayAsync());
        var prompt = Assert.Single(await context.RcaPrompts.ToArrayAsync());
        Assert.Equal(Start.AddMinutes(15), rca.FollowUpDueAt);
        Assert.Equal(RcaStatuses.Open, rca.Status);
        Assert.True(rca.IsActive);
        Assert.Equal(0, prompt.Sequence);
        Assert.Contains("DC01", prompt.RawPrompt);
        Assert.Contains("DC02", prompt.RawPrompt);
        Assert.Equal(2, JsonSerializer.Deserialize<Guid[]>(rca.EvidenceEventIdsJson)!.Length);
    }

    [Fact]
    public async Task CapturesFollowUpAtExactThresholdWithNewEvidenceAndNeverOverwritesInitialPrompt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var initial = Assert.Single(await fixture.Service.GetPromptsAsync(
            Assert.Single(await fixture.Service.ListAsync()).Id));
        await fixture.AddAlertAsync("second", "DC02", Start.AddMinutes(10));
        await fixture.ReconcileAsync(Start.AddMinutes(15).AddTicks(-1));
        Assert.Single(await fixture.Service.GetPromptsAsync(initial.RcaId));
        await fixture.ReconcileAsync(Start.AddMinutes(15));
        await fixture.ReconcileAsync(Start.AddMinutes(16));
        var prompts = await fixture.Service.GetPromptsAsync(initial.RcaId);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(initial.RawPrompt, prompts[0].RawPrompt);
        Assert.DoesNotContain("DC02", prompts[0].RawPrompt);
        Assert.Contains("DC02", prompts[1].RawPrompt);
        Assert.Equal(Start.AddMinutes(15), prompts[1].CreatedAt);
    }

    [Fact]
    public async Task GeneratesLateFollowUpAfterResolutionAndKeepsManualWorkflowOpen()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.AddAlertAsync("first", "DC01", Start.AddMinutes(5), "Resolved");
        await fixture.ReconcileAsync(Start.AddMinutes(5));
        var rca = Assert.Single(await fixture.Service.ListAsync());
        Assert.False(rca.IsActive);
        Assert.Equal(Start.AddMinutes(5), rca.CriticalEndedAt);
        Assert.Equal(RcaStatuses.Open, rca.Status);
        // A fresh context simulates recovery of the persisted schedule after downtime.
        await fixture.ReconcileAsync(Start.AddHours(1));
        var prompts = await fixture.Service.GetPromptsAsync(rca.Id);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("Resolved", prompts[1].RawPrompt);
        Assert.Contains("\"IsActive\":false", prompts[1].RawPrompt);
        Assert.Equal(Start.AddHours(1), prompts[1].CreatedAt);
    }

    [Fact]
    public async Task ManualClosureDoesNotDuplicateIncidentButRecurrenceCreatesNewCase()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", "", "Closed", "");
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        Assert.Single(await fixture.Service.ListAsync());
        await fixture.AddAlertAsync("first", "DC01", Start.AddMinutes(2), "Resolved");
        await fixture.ReconcileAsync(Start.AddMinutes(2));
        await fixture.AddAlertAsync("recurrence", "DC02", Start.AddMinutes(3));
        await fixture.ReconcileAsync(Start.AddMinutes(3));
        Assert.Equal(2, (await fixture.Service.ListAsync()).Count);
        await fixture.ReconcileAsync(Start.AddMinutes(18));
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(4, await context.RcaPrompts.CountAsync());
    }

    [Fact]
    public async Task DomainThenSiteThenSystemOutageKeepsOneRcaAndOriginalPromptSchedule()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await context.AlertRules.ExecuteDeleteAsync();
            context.AlertRules.AddRange(
                new AlertRule
                {
                    Id = Guid.NewGuid(), Name = "Domain", CategoryName = "Domain outage",
                    IsCritical = true, Priority = 1, Threshold = 50,
                    ConditionType = AlertRuleConditionTypes.DomainControllerPercentageInDomain
                },
                new AlertRule
                {
                    Id = Guid.NewGuid(), Name = "Site", CategoryName = "Site outage",
                    IsCritical = true, Priority = 2,
                    ConditionType = AlertRuleConditionTypes.AllDomainControllersInSite
                },
                new AlertRule
                {
                    Id = Guid.NewGuid(), Name = "System", CategoryName = "System outage",
                    IsCritical = true, Priority = 3, ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
                });
            for (var index = 1; index <= 6; index++)
                context.ComputerInventory.Add(new()
                {
                    SubscriptionId = "sub", Computer = $"DC0{index}", Domain = "example.org",
                    Site = index <= 3 ? "A" : "B", Role = "Domain Controller"
                });
            await context.SaveChangesAsync();
        }
        async Task AssertCategoryAsync(string expected)
        {
            await using var context = fixture.Factory.CreateDbContext();
            var active = AlertLifecycle.GetActiveAlerts(await context.Alerts.ToArrayAsync());
            var categorized = fixture.Services.GetRequiredService<AlertRuleEvaluator>().Categorize(
                active, await context.AlertRules.ToArrayAsync(), await context.ComputerInventory.ToArrayAsync(), active);
            Assert.Equal(expected, Assert.Single(categorized.Categories).Name);
        }
        for (var index = 1; index <= 4; index++)
            await fixture.AddAlertAsync($"alert-{index}", $"DC0{index}", Start);
        await AssertCategoryAsync("Domain outage");
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        var initial = Assert.Single(await fixture.Service.GetPromptsAsync(original.Id));

        await fixture.AddAlertAsync("alert-4", "DC04", Start.AddMinutes(1), "Resolved");
        await AssertCategoryAsync("Site outage");
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        Assert.Equal(original.Id, Assert.Single(await fixture.Service.ListAsync()).Id);
        await fixture.AddAlertAsync("alert-2", "DC02", Start.AddMinutes(2), "Resolved");
        await fixture.AddAlertAsync("alert-3", "DC03", Start.AddMinutes(2), "Resolved");
        await AssertCategoryAsync("System outage");
        await fixture.ReconcileAsync(Start.AddMinutes(2));
        var remaining = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(original.Id, remaining.Id);
        Assert.True(remaining.IsActive);
        Assert.Null(remaining.CriticalEndedAt);
        Assert.Equal("Domain outage", remaining.CategoryName);
        Assert.Equal(original.FollowUpDueAt, remaining.FollowUpDueAt);
        Assert.Single(await fixture.Service.GetPromptsAsync(original.Id));
        Assert.Equal(7, (await fixture.Service.GetLinkedAlertsAsync(original.Id)).Count);
        await fixture.ReconcileAsync(Start.AddMinutes(15));
        var prompts = await fixture.Service.GetPromptsAsync(original.Id);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(initial.RawPrompt, prompts[0].RawPrompt);
        Assert.Contains("Resolved", prompts[1].RawPrompt);
        await fixture.AddAlertAsync("alert-1", "DC01", Start.AddMinutes(16), "Resolved");
        await fixture.ReconcileAsync(Start.AddMinutes(16));
        Assert.False(Assert.Single(await fixture.Service.ListAsync()).IsActive);
    }

    [Fact]
    public async Task NewLifecycleForSameSystemReusesOpenRcaAfterResolutionWithoutResettingWorkflow()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), original.Id, original.Revision,
            "Investigation", "owner", "InAnalysis", "Keep history");
        await fixture.AddAlertAsync("first", "DC01", Start.AddMinutes(1), "Resolved");
        await fixture.ReconcileAsync(Start.AddMinutes(16));
        Assert.False(Assert.Single(await fixture.Service.ListAsync()).IsActive);
        await fixture.AddAlertAsync("second", "DC01", Start.AddHours(1));
        await fixture.ReconcileAsync(Start.AddHours(1));
        var saved = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(original.Id, saved.Id);
        Assert.True(saved.IsActive);
        Assert.Null(saved.CriticalEndedAt);
        Assert.Equal("InAnalysis", saved.Status);
        Assert.Equal("owner", saved.AssignedTo);
        Assert.Equal("Investigation", saved.Analysis);
        Assert.Equal(original.FollowUpDueAt, saved.FollowUpDueAt);
        Assert.Equal(2, (await fixture.Service.GetPromptsAsync(saved.Id)).Count);
        Assert.Equal(3, (await fixture.Service.GetLinkedAlertsAsync(saved.Id)).Count);
        Assert.Contains(await fixture.Service.GetChangesAsync(saved.Id), change => change.After == "Keep history");
        await fixture.ReconcileAsync(Start.AddHours(2));
        Assert.Equal(saved.Revision, Assert.Single(await fixture.Service.ListAsync()).Revision);
    }

    [Fact]
    public async Task NewLifecycleAfterManualClosureCreatesNewCaseForSameSystem()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), original.Id, original.Revision, "", "", "Closed", "");
        await fixture.AddAlertAsync("first", "DC01", Start.AddMinutes(1), "Resolved");
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        await fixture.AddAlertAsync("second", "DC01", Start.AddMinutes(2));
        await fixture.ReconcileAsync(Start.AddMinutes(2));
        var cases = await fixture.Service.ListAsync();
        Assert.Equal(2, cases.Count);
        var current = Assert.Single(cases, rca => rca.Id != original.Id);
        Assert.Equal("second", Assert.Single(await fixture.Service.GetLinkedAlertsAsync(current.Id)).AlertId);
    }

    [Fact]
    public async Task SameComputerNameInAnotherSubscriptionDoesNotReuseRcaAcrossCategories()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.AddAlertAsync("second", "DC01", Start.AddMinutes(1), subscriptionId: "other");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            (await context.AlertRules.SingleAsync()).Enabled = false;
            context.AlertRules.Add(new()
            {
                Id = Guid.NewGuid(), Name = "Different category", CategoryName = "Site outage",
                IsCritical = true, ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
            });
            await context.SaveChangesAsync();
        }
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        Assert.Equal(2, (await fixture.Service.ListAsync()).Count);
        await using var verification = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await verification.RcaAlertLinks.Select(link => link.RcaId).Distinct().CountAsync());
    }

    [Fact]
    public async Task LegacyOverlappingEvidenceUsesOldestCaseWithoutMergingCasesOrPrompts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        var duplicateId = Guid.NewGuid();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await context.RcaAlertLinks.ExecuteDeleteAsync();
            context.RootCauseAnalyses.Add(new()
            {
                Id = duplicateId, RuleId = original.RuleId, CategoryName = "Site outage",
                CreatedAt = Start.AddMinutes(1), FollowUpDueAt = Start.AddMinutes(16),
                EvidenceEventIdsJson = original.EvidenceEventIdsJson, Status = "Actions",
                AssignedTo = "owner", Analysis = "Preserved analysis", IsActive = true
            });
            context.RcaPrompts.Add(new() { Id = Guid.NewGuid(), RcaId = duplicateId, RawPrompt = "Preserved prompt" });
            context.RcaChanges.Add(new() { Id = Guid.NewGuid(), RcaId = duplicateId, Field = "Comment", After = "Preserved comment" });
            await context.SaveChangesAsync();
        }
        await fixture.ReconcileAsync(Start.AddMinutes(2));
        Assert.Equal(2, (await fixture.Service.ListAsync()).Count);
        var duplicate = Assert.Single(await fixture.Service.ListAsync(), rca => rca.Id == duplicateId);
        Assert.Equal("Actions", duplicate.Status);
        Assert.Equal("owner", duplicate.AssignedTo);
        Assert.Equal("Preserved analysis", duplicate.Analysis);
        Assert.Equal("Preserved prompt", Assert.Single(await fixture.Service.GetPromptsAsync(duplicateId)).RawPrompt);
        Assert.Contains(await fixture.Service.GetChangesAsync(duplicateId), change => change.After == "Preserved comment");
        await using var verification = fixture.Factory.CreateDbContext();
        Assert.Equal(original.Id, (await verification.RcaAlertLinks.SingleAsync()).RcaId);
    }

    [Fact]
    public async Task NewAlertsChooseOldestOpenCaseEvenWhenOldestHistoricalOwnerIsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), original.Id, original.Revision, "", "", "Closed", "");
        var oldestOpenId = Guid.NewGuid();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.RootCauseAnalyses.AddRange(
                new RootCauseAnalysis
                {
                    Id = oldestOpenId, RuleId = Guid.NewGuid(), CategoryName = "Earlier open case",
                    CreatedAt = Start.AddMinutes(1), FollowUpDueAt = Start.AddMinutes(16),
                    EvidenceEventIdsJson = original.EvidenceEventIdsJson, Status = "Actions"
                },
                new RootCauseAnalysis
                {
                    Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CategoryName = "Later open case",
                    CreatedAt = Start.AddMinutes(2), FollowUpDueAt = Start.AddMinutes(17),
                    EvidenceEventIdsJson = original.EvidenceEventIdsJson
                });
            await context.SaveChangesAsync();
        }
        await fixture.AddAlertAsync("new-lifecycle", "DC01", Start.AddMinutes(3));
        await fixture.ReconcileAsync(Start.AddMinutes(3));
        Assert.Equal(3, (await fixture.Service.ListAsync()).Count);
        Assert.Equal("new-lifecycle", Assert.Single(await fixture.Service.GetLinkedAlertsAsync(oldestOpenId)).AlertId);
    }

    [Fact]
    public async Task NewAlertsUseParsedInventorySystemInsteadOfAlertSubscriptionForOpenCaseMatching()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.LinkInventoryAsync("DC01", "example.org");
        var original = Assert.Single(await fixture.Service.ListAsync());
        await fixture.AddAlertAsync("second", "workspace-target", Start.AddMinutes(1),
            subscriptionId: "workspace-subscription");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var alert = await context.Alerts.SingleAsync(alert => alert.AlertId == "second");
            context.ParsedAlerts.Add(new()
            {
                Id = alert.Id, InventorySubscriptionId = "sub", InventoryComputer = "DC01"
            });
            (await context.AlertRules.SingleAsync()).Enabled = false;
            context.AlertRules.Add(new()
            {
                Id = Guid.NewGuid(), Name = "Other", CategoryName = "Other outage",
                IsCritical = true, ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
            });
            await context.SaveChangesAsync();
        }
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        Assert.Equal(original.Id, Assert.Single(await fixture.Service.ListAsync()).Id);
        Assert.Equal(2, (await fixture.Service.GetLinkedAlertsAsync(original.Id)).Count);
    }

    [Fact]
    public void AlertLinkMigrationCreatesReferencesAndReplacesCategoryUniqueness()
    {
        var migration = new MonitoringApp.Migrations.AddRcaAlertLinks();
        var table = Assert.Single(migration.UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation>());
        Assert.Equal("RcaAlertLinks", table.Name);
        Assert.Equal("AlertEventId", Assert.Single(table.PrimaryKey!.Columns));
        Assert.Contains(table.ForeignKeys, key => key.PrincipalTable == "Alerts");
        Assert.Contains(table.ForeignKeys, key => key.PrincipalTable == "RootCauseAnalyses");
        var index = Assert.Single(migration.UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation>(),
            operation => operation.Table == "RootCauseAnalyses");
        Assert.False(index.IsUnique);
        Assert.Null(index.Filter);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Database", "CreateMonitoringDatabase.template.sql"));
        Assert.Contains("20261022133544_AddRcaAlertLinks", script);
        Assert.Contains("CREATE TABLE [dbo].[RcaAlertLinks]", script);
    }

    [Fact]
    public async Task HonorsRulePriorityAndIgnoresNoncriticalOrDisabledRules()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var critical = await context.AlertRules.SingleAsync();
            critical.Priority = 20;
            context.AlertRules.Add(new()
            {
                Id = Guid.NewGuid(), Name = "Noncritical winner", CategoryName = "Healthy",
                ConditionType = AlertRuleConditionTypes.AnyMatchingAlert, Priority = 1
            });
            await context.SaveChangesAsync();
        }
        await fixture.ReconcileAsync(Start);
        Assert.Empty(await fixture.Service.ListAsync());
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var rules = await context.AlertRules.ToArrayAsync();
            foreach (var rule in rules)
                rule.Enabled = false;
            await context.SaveChangesAsync();
        }
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task SavesFieldLevelHistoryAndCommentsWithActorAndRejectsStaleOrInvalidEdits()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision,
            "Confirmed evidence", "owner", "InAnalysis", "Investigating");
        var saved = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal("owner", saved.AssignedTo);
        Assert.Equal("Confirmed evidence", saved.Analysis);
        var edits = (await fixture.Service.GetChangesAsync(rca.Id)).Where(change => change.Actor == "admin").ToArray();
        Assert.Equal(4, edits.Length);
        var status = Assert.Single(edits, change => change.Field == "Status");
        Assert.Equal("Open", status.Before);
        Assert.Equal("InAnalysis", status.After);
        Assert.All(edits, change => Assert.Equal(Start, change.CreatedAt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, rca.Revision, "", "", "Open", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, saved.Revision, "", "missing-user", "Open", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, saved.Revision, "", "", "Invalid", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, saved.Revision, "", "", "Open", new string('x', 4001)));
        Assert.Equal(saved.Revision, Assert.Single(await fixture.Service.ListAsync()).Revision);
    }

    [Fact]
    public async Task AssigneeSuggestionsIncludeKnownEmailsAndCurrentEntraIdentityWithoutSqlAccounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "",
            "assigned@example.org", "Open", "");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.AnalysisDashboards.Add(new() { OwnerKey = "dashboard@example.org" });
            context.InboxLayouts.Add(new() { OwnerKey = "inbox@example.org" });
            context.LogbookEntries.AddRange(
                new LogbookEntry { Id = Guid.NewGuid(), User = "logbook@example.org" },
                new LogbookEntry { Id = Guid.NewGuid(), User = " DASHBOARD@EXAMPLE.ORG " },
                new LogbookEntry { Id = Guid.NewGuid(), User = "System" });
            var job = new AgentAnalysisJob
            {
                Id = Guid.NewGuid(), CreatedBy = "job@example.org", IdempotencyKey = "known-assignee"
            };
            context.AgentAnalysisJobs.Add(job);
            context.AgentAnalysisReviewEvents.Add(new()
            {
                Id = Guid.NewGuid(), AgentAnalysisJobId = job.Id, Actor = "review@example.org"
            });
            context.AnalysisDashboardTemplates.Add(new()
            {
                Id = Guid.NewGuid(), UpdatedBy = "publisher@example.org"
            });
            var query = new AnalysisQueryDefinition
            {
                Id = Guid.NewGuid(), Key = "assignee-query", UpdatedBy = "query-editor@example.org"
            };
            query.Versions.Add(new()
            {
                Id = Guid.NewGuid(), PublishedBy = "query-publisher@example.org", SnapshotJson = "{}"
            });
            context.AnalysisQueryDefinitions.Add(query);
            context.RcaChanges.Add(new()
            {
                Id = Guid.NewGuid(), RcaId = rca.Id, Actor = "rca@example.org", Field = "Comment"
            });
            await context.SaveChangesAsync();
        }
        var entraUser = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "current@example.org"), new Claim(ClaimTypes.Role, "Admin")],
            "AppServiceEntra"));

        var suggestions = await fixture.Service.GetAssigneesAsync(entraUser);

        foreach (var email in new[] { "dashboard@example.org", "inbox@example.org", "logbook@example.org",
                     "job@example.org", "review@example.org", "publisher@example.org",
                     "query-editor@example.org", "query-publisher@example.org",
                     "rca@example.org", "assigned@example.org", "current@example.org" })
            Assert.Contains(suggestions, value => value.Equals(email, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("owner", suggestions);
        Assert.Single(suggestions, value => value.Equals("dashboard@example.org", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("System", suggestions);
        Assert.DoesNotContain("admin", suggestions);
        Assert.Equal(suggestions.Order(StringComparer.OrdinalIgnoreCase), suggestions);
    }

    [Fact]
    public async Task FreeTextEmailIsNormalizedAuditedSuggestedAndCanBeCleared()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "",
            " external@example.org ", "Open", "");
        var saved = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal("external@example.org", saved.AssignedTo);
        var change = Assert.Single(await fixture.Service.GetChangesAsync(rca.Id),
            change => change.Field == "AssignedTo");
        Assert.Equal("", change.Before);
        Assert.Equal("external@example.org", change.After);
        Assert.Equal("admin", change.Actor);
        Assert.Contains("external@example.org", await fixture.Service.GetAssigneesAsync(User("Admin")));

        await fixture.Service.SaveAsync(User("Admin"), rca.Id, saved.Revision, "", "  ", "Open", "");
        Assert.Equal("", Assert.Single(await fixture.Service.ListAsync()).AssignedTo);
        Assert.Contains("external@example.org", await fixture.Service.GetAssigneesAsync(User("Admin")));
    }

    [Fact]
    public async Task AcceptsEmailLongerThanLegacySqlUsernameLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        var email = $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.org";
        Assert.True(email.Length > 128);
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", email, "Open", "");
        Assert.Equal(email, Assert.Single(await fixture.Service.ListAsync()).AssignedTo);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(256, context.Model.FindEntityType(typeof(RootCauseAnalysis))!
            .FindProperty(nameof(RootCauseAnalysis.AssignedTo))!.GetMaxLength());
        var saved = Assert.Single(await fixture.Service.ListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, saved.Revision, "", new string('x', 257), "Open", ""));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Person <person@example.org>")]
    [InlineData("one@example.org, two@example.org")]
    [InlineData("one@example.org\r\nanother")]
    public async Task InvalidFreeTextDoesNotChangeRcaOrAuditHistory(string assignee)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        var changes = (await fixture.Service.GetChangesAsync(rca.Id)).Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", assignee, "Open", ""));
        Assert.Equal(rca.Revision, Assert.Single(await fixture.Service.ListAsync()).Revision);
        Assert.Equal(changes, (await fixture.Service.GetChangesAsync(rca.Id)).Count);
    }

    [Fact]
    public async Task ExistingAssignmentCanBeRetainedAfterSqlAccountRemoval()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", "owner", "Open", "");
        await using (var context = fixture.Factory.CreateDbContext())
            await context.AuthenticationUsers.ExecuteDeleteAsync();
        rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "Investigation", "owner", "InAnalysis", "");
        Assert.Equal("owner", Assert.Single(await fixture.Service.ListAsync()).AssignedTo);
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Operator")]
    [InlineData(null)]
    public async Task RejectsNonAdminWrites(string? role)
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveAsync(
            User(role), Guid.NewGuid(), 1, "", "", "Open", ""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.GetAssigneesAsync(User(role)));
    }

    [Fact]
    public async Task ConcurrentRevisionConflictCannotOverwriteAnalysis()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        await using var staleContext = fixture.Factory.CreateDbContext();
        var stale = await staleContext.RootCauseAnalyses.SingleAsync();
        await fixture.ReconcileAsync(Start.AddMinutes(15));
        stale.Analysis = "Stale write";
        stale.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());
    }

    [Fact]
    public void MigrationOnlyAddsRcaTablesAndIndexes()
    {
        var migration = new MonitoringApp.Migrations.AddRootCauseAnalysis();
        var tables = migration.UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation>()
            .Select(operation => operation.Name).Order().ToArray();
        Assert.Equal(new[] { "RcaChanges", "RcaPrompts", "RootCauseAnalyses" }, tables);
        Assert.All(migration.UpOperations, operation => Assert.True(
            operation is Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation));
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Database", "CreateMonitoringDatabase.template.sql"));
        Assert.Contains("20261022102938_AddRootCauseAnalysis", script);
    }

    [Fact]
    public void EmailMigrationOnlyWidensAssigneeColumnAndTemplateRecordsIt()
    {
        var migration = new MonitoringApp.Migrations.ExpandRcaAssigneeEmail();
        var operation = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation>(
            Assert.Single(migration.UpOperations));
        Assert.Equal("RootCauseAnalyses", operation.Table);
        Assert.Equal("AssignedTo", operation.Name);
        Assert.Equal(256, operation.MaxLength);
        Assert.Equal(128, operation.OldColumn.MaxLength);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Database", "CreateMonitoringDatabase.template.sql"));
        Assert.Contains("20261022113434_ExpandRcaAssigneeEmail", script);
        Assert.Contains("ALTER COLUMN [AssignedTo] nvarchar(256) NOT NULL", script);
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateAlertOwnershipAndSnapshotSequences()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var rca = Assert.Single(await fixture.Service.ListAsync());
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var eventId = await context.Alerts.Select(alert => alert.Id).SingleAsync();
            context.RcaAlertLinks.Add(new() { RcaId = rca.Id, AlertEventId = eventId });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.RcaPrompts.Add(new() { Id = Guid.NewGuid(), RcaId = rca.Id, Sequence = 0 });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Single(await fixture.Service.GetPromptsAsync(rca.Id));
    }

    [Fact]
    public async Task RendersRcaNavigationPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var renderer = new StaticPageRenderer(fixture.Services,
            fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(Rca), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return rendered.ToHtmlString();
        });
        Assert.Contains("Root cause analysis", html);
        Assert.Contains("No RCA incidents have been recorded", html);
        Assert.DoesNotContain("Save changes", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendersCompactGroupsAndSelectedCaseAlertsAboveAnalysis(bool selectClosed)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("linked-alert", "DC01", Start);
        await fixture.AddAlertAsync("unrelated-alert", "DC02", Start);
        var openId = Guid.NewGuid();
        var closedId = Guid.NewGuid();
        Guid eventId;
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var ruleId = await context.AlertRules.Select(rule => rule.Id).SingleAsync();
            context.RootCauseAnalyses.AddRange(
                new() { Id = openId, RuleId = ruleId, CategoryName = "Open domain outage", CreatedAt = Start },
                new() { Id = closedId, RuleId = ruleId, CategoryName = "Closed site outage",
                    CreatedAt = Start, Status = RcaStatuses.Closed });
            eventId = await context.Alerts.Where(alert => alert.AlertId == "linked-alert")
                .Select(alert => alert.Id).SingleAsync();
            context.RcaAlertLinks.Add(new()
            {
                AlertEventId = eventId, RcaId = selectClosed ? closedId : openId, LinkedAt = Start
            });
            await context.SaveChangesAsync();
        }
        await using var renderer = new StaticPageRenderer(fixture.Services,
            fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(SelectedRcaPage),
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SelectedRcaPage.SelectedCaseId)] = selectClosed ? closedId : openId
                }));
            await rendered.QuiescenceTask;
            return rendered.ToHtmlString();
        });
        Assert.Contains("Open RCAs (1)", html);
        Assert.Contains("Closed RCAs (1)", html);
        var closedGroupStart = html.IndexOf("<details class=\"rca-closed-group\"", StringComparison.Ordinal);
        var closedGroupTag = html[closedGroupStart..(html.IndexOf('>', closedGroupStart) + 1)];
        Assert.DoesNotContain(" open", closedGroupTag);
        Assert.True(html.IndexOf("Open domain outage", StringComparison.Ordinal) < closedGroupStart);
        Assert.True(html.IndexOf("Closed site outage", StringComparison.Ordinal) > closedGroupStart);
        Assert.Contains("--rca-color:#b42318", html);
        Assert.Contains("--rca-color:#137547", html);
        Assert.DoesNotContain("admin-rule-card", html);
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.Contains("aria-pressed=\"false\"", html);
        Assert.Contains("Associated alerts (1)", html);
        Assert.Contains("linked-alert", html);
        Assert.Contains(eventId.ToString(), html);
        Assert.Contains("DC01", html);
        Assert.DoesNotContain("unrelated-alert", html);
        Assert.DoesNotContain("DC02", html);
        Assert.True(html.IndexOf("Associated alerts", StringComparison.Ordinal) <
            html.IndexOf("<span>Analysis</span>", StringComparison.Ordinal));
        Assert.DoesNotContain("Save changes", html);
    }

    [Theory]
    [InlineData("Open", 1)]
    [InlineData("InAnalysis", 1)]
    [InlineData("Actions", 1)]
    [InlineData("Closed", 0)]
    public async Task InboxNoticesIncludeAllUnfinishedStatusesEvenAfterEntwarnung(string status, int expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.LinkInventoryAsync("DC01", "example.org");
        await fixture.AddAlertAsync("first", "DC01", Start.AddMinutes(1), "Resolved");
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        var rca = Assert.Single(await fixture.Service.ListAsync());
        Assert.False(rca.IsActive);
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", "", status, "");

        var notices = await fixture.Service.GetOpenNoticesAsync();
        Assert.Equal(expected, notices.Count);
        if (expected > 0)
        {
            Assert.Equal("Open RCA Outage example.org", notices[0].Label);
            Assert.Equal(status, notices[0].Status);
            Assert.Equal(status == "InAnalysis" ? "In analysis" : status, notices[0].StatusLabel);
            Assert.Equal("Unassigned", notices[0].AssigneeLabel);
        }
    }

    [Fact]
    public async Task InboxNoticesListDistinctDomainsUsingSubscriptionScopedInventoryLinks()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.AddAlertAsync("second", "DC02", Start);
        await fixture.AddAlertAsync("third", "DC03", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.LinkInventoryAsync("DC01", " example.org ");
        await fixture.LinkInventoryAsync("DC02", "example.org");
        await fixture.LinkInventoryAsync("DC03", "branch.org");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "other-subscription", Computer = "DC01", Domain = "unrelated.org"
            });
            await context.SaveChangesAsync();
        }

        var notice = Assert.Single(await fixture.Service.GetOpenNoticesAsync());
        Assert.Equal(new[] { "branch.org", "example.org" }, notice.Domains);
        Assert.Equal("Open RCA Outage branch.org, example.org", notice.Label);
    }

    [Fact]
    public async Task InboxNoticesRemainVisibleWithoutKnownDomainAndInvalidEvidenceSurfacesError()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Empty(await fixture.Service.GetOpenNoticesAsync());
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        Assert.Equal("Open RCA Outage", Assert.Single(await fixture.Service.GetOpenNoticesAsync()).Label);
        await using var context = fixture.Factory.CreateDbContext();
        var rca = await context.RootCauseAnalyses.SingleAsync();
        rca.EvidenceEventIdsJson = "null";
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GetOpenNoticesAsync());
    }

    [Fact]
    public async Task InboxRendersOpenRcaDomainLinkAndHidesClosedCases()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.LinkInventoryAsync("DC01", "example.org");
        var rca = Assert.Single(await fixture.Service.ListAsync());
        async Task<string> RenderInboxAsync()
        {
            await using var renderer = new StaticPageRenderer(fixture.Services,
                fixture.Services.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var rendered = renderer.BeginRenderingComponent(typeof(Home), ParameterView.Empty);
                await rendered.QuiescenceTask;
                return rendered.ToHtmlString();
            });
        }
        var html = await RenderInboxAsync();
        Assert.Contains("Open RCA Outage example.org", html);
        Assert.Contains($"/rca?caseId={rca.Id}", html);
        Assert.Contains("aria-label=\"Open root cause analyses\"", html);
        Assert.Contains("Status: Open / Assigned: Unassigned", html);
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", "admin@example.org", "InAnalysis", "");
        html = await RenderInboxAsync();
        Assert.Contains("Status: In analysis / Assigned: admin@example.org", html);
        rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", "admin@example.org", "Closed", "");
        html = await RenderInboxAsync();
        Assert.DoesNotContain("Open RCA Outage", html);
        Assert.DoesNotContain("aria-label=\"Open root cause analyses\"", html);
    }

    private static ClaimsPrincipal User(string? role) => new(new ClaimsIdentity(
        role is null ? [] : new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, role) },
        role is null ? null : "test"));

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, Factory factory) : IAsyncDisposable
    {
        public Factory Factory => factory;
        public ServiceProvider Services => services;
        public RcaService Service => services.GetRequiredService<RcaService>();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            context.AlertRules.Add(new()
            {
                Id = Guid.NewGuid(), Name = "Critical", CategoryName = "Outage", IsCritical = true,
                ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
            });
            context.AuthenticationUsers.Add(new() { Username = "owner", PasswordHash = "unused" });
            await context.SaveChangesAsync();
            var collection = new ServiceCollection().AddLogging().AddAuthorization(options =>
                    options.AddPolicy("AdminAccess", policy => policy.RequireRole("Admin")));
            collection.AddCascadingAuthenticationState();
            var services = collection
                .AddSingleton<AuthenticationStateProvider, ReaderAuthenticationState>()
                .AddSingleton<IDbContextFactory<AlertDbContext>>(factory)
                .AddSingleton(new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()))
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
                .AddSingleton(new ApplicationAuthenticationOptions())
                .AddSingleton<TimeProvider>(new FixedTime())
                .AddSingleton(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")))
                .AddSingleton<AlertRuleEvaluator>().AddSingleton<AlertStore>()
                .AddSingleton(new AlertHistoryOptions())
                .AddSingleton(new AlertSeverityDisplayOptions())
                .AddSingleton(new InboxTargetOutputDisplayOptions())
                .AddSingleton(new RcaDisplayOptions())
                .AddSingleton<InboxDashboardState>()
                .AddSingleton<InboxLayoutRepository>()
                .AddSingleton<Microsoft.JSInterop.IJSRuntime, UnusedJavaScript>()
                .AddSingleton<RcaService>().BuildServiceProvider();
            return new(connection, services, factory);
        }

        public async Task AddAlertAsync(
            string alertId, string target, DateTimeOffset at, string condition = "Fired", string subscriptionId = "sub")
        {
            await using var context = factory.CreateDbContext();
            context.Alerts.Add(new(Guid.NewGuid(), at, alertId, "Test outage", "Sev0", "New",
                "Log", condition, target, "rg", subscriptionId, at, "Description", "", "", "{}"));
            await context.SaveChangesAsync();
        }

        public async Task ReconcileAsync(DateTimeOffset now)
        {
            await using var context = factory.CreateDbContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            await RcaAutomation.ReconcileAsync(context, services.GetRequiredService<AlertRuleEvaluator>(),
                services.GetRequiredService<QueryResultPresenter>(), now, default);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        public async Task LinkInventoryAsync(string computer, string domain)
        {
            await using var context = factory.CreateDbContext();
            var inventory = new ComputerInventoryEntry
            {
                SubscriptionId = "sub", Computer = computer, Domain = domain
            };
            context.ComputerInventory.Add(inventory);
            var alert = await context.Alerts.SingleAsync(alert => alert.Target == computer);
            context.ParsedAlerts.Add(new()
            {
                Id = alert.Id, Alert = alert, InventoryComputerEntry = inventory,
                InventoryComputer = computer, InventorySubscriptionId = "sub"
            });
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Start;
    }

    private sealed class ReaderAuthenticationState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(User("Reader")));
    }

    private sealed class SelectedRcaPage : Rca
    {
        [Parameter] public Guid SelectedCaseId { get; set; }

        protected override Task OnParametersSetAsync()
        {
            CaseId = SelectedCaseId;
            return base.OnParametersSetAsync();
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
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static HTML rendering must not invoke JavaScript.");
    }
}
