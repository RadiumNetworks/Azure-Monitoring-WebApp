using System.ClientModel.Primitives;
using System.Text.Json;
using System.Reflection;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class ScheduledQueryTests
{
    [Theory]
    [InlineData("=", 0, 0, true)]
    [InlineData("=", 0, 1, false)]
    [InlineData(">", 90, 90, false)]
    [InlineData(">", 90, 90.01, true)]
    [InlineData(">=", 90, 90, true)]
    [InlineData("<", 1, 0, true)]
    [InlineData("<", 1, 1, false)]
    [InlineData("<=", 1, 1, true)]
    public void ComparesThresholdBoundaries(string comparison, double threshold, double value, bool matches) =>
        Assert.Equal(matches, new ScheduledQueryRule { Comparison = comparison, Threshold = threshold }.Matches(value));

    [Fact]
    public async Task EvaluatesFullUtcWindowWithDelayAndPreviewDoesNotEmitAlerts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Query.Response = Result(("DC01", 95), ("DC02", null));
        ScheduledQueryExecutionPreview? prepared = null;
        var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: value => prepared = value);
        Assert.NotNull(prepared);
        Assert.Equal(fixture.Query.Kql, prepared.Query.EffectiveKql);
        Assert.Equal(fixture.Query.TimeRange, prepared.ApiTimeRangeMinutes);
        Assert.Equal(fixture.Options.Workspaces[0].WorkspaceId, prepared.WorkspaceId);
        Assert.Equal(result.WindowEndUtc.AddMinutes(-5), prepared.WindowStartUtc);
        Assert.Equal(result.WindowEndUtc, prepared.WindowEndUtc);
        Assert.Equal(new DateTime(2026, 10, 28, 12, 6, 0, DateTimeKind.Utc), result.WindowEndUtc);
        Assert.Contains("datetime(2026-10-28T12:01:00.0000000Z)", fixture.Query.Kql);
        Assert.Contains("datetime(2026-10-28T12:06:00.0000000Z)", fixture.Query.Kql);
        Assert.Contains("let _SelectedSystems = dynamic([\"DC01\",\"DC02\"])", fixture.Query.Kql);
        Assert.Contains("set truncationmaxsize=1048576;", fixture.Query.Kql);
        Assert.EndsWith("| take 1001", fixture.Query.Kql);
        Assert.Equal(10, fixture.Query.TimeRange);
        Assert.Equal(95, Assert.Single(result.Workspaces).Values["DC01"]);
        Assert.Null(Assert.Single(result.Workspaces).Values["DC02"]);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.Alerts.ToArrayAsync());
        Assert.Empty(await context.ScheduledQueryNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExposesExactKqlBeforeExecutionEvenForEmptyOrFailedQueries(bool fail)
    {
        await using var fixture = await Fixture.CreateAsync();
        ScheduledQueryExecutionPreview? prepared = null;
        fixture.Query.BeforeResult = () =>
        {
            Assert.NotNull(prepared);
            Assert.Equal(fixture.Query.Kql, prepared.Query.EffectiveKql);
            return Task.CompletedTask;
        };
        if (fail)
        {
            fixture.Query.Failure = new InvalidOperationException("Simulated preview failure");
            var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: value => prepared = value);
            Assert.Contains("Simulated preview failure", Assert.Single(result.Workspaces).Error!);
            Assert.Empty(Assert.Single(result.Workspaces).Values);
        }
        else
        {
            var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: value => prepared = value);
            Assert.Empty(Assert.Single(result.Workspaces).Values);
            Assert.Null(Assert.Single(result.Workspaces).Error);
        }
        Assert.NotNull(prepared);
        Assert.Equal("production", prepared.Query.WorkspaceKey);
        Assert.Equal(["DC01", "DC02"], prepared.Query.Systems);
        Assert.Equal(fixture.Query.Kql, prepared.Query.EffectiveKql);
    }

    [Fact]
    public async Task ShowsPreparedKqlWithoutExecutingWhenNoInventoryTargetsMatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Rule.Computers = "NOT-IN-INVENTORY";
        ScheduledQueryExecutionPreview? prepared = null;
        var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: value => prepared = value);
        Assert.Empty(Assert.Single(result.Workspaces).Targets);
        Assert.NotNull(prepared);
        Assert.Empty(prepared.Query.Systems);
        Assert.Contains("let _SelectedSystems = dynamic([]);", prepared.Query.EffectiveKql);
        Assert.Equal(0, fixture.Query.Calls);
    }

    [Fact]
    public async Task SavesLegacySelectionAndSnapshotsMultipleWorkspaceKeys()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        Assert.Equal(["production"], fixture.Rule.WorkspaceKeys);
        await fixture.Service.SaveAsync(fixture.Rule);
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(["production"], stored.WorkspaceKeys);
        Assert.Equal("production", stored.WorkspaceKey);
        stored.WorkspaceKeys = ["production", "workspace-1", "PRODUCTION"];
        fixture.Factory.OnCreate = () => stored.WorkspaceKeys = ["unvalidated-edit"];
        await fixture.Service.SaveAsync(stored);
        fixture.Factory.OnCreate = null;
        var saved = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(["production", "workspace-1"], saved.WorkspaceKeys);
        saved.WorkspaceKeys = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(saved));
        saved.WorkspaceKeys = ["removed-workspace"];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(saved));
    }

    [Fact]
    public async Task KeepsSameComputerNamesAndEffectiveQueriesSeparateAcrossWorkspaces()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["production", "workspace-1"];
        fixture.Query.Responses["production"] = Result(("DC01", 95));
        fixture.Query.Responses["workspace-1"] = Result(("DC01", 10));
        var prepared = new List<ScheduledQueryExecutionPreview>();
        var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: prepared.Add);
        Assert.Equal(2, result.Workspaces.Count);
        Assert.Equal(95, result.Workspaces[0].Values["DC01"]);
        Assert.Equal(10, result.Workspaces[1].Values["DC01"]);
        Assert.All(result.Workspaces, workspace => Assert.Null(workspace.Error));
        Assert.Equal(2, prepared.Count);
        for (var index = 0; index < prepared.Count; index++)
        {
            Assert.Equal(fixture.Query.Requests[index].Kql, prepared[index].Query.EffectiveKql);
            Assert.Equal(fixture.Query.Requests[index].Workspace, prepared[index].Query.WorkspaceKey);
            Assert.Equal(result.WindowEndUtc, prepared[index].WindowEndUtc);
        }
        Assert.DoesNotContain("DC02", fixture.Query.Requests[1].Kql);
        Assert.Empty(await fixture.AlertRecordsAsync());
        await fixture.Service.SaveAsync(fixture.Rule);
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(Fixture.Subscription, Assert.Single(await fixture.AlertRecordsAsync()).SubscriptionId);
        var states = await fixture.Service.StatesAsync(fixture.Rule.Id);
        Assert.Equal("Alerting", Assert.Single(states, state => state.WorkspaceKey == "production" && state.Computer == "DC01").Status);
        Assert.Equal("Normal", Assert.Single(states, state => state.WorkspaceKey == "workspace-1").Status);
    }

    [Theory]
    [InlineData("request", true)]
    [InlineData("request", false)]
    [InlineData("azure", true)]
    [InlineData("timeout", true)]
    [InlineData("invalid-rows", true)]
    public async Task FailedWorkspaceDoesNotPreventOtherWorkspaceAlerts(string kind, bool failedFirst)
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = failedFirst ? ["production", "workspace-1"] : ["workspace-1", "production"];
        fixture.Query.Responses["workspace-1"] = Result(("DC01", 95));
        if (kind == "invalid-rows")
            fixture.Query.Responses["production"] = Result(("DC01", 99), ("UNSELECTED", 100));
        else
            fixture.Query.Failures["production"] = kind == "timeout"
                ? new OperationCanceledException()
                : kind == "azure" ? new Azure.RequestFailedException(400, "The table does not exist in this workspace.")
                : new InvalidOperationException("Workspace-specific failure");
        await fixture.Service.SaveAsync(fixture.Rule);
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(2, fixture.Query.Calls);
        var alert = Assert.Single(await fixture.AlertRecordsAsync());
        Assert.Equal(fixture.Options.Workspaces[1].SubscriptionIds[0], alert.SubscriptionId);
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.StartsWith("Partial error: 1/2", stored.LastStatus);
        Assert.Contains("production:", stored.LastError);
        Assert.NotNull(Assert.Single(stored.WorkspaceResults, workspace => workspace.WorkspaceKey == "production").Error);
        Assert.Null(Assert.Single(stored.WorkspaceResults, workspace => workspace.WorkspaceKey == "workspace-1").Error);
        Assert.All((await fixture.Service.StatesAsync(fixture.Rule.Id)).Where(state => state.WorkspaceKey == "production"),
            state => { Assert.Equal("Error", state.Status); Assert.Null(state.ActiveAlertId); Assert.Null(state.Value); });
    }

    [Fact]
    public async Task FailedWorkspaceRetainsItsEpisodeWhileAnotherWorkspaceRecovers()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["production", "workspace-1"];
        fixture.Query.Response = Result(("DC01", 99));
        await fixture.Service.SaveAsync(fixture.Rule);
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(2, (await fixture.AlertRecordsAsync()).Length);
        var originalState = Assert.Single(await fixture.Service.StatesAsync(fixture.Rule.Id),
            state => state.WorkspaceKey == "production" && state.Computer == "DC01");
        fixture.Clock.Advance();
        fixture.Query.Failures["production"] = new InvalidOperationException("Workspace unavailable");
        fixture.Query.Responses["workspace-1"] = Result(("DC01", 10));
        await fixture.NewService().RunOnceAsync(default);
        var states = await fixture.Service.StatesAsync(fixture.Rule.Id);
        var failed = Assert.Single(states, state => state.WorkspaceKey == "production" && state.Computer == "DC01");
        Assert.Equal("Error", failed.Status);
        Assert.Equal(originalState.ActiveAlertId, failed.ActiveAlertId);
        Assert.Null(Assert.Single(states, state => state.WorkspaceKey == "workspace-1").ActiveAlertId);
        var alerts = await fixture.AlertRecordsAsync();
        Assert.Equal("Fired", Assert.Single(alerts, alert => alert.SubscriptionId == Fixture.Subscription).MonitorCondition);
        Assert.Single(alerts, alert => alert.SubscriptionId != Fixture.Subscription && alert.MonitorCondition == "Resolved");
        fixture.Clock.Advance();
        fixture.Query.Failures.Clear();
        fixture.Query.Responses["production"] = Result(("DC01", 10));
        await fixture.NewService().RunOnceAsync(default);
        Assert.Equal(2, (await fixture.AlertRecordsAsync()).Count(alert => alert.MonitorCondition == "Resolved"));
        Assert.Equal(4, (await fixture.AlertRecordsAsync()).Length);
        Assert.All(await fixture.Service.StatesAsync(fixture.Rule.Id), state => Assert.Null(state.ActiveAlertId));
        Assert.DoesNotContain("Partial error", Assert.Single(await fixture.Service.ListAsync()).LastStatus);
    }

    [Fact]
    public async Task MissingConfiguredWorkspaceIsAnExplicitErrorButDoesNotBlockRemainingWorkspaces()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Rule.WorkspaceKeys = ["removed-workspace", "production"];
        fixture.Query.Response = Result(("DC01", 99));
        var result = await fixture.Service.PreviewAsync(fixture.Rule);
        Assert.Contains("no longer configured", result.Workspaces[0].Error!);
        Assert.Null(result.Workspaces[1].Error);
        Assert.Equal(99, result.Workspaces[1].Values["DC01"]);
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task RenewsLeaseBetweenWorkspacesForRunsLongerThanFiveMinutes()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 5);
        fixture.Rule.WorkspaceKeys = fixture.Options.Workspaces.Select(workspace => workspace.Key).ToArray();
        fixture.Query.Response = Result(("DC01", 99));
        fixture.Query.BeforeResult = async () =>
        {
            if (fixture.Query.Calls == 1)
            {
                await fixture.NewService().RunOnceAsync(default);
                Assert.Equal(1, fixture.Query.Calls);
            }
            fixture.Clock.Advance(TimeSpan.FromSeconds(80));
        };
        await fixture.Service.SaveAsync(fixture.Rule);
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(5, fixture.Query.Calls);
        Assert.Equal(10, fixture.Query.Requests[0].TimeRange);
        Assert.Equal(16, fixture.Query.Requests[4].TimeRange);
        Assert.Equal(5, (await fixture.AlertRecordsAsync()).Length);
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal("", stored.LastError);
        Assert.Null(stored.LeaseId);
    }

    [Fact]
    public async Task ExternalCancellationStopsBeforeTheNextWorkspace()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["production", "workspace-1"];
        using var cancellation = new CancellationTokenSource();
        fixture.Query.BeforeResult = () =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.PreviewAsync(fixture.Rule, cancellation.Token));
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task ChangedConfigurationStopsBeforeAnotherWorkspaceAndDiscardsEarlierResults()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["production", "workspace-1"];
        fixture.Query.Response = Result(("DC01", 99));
        fixture.Query.BeforeResult = async () =>
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.ScheduledQueryRules.ExecuteUpdateAsync(update =>
                update.SetProperty(rule => rule.Revision, rule => rule.Revision + 1));
        };
        await fixture.Service.SaveAsync(fixture.Rule);
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Null(Assert.Single(await fixture.Service.ListAsync()).LeaseId);
    }

    [Fact]
    public async Task WorkspaceWithNoMatchingSystemsDoesNotStopTheOtherWorkspace()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["workspace-1", "production"];
        fixture.Rule.Domain = "example.com";
        fixture.Query.Response = Result(("DC01", 99));
        var result = await fixture.Service.PreviewAsync(fixture.Rule);
        Assert.Empty(result.Workspaces[0].Targets);
        Assert.Null(result.Workspaces[0].Error);
        Assert.Equal(99, result.Workspaces[1].Values["DC01"]);
        Assert.Equal(1, fixture.Query.Calls);
    }

    [Fact]
    public async Task WorkspaceUpgradePreservesLegacySelectionAndActiveAlertState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE ScheduledQueryRules (Id TEXT NOT NULL PRIMARY KEY, WorkspaceKey TEXT NOT NULL);
            CREATE TABLE ScheduledQueryTargetStates (RuleId TEXT NOT NULL, ActiveAlertId TEXT NOT NULL);
            INSERT INTO ScheduledQueryRules VALUES ('legacy-rule', 'production');
            INSERT INTO ScheduledQueryTargetStates VALUES ('legacy-rule', 'existing-alert-episode');
            """);
        var migration = new Migrations.AddScheduledQueryWorkspaces { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, context.Model);
        foreach (var command in commands)
            await context.Database.ExecuteSqlRawAsync(command.CommandText.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
        await using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT r.WorkspaceKey, r.WorkspaceKeysJson, r.LastWorkspaceResultsJson, s.WorkspaceKey, s.ActiveAlertId
            FROM ScheduledQueryRules r JOIN ScheduledQueryTargetStates s ON s.RuleId = r.Id;
            """;
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var rule = new ScheduledQueryRule { WorkspaceKey = reader.GetString(0), WorkspaceKeysJson = reader.GetString(1) };
        Assert.Equal(["production"], rule.WorkspaceKeys);
        Assert.Equal("[]", reader.GetString(2));
        Assert.Equal("production", reader.GetString(3));
        Assert.Equal("existing-alert-episode", reader.GetString(4));
    }

    [Fact]
    public async Task KeepsScheduleAnchoredAndSkipsMissedIntervalsInsteadOfReplayingThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        var firstDue = fixture.Clock.Now.UtcDateTime;
        fixture.Query.Response = Result(("DC01", 99));
        fixture.Query.BeforeResult = () =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(12));
            return Task.CompletedTask;
        };
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(firstDue.AddMinutes(5), (await fixture.Service.ListAsync()).Single().NextRunUtc);
        fixture.Query.BeforeResult = null;
        fixture.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(30));
        await fixture.NewService().RunOnceAsync(default);
        var next = (await fixture.Service.ListAsync()).Single().NextRunUtc;
        Assert.True(next > fixture.Clock.Now.UtcDateTime);
        Assert.True(next <= fixture.Clock.Now.UtcDateTime.AddMinutes(5));
        Assert.Equal(2, fixture.Query.Calls);
        Assert.Single(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task PersistsOneLifecycleThroughNoDataErrorsRecoveryAndAnotherBreach()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Query.Response = Result(("DC01", 95), ("DC02", 60));
        await fixture.Service.RunOnceAsync(default);
        var first = Assert.Single(await fixture.AlertRecordsAsync());
        Assert.Equal("Fired", first.MonitorCondition);
        Assert.Equal("DC01", first.TargetName);
        Assert.Equal("Sev2", first.Severity);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var parsed = Assert.Single(await context.ParsedAlerts.ToArrayAsync());
            Assert.Equal("DC01", parsed.InventoryComputer);
            Assert.Equal(Fixture.Subscription, parsed.InventorySubscriptionId);
        }
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        Assert.NotNull(presenter.Parse(first.RawJson));
        var category = Assert.Single(new AlertRuleEvaluator(presenter).Categorize([first], []).Categories);
        Assert.Equal("System Resource", category.Name);
        Assert.Empty(new AlertRuleEvaluator(presenter).Categorize([first], []).Uncategorized);
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Query.Calls);
        fixture.Clock.Advance();
        await fixture.Service.RunOnceAsync(default);
        Assert.Single(await fixture.AlertRecordsAsync());
        fixture.Clock.Advance();
        fixture.Query.Response = Result(("DC01", null));
        await fixture.Service.RunOnceAsync(default);
        Assert.Single(await fixture.AlertRecordsAsync());
        var state = (await fixture.Service.StatesAsync(fixture.Rule.Id)).Single(item => item.Computer == "DC01");
        Assert.Equal("No data", state.Status);
        Assert.NotNull(state.ActiveAlertId);
        fixture.Clock.Advance();
        fixture.Query.Failure = new InvalidOperationException("Simulated upstream failure");
        await fixture.Service.RunOnceAsync(default);
        Assert.Single(await fixture.AlertRecordsAsync());
        Assert.Contains("Simulated upstream failure", (await fixture.Service.ListAsync()).Single().LastError);
        Assert.Equal("Error", (await fixture.Service.StatesAsync(fixture.Rule.Id)).First().Status);
        fixture.Query.Failure = null;
        fixture.Query.Response = Result(("DC01", 80));
        fixture.Clock.Advance();
        await fixture.Service.RunOnceAsync(default);
        var alerts = await fixture.AlertRecordsAsync();
        Assert.Equal(2, alerts.Length);
        Assert.Equal(first.AlertId, alerts.Single(alert => alert.MonitorCondition == "Resolved").AlertId);
        Assert.Empty(AlertLifecycle.GetActiveAlerts(alerts));
        fixture.Clock.Advance();
        fixture.Query.Response = Result(("DC01", 99));
        await fixture.Service.RunOnceAsync(default);
        var newAlert = Assert.Single(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
        Assert.NotEqual(first.AlertId, newAlert.AlertId);
    }

    [Fact]
    public async Task KerberosMissingIsNotZeroButMeasuredZeroFires()
    {
        await using var fixture = await Fixture.CreateAsync(kerberos: true);
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Query.Response = Result();
        await fixture.Service.RunOnceAsync(default);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.All(await fixture.Service.StatesAsync(fixture.Rule.Id), state => Assert.Equal("No data", state.Status));
        fixture.Clock.Advance();
        fixture.Query.Response = Result(("DC01", 0), ("DC02", 1));
        await fixture.Service.RunOnceAsync(default);
        var alert = Assert.Single(await fixture.AlertRecordsAsync());
        Assert.Equal("DC01", alert.TargetName);
        Assert.Equal("Authentication", ScheduledQueryAlert.Category(alert)!.Value.Category);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsDuplicateAndOutOfScopeRowsWithoutEmittingAlerts(bool duplicate)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Query.Response = duplicate ? Result(("DC01", 99), ("DC01", 98)) : Result(("UNSELECTED", 99));
        await fixture.Service.RunOnceAsync(default);
        Assert.Empty(await fixture.AlertRecordsAsync());
        var rule = (await fixture.Service.ListAsync()).Single();
        Assert.Equal("Error", rule.LastStatus);
        Assert.Contains("unknown or duplicate", rule.LastError);
    }

    [Fact]
    public async Task DeliversPersistedNotificationsAfterFailureAndDoesNotDuplicateAfterRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Query.Response = Result(("DC01", 99));
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailDelivery BEFORE INSERT ON Alerts BEGIN SELECT RAISE(ABORT, 'Delivery unavailable'); END;");
        await fixture.Service.RunOnceAsync(default);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Equal(1, (await fixture.Service.DeliveryStatusAsync()).Pending);
        Assert.NotEmpty((await fixture.Service.DeliveryStatusAsync()).Error!);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var pending = Assert.Single(await context.ScheduledQueryNotifications.ToArrayAsync());
            Assert.Null(pending.DeliveredUtc);
            Assert.NotEmpty(pending.LastError);
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailDelivery;");
        }
        await fixture.NewService().RunOnceAsync(default);
        Assert.Single(await fixture.AlertRecordsAsync());
        Assert.Equal(0, (await fixture.Service.DeliveryStatusAsync()).Pending);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var notification = await context.ScheduledQueryNotifications.SingleAsync();
            Assert.NotNull(notification.DeliveredUtc);
            // Simulate process failure after ingestion committed but before acknowledging the outbox.
            notification.DeliveredUtc = null;
            await context.SaveChangesAsync();
        }
        await fixture.NewService().DeliverAsync(default);
        Assert.Single(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task HonorsDatabaseLeaseAndDiscardsResultsWhenConfigurationChangesDuringQuery()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var stored = await context.ScheduledQueryRules.SingleAsync();
            stored.LeaseId = Guid.NewGuid();
            stored.LeaseUntilUtc = fixture.Clock.Now.UtcDateTime.AddMinutes(1);
            await context.SaveChangesAsync();
        }
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(0, fixture.Query.Calls);
        fixture.Clock.Advance();
        fixture.Query.Response = Result(("DC01", 99));
        fixture.Query.BeforeResult = async () =>
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.ScheduledQueryRules.ExecuteUpdateAsync(update => update
                .SetProperty(rule => rule.Enabled, false).SetProperty(rule => rule.Revision, rule => rule.Revision + 1));
        };
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Null((await fixture.Service.ListAsync()).Single().LeaseId);
    }

    [Fact]
    public async Task DoesNotAlertForSystemsEnteringMaintenanceDuringTheQuery()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Query.Response = Result(("DC01", 99));
        fixture.Query.BeforeResult = async () =>
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.ComputerInventory.Where(item => item.Computer == "DC01")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Maintenance, true));
        };
        await fixture.Service.RunOnceAsync(default);
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task AppliesInventoryFiltersMaintenanceAndWorkspaceRouting()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.Add(new() { SubscriptionId = Guid.NewGuid().ToString(), Computer = "OTHER-WORKSPACE", Domain = "other.example" });
            (await context.ComputerInventory.SingleAsync(item => item.Computer == "DC02")).Maintenance = true;
            await context.SaveChangesAsync();
        }
        fixture.Rule.Domain = "EXAMPLE.COM";
        fixture.Rule.Site = "NORTH";
        fixture.Rule.Role = "DOMAIN CONTROLLER";
        fixture.Rule.Computers = "DC01,DC02";
        fixture.Query.Response = Result(("DC01", 95));
        Assert.Equal("DC01", Assert.Single(Assert.Single((await fixture.Service.PreviewAsync(fixture.Rule)).Workspaces).Targets).Computer);
        Assert.DoesNotContain("OTHER-WORKSPACE", fixture.Query.Kql);
        Assert.DoesNotContain("DC02", fixture.Query.Kql);
    }

    [Fact]
    public async Task SavesTheValidatedSnapshotEvenIfTheEditorChangesDuringPersistence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var kql = fixture.Rule.Kql;
        fixture.Factory.OnCreate = () => fixture.Rule.Kql = "workspace('unvalidated-edit').Perf";
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Factory.OnCreate = null;
        Assert.Equal(kql, (await fixture.Service.ListAsync()).Single().Kql);
    }

    [Fact]
    public async Task RejectsStaleSavesAndRequiresDisablingBeforeDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.Service.SaveAsync(fixture.Rule));
        var stored = (await fixture.Service.ListAsync()).Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteAsync(stored.Id, stored.Revision));
        await fixture.Service.DisableAsync(stored.Id, stored.Revision);
        var disabled = (await fixture.Service.ListAsync()).Single();
        await fixture.Service.DeleteAsync(disabled.Id, disabled.Revision);
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Theory]
    [InlineData("unsafe")]
    [InlineData("window")]
    [InlineData("threshold")]
    [InlineData("interval")]
    public async Task RejectsInvalidConfiguration(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (kind == "unsafe") fixture.Rule.Kql += "\n| union workspace('external').Perf";
        if (kind == "window") fixture.Rule.Kql = "Perf | where Computer in~ (_SelectedSystems)";
        if (kind == "threshold") fixture.Rule.Threshold = double.NaN;
        if (kind == "interval") fixture.Rule.IntervalMinutes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(fixture.Rule));
    }

    [Theory]
    [InlineData("broken json")]
    [InlineData("[]")]
    [InlineData("""{"scheduledQuery":0}""")]
    [InlineData("""{"scheduledQuery":{"ruleId":5}}""")]
    public void DoesNotBreakCategorizationForMalformedPayloads(string json)
    {
        var alert = new AlertRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "scheduled-query:invalid", "", "", "", "", "", "", "", "", null, "", "", "", json);
        Assert.Null(ScheduledQueryAlert.Category(alert));
    }

    [Fact]
    public void MigrationAndProvisioningCoverAllScheduledTablesAndCurrentModel()
    {
        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(local);Database=test;Trusted_Connection=True;TrustServerCertificate=True").Options);
        Assert.False(context.Database.HasPendingModelChanges());
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261027082000_AddDashboardFilterSetting", "20261028121046_AddScheduledQueryAlerts");
        var provision = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        foreach (var name in new[] { "ScheduledQueryRules", "ScheduledQueryTargetStates", "ScheduledQueryNotifications" })
        {
            Assert.Contains($"CREATE TABLE [{name}]", script);
            Assert.Contains($"CREATE TABLE [dbo].[{name}]", provision);
        }
        Assert.Contains("20261028121046_AddScheduledQueryAlerts", provision);
        var upgrade = context.GetService<IMigrator>().GenerateScript(
            "20261028121046_AddScheduledQueryAlerts", "20261028134046_AddScheduledQueryWorkspaces");
        foreach (var column in new[] { "WorkspaceKeysJson", "LastWorkspaceResultsJson", "WorkspaceKey" })
            Assert.Contains($"ADD [{column}]", upgrade);
        Assert.Contains("20261028134046_AddScheduledQueryWorkspaces", provision);
        Assert.Contains("UPDATE [ScheduledQueryTargetStates]", upgrade);
        Assert.Contains("SET [WorkspaceKey] = (SELECT [WorkspaceKey]", provision);
    }

    [Fact]
    public async Task ConfigurationPageExposesOnlyOwnKqlAndDisabledExamples()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        Func<string>? readHtml = null;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            readHtml = root.ToHtmlString;
            return root.ToHtmlString();
        });
        Assert.Contains("Create custom KQL rule", html);
        Assert.Contains("CPU example", html);
        Assert.Contains("Kerberos example", html);
        Assert.Contains("Always On", html);
        Assert.DoesNotContain("Published queries", html);
        Assert.False(ScheduledQueryRule.Example(false).Enabled);
        Assert.False(ScheduledQueryRule.Example(true).Enabled);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = activator.Page!;
        fixture.Query.Response = Result(("DC01", 99));
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            page.GetType().GetMethod("BeginNew", flags)!.Invoke(page, [false]);
            var editing = (ScheduledQueryRule)page.GetType().GetField("editing", flags)!.GetValue(page)!;
            Assert.False(editing.Enabled);
            Assert.Equal(["production"], editing.WorkspaceKeys);
            page.GetType().GetMethod("SetWorkspace", flags)!.Invoke(page, ["workspace-1", true]);
            Assert.Equal(["production", "workspace-1"], editing.WorkspaceKeys);
            await (Task)page.GetType().GetMethod("PreviewAsync", flags)!.Invoke(page, [])!;
            Assert.Null(page.GetType().GetField("error", flags)!.GetValue(page));
            Assert.NotNull(page.GetType().GetField("preview", flags)!.GetValue(page));
            page.GetType().GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            var form = readHtml!();
            Assert.Contains("Test query (no alerts)", form);
            Assert.Contains("<legend>Workspaces</legend>", form);
            Assert.Contains("Workspace workspace-1", form);
            Assert.Equal(2, Assert.IsType<ScheduledQueryEvaluation>(page.GetType().GetField("preview", flags)!.GetValue(page)).Workspaces.Count);
            Assert.Contains("Evaluation window (minutes)", form);
            Assert.Contains("Would alert", form);
            Assert.Contains("No data", form);
            Assert.Contains("Effective KQL for this test", form);
            Assert.Contains(fixture.Options.Workspaces[0].WorkspaceId, form);
            Assert.Contains("API lookback: 10 minutes", form);
            Assert.Contains(fixture.Query.Kql, System.Net.WebUtility.HtmlDecode(form));
            fixture.Query.Response = Result();
            await (Task)page.GetType().GetMethod("PreviewAsync", flags)!.Invoke(page, [])!;
            page.GetType().GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            Assert.Contains(fixture.Query.Kql, System.Net.WebUtility.HtmlDecode(readHtml!()));
            fixture.Query.Failures["production"] = new InvalidOperationException("Simulated preview failure");
            fixture.Query.Responses["workspace-1"] = Result(("DC01", 99));
            await (Task)page.GetType().GetMethod("PreviewAsync", flags)!.Invoke(page, [])!;
            Assert.Null(page.GetType().GetField("error", flags)!.GetValue(page));
            var failed = Assert.IsType<ScheduledQueryEvaluation>(page.GetType().GetField("preview", flags)!.GetValue(page));
            Assert.Contains("Simulated preview failure", failed.Workspaces[0].Error!);
            Assert.Null(failed.Workspaces[1].Error);
            page.GetType().GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            var partialHtml = readHtml!();
            Assert.Contains("Simulated preview failure", partialHtml);
            Assert.Contains("Would alert", partialHtml);
            Assert.Contains(fixture.Query.Kql, System.Net.WebUtility.HtmlDecode(partialHtml));
            fixture.Query.Failures.Clear();
            await (Task)page.GetType().GetMethod("SaveAsync", flags)!.Invoke(page, [])!;
            Assert.Null(page.GetType().GetField("error", flags)!.GetValue(page));
        });
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(["production", "workspace-1"], Assert.Single(await fixture.Service.ListAsync()).WorkspaceKeys);
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public Components.Pages.AdminScheduledQueries? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is Components.Pages.AdminScheduledQueries page) Page = page;
            return component;
        }
    }

    private sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Unsupported test render mode: {renderMode.GetType().Name}");
    }

    private static LogsQueryResult Result(params (string Computer, double? Value)[] rows) =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            tables = new[] { new { name = "PrimaryResult",
                columns = new[] { new { name = "Series", type = "string" }, new { name = "Value", type = "real" } },
                rows = rows.Select(row => new object?[] { row.Computer, row.Value }) } }
        })))!;

    private sealed class Fixture(SqliteConnection connection, Factory factory, CrossTenantLogAnalyticsOptions options) : IAsyncDisposable
    {
        public const string Subscription = "11111111-1111-1111-1111-111111111111";
        public Factory Factory { get; } = factory;
        public FakeClock Clock { get; } = new();
        public FakeQuery Query { get; } = new();
        public CrossTenantLogAnalyticsOptions Options { get; } = options;
        public ScheduledQueryRule Rule { get; private set; } = null!;
        public ScheduledQueryService Service { get; private set; } = null!;

        public ScheduledQueryService NewService()
        {
            var resolver = new TargetWorkspaceResolver(Options);
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            var store = new AlertStore(Factory, new(true, null, new SqlConnectionStringBuilder()), presenter,
                new(presenter), NullLogger<AlertStore>.Instance);
            return new(Factory, Options, resolver, new(Options, new Secret(), resolver, Query), store, Clock,
                NullLogger<ScheduledQueryService>.Instance);
        }

        public static async Task<Fixture> CreateAsync(bool kerberos = false, int workspaceCount = 1)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            var options = new CrossTenantLogAnalyticsOptions
            {
                Enabled = true,
                Workspaces = Enumerable.Range(0, workspaceCount).Select(index => new CrossTenantLogAnalyticsWorkspaceOptions
                {
                    Key = index == 0 ? "production" : $"workspace-{index}",
                    DisplayName = index == 0 ? "Production" : $"Workspace {index}",
                    WorkspaceId = index == 0 ? "22222222-2222-2222-2222-222222222222" : Guid.NewGuid().ToString(),
                    SubscriptionIds = [index == 0 ? Subscription : $"{index + 1:D8}-1111-1111-1111-111111111111"],
                    Domains = [index == 0 ? "example.com" : $"workspace-{index}.example.com"]
                }).ToArray()
            };
            var fixture = new Fixture(connection, factory, options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                    .Replace("N'", "'", StringComparison.Ordinal));
            context.ComputerInventory.AddRange(
                new() { SubscriptionId = Subscription, Computer = "DC01", Domain = "example.com", Site = "North", Role = "Domain Controller" },
                new() { SubscriptionId = Subscription, Computer = "DC02", Domain = "example.com", Site = "North", Role = "Domain Controller" });
            foreach (var workspace in options.Workspaces.Skip(1))
                context.ComputerInventory.Add(new()
                {
                    SubscriptionId = workspace.SubscriptionIds[0], Computer = "DC01", Domain = workspace.Domains[0],
                    Site = "North", Role = "Domain Controller"
                });
            await context.SaveChangesAsync();
            fixture.Rule = ScheduledQueryRule.Example(kerberos);
            fixture.Rule.Enabled = true;
            fixture.Rule.WorkspaceKey = "production";
            fixture.Service = fixture.NewService();
            return fixture;
        }

        public async Task<AlertRecord[]> AlertRecordsAsync()
        {
            await using var context = Factory.CreateDbContext();
            return await context.Alerts.ToArrayAsync();
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public Action? OnCreate { get; set; }
        public AlertDbContext CreateDbContext()
        {
            OnCreate?.Invoke();
            return new(options);
        }
    }
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 28, 12, 8, 59, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan? by = null) => Now += by ?? TimeSpan.FromMinutes(5);
    }
    private sealed class Secret : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) => Task.FromResult("test-secret");
    }
    private sealed class FakeQuery : IAnalysisLogAnalyticsQuery
    {
        public LogsQueryResult Response { get; set; } = Result();
        public Dictionary<string, LogsQueryResult> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Workspace, string Kql, int TimeRange)> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public Func<Task>? BeforeResult { get; set; }
        public string Kql { get; private set; } = "";
        public int Calls { get; private set; }
        public int TimeRange { get; private set; }
        public async Task<LogsQueryResult> QueryAsync(CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId,
            string secret, string kql, int timeRangeMinutes, CancellationToken cancellationToken)
        {
            Calls++;
            Kql = kql;
            TimeRange = timeRangeMinutes;
            Requests.Add((workspace.Key, kql, timeRangeMinutes));
            if (BeforeResult is not null) await BeforeResult();
            if (Failures.TryGetValue(workspace.Key, out var failure)) throw failure;
            if (Failure is not null) throw Failure;
            return Responses.GetValueOrDefault(workspace.Key, Response);
        }
    }
}
