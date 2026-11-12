using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class InventorySafetyTests
{
    [Fact]
    public async Task BootTimePersistsOnCreateAndRefreshWithoutBeingErasedByLegacyNullOrStaleEvents()
    {
        await using var fixture = await Fixture.CreateAsync();
        var originalBoot = Now.AddDays(-5);
        var newBoot = Now.AddDays(-1);
        async Task Send(DateTimeOffset heartbeat, object? boot, bool includeColumn = true)
        {
            var names = new[] { "SubscriptionId", "Computer", "LastHeartbeat" }
                .Concat(includeColumn ? ["lastboottime"] : []).ToArray();
            var values = new object?[] { "sub", "DC01", heartbeat.ToString("O") }
                .Concat(includeColumn ? new[] { boot } : []).ToArray();
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                alertId = "boot-time-inventory",
                alertRule = InventoryHeartbeatBatch.AlertName,
                monitorCondition = "Fired",
                queryResult = new
                {
                    type = InventoryHeartbeatBatch.QueryResultType, rowCount = 1,
                    columns = names.Select(name => new { name }).ToArray(), rows = new[] { values }
                }
            }));
            await fixture.Store.AddAsync(payload.RootElement);
        }
        async Task<ComputerInventoryEntry> Stored() =>
            Assert.Single(await fixture.Store.GetComputerInventoryAsync());

        await Send(Now, originalBoot.ToOffset(TimeSpan.FromHours(2)).ToString("O"));
        Assert.Equal(originalBoot, (await Stored()).LastBootTime);
        Assert.Equal(TimeSpan.Zero, (await Stored()).LastBootTime!.Value.Offset);
        await Send(Now.AddMinutes(1), null, includeColumn: false);
        await Send(Now.AddMinutes(2), null);
        Assert.Equal(originalBoot, (await Stored()).LastBootTime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(Now.AddMinutes(3), "invalid"));
        Assert.Equal(Now.AddMinutes(2), (await Stored()).LastHeartbeatAt);
        Assert.Equal(originalBoot, (await Stored()).LastBootTime);
        await Send(Now.AddMinutes(3), newBoot.ToString("O"));
        Assert.Equal(newBoot, (await Stored()).LastBootTime);
        await Send(Now.AddMinutes(1), originalBoot.ToString("O"));
        Assert.Equal(newBoot, (await Stored()).LastBootTime);
        await fixture.Store.SaveComputerInventoryEntryAsync(new()
        {
            SubscriptionId = "sub", Computer = "DC01", Site = "Manually edited site", LastBootTime = null
        });
        Assert.Equal(newBoot, (await Stored()).LastBootTime);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.Alerts.ToArrayAsync());
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData("ingestion", false)]
    [InlineData("ingestion", true)]
    [InlineData("backfill", false)]
    [InlineData("backfill", true)]
    public async Task NonHeartbeatAlertsNeverModifyManualOrHeartbeatData(
        string path, bool populated)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "sub", Computer = "DC01", Domain = "known.example",
                Site = populated ? "Known site" : null,
                ResourceGroup = populated ? "registered-rg" : null,
                Role = populated ? "Custom role" : null,
                Maintenance = true, LastHeartbeatAt = Now, PrivateIP = "10.0.0.1",
                NtdsFileSizeInGb = 8.25, CertificatesJson = "[]", ServicesJson = "[]"
            });
            await context.SaveChangesAsync();
        }
        var notifications = 0;
        fixture.Store.Changed += () => notifications++;
        using var payload = JsonDocument.Parse(LearningPayload());
        if (path == "ingestion")
        {
            Assert.True((await fixture.Store.AddAsync(payload.RootElement)).Created);
            Assert.False((await fixture.Store.AddAsync(payload.RootElement)).Created);
        }
        else
        {
            await fixture.AddAlertAsync("learning", rawJson: LearningPayload());
            await fixture.Store.GetAlertGraphRecordsAsync(Now.AddDays(-1));
            await fixture.Store.GetAlertGraphRecordsAsync(Now.AddDays(-1));
        }
        Assert.Equal(path == "ingestion" ? 1 : 0, notifications);
        await using var verify = fixture.Factory.CreateDbContext();
        var entry = await verify.ComputerInventory.SingleAsync();
        Assert.Equal("sub", entry.SubscriptionId);
        Assert.Equal("DC01", entry.Computer);
        Assert.Equal("known.example", entry.Domain);
        Assert.Equal(populated ? "Known site" : null, entry.Site);
        Assert.Equal(populated ? "registered-rg" : null, entry.ResourceGroup);
        Assert.Equal(populated ? "Custom role" : null, entry.Role);
        Assert.True(entry.Maintenance);
        Assert.Equal(Now, entry.LastHeartbeatAt);
        Assert.Equal("10.0.0.1", entry.PrivateIP);
        Assert.Equal(8.25, entry.NtdsFileSizeInGb);
        Assert.Equal("[]", entry.CertificatesJson);
        Assert.Equal("[]", entry.ServicesJson);
        Assert.Single(await verify.Alerts.ToArrayAsync());
        Assert.Single(await verify.ParsedAlerts.ToArrayAsync());
    }

    [Fact]
    public async Task RcaFailureRollsBackAlertHistoryParsedInventoryAndAuditWithoutNotifyingFacadeSubscribers()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddInventoryAsync();
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailRca BEFORE INSERT ON RootCauseAnalyses
                BEGIN SELECT RAISE(ABORT, 'RCA failed'); END;
                """);
        var notifications = 0;
        fixture.Store.Changed += () =>
        {
            using var verify = fixture.Factory.CreateDbContext();
            using var transaction = verify.Database.BeginTransaction();
            Assert.Single(verify.Alerts.ToArray());
            Assert.Single(verify.AlertHistory.ToArray());
            Assert.Single(verify.ParsedAlerts.ToArray());
            Assert.Single(verify.ComputerInventory.ToArray());
            Assert.Single(verify.RootCauseAnalyses.ToArray());
            notifications++;
        };
        using var payload = JsonDocument.Parse(LearningPayload());
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Store.AddAsync(payload.RootElement));
        Assert.Equal(0, notifications);
        await using (var verify = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(await verify.Alerts.ToArrayAsync());
            Assert.Empty(await verify.AlertHistory.ToArrayAsync());
            Assert.Empty(await verify.ParsedAlerts.ToArrayAsync());
            Assert.Single(await verify.ComputerInventory.ToArrayAsync());
            Assert.Empty(await verify.LogbookEntries.ToArrayAsync());
            Assert.Empty(await verify.RootCauseAnalyses.ToArrayAsync());
            Assert.Empty(await verify.RcaAlertLinks.ToArrayAsync());
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER FailRca");
        }
        Assert.True((await fixture.Store.AddAsync(payload.RootElement)).Created);
        Assert.Equal(1, notifications);
    }

    private static string LearningPayload() => JsonSerializer.Serialize(new
    {
        alertId = "learning", alertRule = "DCDiag", monitorCondition = "Fired",
        targetSubscriptionId = "sub", targetResourceName = "DC01", targetResourceGroup = "rg",
        queryResult = new
        {
            type = "DCDiag", rowCount = 1,
            columns = new[] { new { name = "Computer" }, new { name = "Site" }, new { name = "Status" } },
            rows = new[] { new[] { "DC01", "Learned site", """{"Connectivity":"Failed"}""" } }
        }
    });

    [Theory]
    [InlineData("DCDiag", false)]
    [InlineData("DCDiag", true)]
    [InlineData("DCPort", false)]
    [InlineData("DCPort", true)]
    [InlineData("ScheduledKql", false)]
    [InlineData("ScheduledKql", true)]
    [InlineData("Unclassified", false)]
    [InlineData("Unclassified", true)]
    public async Task NonHeartbeatAlertsCannotDiscoverSystemsEvenWhenSuppressed(string type, bool suppressed)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (suppressed)
            await fixture.Store.SaveAlertRuleAsync(new()
            {
                Name = "Suppress non-heartbeat", RuleType = AlertRuleTypes.Suppression,
                QueryResultType = type, ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
            });
        using var payload = JsonDocument.Parse(LearningPayload().Replace("DCDiag", type, StringComparison.Ordinal));
        await fixture.Store.AddAsync(payload.RootElement);
        await fixture.Store.AddAsync(payload.RootElement);
        using var resolved = JsonDocument.Parse(payload.RootElement.GetRawText().Replace("Fired", "Resolved", StringComparison.Ordinal));
        await fixture.Store.AddAsync(resolved.RootElement);
        Assert.Empty(await fixture.Store.GetComputerInventoryAsync());
        Assert.Empty(await fixture.Store.GetAlertGraphRecordsAsync(Now.AddDays(-1)));
        await using var context = fixture.Factory.CreateDbContext();
        Assert.All(await context.ParsedAlerts.ToArrayAsync(), parsed =>
        {
            Assert.Null(parsed.InventorySubscriptionId);
            Assert.Null(parsed.InventoryComputer);
        });
    }

    [Fact]
    public async Task HeartbeatIsTheOnlyDiscoverySourceAndLinksPreviouslyUnknownAlerts()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var alert = JsonDocument.Parse(LearningPayload());
        await fixture.Store.AddAsync(alert.RootElement);
        Assert.Empty(await fixture.Store.GetComputerInventoryAsync());
        using var heartbeat = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            alertId = "heartbeat-discovery", alertRule = InventoryHeartbeatBatch.AlertName,
            queryResult = new
            {
                type = InventoryHeartbeatBatch.QueryResultType, rowCount = 1,
                columns = new[]
                {
                    new { name = "SubscriptionId" }, new { name = "Computer" }, new { name = "Domain" },
                    new { name = "Site" }, new { name = "LastHeartbeat" }, new { name = "Services" }
                },
                rows = new[] { new object[] { "sub", "DC01", "heartbeat.example", "Heartbeat site", Now.ToString("O"),
                    new[] { new InventoryService("NTDS", 1) } } }
            }
        }));
        Assert.True((await fixture.Store.AddAsync(heartbeat.RootElement)).Suppressed);
        await fixture.Store.AddAsync(alert.RootElement);
        var entry = Assert.Single(await fixture.Store.GetComputerInventoryAsync());
        Assert.Equal("heartbeat.example", entry.Domain);
        Assert.Equal("Heartbeat site", entry.Site);
        Assert.Equal("Domain Controller", entry.Role);
        Assert.Null(entry.ResourceGroup);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Single(await context.Alerts.ToArrayAsync());
        var parsed = Assert.Single(await context.ParsedAlerts.ToArrayAsync());
        Assert.Equal("sub", parsed.InventorySubscriptionId);
        Assert.Equal("DC01", parsed.InventoryComputer);
    }

    [Fact]
    public async Task DisabledHeartbeatRuleDoesNotFallBackToLearningFromTheAlertTarget()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = Assert.Single(await fixture.Store.GetAllAlertRulesAsync(),
            candidate => candidate.RuleType == AlertRuleTypes.InventoryStatus);
        rule.Enabled = false;
        await fixture.Store.SaveAlertRuleAsync(rule);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            alertId = "disabled-heartbeat", alertRule = InventoryHeartbeatBatch.AlertName,
            targetSubscriptionId = "sub", targetResourceName = "DC01",
            queryResult = new
            {
                type = InventoryHeartbeatBatch.QueryResultType,
                columns = new[] { new { name = "SubscriptionId" }, new { name = "Computer" }, new { name = "LastHeartbeat" } },
                rows = new[] { new[] { "sub", "DC01", Now.ToString("O") } }
            }
        }));
        await fixture.Store.AddAsync(payload.RootElement);
        Assert.Empty(await fixture.Store.GetComputerInventoryAsync());
    }

    [Fact]
    public async Task AlertRoleAssignmentRulesCannotBeEnabledAgain()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = Assert.Single(await fixture.Store.GetAllAlertRulesAsync(),
            candidate => candidate.QueryResultType == "DCDiag");
        Assert.False(rule.Enabled);
        rule.Enabled = true;
        var error = await Assert.ThrowsAsync<AlertRuleException>(() => fixture.Store.SaveAlertRuleAsync(rule));
        Assert.Equal(AlertRuleErrorCode.RetiredRoleAssignment, error.Code);
        Assert.Contains("retired", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"queryResult":{"rowCount":0}}""", true)]
    [InlineData("""{"queryResult":{"rowCount":0.0}}""", true)]
    [InlineData("""{"queryResult":{"rowCount":"0"}}""", true)]
    [InlineData("""{"queryResult":{"rowCount":1}}""", false)]
    [InlineData("""{"queryResult":{"rowCount":null}}""", false)]
    [InlineData("""{"queryResult":{"rows":[]}}""", false)]
    [InlineData("{}", false)]
    public void RecognizesExplicitZeroRowCountsWithoutChangingMissingCountBehavior(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AlertInventoryLearning.HasZeroRowCount(document.RootElement));
        Assert.Equal(expected, AlertInventoryLearning.HasZeroRowCount(json));
    }

    [Theory]
    [InlineData("DCDiag", false)]
    [InlineData("DCDiag", true)]
    [InlineData("DCPort", false)]
    [InlineData("DCPort", true)]
    [InlineData("InventoryHeartbeat", false)]
    [InlineData("InventoryHeartbeat", true)]
    public async Task ZeroRowIngestionNeverCreatesOrModifiesInventory(string type, bool hasInventory)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (hasInventory)
            await fixture.AddInventoryAsync();
        using var payload = JsonDocument.Parse(Payload(type));
        await fixture.Store.AddAsync(payload.RootElement);
        await fixture.Store.AddAsync(payload.RootElement);
        await using var context = fixture.Factory.CreateDbContext();
        var entries = await context.ComputerInventory.ToArrayAsync();
        if (hasInventory)
        {
            var entry = Assert.Single(entries);
            Assert.Equal("known.example", entry.Domain);
            Assert.Null(entry.Site);
            Assert.Null(entry.Role);
            Assert.Null(entry.ResourceGroup);
            Assert.Null(entry.PrivateIP);
            Assert.Null(entry.LastHeartbeatAt);
        }
        else
            Assert.Empty(entries);
        if (type == "InventoryHeartbeat")
            Assert.Empty(await context.Alerts.ToArrayAsync());
        else
        {
            Assert.Single(await context.Alerts.ToArrayAsync());
            var parsed = Assert.Single(await context.ParsedAlerts.ToArrayAsync());
            Assert.Equal(hasInventory ? "sub" : null, parsed.InventorySubscriptionId);
            Assert.Equal(hasInventory ? "DC01" : null, parsed.InventoryComputer);
        }
        Assert.Empty(AlertInventoryLearning.LearnComputerDomains(Payload("DCPort")));
        using var heartbeat = JsonDocument.Parse(Payload("InventoryHeartbeat"));
        Assert.Empty(InventoryHeartbeatBatch.Parse(heartbeat.RootElement));
    }

    [Theory]
    [InlineData(false, "AzureADConnectHealthAgent", "Entra Connect")]
    [InlineData(true, "AzureADConnectHealthAgent", "Entra Connect")]
    [InlineData(true, "OtherService", "Custom role")]
    public async Task InventoryHeartbeatPersistsDetectedEntraConnectRole(
        bool hasInventory, string serviceName, string expectedRole)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (hasInventory)
        {
            await fixture.AddInventoryAsync();
            await using var existingContext = fixture.Factory.CreateDbContext();
            (await existingContext.ComputerInventory.SingleAsync()).Role = "Custom role";
            await existingContext.SaveChangesAsync();
        }
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            alertId = "entra-connect-inventory",
            alertRule = InventoryHeartbeatBatch.AlertName,
            monitorCondition = "Fired",
            queryResult = new
            {
                type = InventoryHeartbeatBatch.QueryResultType,
                rowCount = 1,
                columns = new[]
                {
                    new { name = "SubscriptionId" }, new { name = "Computer" },
                    new { name = "LastHeartbeat" }, new { name = "Services" }
                },
                rows = new[] { new object[] { "sub", "DC01", Now.ToString("O"),
                    new[] { new InventoryService(serviceName, 1) } } }
            }
        }));

        await fixture.Store.AddAsync(payload.RootElement);
        await using var context = fixture.Factory.CreateDbContext();
        var inventory = Assert.Single(await context.ComputerInventory.ToArrayAsync());
        Assert.Equal(expectedRole, inventory.Role);
        Assert.Equal(serviceName, Assert.Single(inventory.Services).Name);
        Assert.Equal(Now, inventory.LastHeartbeatAt);
        Assert.Empty(await context.Alerts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ZeroRowHistoryCannotLearnInventoryThroughBackfill(
        bool dcPort, bool hasInventory)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (hasInventory)
            await fixture.AddInventoryAsync();
        await fixture.AddAlertAsync("zero-history", rawJson: Payload(dcPort ? "DCPort" : "DCDiag"));
        await fixture.Store.GetAlertGraphRecordsAsync(Now.AddDays(-1));
        await using var context = fixture.Factory.CreateDbContext();
        var entries = await context.ComputerInventory.ToArrayAsync();
        Assert.Equal(hasInventory ? 1 : 0, entries.Length);
        if (hasInventory)
        {
            Assert.Equal("known.example", entries[0].Domain);
            Assert.Null(entries[0].Site);
            Assert.Null(entries[0].Role);
            Assert.Null(entries[0].ResourceGroup);
        }
        var parsed = Assert.Single(await context.ParsedAlerts.ToArrayAsync());
        Assert.Equal(hasInventory ? "sub" : null, parsed.InventorySubscriptionId);
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Operator")]
    [InlineData(null)]
    public async Task LegacyResolutionEnforcesAdminForPreviewAndExecution(string? role)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("legacy");
        var error = await Assert.ThrowsAsync<MaintenanceAccessDeniedException>(() => fixture.Service.CountAsync(User(role)));
        Assert.Equal("Only administrators may resolve legacy alerts.", error.Message);
        Assert.IsAssignableFrom<UnauthorizedAccessException>(error);
        await Assert.ThrowsAsync<MaintenanceAccessDeniedException>(() => fixture.Service.ResolveAsync(User(role), "RESOLVE"));
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.LogbookEntries.ToArrayAsync());
        Assert.Equal("", (await context.Alerts.SingleAsync()).Comments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("resolve")]
    [InlineData(" RESOLVE")]
    [InlineData("RESOLVE ")]
    [InlineData("RESET")]
    public async Task LegacyResolutionRequiresExactConfirmation(string confirmation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("legacy");
        var error = await Assert.ThrowsAsync<MaintenanceValidationException>(() => fixture.Service.ResolveAsync(User(), confirmation));
        Assert.Equal(MaintenanceValidationCode.ConfirmationInvalid, error.Code);
        Assert.Equal("Type RESOLVE exactly to confirm legacy alert resolution.", error.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(1, await fixture.Service.CountAsync(User()));
    }

    [Theory]
    [InlineData("ScheduledKqlWorkspace")]
    [InlineData("ScheduledKqlInventoryTarget")]
    public async Task ExternalTargetAlertsAreNotLegacyInventoryCleanupCandidates(string type)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("legacy");
        var workspaceId = await fixture.AddAlertAsync("scheduled-query:workspace",
            parsed: true, computer: "Workspace: production",
            rawJson: JsonSerializer.Serialize(new { queryResult = new { type } }));
        Assert.Equal(1, await fixture.Service.CountAsync(User()));
        Assert.Equal(1, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        await using var context = fixture.Factory.CreateDbContext();
        var active = Assert.Single(AlertLifecycle.GetActiveAlerts(await context.Alerts.ToArrayAsync()));
        Assert.Equal(workspaceId, active.Id);
        Assert.Equal(0, await fixture.Service.CountAsync(User()));
    }

    [Fact]
    public async Task ResolvesOnlyActiveUnassignedAlertsAndPreservesHistoryWithAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacyId = await fixture.AddAlertAsync("legacy", comments: "Original comment");
        var parsedId = await fixture.AddAlertAsync("parsed", parsed: true);
        await fixture.AddAlertAsync("", parsed: true, computer: "NO-ID");
        await fixture.AddInventoryAsync();
        var linkedId = await fixture.AddAlertAsync("linked", parsed: true, assigned: true);
        await fixture.AddAlertAsync("resolved", condition: "Resolved");
        await fixture.AddAlertAsync("manual", comments: "Resolved");
        await fixture.AddAlertAsync("completed");
        await fixture.AddAlertAsync("completed", condition: "Resolved", at: Now.AddMinutes(-1));
        Assert.Equal(3, await fixture.Service.CountAsync(User()));
        var notifications = 0;
        fixture.Store.Changed += () => notifications++;
        Assert.Equal(3, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(1, notifications);
        Assert.Equal(0, await fixture.Service.CountAsync(User()));
        await using var context = fixture.Factory.CreateDbContext();
        var alerts = await context.Alerts.ToArrayAsync();
        Assert.Equal(8, alerts.Length);
        Assert.Equal(linkedId, Assert.Single(AlertLifecycle.GetActiveAlerts(alerts)).Id);
        Assert.Equal("Original comment\nResolved", alerts.Single(alert => alert.Id == legacyId).Comments);
        Assert.Equal("Fired", alerts.Single(alert => alert.Id == legacyId).MonitorCondition);
        var audit = await context.LogbookEntries.ToArrayAsync();
        Assert.Equal(3, audit.Length);
        Assert.All(audit, entry =>
        {
            Assert.Equal("admin@example.org", entry.User);
            Assert.Equal(Now, entry.CreatedAt);
            Assert.NotNull(entry.AlertRecordId);
        });
        var parsed = await context.ParsedAlerts.Include(record => record.Alert).ToArrayAsync();
        Assert.All(parsed.Where(record => record.Id == legacyId || record.Id == parsedId),
            record => Assert.Equal(Now, record.ResolvedAt));
        ParsedAlertLifecycle.Synchronize(parsed, await context.AlertRules.ToArrayAsync(),
            fixture.Services.GetRequiredService<AlertRuleEvaluator>());
        Assert.All(parsed.Where(record => record.Id == legacyId || record.Id == parsedId),
            record => Assert.Equal(Now, record.ResolvedAt));
        Assert.Single(await context.ComputerInventory.ToArrayAsync());
        Assert.Equal(0, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task LegacyResolutionRechecksNewInventoryAssignmentsAfterPreview()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddAlertAsync("legacy", parsed: true);
        Assert.Equal(1, await fixture.Service.CountAsync(User()));
        await fixture.AddInventoryAsync();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var parsed = await context.ParsedAlerts.SingleAsync(record => record.Id == id);
            parsed.InventorySubscriptionId = "sub";
            parsed.InventoryComputer = "DC01";
            await context.SaveChangesAsync();
        }
        Assert.Equal(0, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal("", (await check.Alerts.SingleAsync()).Comments);
        Assert.Empty(await check.LogbookEntries.ToArrayAsync());
        Assert.Empty(await check.RootCauseAnalyses.ToArrayAsync());
    }

    [Fact]
    public async Task LegacyResolutionEndsCriticalActivityWithoutClosingOrDeletingRca()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("legacy", parsed: true);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await RcaAutomation.ReconcileAsync(context,
                fixture.Services.GetRequiredService<AlertRuleEvaluator>(),
                fixture.Services.GetRequiredService<QueryResultPresenter>(), Now.AddMinutes(-1), default);
            await context.SaveChangesAsync();
        }
        Assert.Equal(1, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        await using var check = fixture.Factory.CreateDbContext();
        var rca = await check.RootCauseAnalyses.SingleAsync();
        Assert.False(rca.IsActive);
        Assert.Equal("Open", rca.Status);
        Assert.Equal(Now, rca.CriticalEndedAt);
        Assert.Single(await check.RcaPrompts.ToArrayAsync());
        Assert.Single(await check.Alerts.ToArrayAsync());
        Assert.Single(await check.RcaAlertLinks.ToArrayAsync());
    }

    [Fact]
    public async Task RcaFailureRollsBackLegacyResolutionAndRetryNotifiesOnlyAfterCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddInventoryAsync();
        await fixture.AddAlertAsync("one", parsed: true, comments: "Keep comment");
        await fixture.AddAlertAsync("two");
        RootCauseAnalysis original;
        int changes;
        string prompt;
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await RcaAutomation.ReconcileAsync(context,
                fixture.Services.GetRequiredService<AlertRuleEvaluator>(),
                fixture.Services.GetRequiredService<QueryResultPresenter>(), Now.AddMinutes(-1), default);
            await context.SaveChangesAsync();
            original = await context.RootCauseAnalyses.AsNoTracking().SingleAsync();
            changes = await context.RcaChanges.CountAsync();
            prompt = (await context.RcaPrompts.SingleAsync()).RawPrompt;
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailLegacyRca BEFORE INSERT ON RcaChanges BEGIN SELECT RAISE(ABORT, 'RCA failed'); END;");
        }
        var notifications = 0;
        fixture.Store.Changed += () =>
        {
            using var context = fixture.Factory.CreateDbContext();
            using var transaction = context.Database.BeginTransaction();
            Assert.Equal(2, context.LogbookEntries.Count());
            Assert.All(context.ParsedAlerts.ToArray(), record => Assert.Equal(Now, record.ResolvedAt));
            Assert.False(context.RootCauseAnalyses.Single().IsActive);
            notifications++;
        };

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(0, notifications);
        Assert.Equal(2, await fixture.Service.CountAsync(User()));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            Assert.Equal("Keep comment", (await context.Alerts.SingleAsync(alert => alert.AlertId == "one")).Comments);
            Assert.Equal("", (await context.Alerts.SingleAsync(alert => alert.AlertId == "two")).Comments);
            Assert.Empty(await context.LogbookEntries.ToArrayAsync());
            Assert.Null((await context.ParsedAlerts.SingleAsync()).ResolvedAt);
            Assert.Equal(JsonSerializer.Serialize(original),
                JsonSerializer.Serialize(await context.RootCauseAnalyses.SingleAsync()));
            Assert.Equal(changes, await context.RcaChanges.CountAsync());
            Assert.Equal(prompt, (await context.RcaPrompts.SingleAsync()).RawPrompt);
            Assert.Equal(2, await context.RcaAlertLinks.CountAsync());
            Assert.Equal("known.example", (await context.ComputerInventory.SingleAsync()).Domain);
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailLegacyRca;");
        }

        Assert.Equal(2, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(1, notifications);
        Assert.Equal(0, await fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(1, notifications);
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await check.Alerts.CountAsync());
        Assert.Equal(2, await check.ParsedAlerts.CountAsync());
        Assert.Equal(2, await check.LogbookEntries.CountAsync());
        var rca = await check.RootCauseAnalyses.SingleAsync();
        Assert.Equal(original.Id, rca.Id);
        Assert.Equal("Open", rca.Status);
        Assert.Equal(Now, rca.CriticalEndedAt);
        Assert.Equal(prompt, (await check.RcaPrompts.SingleAsync()).RawPrompt);
    }

    [Fact]
    public async Task AuditFailureRollsBackAllLegacyResolutions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("one");
        await fixture.AddAlertAsync("two");
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailAudit BEFORE INSERT ON LogbookEntries BEGIN SELECT RAISE(ABORT, 'Audit failed'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(2, await fixture.Service.CountAsync(User()));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.All(await check.Alerts.ToArrayAsync(), alert => Assert.Equal("", alert.Comments));
        Assert.Empty(await check.LogbookEntries.ToArrayAsync());
    }

    [Fact]
    public async Task FullCommentsAreNotTruncatedAndPreventPartialResolution()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("one");
        var id = await fixture.AddAlertAsync("two", comments: new string('x', 4000));
        var error = await Assert.ThrowsAsync<MaintenanceValidationException>(() => fixture.Service.ResolveAsync(User(), "RESOLVE"));
        Assert.Equal(MaintenanceValidationCode.CommentsTooLong, error.Code);
        Assert.Equal(id, error.AlertId);
        Assert.Equal($"Alert {id} has no room for the Resolved marker. Shorten its comments and retry. No alerts were changed.", error.Message);
        Assert.Equal(2, await fixture.Service.CountAsync(User()));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Empty(await check.LogbookEntries.ToArrayAsync());
        Assert.Equal(new string('x', 4000), (await check.Alerts.SingleAsync(alert => alert.Id == id)).Comments);
    }

    [Fact]
    public async Task RendersSeparateLegacyActionWithClearInventoryScope()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var renderer = new HtmlRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<LegacyAlertResolutionPanel>()).ToHtmlString());
        var text = fixture.Services.GetRequiredService<UiText>();
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains(text["LegacyAlerts.Title"], decoded);
        Assert.Contains(text["LegacyAlerts.Description"], decoded);
        Assert.DoesNotContain(text["DatabaseReset.Confirm"], decoded);
    }

    private static ClaimsPrincipal User(string? role = "Admin") => new(new ClaimsIdentity(
        role is null ? [] : new[] { new Claim(ClaimTypes.Name, "admin@example.org"), new Claim(ClaimTypes.Role, role) },
        role is null ? null : "test"));

    private static string Payload(string type) => JsonSerializer.Serialize(new
    {
        alertId = $"zero-{type}", alertRule = "Inventory Heartbeat Status", monitorCondition = "Fired",
        targetResourceName = "DC01", targetSubscriptionId = "sub", targetResourceGroup = "new-rg",
        queryResult = new
        {
            type, rowCount = 0,
            columns = new[]
            {
                new { name = "SubscriptionId" }, new { name = "Computer" }, new { name = "Domain" },
                new { name = "Site" }, new { name = "LastHeartbeat" }, new { name = "PrivateIP" },
                new { name = "LocalSystem" }, new { name = "LocalDomain" }, new { name = "LocalSite" },
                new { name = "Services" }
            },
            rows = new[] { new[] { "sub", "DC01", "changed.example", "New site", Now.ToString("O"),
                "10.0.0.1", "DC01", "changed.example", "New site",
                JsonSerializer.Serialize(new[] { new InventoryService("AzureADConnectHealthAgent", 4) }) } }
        }
    });

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, Factory factory) : IAsyncDisposable
    {
        public Factory Factory => factory;
        public ServiceProvider Services => services;
        public AlertStore Store => services.GetRequiredService<AlertStore>();
        public LegacyAlertResolutionService Service => services.GetRequiredService<LegacyAlertResolutionService>();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, AlertInventoryLearningTests.InventoryLearningTestModelCustomizer>().Options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            context.AlertRules.AddRange(DefaultAlertRules.Create().Where(rule =>
                rule.RuleType is AlertRuleTypes.InventoryStatus or AlertRuleTypes.InventoryRoleAssignment));
            context.AlertRules.Add(new()
            {
                Id = Guid.NewGuid(), Name = "Critical", CategoryName = "Outage", IsCritical = true,
                ConditionType = AlertRuleConditionTypes.AnyMatchingAlert
            });
            await context.SaveChangesAsync();
            var services = new ServiceCollection().AddLogging().AddAuthorization(options =>
                    options.AddPolicy("AdminAccess", policy => policy.RequireRole("Admin")))
                .AddSingleton<IDbContextFactory<AlertDbContext>>(factory)
                .AddSingleton(new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()))
                .AddSingleton(new ApplicationAuthenticationOptions())
                .AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()))
                .AddSingleton<TimeProvider>(new FixedTime())
                .AddSingleton(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")))
                .AddSingleton<AlertRuleEvaluator>().AddSingleton<AlertStore>()
                .AddSingleton<AuthenticationStateProvider, AdminState>()
                .AddSingleton<LegacyAlertResolutionService>();
            services.AddUiLocalization();
            return new(connection, services.BuildServiceProvider(), factory);
        }

        public async Task AddInventoryAsync()
        {
            await using var context = factory.CreateDbContext();
            context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "DC01", Domain = "known.example" });
            await context.SaveChangesAsync();
        }

        public async Task<Guid> AddAlertAsync(
            string id, bool parsed = false, bool assigned = false, string condition = "Fired",
            string comments = "", string computer = "DC01", string rawJson = "{}", DateTimeOffset? at = null)
        {
            await using var context = factory.CreateDbContext();
            var alert = new AlertRecord(Guid.NewGuid(), at ?? Now.AddMinutes(-10), id, "Test", "Sev0",
                "New", "Log", condition, computer, "rg", "sub", at ?? Now.AddMinutes(-10), "", "", comments, rawJson);
            context.Alerts.Add(alert);
            if (parsed)
            {
                var record = ParsedAlertFactory.Create(alert);
                if (!assigned)
                {
                    record.InventorySubscriptionId = null;
                    record.InventoryComputer = null;
                }
                context.ParsedAlerts.Add(record);
            }
            await context.SaveChangesAsync();
            return alert.Id;
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
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdminState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(User()));
    }
}
