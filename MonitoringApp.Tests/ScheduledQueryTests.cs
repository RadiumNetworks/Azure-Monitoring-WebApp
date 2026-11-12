using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp.Tests;

public sealed class ScheduledQueryTests
{
    [Fact]
    public async Task ReplicationBaselineUsesFullRoutedInventoryIncludingMaintenanceWithoutOtherWorkspacesOrUnroutedSystems()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            (await context.ComputerInventory.SingleAsync(item => item.Computer == "DC02")).Maintenance = true;
            context.ComputerInventory.Add(new() { SubscriptionId = "not-routed", Computer = "OUTSIDE" });
            await context.SaveChangesAsync();
        }
        var rule = ReplicationRule();
        rule.WorkspaceKeys = ["production", "workspace-1"];
        fixture.Query.Response = TargetResult(("UNKNOWN", 7));
        var prepared = new List<ScheduledQueryExecutionPreview>();
        var evaluation = await fixture.Service.PreviewAsync(rule, onPrepared: prepared.Add);
        Assert.All(evaluation.Workspaces, workspace => Assert.Null(workspace.Error));
        Assert.Equal(2, prepared.Count);
        var inventories = prepared.Select(preview => JsonSerializer.Deserialize<string[]>(
            Regex.Match(preview.Query.EffectiveKql, @"let _InventoryComputers = dynamic\((.*?)\);").Groups[1].Value)!).ToArray();
        Assert.Equal(["DC01", "DC01.example.com", "DC02", "DC02.example.com"], inventories[0]);
        Assert.Equal(["DC01", "DC01.workspace-1.example.com"], inventories[1]);
        Assert.DoesNotContain("OUTSIDE", fixture.Query.Kql);
        Assert.All(prepared, preview =>
        {
            Assert.DoesNotContain("column_ifexists", preview.Query.EffectiveKql);
            Assert.DoesNotContain("UsersTruncated", preview.Query.EffectiveKql);
            Assert.DoesNotContain("_SelectedSystems", preview.Query.EffectiveKql);
            Assert.DoesNotContain("_BinSize", preview.Query.EffectiveKql);
            Assert.DoesNotContain("_TimePeriod", preview.Query.EffectiveKql);
            Assert.DoesNotContain("_GroupBy", preview.Query.EffectiveKql);
        });
        Assert.All(evaluation.Workspaces, workspace => Assert.Equal(7, workspace.Values["UNKNOWN"]));
        Assert.All(prepared, preview => Assert.Equal(TimeSpan.FromMinutes(5), preview.WindowEndUtc - preview.WindowStartUtc));
        Assert.Contains("DestDSA", rule.Kql);
        Assert.DoesNotContain("SourceDSA", rule.Kql);
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task MissingReplicationInventoryBaselineFailsExplicitlyWithoutQueryingOrGeneratingAlerts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.Factory.CreateDbContext())
            await context.ComputerInventory.ExecuteDeleteAsync();
        var evaluation = await fixture.Service.PreviewAsync(ReplicationRule());
        Assert.Contains("Inventory baseline", Assert.Single(evaluation.Workspaces).Error);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task ScheduledReplicationAlertRunsEveryFiveMinutesWithIndependentTargetEpisodesAndExactCounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ReplicationRule();
        Assert.False(rule.Enabled);
        Assert.Equal(5, rule.IntervalMinutes);
        Assert.Equal(0, rule.Threshold);
        rule.Enabled = true;
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = TargetResult(("UNKNOWN-A", 7), ("UNKNOWN-B", 23));
        await fixture.Service.RunOnceAsync(default);
        var first = await fixture.AlertRecordsAsync();
        Assert.Equal(2, first.Length);
        Assert.All(first, alert =>
        {
            Assert.Equal("Fired", alert.MonitorCondition);
            Assert.Empty(alert.SubscriptionId);
            var parsed = ParsedAlertFactory.Create(alert);
            Assert.Null(parsed.InventorySubscriptionId);
            Assert.Null(parsed.InventoryComputer);
            var presentation = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")).Parse(alert.RawJson)!;
            Assert.Contains(presentation.Rows, row => row.Title == alert.TargetName);
        });
        Assert.Equal(7, (await fixture.Service.StatesAsync(rule.Id)).Single(state => state.Computer == "UNKNOWN-A").Value);
        Assert.Equal(23, (await fixture.Service.StatesAsync(rule.Id)).Single(state => state.Computer == "UNKNOWN-B").Value);
        Assert.Equal(fixture.Clock.Now.UtcDateTime.AddMinutes(5), Assert.Single(await fixture.Service.ListAsync()).NextRunUtc);
        fixture.Clock.Advance();
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(2, (await fixture.AlertRecordsAsync()).Length);
        fixture.Clock.Advance();
        fixture.Query.Response = TargetResult(("UNKNOWN-B", 11));
        await fixture.Service.RunOnceAsync(default);
        var resolved = Assert.Single(await fixture.AlertRecordsAsync(), item => item.MonitorCondition == "Resolved");
        Assert.Equal(first.Single(alert => alert.TargetName == "UNKNOWN-A").AlertId, resolved.AlertId);
        Assert.Equal(11, (await fixture.Service.StatesAsync(rule.Id)).Single(state => state.Computer == "UNKNOWN-B").Value);
        fixture.Clock.Advance();
        fixture.Query.Response = TargetResult();
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(4, (await fixture.AlertRecordsAsync()).Length);
        Assert.All(await fixture.Service.StatesAsync(rule.Id), state => Assert.Null(state.ActiveAlertId));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await check.ComputerInventory.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplicationQueryFailureNeverResolvesExistingTargetsOrConvertsValuesToZero(bool malformed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ReplicationRule();
        rule.Enabled = true;
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = TargetResult(("UNKNOWN", 42));
        await fixture.Service.RunOnceAsync(default);
        fixture.Clock.Advance();
        if (malformed) fixture.Query.Response = Result(("Workspace", 0));
        else fixture.Query.Failure = new InvalidOperationException("Query failed");
        await fixture.Service.RunOnceAsync(default);
        var state = Assert.Single(await fixture.Service.StatesAsync(rule.Id));
        Assert.Equal("Error", state.Status);
        Assert.Null(state.Value);
        Assert.NotNull(state.ActiveAlertId);
        Assert.Single(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task ReplicationUsesSeparateWorkspaceTargetKeysAndRechecksNewInventoryEntriesBeforeCommit()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        var rule = ReplicationRule();
        rule.WorkspaceKeys = ["production", "workspace-1"];
        rule.Enabled = true;
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = TargetResult(("UNKNOWN", 19));
        await fixture.Service.RunOnceAsync(default);
        var first = await fixture.Service.StatesAsync(rule.Id);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.Select(state => state.SubscriptionId).Distinct().Count());
        Assert.All(first, state => Assert.Equal(19, state.Value));
        fixture.Clock.Advance();
        fixture.Query.BeforeResult = async () =>
        {
            await using var context = fixture.Factory.CreateDbContext();
            if (!await context.ComputerInventory.AnyAsync(entry => entry.Computer == "UNKNOWN"))
            {
                context.ComputerInventory.Add(new() { SubscriptionId = Fixture.Subscription, Computer = "UNKNOWN" });
                await context.SaveChangesAsync();
            }
        };
        await fixture.Service.RunOnceAsync(default);
        var final = await fixture.Service.StatesAsync(rule.Id);
        Assert.Null(final.Single(state => state.WorkspaceKey == "production").ActiveAlertId);
        Assert.NotNull(final.Single(state => state.WorkspaceKey == "workspace-1").ActiveAlertId);
        Assert.Single(await fixture.AlertRecordsAsync(), alert => alert.MonitorCondition == "Resolved");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task ReplicationResultLimitsNeverAcceptPartialTargetSets(int count)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Query.Response = TargetResult(Enumerable.Range(0, count).Select(index => ($"UNKNOWN-{index}", 3L)).ToArray());
        var result = Assert.Single((await fixture.Service.PreviewAsync(ReplicationRule())).Workspaces);
        if (count > 1000)
        {
            Assert.NotNull(result.Error);
            Assert.Empty(result.Targets);
            Assert.Empty(result.Values);
        }
        else
        {
            Assert.Null(result.Error);
            Assert.Equal(count, result.Targets.Count);
            Assert.All(result.Values.Values, value => Assert.Equal(3, value));
        }
    }

    [Theory]
    [InlineData("<", 5)]
    [InlineData("<=", 5)]
    [InlineData(">=", 0)]
    public async Task MissingUnknownTargetsRecoverEvenWhenTheThresholdWouldMatchZero(string comparison, double threshold)
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ReplicationRule();
        rule.Enabled = true;
        rule.Comparison = comparison;
        rule.Threshold = threshold;
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = TargetResult(("UNKNOWN", 3));
        await fixture.Service.RunOnceAsync(default);
        var fired = Assert.Single(await fixture.AlertRecordsAsync());
        fixture.Clock.Advance();
        fixture.Query.Response = TargetResult();
        await fixture.Service.RunOnceAsync(default);
        var state = Assert.Single(await fixture.Service.StatesAsync(rule.Id));
        Assert.Equal("Normal", state.Status);
        Assert.Equal(0, state.Value);
        Assert.Null(state.ActiveAlertId);
        var recovery = Assert.Single(await fixture.AlertRecordsAsync(), alert => alert.MonitorCondition == "Resolved");
        Assert.Equal(fired.AlertId, recovery.AlertId);
        fixture.Clock.Advance();
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(2, (await fixture.AlertRecordsAsync()).Length);
    }

    [Theory]
    [InlineData(null, "42")]
    [InlineData("", "42")]
    [InlineData("<EMPTY_VALUE>", "42")]
    [InlineData("INVALID\nTARGET", "42")]
    [InlineData("UNKNOWN-DC", null)]
    [InlineData("UNKNOWN-DC", "0")]
    [InlineData("UNKNOWN-DC", "-1")]
    [InlineData("UNKNOWN-DC", "1.5")]
    [InlineData("UNKNOWN-DC", "not a count")]
    [InlineData("UNKNOWN-DC", "9007199254740992")]
    [InlineData("DC01.", "42")]
    public void UnknownTargetResultRejectsInvalidNamesCountsAndKnownTargets(string? target, string? count)
    {
        var table = new AnalysisQueryTable("production",
            [new("Target", "string"), new("count_", "long")], [[target, count]]);
        Assert.Throws<InvalidOperationException>(() =>
            ScheduledQueryResultNormalizer.NormalizeUnknownTargets(table, "production", ["DC01"]));
    }

    [Fact]
    public void UnknownTargetResultRejectsDuplicateAndOversizedTargetsWithoutLosingCountPrecision()
    {
        var columns = new AnalysisQueryColumn[] { new("Target", "string"), new("count_", "long") };
        foreach (var rows in new IReadOnlyList<string?>[][]
        {
            [["UNKNOWN-DC", "42"], ["unknown-dc.", "7"]],
            [[new string('A', 257), "42"]]
        })
            Assert.Throws<InvalidOperationException>(() => ScheduledQueryResultNormalizer.NormalizeUnknownTargets(
                new("production", columns, rows), "production", ["DC01"]));
        var normalized = ScheduledQueryResultNormalizer.NormalizeUnknownTargets(
            new("production", columns, [["UNKNOWN-DC", "9007199254740991"]]), "production", ["DC01"]);
        Assert.Equal(9_007_199_254_740_991d, normalized.Values["UNKNOWN-DC"]);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task UnknownTargetPageShowsNativeCountInPreviewAndStateWithoutSyntheticSubscription(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        await page.InvokeAsync("BeginTemplate", "replication-unknown-targets.json");
        var rule = Assert.IsType<ScheduledQueryRule>(page.Field("editing"));
        Assert.Equal(ScheduledQueryScopes.UnknownInventoryTargets, rule.Scope);
        fixture.Query.Response = TargetResult(("UNKNOWN-DC", 42));
        await page.InvokeAsync("PreviewAsync");
        var preview = await page.HtmlAsync();
        Assert.Contains(page.Text["AdminScheduledQueries.UnknownInventoryTargets"], preview);
        Assert.Contains("<th>count_</th>", preview);
        Assert.Contains("<td>42</td>", preview);
        Assert.DoesNotContain("column_ifexists", preview);
        rule.Enabled = true;
        await page.InvokeAsync("SaveAsync");
        await fixture.Service.RunOnceAsync(default);
        await page.InvokeAsync("EditAsync", Assert.Single(await fixture.Service.ListAsync()));
        var saved = await page.HtmlAsync();
        Assert.Contains("UNKNOWN-DC", saved);
        Assert.Contains("<th>count_</th>", saved);
        Assert.Contains("<td>42</td>", saved);
        Assert.DoesNotContain("query-target:", saved);
        using var payload = JsonDocument.Parse(Assert.Single(await fixture.AlertRecordsAsync()).RawJson);
        var result = payload.RootElement.GetProperty("queryResult");
        Assert.Equal("ScheduledKqlInventoryTarget", result.GetProperty("type").GetString());
        Assert.Contains(result.GetProperty("columns").EnumerateArray(), column => column.GetProperty("name").GetString() == "count_");
    }

    private static ScheduledQueryRule ReplicationRule()
    {
        var rule = ScheduledQueryTemplates.Load(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "Scheduled", "replication-unknown-targets.json"));
        rule.WorkspaceKeys = ["production"];
        return rule;
    }

    [Theory]
    [InlineData(false, AnalysisQueryScopes.SelectedSystems)]
    [InlineData(false, AnalysisQueryScopes.Workspace)]
    [InlineData(false, ScheduledQueryScopes.UnknownInventoryTargets)]
    [InlineData(true, AnalysisQueryScopes.SelectedSystems)]
    [InlineData(true, AnalysisQueryScopes.Workspace)]
    [InlineData(true, ScheduledQueryScopes.UnknownInventoryTargets)]
    public async Task ExternalKqlNeverKeepsDatabaseContextsOpen(bool scheduled, string scope)
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["production", "workspace-1"];
        fixture.Rule.Scope = scope;
        var workspaceScope = scope == AnalysisQueryScopes.Workspace;
        if (workspaceScope)
        {
            fixture.Rule.Scope = AnalysisQueryScopes.Workspace;
            fixture.Rule.Kql = ScheduledQueryRule.EventExample(4624).Kql;
        }
        if (scope == ScheduledQueryScopes.UnknownInventoryTargets)
        {
            fixture.Rule.Kql = ReplicationRule().Kql;
            fixture.Query.Response = TargetResult(("UNKNOWN-DC", 99));
        }
        else fixture.Query.Response = Result((workspaceScope ? "Workspace" : "DC01", 99));
        var contexts = new List<AlertDbContext>();
        fixture.Factory.OnCreated = contexts.Add;
        void AssertDisposedContexts()
        {
            Assert.NotEmpty(contexts);
            Assert.All(contexts, context => Assert.Throws<ObjectDisposedException>(() => context.Database));
        }
        fixture.Query.BeforeResult = () =>
        {
            AssertDisposedContexts();
            return Task.CompletedTask;
        };
        if (scheduled)
        {
            await fixture.Service.SaveAsync(fixture.Rule);
            await fixture.Service.RunOnceAsync(default);
            Assert.Equal(2, (await fixture.AlertRecordsAsync()).Length);
            Assert.Equal(0, (await fixture.Service.DeliveryStatusAsync()).Pending);
        }
        else
        {
            var prepared = 0;
            var preview = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: _ =>
            {
                AssertDisposedContexts();
                prepared++;
            });
            Assert.Equal(2, prepared);
            Assert.All(preview.Workspaces, workspace => Assert.Null(workspace.Error));
            Assert.Empty(await fixture.AlertRecordsAsync());
            Assert.Empty(await fixture.Service.StatesAsync(fixture.Rule.Id));
        }
        Assert.Equal(2, fixture.Query.Calls);
        AssertDisposedContexts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxFailureRollsBackTargetAndScheduleAndRetryKeepsOneEpisode(bool recovery)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Rule.Computers = "DC01";
        fixture.Query.Response = Result(("DC01", 99));
        await fixture.Service.SaveAsync(fixture.Rule);
        if (recovery)
        {
            await fixture.Service.RunOnceAsync(default);
            fixture.Clock.Advance();
            fixture.Query.Response = Result(("DC01", 10));
        }
        var beforeRule = Assert.Single(await fixture.Service.ListAsync());
        var beforeStates = await fixture.Service.StatesAsync(fixture.Rule.Id);
        var beforeAlerts = await fixture.AlertRecordsAsync();
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailScheduledOutbox BEFORE INSERT ON ScheduledQueryNotifications BEGIN SELECT RAISE(ABORT, 'outbox failed'); END;");

        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(JsonSerializer.Serialize(beforeRule),
            JsonSerializer.Serialize(Assert.Single(await fixture.Service.ListAsync())));
        Assert.Equal(JsonSerializer.Serialize(beforeStates),
            JsonSerializer.Serialize(await fixture.Service.StatesAsync(fixture.Rule.Id)));
        Assert.Equal(beforeAlerts.Length, (await fixture.AlertRecordsAsync()).Length);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            Assert.Equal(recovery ? 1 : 0, await context.ScheduledQueryNotifications.CountAsync());
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailScheduledOutbox;");
        }

        await fixture.NewService().RunOnceAsync(default);
        var state = Assert.Single(await fixture.Service.StatesAsync(fixture.Rule.Id));
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(recovery ? "Normal" : "Alerting", state.Status);
        Assert.Equal(recovery ? 10d : 99d, state.Value);
        Assert.Equal(fixture.Clock.Now.UtcDateTime, stored.LastRunUtc);
        Assert.True(stored.NextRunUtc > fixture.Clock.Now.UtcDateTime);
        Assert.Null(stored.LeaseId);
        var events = await fixture.AlertRecordsAsync();
        Assert.Equal(recovery ? 2 : 1, events.Length);
        var fired = Assert.Single(events, alert => alert.MonitorCondition == "Fired");
        if (recovery)
        {
            Assert.Null(state.ActiveAlertId);
            Assert.Null(state.FiredUtc);
            Assert.Equal(fired.AlertId, Assert.Single(events, alert => alert.MonitorCondition == "Resolved").AlertId);
        }
        else
            Assert.Equal(fired.AlertId, state.ActiveAlertId);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var notifications = await context.ScheduledQueryNotifications.ToArrayAsync();
            Assert.Equal(events.Length, notifications.Length);
            Assert.All(notifications, notification => Assert.NotNull(notification.DeliveredUtc));
        }
        await fixture.NewService().RunOnceAsync(default);
        Assert.Equal(events.Length, (await fixture.AlertRecordsAsync()).Length);
    }

    [Fact]
    public async Task LosingLeaseBetweenWorkspacesDiscardsResultsAndNeverReleasesReplacementLease()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["production", "workspace-1"];
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Query.Response = Result(("DC01", 99));
        var replacement = Guid.NewGuid();
        var until = fixture.Clock.Now.UtcDateTime.AddHours(1);
        fixture.Query.BeforeResult = async () =>
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.ScheduledQueryRules.ExecuteUpdateAsync(update => update
                .SetProperty(rule => rule.LeaseId, replacement)
                .SetProperty(rule => rule.LeaseUntilUtc, until));
        };

        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Empty(await fixture.Service.StatesAsync(fixture.Rule.Id));
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(replacement, stored.LeaseId);
        Assert.Equal(until, stored.LeaseUntilUtc);
        Assert.Null(stored.LastRunUtc);
        Assert.Equal(fixture.Clock.Now.UtcDateTime, stored.NextRunUtc);
        await using (var context = fixture.Factory.CreateDbContext())
            Assert.Empty(await context.ScheduledQueryNotifications.ToArrayAsync());
        await fixture.NewService().RunOnceAsync(default);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Equal(replacement, Assert.Single(await fixture.Service.ListAsync()).LeaseId);
    }

    [Theory]
    [InlineData(4624, 5, "Sev2")]
    [InlineData(4768, 0, "Sev2")]
    [InlineData(4769, 0, "Sev2")]
    [InlineData(5136, 3, "Sev2")]
    [InlineData(4724, 0, "Sev2")]
    [InlineData(4736, 0, "Sev2")]
    [InlineData(4794, 1, "Sev1")]
    public void EventExamplesAreDisabledWorkspaceCountsWithExactXmlPositions(int eventId, int userIndex, string severity)
    {
        var rule = ScheduledQueryRule.EventExample(eventId);
        Assert.False(rule.Enabled);
        Assert.Equal(AnalysisQueryScopes.Workspace, rule.Scope);
        Assert.Equal(">", rule.Comparison);
        Assert.Equal(0, rule.Threshold);
        Assert.Equal(severity, rule.Severity);
        Assert.Contains($"| where EventID == {eventId}", rule.Kql);
        Assert.Contains($"EventId = {eventId}", rule.Kql);
        Assert.Contains("parse_xml(EventData)", rule.Kql);
        Assert.Contains($"eventdata.DataItem.EventData.Data[{userIndex}][\"#text\"]", rule.Kql);
        if (eventId is 4724 or 4736)
        {
            Assert.Contains("Actor = tostring(eventdata.DataItem.EventData.Data[4][\"#text\"])", rule.Kql);
            Assert.Contains("Account = tostring(eventdata.DataItem.EventData.Data[0][\"#text\"])", rule.Kql);
            Assert.Contains("strcat(Actor, \" changes \", Account)", rule.Kql);
        }
        Assert.Contains("EventLog == \"Security\"", rule.Kql);
        Assert.Contains("_WindowStart", rule.Kql);
        Assert.Contains("_WindowEnd", rule.Kql);
        Assert.Contains("Series = \"Workspace\"", rule.Kql);
        Assert.Contains("iff(_Observations > 0", rule.Kql);
        Assert.Contains("make_set_if(User, isnotempty(User), 101)", rule.Kql);
        Assert.Contains("UnparsedEvents = countif(isempty(User))", rule.Kql);
        Assert.Contains("UsersTruncated = array_length(Users) > 100", rule.Kql);
        Assert.Contains("real(null)", rule.Kql);
        Assert.DoesNotContain("_SelectedSystems", rule.Kql);
        Assert.DoesNotContain("4678", rule.Kql);
        Assert.DoesNotContain("4679", rule.Kql);
    }

    [Fact]
    public async Task WorkspaceEventAccountsReachPreviewOutboxAndRenderedAlertAsTransitionSnapshots()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ScheduledQueryRule.EventExample(4724);
        rule.Enabled = true;
        rule.WorkspaceKeys = ["production"];
        await fixture.Service.SaveAsync(rule);
        var users = new[] { "alice", "operator changes <script>target</script> & user" };
        fixture.Query.Response = EventResult(4724, 4, JsonSerializer.Serialize(users), unparsed: 2);

        var preview = Assert.Single((await fixture.Service.PreviewAsync(rule)).Workspaces);
        Assert.Null(preview.Error);
        var details = preview.EventDetails!["Workspace"];
        Assert.Equal(users, details.Users);
        Assert.Equal(4724, details.EventId);
        Assert.Equal(2, details.UnparsedEvents);
        Assert.DoesNotContain("column_ifexists(\"Users\"", fixture.Query.Kql);
        Assert.Empty(await fixture.AlertRecordsAsync());

        await fixture.Service.RunOnceAsync(default);
        var alert = Assert.Single(await fixture.AlertRecordsAsync());
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var notification = Assert.Single(await context.ScheduledQueryNotifications.ToArrayAsync());
            Assert.NotNull(notification.DeliveredUtc);
            using var stored = JsonDocument.Parse(alert.RawJson);
            using var queued = JsonDocument.Parse(notification.PayloadJson);
            Assert.True(JsonElement.DeepEquals(stored.RootElement, queued.RootElement));
        }
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var presentation = Assert.IsType<QueryResultPresentation>(presenter.Parse(alert.RawJson));
        var facts = Assert.Single(presentation.Rows).Facts;
        Assert.Contains(facts, fact => fact.Label == "Event ID" && fact.Value == "4724");
        Assert.Contains(facts, fact => fact.Value == string.Join("; ", users));
        Assert.Contains(facts, fact => fact.Value.Contains("2 event(s) without usable account fields"));
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<Components.QueryResultPresentationView>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Presentation"] = presentation }));
            return root.ToHtmlString();
        });
        Assert.Contains("alice", html);
        Assert.Contains("operator changes &lt;script&gt;target&lt;/script&gt; &amp; user", html);
        Assert.DoesNotContain("<script>", html);

        fixture.Clock.Advance();
        fixture.Query.Response = EventResult(4724, 1, "[\"later-account\"]");
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(alert.RawJson, Assert.Single(await fixture.AlertRecordsAsync()).RawJson);
        fixture.Clock.Advance();
        fixture.Query.Response = EventResult(4724, 0, "[]");
        await fixture.Service.RunOnceAsync(default);
        Assert.Empty(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
        var resolved = (await fixture.AlertRecordsAsync()).Single(item => item.MonitorCondition == "Resolved");
        Assert.Equal(alert.AlertId, resolved.AlertId);
        Assert.Contains(presenter.Parse(resolved.RawJson)!.Rows.Single().Facts,
            fact => fact.Label == "Accounts / changes" && fact.Value == "No accounts extracted");
    }

    [Fact]
    public async Task EventRulesKeepAccountsSeparateAcrossWorkspacesAndEventIdsWithoutInventory()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        await using (var context = fixture.Factory.CreateDbContext())
            await context.ComputerInventory.ExecuteDeleteAsync();
        foreach (var eventId in new[] { 4624, 4769 })
        {
            var rule = ScheduledQueryRule.EventExample(eventId);
            rule.Enabled = true;
            rule.WorkspaceKeys = ["production", "workspace-1"];
            await fixture.Service.SaveAsync(rule);
        }
        fixture.Query.BeforeResult = () =>
        {
            var eventId = fixture.Query.Kql.Contains("| where EventID == 4624") ? 4624 : 4769;
            fixture.Query.Responses["production"] = EventResult(eventId, 1, "[\"production-user\"]");
            fixture.Query.Responses["workspace-1"] = EventResult(eventId, 1, "[\"other-user\"]");
            return Task.CompletedTask;
        };
        await fixture.Service.RunOnceAsync(default);
        var alerts = await fixture.AlertRecordsAsync();
        Assert.Equal(4, alerts.Length);
        Assert.Equal(4, alerts.Select(alert => alert.AlertId).Distinct().Count());
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        Assert.All(alerts, alert =>
        {
            var facts = presenter.Parse(alert.RawJson)!.Rows.Single().Facts;
            Assert.Contains(facts, fact => fact.Label == "Accounts / changes" &&
                fact.Value == (alert.TargetName == "Workspace: production" ? "production-user" : "other-user"));
        });
        Assert.Equal(2, alerts.Count(alert => presenter.Parse(alert.RawJson)!.Rows.Single().Facts
            .Any(fact => fact.Label == "Event ID" && fact.Value == "4624")));
        await using var finalContext = fixture.Factory.CreateDbContext();
        Assert.Empty(await finalContext.ComputerInventory.ToArrayAsync());
    }

    [Theory]
    [InlineData("not JSON", 4624, 0)]
    [InlineData("null", 4624, 0)]
    [InlineData("[null]", 4624, 0)]
    [InlineData("[\" \"]", 4624, 0)]
    [InlineData("[1]", 4624, 0)]
    [InlineData("[]", 0, 0)]
    [InlineData("[]", 4624, -1)]
    public async Task InvalidEventDetailsReportErrorWithoutResolvingAlert(string users, int eventId, long unparsed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ScheduledQueryRule.EventExample(4624);
        rule.Enabled = true;
        rule.WorkspaceKeys = ["production"];
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = EventResult(4624, 1, "[\"alice\"]");
        await fixture.Service.RunOnceAsync(default);
        fixture.Clock.Advance();
        fixture.Query.Response = EventResult(eventId, 0, users, unparsed);
        var preview = Assert.Single((await fixture.Service.PreviewAsync(rule)).Workspaces);
        Assert.NotEmpty(preview.Error!);
        Assert.Empty(preview.Values);
        Assert.Empty(preview.EventDetails!);
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal("Error", Assert.Single(await fixture.Service.ListAsync()).LastStatus);
        Assert.Single(await fixture.AlertRecordsAsync());
        Assert.Single(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
    }

    [Fact]
    public async Task AccountBoundsAreEnforcedAndTruncationIsVisibleWithoutLosingEventCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ScheduledQueryRule.EventExample(4624);
        rule.WorkspaceKeys = ["production"];
        var users = Enumerable.Range(1, 100).Select(index => $"user-{index}").ToArray();
        fixture.Query.Response = EventResult(4624, 200, JsonSerializer.Serialize(users), unparsed: 10, truncated: true);
        var preview = Assert.Single((await fixture.Service.PreviewAsync(rule)).Workspaces);
        Assert.Null(preview.Error);
        Assert.Equal(200, preview.Values["Workspace"]);
        var details = preview.EventDetails!["Workspace"];
        Assert.Equal(100, details.Users.Count);
        Assert.Contains("more accounts exist", details.Warnings);
        Assert.Contains("10 event(s)", details.Warnings);
        foreach (var invalid in new[] { users.Append("user-101").ToArray(), new[] { new string('x', 1025) } })
        {
            fixture.Query.Response = EventResult(4624, 200, JsonSerializer.Serialize(invalid));
            Assert.Contains("at most", Assert.Single((await fixture.Service.PreviewAsync(rule)).Workspaces).Error!);
        }
        fixture.Query.Response = EventResult(4624, 3, "[]", unparsed: 3);
        preview = Assert.Single((await fixture.Service.PreviewAsync(rule)).Workspaces);
        Assert.Null(preview.Error);
        Assert.Equal(3, preview.Values["Workspace"]);
        Assert.Contains("3 event(s)", preview.EventDetails!["Workspace"].Warnings);
        fixture.Query.Response = EventResult(4624, null, "[]");
        preview = Assert.Single((await fixture.Service.PreviewAsync(rule)).Workspaces);
        Assert.Null(preview.Error);
        Assert.Null(preview.Values["Workspace"]);
    }

    [Fact]
    public async Task WorkspacePreviewRunsWithoutInventoryAndDoesNotEmitAlerts()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        await using (var context = fixture.Factory.CreateDbContext())
            await context.ComputerInventory.ExecuteDeleteAsync();
        var rule = ScheduledQueryRule.EventExample(4624);
        rule.WorkspaceKeys = ["production", "workspace-1"];
        fixture.Query.Responses["production"] = Result(("Workspace", 3));
        fixture.Query.Responses["workspace-1"] = Result(("Workspace", 7));
        var prepared = new List<ScheduledQueryExecutionPreview>();
        var evaluation = await fixture.Service.PreviewAsync(rule, onPrepared: prepared.Add);
        Assert.Equal(2, fixture.Query.Calls);
        Assert.Equal(2, prepared.Count);
        Assert.All(prepared, preview => Assert.Empty(preview.Query.Systems));
        Assert.All(evaluation.Workspaces, workspace =>
        {
            Assert.Null(workspace.Error);
            var target = Assert.Single(workspace.Targets);
            Assert.Empty(target.Computer);
            Assert.Empty(target.SubscriptionId);
            Assert.Equal("Workspace", target.Series);
        });
        Assert.Equal(3, evaluation.Workspaces[0].Values["Workspace"]);
        Assert.Equal(7, evaluation.Workspaces[1].Values["Workspace"]);
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task WorkspaceAlertsStayIndependentAndNeverLearnInventory()
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        await using (var context = fixture.Factory.CreateDbContext())
            await context.ComputerInventory.ExecuteDeleteAsync();
        var rule = ScheduledQueryRule.EventExample(4794);
        rule.Enabled = true;
        rule.WorkspaceKeys = ["production", "workspace-1"];
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = Result(("Workspace", 1));
        await fixture.Service.RunOnceAsync(default);
        var fired = await fixture.AlertRecordsAsync();
        Assert.Equal(2, fired.Length);
        Assert.Equal(2, fired.Select(alert => alert.AlertId).Distinct().Count());
        Assert.All(fired, alert =>
        {
            Assert.Equal("Fired", alert.MonitorCondition);
            Assert.Empty(alert.SubscriptionId);
            Assert.StartsWith("Workspace: ", alert.TargetName);
            Assert.True(ScheduledQueryAlert.IsWorkspace(alert));
            Assert.Equal("DSRM Password Reset", ScheduledQueryAlert.Category(alert)!.Value.Category);
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            Assert.NotNull(presenter.Parse(alert.RawJson));
        });
        await using (var context = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(await context.ComputerInventory.ToArrayAsync());
            Assert.All(await context.ParsedAlerts.ToArrayAsync(), parsed =>
            {
                Assert.Null(parsed.InventoryComputer);
                Assert.Null(parsed.InventorySubscriptionId);
            });
        }
        Assert.Empty(await fixture.Store.GetAlertGraphRecordsAsync(fixture.Clock.Now.AddDays(-1)));
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(2, (await fixture.AlertRecordsAsync()).Length);
        Assert.Equal(2, fixture.Query.Calls);
        fixture.Clock.Advance();
        fixture.Query.Failures["production"] = new InvalidOperationException("Workspace temporarily unavailable");
        fixture.Query.Responses["workspace-1"] = Result(("Workspace", 0));
        await fixture.Service.RunOnceAsync(default);
        var alerts = await fixture.AlertRecordsAsync();
        Assert.Equal(3, alerts.Length);
        Assert.Equal("Workspace: workspace-1", alerts.Single(alert => alert.MonitorCondition == "Resolved").TargetName);
        Assert.Equal("Workspace: production", Assert.Single(AlertLifecycle.GetActiveAlerts(alerts)).TargetName);
        Assert.StartsWith("Partial error", Assert.Single(await fixture.Service.ListAsync()).LastStatus);
        fixture.Clock.Advance();
        fixture.Query.Failures.Clear();
        fixture.Query.Responses["production"] = Result(("Workspace", null));
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(3, (await fixture.AlertRecordsAsync()).Length);
        Assert.Equal("No data", (await fixture.Service.StatesAsync(rule.Id)).Single(state => state.WorkspaceKey == "production").Status);
        fixture.Clock.Advance();
        fixture.Query.Responses["production"] = Result(("Workspace", 0));
        await fixture.Service.RunOnceAsync(default);
        Assert.Empty(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
        fixture.Clock.Advance();
        fixture.Query.Responses["production"] = Result(("Workspace", 2));
        await fixture.Service.RunOnceAsync(default);
        var next = Assert.Single(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
        Assert.DoesNotContain(fired, alert => alert.AlertId == next.AlertId);
        await using var finalContext = fixture.Factory.CreateDbContext();
        Assert.Empty(await finalContext.ComputerInventory.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceMissingMeasurementsNeverBecomeZero(bool nullRow)
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ScheduledQueryRule.EventExample(4624);
        rule.Enabled = true;
        rule.WorkspaceKeys = ["production"];
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = nullRow ? Result(("Workspace", null)) : Result();
        await fixture.Service.RunOnceAsync(default);
        var state = Assert.Single(await fixture.Service.StatesAsync(rule.Id));
        Assert.Equal("No data", state.Status);
        Assert.Null(state.Value);
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceRulesRejectUnknownAndDuplicateSeries(bool duplicate)
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ScheduledQueryRule.EventExample(5136);
        rule.Enabled = true;
        rule.WorkspaceKeys = ["production"];
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = duplicate ? Result(("Workspace", 1), ("Workspace", 2)) : Result(("DC01", 1));
        await fixture.Service.RunOnceAsync(default);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Equal("Error", Assert.Single(await fixture.Service.ListAsync()).LastStatus);
        Assert.Contains("Series = Workspace", Assert.Single(await fixture.Service.ListAsync()).LastError);
    }

    [Fact]
    public async Task WorkspaceValidationAndScopeChangesPreserveActiveAlerts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rule = ScheduledQueryRule.EventExample(5136);
        rule.WorkspaceKeys = ["production"];
        rule.Domain = "example.com";
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.SaveAsync(rule));
        rule.Domain = "";
        var kql = rule.Kql;
        rule.Kql += "\n| where Computer in~ (_SelectedSystems)";
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.SaveAsync(rule));
        rule.Kql = kql;
        rule.Enabled = true;
        await fixture.Service.SaveAsync(rule);
        fixture.Query.Response = Result(("Workspace", 1));
        await fixture.Service.RunOnceAsync(default);
        var stored = Assert.Single(await fixture.Service.ListAsync());
        stored.Scope = AnalysisQueryScopes.SelectedSystems;
        stored.Kql = ScheduledQueryRule.Example(false).Kql;
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.SaveAsync(stored));
        Assert.Equal(AnalysisQueryScopes.Workspace, Assert.Single(await fixture.Service.ListAsync()).Scope);
        Assert.Single(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
    }

    [Fact]
    public async Task ScopeChangesRequirePreviouslyDisabledRulesAndNoActiveAlerts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        var stored = Assert.Single(await fixture.Service.ListAsync());
        stored.Enabled = false;
        stored.Scope = AnalysisQueryScopes.Workspace;
        stored.Kql = ScheduledQueryRule.EventExample(5136).Kql;
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.SaveAsync(stored));
        await fixture.Service.DisableAsync(stored.Id, stored.Revision);
        stored = Assert.Single(await fixture.Service.ListAsync());
        stored.Scope = AnalysisQueryScopes.Workspace;
        stored.Kql = ScheduledQueryRule.EventExample(5136).Kql;
        await fixture.Service.SaveAsync(stored);
        Assert.Equal(AnalysisQueryScopes.Workspace, Assert.Single(await fixture.Service.ListAsync()).Scope);
        Assert.False(Assert.Single(await fixture.Service.ListAsync()).Enabled);
    }

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
    [InlineData(1, 0, 5, "2026-10-28T12:07:00.0000000Z", "2026-10-28T12:08:00.0000000Z", "en-US")]
    [InlineData(5, 2, 10, "2026-10-28T12:01:00.0000000Z", "2026-10-28T12:06:00.0000000Z", "en-US")]
    [InlineData(1440, 60, 1503, "2026-10-27T11:08:00.0000000Z", "2026-10-28T11:08:00.0000000Z", "en-US")]
    [InlineData(1, 0, 5, "2026-10-28T12:07:00.0000000Z", "2026-10-28T12:08:00.0000000Z", "de-DE")]
    [InlineData(5, 2, 10, "2026-10-28T12:01:00.0000000Z", "2026-10-28T12:06:00.0000000Z", "de-DE")]
    [InlineData(1440, 60, 1503, "2026-10-27T11:08:00.0000000Z", "2026-10-28T11:08:00.0000000Z", "de-DE")]
    public async Task ScheduledProfilePreservesItsExactWrapperWindowAndApiLookback(
        int windowMinutes, int delayMinutes, int lookback, string start, string end, string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var fixture = await Fixture.CreateAsync();
        fixture.Rule.WindowMinutes = windowMinutes;
        fixture.Rule.IngestionDelayMinutes = delayMinutes;
        ScheduledQueryExecutionPreview? prepared = null;
        var expected = string.Join("\n",
            "set truncationmaxsize=1048576;",
            "let _SelectedSystems = dynamic([\"DC01\",\"DC02\"]);",
            $"let _WindowStart = datetime({start});",
            $"let _WindowEnd = datetime({end});",
            "let _ScheduledQueryResult = () {",
            fixture.Rule.Kql.ReplaceLineEndings("\n"),
            "};",
            "_ScheduledQueryResult()",
            "| take 1001");

        var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: value => prepared = value);

        Assert.NotNull(prepared);
        Assert.Equal(expected, fixture.Query.Kql.ReplaceLineEndings("\n"));
        Assert.Equal(fixture.Query.Kql, prepared.Query.EffectiveKql);
        Assert.Equal(lookback, fixture.Query.TimeRange);
        Assert.Equal(lookback, prepared.ApiTimeRangeMinutes);
        Assert.Equal(start, prepared.WindowStartUtc.ToString("O"));
        Assert.Equal(end, prepared.WindowEndUtc.ToString("O"));
        Assert.Equal(prepared.WindowEndUtc, result.WindowEndUtc);
        Assert.Null(Assert.Single(result.Workspaces).Error);
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    [Fact]
    public async Task PreparedPreviewCannotMutateTheExecutedScheduledSystems()
    {
        await using var fixture = await Fixture.CreateAsync();
        var originalKql = fixture.Rule.Kql;
        ScheduledQueryExecutionPreview? prepared = null;

        var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: preview =>
        {
            prepared = preview;
            var systems = Assert.IsAssignableFrom<IList<string>>(preview.Query.Systems);
            Assert.Throws<NotSupportedException>(() => systems[0] = "changed");
            fixture.Rule.Kql = "print Value = 999";
        });

        Assert.NotNull(prepared);
        Assert.Null(Assert.Single(result.Workspaces).Error);
        Assert.Equal(prepared.Query.EffectiveKql, fixture.Query.Kql);
        Assert.Contains(originalKql, fixture.Query.Kql);
        Assert.DoesNotContain("changed", fixture.Query.Kql);
        Assert.DoesNotContain("999", fixture.Query.Kql);
        Assert.Empty(await fixture.AlertRecordsAsync());
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
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.SaveAsync(saved));
        saved.WorkspaceKeys = ["removed-workspace"];
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.SaveAsync(saved));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task ScheduledTargetBoundaryExecutesExactlyOneThousandAndRejectsTheSentinel(int targetCount)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.ComputerInventory.AddRange(Enumerable.Range(3, targetCount - 2).Select(index =>
                new ComputerInventoryEntry
                {
                    SubscriptionId = Fixture.Subscription, Computer = $"DC{index:D4}", Domain = "example.com"
                }));
            await context.SaveChangesAsync();
        }
        var previews = new List<ScheduledQueryExecutionPreview>();
        var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: preview =>
        {
            previews.Add(preview);
            fixture.Query.Response = Result(preview.Query.Systems.Select(computer => (computer, (double?)99)).ToArray());
        });

        var workspace = Assert.Single(result.Workspaces);
        if (targetCount == 1000)
        {
            Assert.Null(workspace.Error);
            Assert.Equal(1000, workspace.Targets.Count);
            Assert.Equal(1000, workspace.Values.Count);
            Assert.Equal(1000, Assert.Single(previews).Query.Systems.Count);
            Assert.EndsWith("| take 1001", fixture.Query.Kql);
            Assert.Equal(1, fixture.Query.Calls);
        }
        else
        {
            Assert.Contains("More than 1000 systems match", workspace.Error!);
            Assert.Empty(workspace.Targets);
            Assert.Empty(workspace.Values);
            Assert.Empty(previews);
            Assert.Equal(0, fixture.Query.Calls);
        }
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Empty(await fixture.Service.StatesAsync(fixture.Rule.Id));
        Assert.Equal(0, (await fixture.Service.DeliveryStatusAsync()).Pending);
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
    [InlineData(0, 10)]
    [InlineData(0.001, 11)]
    [InlineData(60, 11)]
    [InlineData(60.001, 12)]
    [InlineData(300.001, 16)]
    public async Task AllWorkspacesKeepTheOriginalDefinitionAndWindowWhileLookbackTracksElapsedTime(
        double elapsedSeconds, int secondLookback)
    {
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        fixture.Rule.WorkspaceKeys = ["production", "workspace-1"];
        var originalKql = fixture.Rule.Kql;
        var prepared = new List<ScheduledQueryExecutionPreview>();
        fixture.Query.BeforeResult = () =>
        {
            if (fixture.Query.Calls == 1)
                fixture.Clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
            return Task.CompletedTask;
        };

        var result = await fixture.Service.PreviewAsync(fixture.Rule, onPrepared: preview =>
        {
            prepared.Add(preview);
            fixture.Rule.WorkspaceKeys = ["removed-workspace"];
            fixture.Rule.Kql = "print Value = 999";
            fixture.Rule.Scope = AnalysisQueryScopes.Workspace;
            fixture.Rule.Computers = "NOT-IN-INVENTORY";
            fixture.Rule.WindowMinutes = 1440;
            fixture.Rule.IngestionDelayMinutes = 60;
        });

        Assert.Equal(2, prepared.Count);
        Assert.Equal(2, fixture.Query.Calls);
        Assert.Equal(["production", "workspace-1"], result.Workspaces.Select(workspace => workspace.WorkspaceKey));
        Assert.Equal(new DateTime(2026, 10, 28, 12, 6, 0, DateTimeKind.Utc), result.WindowEndUtc);
        for (var index = 0; index < prepared.Count; index++)
        {
            var preview = prepared[index];
            var request = fixture.Query.Requests[index];
            Assert.Equal(result.WindowEndUtc, preview.WindowEndUtc);
            Assert.Equal(result.WindowEndUtc.AddMinutes(-5), preview.WindowStartUtc);
            Assert.Equal(index == 0 ? 10 : secondLookback, preview.ApiTimeRangeMinutes);
            Assert.Equal(preview.ApiTimeRangeMinutes, request.TimeRange);
            Assert.Equal(preview.Query.EffectiveKql, request.Kql);
            Assert.Contains(originalKql, request.Kql);
            Assert.Contains($"let _WindowStart = datetime({preview.WindowStartUtc:O});", request.Kql);
            Assert.Contains($"let _WindowEnd = datetime({preview.WindowEndUtc:O});", request.Kql);
            Assert.DoesNotContain("999", request.Kql);
            Assert.Null(result.Workspaces[index].Error);
        }
        Assert.Equal(["DC01", "DC02"], prepared[0].Query.Systems);
        Assert.Equal(["DC01"], prepared[1].Query.Systems);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Empty(await fixture.Service.StatesAsync(fixture.Rule.Id));
        Assert.Empty(await fixture.Service.ListAsync());
        Assert.Equal(0, (await fixture.Service.DeliveryStatusAsync()).Pending);
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
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.DeleteAsync(stored.Id, stored.Revision));
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
        await Assert.ThrowsAsync<ScheduledQueryValidationException>(() => fixture.Service.SaveAsync(fixture.Rule));
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
        var scopeUpgrade = context.GetService<IMigrator>().GenerateScript(
            "20261028134046_AddScheduledQueryWorkspaces", "20261102131756_AddScheduledQueryWorkspaceScope");
        Assert.Contains("ADD [Scope]", scopeUpgrade);
        Assert.Contains("DEFAULT N'SelectedSystems'", scopeUpgrade);
        Assert.DoesNotContain("DROP TABLE", scopeUpgrade);
        Assert.Contains("20261102131756_AddScheduledQueryWorkspaceScope", provision);
        Assert.Contains("DEFAULT N'SelectedSystems'", provision);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ConfigurationPageExposesOnlyOwnKqlAndDisabledExamples(string culture)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var german = culture == "de-DE";
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
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
        Assert.Contains(german ? "Eigene KQL-Regel erstellen" : "Create custom KQL rule", WebUtility.HtmlDecode(html));
        Assert.Contains(german ? "CPU-Beispiel" : "CPU example", html);
        Assert.Contains(german ? "Kerberos-Beispiel" : "Kerberos example", html);
        foreach (var eventId in ScheduledQueryRule.EventIds)
            Assert.Contains(services.GetRequiredService<UiText>()[QueryTemplateCatalog.GetActive($"kql/scheduled/event-{eventId}").NameResourceKey],
                WebUtility.HtmlDecode(html));
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
            Assert.Contains(german ? "Abfrage testen (keine Alarme)" : "Test query (no alerts)", WebUtility.HtmlDecode(form));
            Assert.Contains("<legend>Workspaces</legend>", form);
            Assert.Contains("Workspace workspace-1", form);
            Assert.Equal(2, Assert.IsType<ScheduledQueryEvaluation>(page.GetType().GetField("preview", flags)!.GetValue(page)).Workspaces.Count);
            Assert.Contains(german ? "Auswertungsfenster (Minuten)" : "Evaluation window (minutes)", form);
            Assert.Contains(german ? "Würde Alarm auslösen" : "Would alert", WebUtility.HtmlDecode(form));
            Assert.Contains(german ? "Keine Daten" : "No data", form);
            Assert.Contains(german ? "Effektive KQL für diesen Test" : "Effective KQL for this test", WebUtility.HtmlDecode(form));
            Assert.Contains(fixture.Options.Workspaces[0].WorkspaceId, form);
            Assert.Contains(german ? "API-Rückblick: 10 Minuten" : "API lookback: 10 minutes", WebUtility.HtmlDecode(form));
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
            Assert.DoesNotContain("Simulated preview failure", partialHtml);
            Assert.Contains(services.GetRequiredService<UiText>()["AdminScheduledQueries.Errors.PreviewWorkspaceFailed"], WebUtility.HtmlDecode(partialHtml));
            Assert.Contains(german ? "Würde Alarm auslösen" : "Would alert", WebUtility.HtmlDecode(partialHtml));
            Assert.Contains(fixture.Query.Kql, System.Net.WebUtility.HtmlDecode(partialHtml));
            fixture.Query.Failures.Clear();
            await (Task)page.GetType().GetMethod("SaveAsync", flags)!.Invoke(page, [])!;
            Assert.Null(page.GetType().GetField("error", flags)!.GetValue(page));
        });
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(["production", "workspace-1"], Assert.Single(await fixture.Service.ListAsync()).WorkspaceKeys);
        Assert.Empty(await fixture.AlertRecordsAsync());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            page.GetType().GetMethod("BeginEventExample", flags)!.Invoke(page, [4624]);
            var editing = (ScheduledQueryRule)page.GetType().GetField("editing", flags)!.GetValue(page)!;
            Assert.False(editing.Enabled);
            Assert.Equal(AnalysisQueryScopes.Workspace, editing.Scope);
            Assert.Equal(["production"], editing.WorkspaceKeys);
            page.GetType().GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            var form = readHtml!();
            Assert.Contains(german ? "Workspaceweit" : "Workspace-wide", form);
            Assert.DoesNotContain(german ? "Computer (optional)" : "Computers (optional)", form);
            Assert.Contains("Series = &quot;Workspace&quot;", form);
            fixture.Query.Responses.Clear();
            fixture.Query.Response = EventResult(4624, 3, "[\"preview-user\", \"<script>user</script>\"]", unparsed: 1, truncated: true);
            await (Task)page.GetType().GetMethod("PreviewAsync", flags)!.Invoke(page, [])!;
            page.GetType().GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            form = readHtml!();
            Assert.Contains("preview-user", form);
            Assert.Contains("&lt;script&gt;user&lt;/script&gt;", form);
            Assert.DoesNotContain("<script>user</script>", form);
            Assert.Contains(services.GetRequiredService<UiText>().Format(
                "AdminScheduledQueries.EventWarnings.Both.One", 1L, ScheduledQueryEventDetails.MaxUsers),
                WebUtility.HtmlDecode(form));
            editing.Domain = "example.com";
            editing.Site = "North";
            editing.Role = "Domain Controller";
            editing.Computers = "DC01";
            page.GetType().GetMethod("ScopeChanged", flags)!.Invoke(page, []);
            Assert.All(new[] { editing.Domain, editing.Site, editing.Role, editing.Computers }, value => Assert.Empty(value));
        });
    }

    [Fact]
    public async Task TemplateLoadFailureIsVisibleAndPreservesTheCurrentEditorWithoutExecutingKql()
    {
        await using var fixture = await Fixture.CreateAsync();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var missing = Path.Combine(Path.GetTempPath(), $"missing-scheduled-template-{Guid.NewGuid():N}.json");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = activator.Page!;
            await (Task)page.GetType().GetMethod("BeginNew", flags)!.Invoke(page, [false])!;
            var original = page.GetType().GetField("editing", flags)!.GetValue(page);
            Func<ScheduledQueryRule> load = () => ScheduledQueryTemplates.Load(missing);

            await (Task)page.GetType().GetMethod("BeginRule", flags)!.Invoke(page, [load])!;

            Assert.Same(original, page.GetType().GetField("editing", flags)!.GetValue(page));
            page.GetType().GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            Assert.Contains(services.GetRequiredService<UiText>()["AdminScheduledQueries.Errors.TemplateFailed"], WebUtility.HtmlDecode(root.ToHtmlString()));
            Assert.DoesNotContain(Path.GetFileName(missing), root.ToHtmlString());
            Assert.Contains("role=\"alert\"", root.ToHtmlString());
        });
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Empty(await fixture.Service.ListAsync());
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PastedScheduledJsonOnlyFillsANewDisabledDraftUntilSave(string culture)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "Scheduled", "cpu-high.json"));
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var html = WebUtility.HtmlDecode(root.ToHtmlString());
            Assert.Contains(culture == "de-DE" ? "JSON in neuen Entwurf einlesen" : "Read JSON into new draft", html);
            Assert.Contains(culture == "de-DE"
                ? "Ein JSON-Dokument mit einer Vorlage für eine geplante Abfrage einfügen. Datenbankexporte, JSON für Analyseabfragen und Laufzeitstatus werden nicht unterstützt. Vor dem Speichern einen anderen Namen wählen, falls bereits eine Regel mit diesem Namen existiert."
                : "Paste one Scheduled query template JSON document. Database exports, Analysis query JSON and runtime state are not supported. Choose a different name before saving if a rule with this name already exists.", html);
            var page = activator.Page!;
            await (Task)page.GetType().GetMethod("BeginNew", flags)!.Invoke(page, [true])!;
            var previous = page.GetType().GetField("editing", flags)!.GetValue(page);
            var input = activator.Import!;
            async Task Import(string text)
            {
                input.GetType().GetField("json", flags)!.SetValue(input, text);
                await (Task)input.GetType().GetMethod("ImportAsync", flags)!.Invoke(input, [])!;
            }
            foreach (var invalid in new[] { "", "{", "null", "{}", json + new string(' ', 65536) })
            {
                await Import(invalid);
                Assert.Same(previous, page.GetType().GetField("editing", flags)!.GetValue(page));
                Assert.Contains("role=\"alert\"", root.ToHtmlString());
                Assert.Null(page.GetType().GetField("message", flags)!.GetValue(page));
            }
            await Import(json.PadRight(65536));
            Assert.Null(page.GetType().GetField("error", flags)!.GetValue(page));
            var importedMessage = culture == "de-DE"
                ? "JSON als neue, ungespeicherte, deaktivierte Regel geöffnet. Separat speichern und erst aktivieren, wenn die Regel bereit ist."
                : "JSON opened as a new, unsaved, disabled rule. Save it separately and enable it only when ready.";
            Assert.Equal(importedMessage, page.GetType().GetField("message", flags)!.GetValue(page));
            Assert.Contains(importedMessage, WebUtility.HtmlDecode(root.ToHtmlString()));
            var draft = Assert.IsType<ScheduledQueryRule>(page.GetType().GetField("editing", flags)!.GetValue(page));
            Assert.NotSame(previous, draft);
            Assert.Equal(ScheduledQueryRule.Example(false).Kql, draft.Kql);
            Assert.Equal(["production"], draft.WorkspaceKeys);
            Assert.False(draft.Enabled);
            Assert.Equal(0, draft.Revision);
            Assert.Null(draft.LastRunUtc);
            Assert.Null(page.GetType().GetField("preview", flags)!.GetValue(page));
            Assert.Empty(await fixture.Service.ListAsync());
            await Import(json);
            var second = Assert.IsType<ScheduledQueryRule>(page.GetType().GetField("editing", flags)!.GetValue(page));
            Assert.NotEqual(draft.Id, second.Id);
            page.GetType().GetField("busy", flags)!.SetValue(page, true);
            var message = page.GetType().GetField("message", flags)!.GetValue(page);
            await Import("{");
            Assert.Same(second, page.GetType().GetField("editing", flags)!.GetValue(page));
            Assert.Equal(message, page.GetType().GetField("message", flags)!.GetValue(page));
            page.GetType().GetField("busy", flags)!.SetValue(page, false);
            await (Task)page.GetType().GetMethod("SaveAsync", flags)!.Invoke(page, [])!;
            Assert.Null(page.GetType().GetField("error", flags)!.GetValue(page));
            var savedMessage = culture == "de-DE"
                ? "Regel gespeichert. Aktivierte Regeln werden vom Scheduler innerhalb von ungefähr 30 Sekunden übernommen."
                : "Rule saved. Enabled rules are picked up by the scheduler within approximately 30 seconds.";
            Assert.Equal(savedMessage, page.GetType().GetField("message", flags)!.GetValue(page));
            page.GetType().GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            Assert.Contains(savedMessage, WebUtility.HtmlDecode(root.ToHtmlString()));
        });
        Assert.False(Assert.Single(await fixture.Service.ListAsync()).Enabled);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    public async Task ConfigurationFrameLocalizesWithoutChangingRulesExamplesDeliveryOrAccess(string culture, int count)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        for (var index = 0; index < count; index++)
        {
            var rule = ScheduledQueryRule.Example(false);
            rule.Name = index == 0 ? "CPU example" : "Original <rule> &";
            rule.CategoryName = "Scheduled KQL alert rules";
            rule.WorkspaceKey = "production";
            rule.Enabled = index % 2 == 0;
            await fixture.Service.SaveAsync(rule);
        }
        async Task<string> Snapshot() => JsonSerializer.Serialize((await fixture.Service.ListAsync()).OrderBy(rule => rule.Name));
        var original = await Snapshot();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = Assert.IsType<Components.Pages.AdminScheduledQueries>(activator.Page);
            var type = page.GetType();
            Assert.Null(type.GetField("error", flags)!.GetValue(page));
            void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(page, value);
            string Render()
            {
                type.GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                return root.ToHtmlString();
            }
            foreach (var busy in new[] { false, true })
            foreach (var pending in new[] { 0, 1, 2 })
            foreach (var deliveryError in new string?[] { null, "", "Original <delivery> &" })
            {
                Set("busy", busy);
                Set("delivery", (pending, deliveryError));
                Set("error", "Original <error> &");
                Set("message", "Original <status> &");
                var raw = Render();
                var html = WebUtility.HtmlDecode(raw);
                Assert.Contains(culture == "de-DE" ? "<h1>Geplante KQL-Alarmregeln</h1>" : "<h1>Scheduled KQL alert rules</h1>", html);
                Assert.Contains(culture == "de-DE" ? ">Konfiguration / Alarmregeln</p>" : ">Configuration / Alert rules</p>", html);
                Assert.Contains(culture == "de-DE"
                    ? "Eigene KQL nach Zeitplan ausführen und einen Alarmlebenszyklus je Regel und System oder Workspace erstellen."
                    : "Run your own KQL on a schedule and create one alert lifecycle per rule and system or workspace.", html);
                Assert.Contains(culture == "de-DE" ? ">Andere Alarmregeln</a>" : ">Other alert rules</a>", html);
                Assert.Contains("href=\"admin/rules\"", raw);
                Assert.Contains(culture == "de-DE"
                    ? "Prüfungen laufen auf dem Server, auch ohne geöffnetes Dashboard. Die WebApp muss weiterlaufen (App Service: Always On). Fehlende Daten oder fehlgeschlagene Abfragen werden niemals als 0 gewertet und lösen niemals einen bestehenden Alarm auf."
                    : "Checks run on the server, even without an open dashboard. The WebApp must remain running (App Service: Always On). Missing data or failed queries never count as zero and never resolve an existing alert.", html);
                Assert.Contains(culture == "de-DE" ? $"<h2>Geplante Regeln ({count})</h2>" : $"<h2>Scheduled rules ({count})</h2>", html);
                var collapsedRules = Regex.Matches(raw,
                    """<details class="scheduled-rule-details"(?<attributes>[^>]*)>\s*<summary>(?<title>.*?)</summary>""",
                    RegexOptions.Singleline);
                Assert.Equal(count, collapsedRules.Count);
                Assert.All(collapsedRules.Cast<Match>(), card =>
                {
                    Assert.DoesNotContain("open", card.Groups["attributes"].Value);
                    Assert.Matches("^<h3>[^<]*</h3>$", card.Groups["title"].Value);
                });
                Assert.Equal(count == 0, html.Contains(culture == "de-DE"
                    ? "Keine geplanten KQL-Regeln konfiguriert. Beispiele sind anfangs deaktiviert; vor dem Aktivieren testen."
                    : "No scheduled KQL rules configured. Examples start disabled; test them before enabling."));
                var toolbar = Regex.Match(html, "<div class=\"admin-card-actions[^\"]*kql-query-actions[^\"]*\">.*?</div>", RegexOptions.Singleline).Value;
                Assert.Contains(culture == "de-DE" ? ">Eigene KQL-Regel erstellen</button>" : ">Create custom KQL rule</button>", toolbar);
                Assert.Contains(culture == "de-DE" ? ">CPU-Beispiel</button>" : ">CPU example</button>", toolbar);
                Assert.Contains(culture == "de-DE" ? ">Kerberos-Beispiel</button>" : ">Kerberos example</button>", toolbar);
                Assert.Contains(culture == "de-DE" ? ">Status aktualisieren</button>" : ">Refresh status</button>", toolbar);
                foreach (var eventId in ScheduledQueryRule.EventIds)
                    Assert.Contains($">{services.GetRequiredService<UiText>()[QueryTemplateCatalog.GetActive($"kql/scheduled/event-{eventId}").NameResourceKey]}</button>", toolbar);
                var buttons = Regex.Matches(toolbar, "<button\\b[^>]*>");
                Assert.Equal(ScheduledQueryRule.EventIds.Count() + 5, buttons.Count);
                Assert.Contains(services.GetRequiredService<UiText>()[QueryTemplateCatalog.GetActive("kql/scheduled/replication-unknown-targets").NameResourceKey], toolbar);
                Assert.All(buttons.Cast<Match>(), button => Assert.Equal(busy, button.Value.Contains("disabled")));
                var delivery = Regex.Match(raw, "<div class=\"admin-message admin-message-warning\" role=\"status\">.*?</div>", RegexOptions.Singleline).Value;
                Assert.Equal(pending > 0, delivery.Length > 0);
                if (pending > 0)
                {
                    Assert.Contains(culture == "de-DE"
                        ? $"Ausstehende {(pending == 1 ? "Alarmbenachrichtigung" : "Alarmbenachrichtigungen")}: {pending}. Die Zustellung wird automatisch erneut versucht."
                        : $"Pending alert notifications: {pending}. Delivery is retried automatically.", WebUtility.HtmlDecode(delivery));
                    Assert.Equal(deliveryError is not null, delivery.Contains("role=\"alert\""));
                    if (deliveryError is not null)
                        Assert.Contains(culture == "de-DE" ? $"Letzter Zustellungsfehler: {deliveryError}" : $"Last delivery failure: {deliveryError}", WebUtility.HtmlDecode(delivery));
                    if (deliveryError?.Length > 0) Assert.Contains("Original &lt;delivery&gt; &amp;", delivery);
                }
                Assert.Contains("Original &lt;error&gt; &amp;", raw);
                Assert.Contains("Original &lt;status&gt; &amp;", raw);
                if (count > 0)
                {
                    Assert.Contains("<h3>CPU example</h3>", html);
                    Assert.Contains("Scheduled KQL alert rules ·", html);
                }
                if (count == 2) Assert.Contains("Original &lt;rule&gt; &amp;", raw);
            }
            await (Task)type.GetMethod("RefreshAsync", flags)!.Invoke(page, [])!;
            Assert.Equal("Original <status> &", type.GetField("message", flags)!.GetValue(page));
            Set("busy", false);
            await (Task)type.GetMethod("RefreshAsync", flags)!.Invoke(page, [])!;
            Assert.Null(type.GetField("error", flags)!.GetValue(page));
            Assert.Equal("Original <status> &", type.GetField("message", flags)!.GetValue(page));
            Assert.Equal((0, (string?)null), Assert.IsType<(int, string?)>(type.GetField("delivery", flags)!.GetValue(page)));
            foreach (var kerberos in new[] { false, true })
            {
                await (Task)type.GetMethod("BeginNew", flags)!.Invoke(page, [kerberos])!;
                var draft = Assert.IsType<ScheduledQueryRule>(type.GetField("editing", flags)!.GetValue(page));
                Assert.Equal(ScheduledQueryRule.Example(kerberos).Name, draft.Name);
                Assert.Equal(ScheduledQueryRule.Example(kerberos).Kql, draft.Kql);
                Assert.False(draft.Enabled);
                Assert.Equal(0, draft.Revision);
                Assert.Equal(["production"], draft.WorkspaceKeys);
                AssertScheduledEditorHelp(Render(), culture, draft.Scope, draft.Kql);
            }
            foreach (var eventId in ScheduledQueryRule.EventIds)
            {
                await (Task)type.GetMethod("BeginEventExample", flags)!.Invoke(page, [eventId])!;
                var draft = Assert.IsType<ScheduledQueryRule>(type.GetField("editing", flags)!.GetValue(page));
                var example = ScheduledQueryRule.EventExample(eventId);
                Assert.Equal(example.Name, draft.Name);
                Assert.Equal(example.Kql, draft.Kql);
                Assert.Equal(example.Scope, draft.Scope);
                Assert.False(draft.Enabled);
                Assert.Equal(0, draft.Revision);
                AssertScheduledEditorHelp(Render(), culture, draft.Scope, draft.Kql);
            }
            Assert.Equal("OperatorAccess", Assert.Single(type.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()).Policy);
            Assert.Equal("/admin/rules/queries", Assert.Single(type.GetCustomAttributes<RouteAttribute>()).Template);
        });
        Assert.Equal(original, await Snapshot());
        Assert.Equal(count, (await fixture.Service.ListAsync()).Count);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(0, await context.ScheduledQueryNotifications.CountAsync());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ConfigurationCardsAndEditorLocalizeWithoutChangingTechnicalValuesOrPersistence(string culture)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var german = culture == "de-DE";
        await using var fixture = await Fixture.CreateAsync(workspaceCount: 2);
        var workspace = fixture.Options.Workspaces[0];
        fixture.Options.Workspaces[0] = new()
        {
            Key = workspace.Key, DisplayName = "Selected systems <workspace> &", WorkspaceId = workspace.WorkspaceId,
            SubscriptionIds = workspace.SubscriptionIds, Domains = workspace.Domains
        };
        var seed = ScheduledQueryRule.Example(false);
        seed.WorkspaceKey = "production";
        await fixture.Service.SaveAsync(seed);
        async Task<string> Snapshot() => JsonSerializer.Serialize(await fixture.Service.ListAsync());
        var original = await Snapshot();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const string kql = "// Original <KQL> &\r\n_SelectedSystems | project Series = Computer, Value = real(null)";
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = Assert.IsType<Components.Pages.AdminScheduledQueries>(activator.Page);
            var type = page.GetType();
            void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(page, value);
            var rule = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ScheduledQueryRule>>(type.GetField("rules", flags)!.GetValue(page)));
            rule.Name = "Enabled <rule> &";
            rule.CategoryName = "Severity <category> &";
            rule.WorkspaceKeys = ["PRODUCTION", "stale <workspace> &"];
            rule.IntervalMinutes = 13;
            rule.WindowMinutes = 27;
            rule.IngestionDelayMinutes = 4;
            rule.Comparison = "<=";
            rule.Threshold = 90.125;
            rule.Severity = "Sev4";
            rule.Domain = "Original <domain> &";
            rule.Site = "Original <site> &";
            rule.Role = "Enabled";
            rule.Computers = "DC01,\r\nOriginal <computer> &";
            rule.Kql = kql;
            rule.LastStatus = "Never";
            rule.LastError = "Original <error> &";
            rule.NextRunUtc = new DateTime(2026, 10, 28, 12, 13, 43, DateTimeKind.Utc);
            rule.LastWorkspaceResultsJson = JsonSerializer.Serialize(Enumerable.Range(0, 3)
                .Select(count => new ScheduledQueryWorkspaceStatus($"Original <workspace{count}> &", count, count,
                    count == 2 ? "Original <result error> &" : null)));
            foreach (var busy in new[] { false, true })
            foreach (var enabled in new[] { false, true })
            foreach (var scope in new[] { AnalysisQueryScopes.SelectedSystems, AnalysisQueryScopes.Workspace })
            foreach (var revision in new[] { 0, 7 })
            {
                Set("busy", false);
                rule.Enabled = enabled;
                rule.Scope = scope;
                rule.Revision = revision;
                rule.LastRunUtc = revision == 0 ? null : new DateTime(2026, 10, 28, 12, 8, 59, DateTimeKind.Utc);
                rule.LastWindowEndUtc = revision == 0 ? null : new DateTime(2026, 10, 28, 12, 3, 17, DateTimeKind.Utc);
                var before = JsonSerializer.Serialize(rule);
                await (Task)type.GetMethod("EditAsync", flags)!.Invoke(page, [rule])!;
                Assert.Null(type.GetField("error", flags)!.GetValue(page));
                var draft = Assert.IsType<ScheduledQueryRule>(type.GetField("editing", flags)!.GetValue(page));
                Assert.NotSame(rule, draft);
                Assert.Equal(before, JsonSerializer.Serialize(draft));
                Set("busy", busy);
                type.GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                var raw = root.ToHtmlString();
                var cardRaw = Regex.Match(raw, "<article class=\"admin-rule-card\">.*?</article>", RegexOptions.Singleline).Value;
                var card = WebUtility.HtmlDecode(cardRaw);
                var scopeLabel = scope == AnalysisQueryScopes.Workspace
                    ? (german ? "Workspaceweit" : "Workspace-wide")
                    : (german ? "Ausgewählte Systeme" : "Selected systems");
                var enabledLabel = enabled ? (german ? "Aktiviert" : "Enabled") : (german ? "Deaktiviert" : "Disabled");
                Assert.Contains($"{enabledLabel} · {scopeLabel} · PRODUCTION, stale <workspace> & · {(german ? "alle" : "every")} 13 min", card);
                Assert.Contains($"Severity <category> & · Sev4 · {(german ? "Wert" : "Value")} <= 90,125", card);
                Assert.Contains("<p>Never</p>", card);
                Assert.Contains("Original &lt;error&gt; &amp;", cardRaw);
                Assert.Contains("Original &lt;result error&gt; &amp;", cardRaw);
                var lastRun = revision == 0 ? (german ? "Nie" : "Never") : "2026-10-28 12:08:59";
                var windowEnd = revision == 0 ? (german ? "Nie" : "Never") : "2026-10-28 12:03:17";
                Assert.Contains(german ? $"Letzte Prüfung: {lastRun} · Fensterende (UTC): {windowEnd}"
                    : $"Last check: {lastRun} · Window end (UTC): {windowEnd}", card);
                var nextRun = enabled ? "2026-10-28 12:13:43" : (german ? "Pausiert" : "Paused");
                Assert.Contains(german ? $"Nächste Prüfung (UTC): {nextRun}" : $"Next check (UTC): {nextRun}", card);
                foreach (var count in new[] { 0, 1, 2 })
                    Assert.Contains(german
                        ? $"Workspace Original <workspace{count}> &: Auswertungsziele: {count}; Messwerte: {count}."
                        : $"Workspace Original <workspace{count}> &: {count} evaluation targets; {count} measured values.", card);
                Assert.Contains(german ? ">Bearbeiten / Zielstatus</button>" : ">Edit / target status</button>", card);
                Assert.Contains(german ? ">Deaktivieren</button>" : ">Disable</button>", card);
                Assert.Contains(german ? ">Löschen</button>" : ">Delete</button>", card);
                var cardButtons = Regex.Matches(cardRaw, "<button\\b[^>]*>");
                Assert.Equal(3, cardButtons.Count);
                Assert.Equal(busy, cardButtons[0].Value.Contains("disabled"));
                Assert.Equal(busy || !enabled, cardButtons[1].Value.Contains("disabled"));
                Assert.Equal(busy || enabled, cardButtons[2].Value.Contains("disabled"));
                var form = Regex.Match(raw, "<form class=\"admin-form\".*?</form>", RegexOptions.Singleline).Value;
                var formText = WebUtility.HtmlDecode(form);
                Assert.Contains(revision == 0
                    ? $"<h2>{(german ? "Neue geplante Regel" : "New scheduled rule")}</h2>"
                    : "<h2>Enabled <rule> &</h2>", WebUtility.HtmlDecode(raw));
                foreach (var caption in german
                    ? new[] { "Name", "Aktiviert", "Geltungsbereich", "Workspaces", "Ausführungsintervall (Minuten)",
                        "Auswertungsfenster (Minuten)", "Ingestion-Verzögerung (Minuten)", "Vergleich", "Schwellenwert", "Schweregrad", "Kategorie" }
                    : new[] { "Name", "Enabled", "Scope", "Workspaces", "Run every (minutes)", "Evaluation window (minutes)",
                        "Ingestion delay (minutes)", "Comparison", "Threshold", "Severity", "Category" })
                    Assert.Contains($">{caption}<", formText);
                Assert.Contains("value=\"Enabled &lt;rule&gt; &amp;\"", form);
                Assert.Contains("value=\"Severity &lt;category&gt; &amp;\"", form);
                Assert.Contains("Selected systems &lt;workspace&gt; &amp; (production)", form);
                Assert.Contains(german ? "stale <workspace> & (nicht mehr konfiguriert)" : "stale <workspace> & (no longer configured)", formText);
                var workspaces = Regex.Match(form, "<fieldset\\b.*?</fieldset>", RegexOptions.Singleline).Value;
                var workspaceInputs = Regex.Matches(workspaces, "<input\\b[^>]*>");
                Assert.Equal(3, workspaceInputs.Count);
                Assert.All(workspaceInputs.Cast<Match>(), input => Assert.Equal(busy, input.Value.Contains("disabled")));
                Assert.Contains("checked", workspaceInputs[0].Value);
                Assert.DoesNotContain("checked", workspaceInputs[1].Value);
                Assert.Contains("checked", workspaceInputs[2].Value);
                Assert.Contains(german
                    ? "Workspaceweite Regeln werden einmal je ausgewähltem Workspace ausgeführt, ohne Inventar- oder Computerauswahl. Der Wechsel zu diesem Bereich leert die Inventarfilter. Gespeicherte Regeln deaktivieren und ihre aktiven Alarme auflösen, bevor der Bereich geändert wird."
                    : "Workspace-wide rules run once per selected workspace, without inventory or computer selection. Switching to this scope clears inventory filters. Disable saved rules and resolve their active alerts before changing scope.", formText);
                Assert.Contains(german
                    ? "Mindestens einen Workspace auswählen. Jeder Workspace wird unabhängig ausgeführt; ein Fehler hält die anderen nicht auf."
                    : "Select at least one workspace. Each workspace runs independently; a failure does not stop the others.", formText);
                Assert.Contains(german
                    ? "Auf eingehende Telemetrie warten. Nach dieser Verzögerung wird das Fensterende auf eine volle UTC-Minute abgerundet."
                    : "Wait for telemetry to arrive. Window end is rounded down to a full UTC minute after this delay.", formText);
                var selectedSystems = scope == AnalysisQueryScopes.SelectedSystems;
                Assert.Equal(selectedSystems, formText.Contains(german ? ">Computer (optional)<" : ">Computers (optional)<"));
                foreach (var caption in german ? new[] { "Domäne", "Standort", "Rolle" } : new[] { "Domain", "Site", "Role" })
                    Assert.Equal(selectedSystems, formText.Contains($">{caption}<"));
                if (selectedSystems)
                {
                    Assert.Contains(german
                        ? $"Exakte Inventarnamen, durch Kommas oder Zeilenumbrüche getrennt. Leere Filter wählen alle passenden Systeme in jedem ausgewählten Workspace aus; Systeme im Wartungsmodus werden ausgeschlossen. Höchstens {ScheduledQueryService.MaxTargets} Systeme je Workspace."
                        : $"Exact inventory names, separated by commas or newlines. Blank filters select all matching systems in each selected workspace; systems in maintenance are excluded. At most {ScheduledQueryService.MaxTargets} systems per workspace.", formText);
                    var computers = Regex.Match(form, "<textarea\\b[^>]*rows=\"3\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline);
                    Assert.Contains("maxlength=\"4000\"", computers.Value);
                    Assert.Equal(rule.Computers, WebUtility.HtmlDecode(computers.Groups[1].Value));
                    Assert.Contains("value=\"Enabled\"", form);
                }
                var inputs = Regex.Matches(form, "<input\\b[^>]*maxlength=\"256\"[^>]*>");
                Assert.Equal(selectedSystems ? 5 : 2, inputs.Count);
                Assert.All(inputs.Cast<Match>(), input => Assert.DoesNotContain("disabled", input.Value));
                var numbers = Regex.Matches(form, "<input\\b[^>]*type=\"number\"[^>]*>");
                Assert.Equal(4, numbers.Count);
                Assert.All(numbers.Cast<Match>(), input => Assert.Contains("required", input.Value));
                for (var index = 0; index < 3; index++)
                {
                    Assert.Contains(index == 2 ? "min=\"0\"" : "min=\"1\"", numbers[index].Value);
                    Assert.Contains(index == 2 ? "max=\"60\"" : "max=\"1440\"", numbers[index].Value);
                    Assert.Contains($"value=\"{new[] { 13, 27, 4 }[index]}\"", numbers[index].Value);
                }
                Assert.Contains("step=\"any\"", numbers[3].Value);
                Assert.Contains("value=\"90.125\"", numbers[3].Value);
                var selects = Regex.Matches(form, "<select\\b[^>]*>.*?</select>", RegexOptions.Singleline);
                Assert.Equal(3, selects.Count);
                void AssertSelect(int index, string[] values, string selected)
                {
                    var options = Regex.Matches(selects[index].Value, "<option\\b([^>]*)>(.*?)</option>", RegexOptions.Singleline);
                    string Value(Match option) => WebUtility.HtmlDecode(Regex.Match(option.Groups[1].Value, "value=\"([^\"]*)\"").Groups[1].Value);
                    Assert.Equal(values, options.Cast<Match>().Select(Value));
                    Assert.Equal(selected, Value(Assert.Single(options.Cast<Match>(),
                        option => Regex.IsMatch(option.Groups[1].Value, "\\bselected(?:\\s|=|$)"))));
                    if (index > 0) Assert.All(options.Cast<Match>(), option => Assert.Equal(Value(option), WebUtility.HtmlDecode(option.Groups[2].Value)));
                }
                AssertSelect(0, ["SelectedSystems", "Workspace", ScheduledQueryScopes.UnknownInventoryTargets], scope);
                AssertSelect(1, ["=", "<", "<=", ">", ">="], "<=");
                AssertSelect(2, ["Sev0", "Sev1", "Sev2", "Sev3", "Sev4"], "Sev4");
                Assert.Contains(german ? ">Ausgewählte Systeme</option>" : ">Selected systems</option>", formText);
                Assert.Contains(german ? ">Workspaceweit</option>" : ">Workspace-wide</option>", formText);
                var query = Regex.Match(form, "<textarea\\b[^>]*rows=\"15\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline);
                Assert.Contains("maxlength=\"65536\"", query.Value);
                Assert.Contains("required", query.Value);
                Assert.Contains("spellcheck=\"false\"", query.Value);
                Assert.Equal(kql, WebUtility.HtmlDecode(query.Groups[1].Value));
                AssertScheduledEditorHelp(raw, culture, scope, kql);
                Assert.Contains(german ? ">Regel speichern</button>" : ">Save rule</button>", formText);
                Assert.Contains(german ? ">Abfrage testen (keine Alarme)</button>" : ">Test query (no alerts)</button>", formText);
                Assert.Contains(german ? ">Abbrechen</button>" : ">Cancel</button>", formText);
                Assert.Single(Regex.Matches(form, "<button\\b[^>]*data-kql-keyword-toggle[^>]*>").Cast<Match>());
                var formButtons = Regex.Matches(form, "<button\\b(?![^>]*\\bdata-kql-keyword-toggle\\b)[^>]*>");
                Assert.Equal(3, formButtons.Count);
                Assert.All(formButtons.Cast<Match>(), button => Assert.Equal(busy, button.Value.Contains("disabled")));
                Assert.Equal(before, JsonSerializer.Serialize(rule));
                Assert.Equal(before, JsonSerializer.Serialize(draft));
            }
            Set("busy", false);
            var current = Assert.IsType<ScheduledQueryRule>(type.GetField("editing", flags)!.GetValue(page));
            type.GetMethod("SetWorkspace", flags)!.Invoke(page, ["production", true]);
            Assert.Equal(["PRODUCTION", "stale <workspace> &"], current.WorkspaceKeys);
            type.GetMethod("SetWorkspace", flags)!.Invoke(page, ["workspace-1", true]);
            type.GetMethod("SetWorkspace", flags)!.Invoke(page, ["STALE <WORKSPACE> &", false]);
            Assert.Equal(["PRODUCTION", "workspace-1"], current.WorkspaceKeys);
            type.GetMethod("ScopeChanged", flags)!.Invoke(page, []);
            Assert.All(new[] { current.Domain, current.Site, current.Role, current.Computers }, value => Assert.Empty(value));
            Assert.Equal(kql, current.Kql);
            Assert.Equal("Original <domain> &", rule.Domain);
            Assert.Equal(["PRODUCTION", "stale <workspace> &"], rule.WorkspaceKeys);
        });
        Assert.Equal(original, await Snapshot());
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(0, await context.ScheduledQueryNotifications.CountAsync());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task StructuredEventWarningsLocalizeLongCountsWithoutChangingAccountsOrStoredPayload(string culture)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        await using var fixture = await Fixture.CreateAsync();
        var rule = ScheduledQueryRule.EventExample(4624);
        rule.WorkspaceKeys = ["production"];
        await fixture.Service.SaveAsync(rule);
        var storedRule = JsonSerializer.Serialize(await fixture.Service.ListAsync());
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator).AddSingleton(fixture.Service).AddSingleton(fixture.Options)
            .BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var text = services.GetRequiredService<UiText>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await rendered.QuiescenceTask;
            await (Task)typeof(Components.Pages.AdminScheduledQueries).GetMethod("BeginNew", flags)!.Invoke(activator.Page, [false])!;
            typeof(Components.Pages.AdminScheduledQueries).GetField("editing", flags)!.SetValue(activator.Page, rule);
            return rendered;
        });
        foreach (var count in new[] { 0L, 1L, 2L, 1234L, long.MaxValue })
        foreach (var truncated in new[] { false, true })
        foreach (var accountCount in new[] { 0, 1, ScheduledQueryEventDetails.MaxUsers })
        {
            var users = Enumerable.Range(0, accountCount).Select(index =>
                index == 0 ? "No accounts extracted." : $"Original <account-{index}> &").ToArray();
            fixture.Query.Response = EventResult(4624, 1, JsonSerializer.Serialize(users), count, truncated);
            var preview = await fixture.Service.PreviewAsync(rule);
            var workspace = Assert.Single(preview.Workspaces);
            Assert.Null(workspace.Error);
            var details = Assert.Single(workspace.EventDetails!).Value;
            Assert.Equal(count, details.UnparsedEvents);
            Assert.Equal(truncated, details.UsersTruncated);
            Assert.Equal(users, details.Users);
            var original = JsonSerializer.Serialize(preview);
            var target = new ScheduledQueryTarget("", "", WorkspaceName: "Production", WorkspaceKey: "production");
            var state = new ScheduledQueryTargetState { WorkspaceKey = "production", Value = 1 };
            var now = fixture.Clock.GetUtcNow().UtcDateTime;
            var payload = ScheduledQueryAlert.Payload(rule, target, state, now, true, details);
            var raw = await renderer.Dispatcher.InvokeAsync(() =>
            {
                typeof(Components.Pages.AdminScheduledQueries).GetField("preview", flags)!.SetValue(activator.Page, preview);
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(activator.Page, null);
                return root.ToHtmlString();
            });
            var html = WebUtility.HtmlDecode(raw);
            var warnings = Regex.Matches(raw, "<div class=\"admin-message admin-message-warning\" role=\"status\">(.*?)</div>",
                RegexOptions.Singleline);
            Assert.Equal(count > 0 || truncated ? 1 : 0, warnings.Count);
            if (warnings.Count > 0)
            {
                var expected = count > 0
                    ? text.Format($"AdminScheduledQueries.EventWarnings.{(truncated ? "Both" : "Unparsed")}.{(count == 1 ? "One" : "Other")}",
                        count, ScheduledQueryEventDetails.MaxUsers)
                    : text.Format("AdminScheduledQueries.EventWarnings.Truncated", ScheduledQueryEventDetails.MaxUsers);
                Assert.Equal(expected, WebUtility.HtmlDecode(warnings[0].Groups[1].Value));
                if (count > 0)
                {
                    Assert.Contains(count.ToString("N0", CultureInfo.GetCultureInfo(culture)), expected);
                    Assert.Contains(culture == "de-DE" ? count == 1 ? "Ereignis ohne" : "Ereignisse ohne"
                        : count == 1 ? "event without" : "events without", expected);
                }
                if (truncated) Assert.Contains("100", expected);
            }
            foreach (var account in users)
                Assert.Contains($"<li>{account}</li>", html);
            Assert.DoesNotContain("<account-", raw);
            if (accountCount == 0)
                Assert.Contains(text["AdminScheduledQueries.NoAccounts"], html);
            Assert.Equal(original, JsonSerializer.Serialize(preview));
            using (new UiSelection(culture == "de-DE" ? "en-US" : "de-DE", "fr-FR"))
                Assert.Equal(payload, ScheduledQueryAlert.Payload(rule, target, state, now, true, details));
            var presentation = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")).Parse(payload)!;
            if (details.Warnings.Length > 0)
                Assert.Contains(presentation.Rows.SelectMany(row => row.Facts), fact => fact.Value == details.Warnings);
        }
        Assert.Equal(storedRule, JsonSerializer.Serialize(await fixture.Service.ListAsync()));
        Assert.Empty(await fixture.AlertRecordsAsync());
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.ScheduledQueryTargetStates.ToArrayAsync());
        Assert.Empty(await context.ScheduledQueryNotifications.ToArrayAsync());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ConfigurationResultsLocalizeOwnedStatesAndPreserveRawDataPagingAndLimits(string culture, bool workspaceScope)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var german = culture == "de-DE";
        await using var fixture = await Fixture.CreateAsync();
        var seed = ScheduledQueryRule.Example(false);
        seed.WorkspaceKey = "production";
        await fixture.Service.SaveAsync(seed);
        async Task<string> Snapshot() => JsonSerializer.Serialize(await fixture.Service.ListAsync());
        var original = await Snapshot();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const string kql = "// Original <KQL> &\r\nprint Series = \"Workspace\", Value = real(null)";
        var start = new DateTime(2026, 10, 28, 12, 1, 7, DateTimeKind.Utc);
        var end = new DateTime(2026, 10, 28, 12, 6, 53, DateTimeKind.Utc);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = Assert.IsType<Components.Pages.AdminScheduledQueries>(activator.Page);
            var type = page.GetType();
            void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(page, value);
            string Render()
            {
                type.GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                return root.ToHtmlString();
            }
            await (Task)type.GetMethod("BeginNew", flags)!.Invoke(page, [false])!;
            var draft = Assert.IsType<ScheduledQueryRule>(type.GetField("editing", flags)!.GetValue(page));
            draft.Scope = workspaceScope ? AnalysisQueryScopes.Workspace : AnalysisQueryScopes.SelectedSystems;
            draft.Threshold = 9.125;
            var plans = Assert.IsType<List<ScheduledQueryExecutionPreview>>(type.GetField("executionPreviews", flags)!.GetValue(page));
            plans.AddRange(Enumerable.Range(0, 3).Select(minutes => new ScheduledQueryExecutionPreview(
                new($"Original <key-{minutes}> &", "CPU example <workspace> &",
                    minutes == 0 ? [] : ["Original <system> &"], kql),
                "Original <workspace ID> &", start, end, minutes)));
            var emptyAccounts = new ScheduledQueryEventDetails(4624, [], 0, false);
            var accounts = new ScheduledQueryEventDetails(4768, ["Original <account> &", "No accounts extracted."], 1, true);
            var results = new List<ScheduledQueryWorkspaceEvaluation>();
            foreach (var count in new[] { 0, 1, 2, 51 })
            {
                var targets = Enumerable.Range(0, count)
                    .Select(index => new ScheduledQueryTarget($"subscription-{index}", $"Original <target-{index}> &")).ToArray();
                var values = Enumerable.Range(0, count).ToDictionary(index => targets[index].Series,
                    index => index % 3 == 0 ? (double?)null : index % 3 == 1 ? 10.625 : 8.125);
                values.Add("Unmatched row", 123.875);
                results.Add(new($"Original <result-{count}> &", targets, values, null,
                    count == 51 ? new Dictionary<string, ScheduledQueryEventDetails>
                    {
                        ["empty"] = emptyAccounts, ["accounts"] = accounts
                    } : null));
            }
            foreach (var error in new[] { "", "Original <query error> &" })
                results.Add(new("Error <workspace> &", [new("subscription-error", "Error <target> &")],
                    new Dictionary<string, double?>(), error));
            var preview = new ScheduledQueryEvaluation(end, results);
            Set("preview", preview);
            var originalPreview = JsonSerializer.Serialize(preview);
            var originalPlans = JsonSerializer.Serialize(plans);
            var originalDraft = JsonSerializer.Serialize(draft);
            foreach (var busy in new[] { false, true })
            foreach (var count in new[] { 0, 1, 50, 51, 100, 101 })
            {
                Set("busy", busy);
                var states = Enumerable.Range(0, count).Select(index => new ScheduledQueryTargetState
                {
                    RuleId = seed.Id, WorkspaceKey = $"Original <saved-{index}> &",
                    SubscriptionId = index == 0 ? "" : $"subscription-{index}",
                    Computer = $"Original <saved computer-{index}> &",
                    Status = new[] { "No data", "Error", "Normal", "Would alert", "Original <status> &" }[index % 5],
                    Value = 90.125 + index, ActiveAlertId = index % 2 == 0 ? null : "Original <alert ID> &",
                    CheckedUtc = end.AddSeconds(index)
                }).ToArray();
                Set("states", states);
                var originalStates = JsonSerializer.Serialize(states);
                var pages = Math.Max(1, (count + 49) / 50);
                for (var statePage = 0; statePage < pages; statePage++)
                {
                    Set("statePage", statePage);
                    var raw = Render();
                    var html = WebUtility.HtmlDecode(raw);
                    var planCards = Regex.Matches(raw, "<article class=\"analysis-execution-preview-card\">.*?</article>", RegexOptions.Singleline);
                    Assert.Equal(3, planCards.Count);
                    for (var index = 0; index < planCards.Count; index++)
                    {
                        var plan = WebUtility.HtmlDecode(planCards[index].Value);
                        Assert.Contains(german ? "<h3>Effektive KQL für diesen Test</h3>" : "<h3>Effective KQL for this test</h3>", plan);
                        Assert.Contains($"Workspace: CPU example <workspace> & (Original <key-{index}> &) · {(german ? "Workspace-ID" : "Workspace ID")}: Original <workspace ID> &", plan);
                        Assert.Contains(german
                            ? $"UTC-Fenster: 2026-10-28 12:01:07 (einschließlich) bis 2026-10-28 12:06:53 (ausschließlich). API-Rückblick: {index} {(index == 1 ? "Minute" : "Minuten")}."
                            : $"UTC window: 2026-10-28 12:01:07 (inclusive) to 2026-10-28 12:06:53 (exclusive). API lookback: {index} minutes.", plan);
                        Assert.Contains(german
                            ? workspaceScope
                                ? "Das Fenster berücksichtigt die konfigurierte Ingestion-Verzögerung. Der gesamte ausgewählte Workspace wird ausgewertet; ein Inventarabgleich ist nicht erforderlich."
                                : "Das Fenster berücksichtigt die konfigurierte Ingestion-Verzögerung. Nur Daten mit exakt übereinstimmenden Inventar-Computernamen werden ausgewertet."
                            : workspaceScope
                                ? "The window includes the configured ingestion delay. The entire selected workspace is evaluated; no inventory match is required."
                                : "The window includes the configured ingestion delay. Only data matching the exact inventory computer names is evaluated.", plan);
                        Assert.Equal(!workspaceScope && index == 0, plan.Contains(german
                            ? "Nicht ausgeführt: Zu diesen Filtern passen keine Inventarsysteme."
                            : "Not executed: no inventory systems match these filters."));
                        Assert.Contains(german ? "<summary>Generierte Abfrage · auswählen und nach Log Analytics kopieren</summary>"
                            : "<summary>Generated query · select and copy into Log Analytics</summary>", plan);
                        Assert.Contains("<details open>", plan);
                        Assert.Equal(kql, WebUtility.HtmlDecode(Regex.Match(planCards[index].Value, "<pre>(.*?)</pre>", RegexOptions.Singleline).Groups[1].Value));
                        Assert.Contains("Original &lt;KQL&gt; &amp;", planCards[index].Value);
                    }
                    Assert.Contains(german ? "<h3>Testergebnis · Fensterende (UTC): 2026-10-28 12:06:53</h3>"
                        : "<h3>Test result · window end (UTC): 2026-10-28 12:06:53</h3>", html);
                    Assert.Contains(german
                        ? "Dieser Test erzeugt keine Alarme. Bis zu 50 Ziele je Workspace werden angezeigt. Nach Änderungen an KQL oder Filtern erneut testen."
                        : "This test creates no alerts. Showing up to 50 targets per workspace. Retest after changing KQL or filters.", html);
                    var tables = Regex.Matches(raw, "<table>.*?</table>", RegexOptions.Singleline);
                    Assert.Equal(results.Count + (count > 0 ? 1 : 0), tables.Count);
                    static string[] Cells(string row, string tag) => Regex.Matches(row, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline)
                        .Cast<Match>().Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
                    static MatchCollection Rows(string table) => Regex.Matches(
                        Regex.Match(table, "<tbody>(.*?)</tbody>", RegexOptions.Singleline).Groups[1].Value, "<tr>.*?</tr>", RegexOptions.Singleline);
                    for (var index = 0; index < results.Count; index++)
                    {
                        var result = results[index];
                        Assert.Contains($"<h4>Workspace {result.WorkspaceKey}</h4>", html);
                        Assert.Contains(german
                            ? $"Auswertungsziele: {result.Targets.Count}; zurückgegebene Zeilen: {result.Values.Count}."
                            : $"{result.Targets.Count} evaluation targets; {result.Values.Count} returned rows.", html);
                        Assert.Equal(german ? ["Ziel", "Abonnement", "Wert", "Auswertung"]
                            : new[] { "Target", "Subscription", "Value", "Evaluation" }, Cells(tables[index].Value, "th"));
                        var rows = Rows(tables[index].Value);
                        Assert.Equal(Math.Min(50, result.Targets.Count), rows.Count);
                        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                        {
                            var cells = Cells(rows[rowIndex].Value, "td");
                            var target = result.Targets[rowIndex];
                            Assert.Equal(target.DisplayName, cells[0]);
                            Assert.Equal(target.SubscriptionId, cells[1]);
                            Assert.Equal(rowIndex % 3 == 0 ? "" : rowIndex % 3 == 1 ? "10,625" : "8,125", cells[2]);
                            Assert.Equal(result.Error is not null ? (german ? "Fehler" : "Error")
                                : rowIndex % 3 == 0 ? (german ? "Keine Daten" : "No data")
                                : rowIndex % 3 == 1 ? (german ? "Würde Alarm auslösen" : "Would alert") : "Normal", cells[3]);
                        }
                    }
                    Assert.Contains(german ? "<h5>Event 4624 · Konten / Änderungen</h5>" : "<h5>Event 4624 · Accounts / changes</h5>", html);
                    Assert.Contains(german ? "<h5>Event 4768 · Konten / Änderungen</h5>" : "<h5>Event 4768 · Accounts / changes</h5>", html);
                    Assert.Contains(german ? "<p>Keine Konten extrahiert.</p>" : "<p>No accounts extracted.</p>", html);
                    Assert.Contains("<li>No accounts extracted.</li>", html);
                    Assert.Contains("<li>Original &lt;account&gt; &amp;</li>", raw);
                    Assert.Contains(services.GetRequiredService<UiText>().Format(
                        "AdminScheduledQueries.EventWarnings.Both.One", 1L, ScheduledQueryEventDetails.MaxUsers), html);
                    Assert.DoesNotContain("Original &lt;query error&gt; &amp;", raw);
                    Assert.Contains(services.GetRequiredService<UiText>()["AdminScheduledQueries.Errors.PreviewWorkspaceFailed"], html);
                    Assert.Equal(2, Regex.Matches(raw, "<div class=\"admin-message admin-message-error\" role=\"alert\">").Count);
                    Assert.Equal(count > 0, html.Contains(german ? "<h3>Gespeicherter Zielstatus</h3>" : "<h3>Saved target status</h3>"));
                    if (count > 0)
                    {
                        Assert.Contains(german
                            ? "Enthält frühere Ziele; fehlende oder ausgeschlossene Ziele behalten einen zuvor aktiven Alarm."
                            : "Includes earlier targets; missing or excluded targets retain any previous active alert.", html);
                        var table = tables[tables.Count - 1].Value;
                        Assert.Equal(german ? ["Arbeitsbereich", "Ziel", "Abonnement", "Status", "Wert", "Aktiver Alarm", "Geprüft (UTC)"]
                            : new[] { "Workspace", "Target", "Subscription", "Status", "Value", "Active alert", "Checked (UTC)" }, Cells(table, "th"));
                        var rows = Rows(table);
                        var expectedStates = states.Skip(statePage * 50).Take(50).ToArray();
                        Assert.Equal(expectedStates.Length, rows.Count);
                        for (var index = 0; index < rows.Count; index++)
                        {
                            var state = expectedStates[index];
                            Assert.Equal(new[]
                            {
                                state.WorkspaceKey, state.SubscriptionId.Length == 0 ? $"Workspace: {state.WorkspaceKey}" : state.Computer,
                                state.SubscriptionId, state.Status, state.Value!.Value.ToString(CultureInfo.GetCultureInfo("fr-FR")),
                                state.ActiveAlertId is null ? (german ? "Nein" : "No") : (german ? "Ja" : "Yes"),
                                state.CheckedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                            }, Cells(rows[index].Value, "td"));
                        }
                        Assert.Contains(german ? $"<span>Seite {statePage + 1} / {pages}</span>" : $"<span>Page {statePage + 1} / {pages}</span>", html);
                        var paging = Regex.Matches(raw, "<button\\b([^>]*)>(.*?)</button>", RegexOptions.Singleline)
                            .Cast<Match>().Where(button => new[] { "Previous", "Next", "Zurück", "Weiter" }
                                .Contains(WebUtility.HtmlDecode(button.Groups[2].Value))).ToArray();
                        Assert.Equal(2, paging.Length);
                        Assert.Equal(statePage == 0, paging[0].Groups[1].Value.Contains("disabled"));
                        Assert.Equal(statePage == pages - 1, paging[1].Groups[1].Value.Contains("disabled"));
                    }
                    Assert.Equal(originalStates, JsonSerializer.Serialize(states));
                    Assert.Equal(originalPreview, JsonSerializer.Serialize(preview));
                    Assert.Equal(originalPlans, JsonSerializer.Serialize(plans));
                    Assert.Equal(originalDraft, JsonSerializer.Serialize(draft));
                }
            }
            Set("states", Array.Empty<ScheduledQueryTargetState>());
            Set("preview", null);
            plans.Clear();
            var empty = WebUtility.HtmlDecode(Render());
            Assert.DoesNotContain(german ? "<h3>Testergebnis" : "<h3>Test result", empty);
            Assert.DoesNotContain("analysis-execution-preview-card", empty);
            Set("preview", preview);
            Set("editing", null);
            empty = WebUtility.HtmlDecode(Render());
            Assert.Contains(german ? "Eine eigene Regel erstellen, mit einem Beispiel beginnen oder eine bestehende Regel auswählen."
                : "Create a custom rule, start from an example, or select an existing rule.", empty);
            Assert.DoesNotContain("<table>", empty);
        });
        Assert.Equal(original, await Snapshot());
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Empty(await fixture.AlertRecordsAsync());
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal(0, await context.ScheduledQueryNotifications.CountAsync());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ConfigurationDeletionAndStatusesPreserveGuardsAlertsAndPendingDelivery(string culture, bool sameEditor)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var german = culture == "de-DE";
        await using var fixture = await Fixture.CreateAsync();
        fixture.Rule.Name = "Scheduled rule deleted. Existing alerts are retained. <rule> &";
        await fixture.Service.SaveAsync(fixture.Rule);
        fixture.Query.Response = Result(("DC01", 99));
        await fixture.Service.RunOnceAsync(default);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Single(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var notification = await context.ScheduledQueryNotifications.SingleAsync();
            notification.DeliveredUtc = null;
            notification.LastError = "Original <delivery> &";
            await context.SaveChangesAsync();
        }
        async Task<string> Rules() => JsonSerializer.Serialize(await fixture.Service.ListAsync());
        async Task<string> Alerts() => JsonSerializer.Serialize(await fixture.AlertRecordsAsync());
        async Task<string> Notifications()
        {
            await using var context = fixture.Factory.CreateDbContext();
            return JsonSerializer.Serialize(await context.ScheduledQueryNotifications.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync());
        }
        var originalRules = await Rules();
        var originalAlerts = await Alerts();
        var originalNotifications = await Notifications();
        var stored = Assert.Single(await fixture.Service.ListAsync());
        var originalStates = JsonSerializer.Serialize(await fixture.Service.StatesAsync(stored.Id));
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            var page = Assert.IsType<Components.Pages.AdminScheduledQueries>(activator.Page);
            var type = page.GetType();
            void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(page, value);
            object? Get(string name) => type.GetField(name, flags)!.GetValue(page);
            Task Call(string name, params object?[] arguments) => (Task)type.GetMethod(name, flags)!.Invoke(page, arguments)!;
            string Render()
            {
                type.GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                return root.ToHtmlString();
            }
            await Call("EditAsync", stored);
            var editing = Assert.IsType<ScheduledQueryRule>(Get("editing"));
            Set("deleteRule", stored);
            foreach (var busy in new[] { false, true })
            {
                Set("busy", busy);
                var raw = Render();
                var dialog = Regex.Match(raw, "<section class=\"admin-message admin-message-error\" role=\"alert\">.*?</section>", RegexOptions.Singleline).Value;
                Assert.NotEmpty(dialog);
                Assert.Contains(german
                    ? $"'{stored.Name}' löschen? Prüfungen werden dauerhaft beendet. Bereits erzeugte Alarme bleiben erhalten und müssen, falls noch aktiv, manuell aufgelöst werden. Ausstehende Benachrichtigungen werden weiterhin zugestellt."
                    : $"Delete '{stored.Name}'? Checks stop permanently. Previously emitted alerts are retained and must be resolved manually if still active. Pending notifications will still be delivered.",
                    WebUtility.HtmlDecode(dialog));
                Assert.Contains("Scheduled rule deleted. Existing alerts are retained. &lt;rule&gt; &amp;", dialog);
                Assert.DoesNotContain("<strong>", dialog);
                Assert.Contains(german ? ">Löschen bestätigen</button>" : ">Confirm delete</button>", WebUtility.HtmlDecode(dialog));
                Assert.Contains(german ? ">Abbrechen</button>" : ">Cancel</button>", WebUtility.HtmlDecode(dialog));
                var buttons = Regex.Matches(dialog, "<button\\b[^>]*>");
                Assert.Equal(2, buttons.Count);
                Assert.All(buttons.Cast<Match>(), button => Assert.Equal(busy, button.Value.Contains("disabled")));
            }
            Set("message", "Original <status> &");
            Set("error", "Original <error> &");
            foreach (var call in new Func<Task>[]
            {
                () => Call("ImportJsonAsync", "{"), () => Call("SaveAsync"),
                () => Call("DeleteAsync"), () => Call("DisableAsync", stored)
            })
            {
                await call();
                Assert.Same(editing, Get("editing"));
                Assert.Same(stored, Get("deleteRule"));
                Assert.Equal("Original <status> &", Get("message"));
                Assert.Equal("Original <error> &", Get("error"));
                Assert.Equal(originalRules, await Rules());
            }
            Set("busy", false);
            Set("deleteRule", null);
            Assert.DoesNotContain("<section class=\"admin-message admin-message-error\" role=\"alert\">", Render());
            Assert.Equal(originalRules, await Rules());
            Set("deleteRule", stored);
            await Call("DeleteAsync");
            Assert.Equal(services.GetRequiredService<UiText>()["ScheduledQueries.Validation.DeleteNotAllowed"], Get("error"));
            Assert.Null(Get("message"));
            Assert.Same(stored, Get("deleteRule"));
            Assert.Equal(originalRules, await Rules());
            fixture.Factory.OnCreate = () => throw new InvalidOperationException("Original <storage> &");
            try { await Call("SaveAsync"); }
            finally { fixture.Factory.OnCreate = null; }
            Assert.Equal(services.GetRequiredService<UiText>()["AdminScheduledQueries.Errors.SaveFailed"], Get("error"));
            Assert.Null(Get("message"));
            Assert.Same(editing, Get("editing"));
            Assert.DoesNotContain("Original &lt;storage&gt; &amp;", Render());
            await Call("DisableAsync", new ScheduledQueryRule { Id = stored.Id, Revision = stored.Revision - 1 });
            Assert.Equal(services.GetRequiredService<UiText>()["AdminScheduledQueries.Errors.Changed"], Get("error"));
            Assert.Null(Get("message"));
            Assert.False(Assert.IsType<bool>(Get("busy")));
            Assert.Equal(originalRules, await Rules());
            if (!sameEditor) await Call("BeginNew", (object?)null);
            var beforeDisable = Get("editing");
            await Call("DisableAsync", stored);
            Assert.Null(Get("error"));
            Assert.Equal(german ? "Regel deaktiviert. Bestehende Alarme bleiben erhalten."
                : "Rule disabled. Existing alerts are retained.", Get("message"));
            Assert.Contains(german ? "Regel deaktiviert. Bestehende Alarme bleiben erhalten."
                : "Rule disabled. Existing alerts are retained.", WebUtility.HtmlDecode(Render()));
            if (sameEditor) Assert.Null(Get("editing"));
            else Assert.Same(beforeDisable, Get("editing"));
            var disabled = Assert.Single(await fixture.Service.ListAsync());
            var expectedDisabled = JsonSerializer.Deserialize<ScheduledQueryRule>(JsonSerializer.Serialize(stored))!;
            expectedDisabled.Enabled = false;
            expectedDisabled.Revision++;
            Assert.Equal(JsonSerializer.Serialize(expectedDisabled), JsonSerializer.Serialize(disabled));
            Assert.Equal(originalStates, JsonSerializer.Serialize(await fixture.Service.StatesAsync(stored.Id)));
            Assert.Equal(originalAlerts, await Alerts());
            Assert.Equal(originalNotifications, await Notifications());
            if (sameEditor) await Call("EditAsync", disabled);
            var beforeDelete = Get("editing");
            Set("deleteRule", disabled);
            await Call("DeleteAsync");
            Assert.Null(Get("error"));
            Assert.Null(Get("deleteRule"));
            if (sameEditor) Assert.Null(Get("editing"));
            else Assert.Same(beforeDelete, Get("editing"));
            Assert.Equal(german ? "Geplante Regel gelöscht. Bestehende Alarme bleiben erhalten."
                : "Scheduled rule deleted. Existing alerts are retained.", Get("message"));
            Assert.Contains(german ? "Geplante Regel gelöscht. Bestehende Alarme bleiben erhalten."
                : "Scheduled rule deleted. Existing alerts are retained.", WebUtility.HtmlDecode(Render()));
            Assert.Empty(await fixture.Service.ListAsync());
            Assert.Equal(originalAlerts, await Alerts());
            Assert.Equal(originalNotifications, await Notifications());
            Assert.Equal(1, fixture.Query.Calls);
            await fixture.Service.RunOnceAsync(default);
            Assert.Equal(1, fixture.Query.Calls);
            Assert.Single(await fixture.AlertRecordsAsync());
            Assert.Single(AlertLifecycle.GetActiveAlerts(await fixture.AlertRecordsAsync()));
            Assert.Equal((0, (string?)null), await fixture.Service.DeliveryStatusAsync());
            await using (var context = fixture.Factory.CreateDbContext())
            {
                var notification = await context.ScheduledQueryNotifications.SingleAsync();
                Assert.NotNull(notification.DeliveredUtc);
                Assert.Empty(notification.LastError);
            }
            await Call("RefreshAsync");
            Assert.Equal(services.GetRequiredService<UiText>()["AdminScheduledQueries.Status.Deleted"], Get("message"));
            Assert.Null(Get("error"));
            Assert.Equal((0, (string?)null), Assert.IsType<(int, string?)>(Get("delivery")));
        });
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    private static void AssertScheduledEditorHelp(string raw, string culture, string scope, string kql)
    {
        var german = culture == "de-DE";
        var form = Regex.Match(raw, "<form class=\"admin-form\".*?</form>", RegexOptions.Singleline).Value;
        var label = Regex.Match(form, "<label class=\"admin-field admin-field-wide\">\\s*<span>KQL</span>.*?</label>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(label);
        var query = Regex.Match(label, "<textarea\\b[^>]*>(.*?)</textarea>", RegexOptions.Singleline);
        Assert.Equal(kql, WebUtility.HtmlDecode(query.Groups[1].Value));
        var help = Regex.Matches(label, "<small>(.*?)</small>", RegexOptions.Singleline)
            .Cast<Match>().Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        string[] expected = scope == AnalysisQueryScopes.Workspace
            ? german
                ? [
                    "_WindowStart (einschließlich) und _WindowEnd (ausschließlich) verwenden. Genau eine Zeile mit Series = \"Workspace\" und einem numerischen Wert oder null in Value zurückgeben. _SelectedSystems nicht verwenden. Keine Zeile oder ein null-Wert bedeutet No data.",
                    "Jedes Event-Beispiel filtert eine EventID und enthält EventId, Users (JSON-Array), UnparsedEvents und UsersTruncated als Alarmdetails. Kontenfilter werden bereits bei der Erfassung angewendet. Indizes in XML Data: 4624 = 5; 4768/4769 = 0; 5136 = 3; 4794 = 1; 4724/4736 = Akteur 4 ändert Konto 0. Diese Positionen anhand der erfassten EventData überprüfen.",
                    $"Die Ereignisanzahl wird nur gemessen, wenn Security-Ereignisse beobachtet wurden; ein leeres Telemetriefenster liefert null, nicht 0. Die Kontenliste zeigt höchstens {ScheduledQueryEventDetails.MaxUsers} unterschiedliche Einträge. Fehlende Kontenfelder und gekürzte Listen werden ausdrücklich gemeldet. Bereits gespeicherte Regeln werden nicht überschrieben; gruppierte Regeln bewusst durch separate Event-Vorlagen ersetzen."
                ]
                : [
                    "Use _WindowStart (inclusive) and _WindowEnd (exclusive). Return one row with Series = \"Workspace\" and a numeric or null Value. Do not use _SelectedSystems. No row or a null value means No data.",
                    "Each event example filters one EventID and includes EventId, Users (JSON array), UnparsedEvents and UsersTruncated as alert details. Account filtering is already applied during collection. XML Data indices: 4624 = 5; 4768/4769 = 0; 5136 = 3; 4794 = 1; 4724/4736 = actor 4 changes account 0. Verify these positions against your collected EventData.",
                    $"The event count is measured only when Security events were observed; an empty telemetry window returns null, not zero. The account list shows at most {ScheduledQueryEventDetails.MaxUsers} distinct entries. Missing account fields and truncated lists are reported explicitly. Existing saved rules are not overwritten; replace grouped rules with separate event templates deliberately."
                ]
            : german
                ? [
                    "_SelectedSystems, _WindowStart (einschließlich) und _WindowEnd (ausschließlich) verwenden. Für jeden Computer Series = Computer und einen numerischen Wert oder null in Value zurückgeben. Innerhalb der KQL aggregieren; die Beispiele verwenden den Durchschnitt über das gesamte Fenster, nicht fünf aufeinanderfolgende Schwellwertüberschreitungen. Keine Zeile oder ein null-Wert bedeutet No data.",
                    "Das CPU-Beispiel benötigt den Zähler Processor/Processor Information % Idle Time (_Total); CPU-Auslastung = 100 - durchschnittliche Leerlaufzeit. Kerberos benötigt den Zähler Security System-Wide Statistics / Kerberos Authentications. Fehlende Zähler lösen keinen Alarm mit dem Wert 0 aus."
                ]
                : [
                    "Use _SelectedSystems, _WindowStart (inclusive) and _WindowEnd (exclusive). Return Series = Computer and one numeric or null Value per computer. Aggregate inside the KQL; the examples use the average over the complete window, not five consecutive threshold breaches. No row or a null value means No data.",
                    "CPU example requires the Processor/Processor Information % Idle Time counter (_Total); CPU usage = 100 - average idle. Kerberos requires the Security System-Wide Statistics / Kerberos Authentications counter. Missing counters do not trigger a zero-value alert."
                ];
        Assert.Equal(expected, help);
        var lifecycle = Regex.Match(form, "<p class=\"admin-field-wide\">(.*?)</p>", RegexOptions.Singleline);
        Assert.Equal(german
            ? "Ein Alarm wird nur beim Eintritt in die Schwellwertbedingung ausgelöst und bei gemessener Erholung aufgelöst. Das Deaktivieren oder Ändern der Zielauswahl löst bestehende Alarme nicht auf. Vorhandene Regeln zur Kategorisierung im Posteingang haben Vorrang vor dieser Kategorie."
            : "An alert fires only on entry into the threshold condition and resolves on a measured recovery. Disabling or changing the target selection does not resolve existing alerts. Existing inbox categorization rules take precedence over this category.",
            WebUtility.HtmlDecode(lifecycle.Groups[1].Value));
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        private readonly CultureInfo previousFormat = CultureInfo.CurrentCulture;
        public UiSelection(string culture, string? formatCulture = null)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            if (formatCulture is not null) CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(formatCulture);
        }
        public void Dispose()
        {
            CultureInfo.CurrentUICulture = previous;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }

    [Theory]
    [MemberData(nameof(QueryJsonImportTests.InvalidImportCases), MemberType = typeof(QueryJsonImportTests))]
    public async Task ScheduledImportBoundariesPreserveDraftAndTargetState(string culture, string input, QueryTemplateValidationCode code)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var writes = new ScheduledWriteProbe();
        await using var fixture = await Fixture.CreateAsync(interceptors: [writes]);
        await fixture.Service.SaveAsync(fixture.Rule);
        var saved = Assert.Single(await fixture.Service.ListAsync());
        var snapshot = JsonSerializer.Serialize(saved);
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        await page.InvokeAsync("EditAsync", saved);
        var draft = Assert.IsType<ScheduledQueryRule>(page.Field("editing"));
        draft.Kql = "Original unsaved KQL";
        var states = page.Field("states");
        page.Set("statePage", 3);
        page.Set("deleteRule", saved);
        var attempts = writes.Attempts;
        await page.InvokeAsync("ImportJsonAsync", QueryJsonImportTests.InvalidImportJson(input));
        Assert.Equal(code, Assert.IsType<QueryTemplateValidationException>(Assert.Single(page.Log.Exceptions)).Code);
        var expected = page.Text[$"QueryTemplate.Validation.{code}"];
        Assert.Equal(expected, page.Field("error"));
        var html = await page.HtmlAsync();
        Assert.Contains(expected, html);
        Assert.DoesNotContain("private JSON diagnostic", html);
        Assert.Same(draft, page.Field("editing"));
        Assert.Equal("Original unsaved KQL", draft.Kql);
        Assert.Same(states, page.Field("states"));
        Assert.Equal(3, page.Field("statePage"));
        Assert.Same(saved, page.Field("deleteRule"));
        Assert.Null(page.Field("message"));
        Assert.Null(page.Field("loadError"));
        Assert.Equal(attempts, writes.Attempts);
        Assert.Equal(snapshot, JsonSerializer.Serialize(Assert.Single(await fixture.Service.ListAsync())));
        Assert.Equal(1, page.Log.Warnings);
        Assert.False(Assert.IsType<bool>(page.Field("busy")));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        if (code == QueryTemplateValidationCode.ImportTooLarge)
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "Scheduled", "cpu-high.json"));
            json += new string(' ', QueryTemplateJson.MaxImportBytes - System.Text.Encoding.UTF8.GetByteCount(json));
            Assert.Equal(65536, System.Text.Encoding.UTF8.GetByteCount(json));
            await page.InvokeAsync("ImportJsonAsync", json);
            Assert.Null(page.Field("error"));
            Assert.Equal(page.Text["AdminScheduledQueries.Status.ImportOpened"], page.Field("message"));
            var imported = Assert.IsType<ScheduledQueryRule>(page.Field("editing"));
            Assert.NotSame(draft, imported);
            Assert.NotEqual(saved.Id, imported.Id);
            Assert.False(imported.Enabled);
            Assert.Equal(0, imported.Revision);
            Assert.Equal(fixture.Options.Workspaces.OrderBy(workspace => workspace.DisplayName).Take(1)
                .Select(workspace => workspace.Key), imported.WorkspaceKeys);
            Assert.Equal(0, page.Field("statePage"));
            Assert.Equal(attempts, writes.Attempts);
            Assert.Equal(snapshot, JsonSerializer.Serialize(Assert.Single(await fixture.Service.ListAsync())));
        }
    }

    public static IEnumerable<object[]> ScheduledDataCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var code in Enum.GetValues<ScheduledQueryDataCode>())
        foreach (var json in new[] { "null", "{", "{}" })
            yield return [culture, code, json];
    }

    [Theory]
    [MemberData(nameof(ScheduledDataCases))]
    public async Task ScheduledStoredDataReadFailuresAreAtomicAndRecoverWithoutWrites(
        string culture, ScheduledQueryDataCode code, string corrupt)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var writes = new ScheduledWriteProbe();
        await using var fixture = await Fixture.CreateAsync(interceptors: [writes]);
        await fixture.Service.SaveAsync(fixture.Rule);
        var saved = Assert.Single(await fixture.Service.ListAsync());
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        await page.InvokeAsync("EditAsync", saved);
        var draft = page.Field("editing");
        var rules = page.Field("rules");
        var states = page.Field("states");
        var original = code == ScheduledQueryDataCode.WorkspaceSelectionInvalid ? saved.WorkspaceKeysJson : saved.LastWorkspaceResultsJson;
        await using (var context = fixture.Factory.CreateDbContext())
        {
            if (code == ScheduledQueryDataCode.WorkspaceSelectionInvalid)
                await context.ScheduledQueryRules.ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.WorkspaceKeysJson, corrupt));
            else
                await context.ScheduledQueryRules.ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.LastWorkspaceResultsJson, corrupt));
        }
        var attempts = writes.Attempts;
        await page.InvokeAsync("RefreshAsync");
        var expected = page.Text[$"ScheduledQueries.Data.{code}"];
        Assert.Equal(expected, page.Field("loadError"));
        Assert.Contains(expected, await page.HtmlAsync());
        Assert.Same(rules, page.Field("rules"));
        Assert.Same(draft, page.Field("editing"));
        Assert.Same(states, page.Field("states"));
        var error = Assert.Single(page.Log.Exceptions);
        if (corrupt == "null")
        {
            Assert.Equal(code, Assert.IsType<ScheduledQueryDataException>(error).Code);
            Assert.IsAssignableFrom<InvalidOperationException>(error);
        }
        else
        {
            var json = Assert.IsType<ScheduledQueryDataJsonException>(error);
            Assert.Equal(code, json.Code);
            var originalError = Assert.IsAssignableFrom<JsonException>(json.InnerException);
            Assert.Equal(originalError.Message, json.Message);
            Assert.Equal(originalError.Path, json.Path);
            Assert.Equal(originalError.BytePositionInLine, json.BytePositionInLine);
            Assert.False(typeof(InvalidOperationException).IsInstanceOfType(json));
        }
        await page.InvokeBlockedAsync(saved);
        Assert.Equal(attempts, writes.Attempts);
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(corrupt, code == ScheduledQueryDataCode.WorkspaceSelectionInvalid ? stored.WorkspaceKeysJson : stored.LastWorkspaceResultsJson);
        Assert.Equal(saved.Revision, stored.Revision);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            if (code == ScheduledQueryDataCode.WorkspaceSelectionInvalid)
                await context.ScheduledQueryRules.ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.WorkspaceKeysJson, original));
            else
                await context.ScheduledQueryRules.ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.LastWorkspaceResultsJson, original));
        }
        Assert.Equal(attempts + 1, writes.Attempts);
        var recoveryAttempts = writes.Attempts;
        await page.InvokeAsync("RefreshAsync");
        Assert.Null(page.Field("loadError"));
        Assert.Equal(recoveryAttempts, writes.Attempts);
        Assert.Same(draft, page.Field("editing"));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData(ScheduledQueryDataCode.WorkspaceSelectionInvalid)]
    [InlineData(ScheduledQueryDataCode.WorkspaceResultsInvalid)]
    public async Task ScheduledMalformedCandidateDoesNotReplaceEditor(ScheduledQueryDataCode code)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        await page.InvokeAsync("BeginNew", (object?)null);
        var original = page.Field("editing");
        var candidate = new ScheduledQueryRule();
        if (code == ScheduledQueryDataCode.WorkspaceSelectionInvalid) candidate.WorkspaceKeysJson = "{";
        else candidate.LastWorkspaceResultsJson = "{";
        await page.InvokeAsync("EditAsync", candidate);
        Assert.Same(original, page.Field("editing"));
        Assert.Equal(page.Text[$"ScheduledQueries.Data.{code}"], page.Field("error"));
        Assert.Equal(code, Assert.IsType<ScheduledQueryDataJsonException>(Assert.Single(page.Log.Exceptions)).Code);
    }

    [Fact]
    public void ScheduledStoredDataKeepsLegacyWorkspaceAndNeutralErrors()
    {
        var rule = new ScheduledQueryRule { WorkspaceKey = " legacy ", WorkspaceKeysJson = "[]" };
        Assert.Equal("legacy", Assert.Single(rule.WorkspaceKeys));
        rule.WorkspaceKeys = ["Original workspace"];
        Assert.Equal("", rule.WorkspaceKey);
        Assert.Equal("Original workspace", Assert.Single(rule.WorkspaceKeys));
        rule.LastWorkspaceResultsJson = """[{"WorkspaceKey":"Original workspace","Targets":1,"Values":0,"Error":"Original private diagnostic","Legacy":true}]""";
        Assert.Equal("Original private diagnostic", Assert.Single(rule.WorkspaceResults).Error);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ScheduledDataProjectionRejectsUnknownCodesAndEnglishLookalikes(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.CreateAsync();
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        const string fallback = "AdminScheduledQueries.Errors.LoadFailed";
        foreach (var error in new Exception[]
        {
            new ScheduledQueryDataException((ScheduledQueryDataCode)int.MaxValue, "private unknown"),
            new ScheduledQueryDataJsonException((ScheduledQueryDataCode)int.MaxValue, new JsonException("private unknown")),
            new InvalidOperationException("The scheduled rule workspace selection is invalid."),
            new JsonException("The scheduled rule workspace status is invalid.")
        })
        {
            var value = typeof(Components.Pages.AdminScheduledQueries).GetMethod("OperationError",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page.Page, [error, fallback]);
            Assert.Equal(page.Text[fallback], value);
        }
    }

    public static IEnumerable<object[]> ScheduledValidationCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var code in Enum.GetValues<ScheduledQueryValidationCode>())
            yield return [culture, code];
    }

    [Theory]
    [MemberData(nameof(ScheduledValidationCases))]
    public async Task ScheduledServiceValidationCodesReachTheRealPageWithoutWrites(string culture, ScheduledQueryValidationCode code)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var writes = new ScheduledWriteProbe();
        await using var fixture = await Fixture.CreateAsync(interceptors: [writes], enabled: code != ScheduledQueryValidationCode.LiveDisabled);
        var rule = fixture.Rule;
        if (code is ScheduledQueryValidationCode.ScopeChangeNotAllowed or ScheduledQueryValidationCode.NameExists or ScheduledQueryValidationCode.DeleteNotAllowed)
        {
            await fixture.Service.SaveAsync(rule);
            rule = Assert.Single(await fixture.Service.ListAsync());
        }
        switch (code)
        {
            case ScheduledQueryValidationCode.LiveDisabled: break;
            case ScheduledQueryValidationCode.WorkspaceSelectionInvalid: rule.WorkspaceKeys = []; break;
            case ScheduledQueryValidationCode.WorkspaceUnavailable: rule.WorkspaceKeys = ["removed-workspace"]; break;
            case ScheduledQueryValidationCode.IdentityInvalid: rule.Id = Guid.Empty; break;
            case ScheduledQueryValidationCode.ScopeInvalid: rule.Scope = "unsupported"; break;
            case ScheduledQueryValidationCode.WorkspaceFiltersInvalid:
                rule.Scope = AnalysisQueryScopes.Workspace;
                rule.Domain = "example.com";
                break;
            case ScheduledQueryValidationCode.FiltersInvalid: rule.CategoryName = ""; break;
            case ScheduledQueryValidationCode.TimingInvalid: rule.IntervalMinutes = 0; break;
            case ScheduledQueryValidationCode.ConditionInvalid: rule.Threshold = double.NaN; break;
            case ScheduledQueryValidationCode.KqlInvalid: rule.Kql = ".show tables"; break;
            case ScheduledQueryValidationCode.ScopeChangeNotAllowed:
                rule.Scope = AnalysisQueryScopes.Workspace;
                rule.Domain = rule.Site = rule.Role = rule.Computers = "";
                rule.Kql = ScheduledQueryRule.EventExample(4624).Kql;
                break;
            case ScheduledQueryValidationCode.NameExists: rule.Id = Guid.NewGuid(); rule.Revision = 0; break;
            case ScheduledQueryValidationCode.DeleteNotAllowed: break;
            default: throw new ArgumentOutOfRangeException(nameof(code));
        }
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        page.Set("editing", rule);
        var attempts = writes.Attempts;
        if (code == ScheduledQueryValidationCode.DeleteNotAllowed)
        {
            page.Set("deleteRule", rule);
            await page.InvokeAsync("DeleteAsync");
            Assert.Same(rule, page.Field("deleteRule"));
            Assert.Equal(attempts + 1, writes.Attempts);
        }
        else
        {
            await page.InvokeAsync("SaveAsync");
            Assert.Equal(attempts, writes.Attempts);
        }
        Assert.Equal(code, Assert.IsType<ScheduledQueryValidationException>(Assert.Single(page.Log.Exceptions)).Code);
        Assert.Equal(1, page.Log.Warnings);
        Assert.Equal(page.Text[$"ScheduledQueries.Validation.{code}"], page.Field("error"));
        Assert.Contains(page.Text[$"ScheduledQueries.Validation.{code}"], await page.HtmlAsync());
        Assert.Null(page.Field("message"));
        Assert.Null(page.Field("loadError"));
        Assert.Same(rule, page.Field("editing"));
        Assert.False(Assert.IsType<bool>(page.Field("busy")));
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", false)]
    [InlineData("de-DE", true)]
    public async Task UnknownScheduledCodesAndEnglishLookalikesUseSafeFallbacks(string culture, bool unknownCode)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        await using var fixture = await Fixture.CreateAsync();
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        Exception exception = unknownCode
            ? new ScheduledQueryValidationException((ScheduledQueryValidationCode)int.MaxValue, "private diagnostic")
            : new InvalidOperationException("A scheduled query rule with this name already exists.");
        Func<Task> action = () => Task.FromException(exception);
        await page.InvokeAsync("ExecuteAsync", action, "AdminScheduledQueries.Errors.SaveFailed");
        Assert.Equal(page.Text["AdminScheduledQueries.Errors.SaveFailed"], page.Field("error"));
        Assert.Same(exception, Assert.Single(page.Log.Exceptions));
        Assert.DoesNotContain(exception.Message, await page.HtmlAsync());
    }

    [Theory]
    [InlineData("name", 256, ScheduledQueryValidationCode.IdentityInvalid)]
    [InlineData("workspace", 128, ScheduledQueryValidationCode.WorkspaceSelectionInvalid)]
    [InlineData("category", 256, ScheduledQueryValidationCode.FiltersInvalid)]
    [InlineData("domain", 256, ScheduledQueryValidationCode.FiltersInvalid)]
    [InlineData("site", 256, ScheduledQueryValidationCode.FiltersInvalid)]
    [InlineData("role", 256, ScheduledQueryValidationCode.FiltersInvalid)]
    [InlineData("computers", 4000, ScheduledQueryValidationCode.FiltersInvalid)]
    public async Task ScheduledValidationPreservesExactStringLengthLimits(string field, int limit, ScheduledQueryValidationCode code)
    {
        await using var fixture = await Fixture.CreateAsync();
        void Set(int length)
        {
            var value = new string('x', length);
            switch (field)
            {
                case "name": fixture.Rule.Name = value; break;
                case "workspace":
                    fixture.Options.Workspaces[0] = new() { Key = value };
                    fixture.Rule.WorkspaceKeys = [value];
                    break;
                case "category": fixture.Rule.CategoryName = value; break;
                case "domain": fixture.Rule.Domain = value; break;
                case "site": fixture.Rule.Site = value; break;
                case "role": fixture.Rule.Role = value; break;
                case "computers": fixture.Rule.Computers = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
        Set(limit);
        fixture.Service.Validate(fixture.Rule);
        Set(limit + 1);
        Assert.Equal(code, Assert.Throws<ScheduledQueryValidationException>(() => fixture.Service.Validate(fixture.Rule)).Code);
    }

    [Theory]
    [InlineData("interval", 1, 1440)]
    [InlineData("window", 1, 1440)]
    [InlineData("delay", 0, 60)]
    public async Task ScheduledValidationPreservesExactTimingLimits(string field, int minimum, int maximum)
    {
        await using var fixture = await Fixture.CreateAsync();
        void Set(int value)
        {
            switch (field)
            {
                case "interval": fixture.Rule.IntervalMinutes = value; break;
                case "window": fixture.Rule.WindowMinutes = value; break;
                case "delay": fixture.Rule.IngestionDelayMinutes = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
        foreach (var value in new[] { minimum, maximum })
        {
            Set(value);
            fixture.Service.Validate(fixture.Rule);
        }
        foreach (var value in new[] { minimum - 1, maximum + 1 })
        {
            Set(value);
            Assert.Equal(ScheduledQueryValidationCode.TimingInvalid,
                Assert.Throws<ScheduledQueryValidationException>(() => fixture.Service.Validate(fixture.Rule)).Code);
        }
    }

    public static IEnumerable<object[]> ScheduledReadFailureCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var source in new[] { 0, 1, 2 })
        foreach (var initial in source == 2 ? new[] { false } : new[] { false, true })
            yield return [culture, source, initial];
    }

    [Theory]
    [MemberData(nameof(ScheduledReadFailureCases))]
    public async Task ScheduledReadsAreAtomicAndBlockActionsUntilRecovery(string culture, int source, bool initial)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SaveAsync(fixture.Rule);
        if (initial) await RenameScheduledSource(fixture, source, false);
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        if (!initial) await page.InvokeAsync("EditAsync", Assert.Single(await fixture.Service.ListAsync()));
        var rules = page.Field("rules");
        var states = page.Field("states");
        var examples = page.Field("eventExamples");
        var draft = page.Field("editing");
        var delivery = page.Field("delivery");
        page.Set("statePage", 3);
        if (!initial)
        {
            await RenameScheduledSource(fixture, source, false);
            await page.InvokeAsync("RefreshAsync");
        }
        Assert.Equal(page.Text["AdminScheduledQueries.Errors.LoadFailed"], page.Field("loadError"));
        Assert.Same(rules, page.Field("rules"));
        Assert.Same(states, page.Field("states"));
        Assert.Same(examples, page.Field("eventExamples"));
        Assert.Same(draft, page.Field("editing"));
        Assert.Equal(delivery, page.Field("delivery"));
        Assert.Equal(3, page.Field("statePage"));
        Assert.IsType<SqliteException>(Assert.Single(page.Log.Exceptions));
        var html = await page.HtmlAsync();
        Assert.Contains(page.Text["AdminScheduledQueries.Errors.LoadFailed"], html);
        Assert.DoesNotContain("SQLite Error", html);
        if (initial)
        {
            Assert.DoesNotContain(page.Text["AdminScheduledQueries.Empty"], html);
            Assert.DoesNotContain(page.Text["AdminScheduledQueries.EditorEmpty"], html);
        }
        Assert.Matches("<button[^>]*disabled[^>]*>" + Regex.Escape(page.Text["AdminScheduledQueries.Create"]) + "</button>", html);
        await page.InvokeBlockedAsync(fixture.Rule);
        Assert.Same(draft, page.Field("editing"));
        Assert.Single(page.Log.Exceptions);
        await RenameScheduledSource(fixture, source, true);
        await page.InvokeAsync("RefreshAsync");
        Assert.Null(page.Field("loadError"));
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ScheduledQueryRule>>(page.Field("rules")));
        Assert.Equal(0, page.Field("statePage"));
        Assert.Equal(1, Assert.Single(await fixture.Service.ListAsync()).Revision);
        Assert.False(Assert.IsType<bool>(page.Field("busy")));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    public static IEnumerable<object[]> ScheduledCommitReadCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var action in new[] { "save", "disable", "delete" })
        foreach (var source in new[] { 0, 1 })
            yield return [culture, action, source];
    }

    [Theory]
    [MemberData(nameof(ScheduledCommitReadCases))]
    public async Task ScheduledCommitsRemainConfirmedWhenReloadFails(string culture, string action, int source)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var writes = new ScheduledWriteProbe();
        await using var fixture = await Fixture.CreateAsync(interceptors: [writes]);
        fixture.Rule.Enabled = action != "delete";
        await fixture.Service.SaveAsync(fixture.Rule);
        var saved = Assert.Single(await fixture.Service.ListAsync());
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        await page.InvokeAsync("EditAsync", saved);
        var oldRules = page.Field("rules");
        writes.AfterWrite = () => fixture.Factory.BeforeNextContext = () => RenameScheduledSource(fixture, source, false).GetAwaiter().GetResult();
        var attempts = writes.Attempts;
        if (action == "save") await page.InvokeAsync("SaveAsync");
        else if (action == "disable") await page.InvokeAsync("DisableAsync", saved);
        else
        {
            page.Set("deleteRule", saved);
            await page.InvokeAsync("DeleteAsync");
            Assert.Null(page.Field("deleteRule"));
        }
        var key = action switch
        {
            "save" => "AdminScheduledQueries.Status.Saved",
            "disable" => "AdminScheduledQueries.Status.Disabled",
            _ => "AdminScheduledQueries.Status.Deleted"
        };
        Assert.Equal(page.Text[key], page.Field("message"));
        Assert.Null(page.Field("error"));
        Assert.Null(page.Field("editing"));
        Assert.Equal(page.Text["AdminScheduledQueries.Errors.LoadFailed"], page.Field("loadError"));
        Assert.Same(oldRules, page.Field("rules"));
        Assert.Equal(attempts + 1, writes.Attempts);
        Assert.IsType<SqliteException>(Assert.Single(page.Log.Exceptions));
        var html = await page.HtmlAsync();
        Assert.Contains(page.Text[key], html);
        Assert.Contains(page.Text["AdminScheduledQueries.Errors.LoadFailed"], html);
        await page.InvokeBlockedAsync(saved);
        Assert.Equal(attempts + 1, writes.Attempts);
        Assert.Equal(page.Text[key], page.Field("message"));
        await RenameScheduledSource(fixture, source, true);
        await page.InvokeAsync("RefreshAsync");
        Assert.Null(page.Field("loadError"));
        Assert.Equal(attempts + 1, writes.Attempts);
        var stored = await fixture.Service.ListAsync();
        if (action == "delete") Assert.Empty(stored);
        else
        {
            Assert.Equal(2, Assert.Single(stored).Revision);
            Assert.Equal(action != "disable", Assert.Single(stored).Enabled);
        }
        Assert.Empty(await fixture.AlertRecordsAsync());
    }

    public static IEnumerable<object[]> ScheduledOperationCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var action in new[] { "save-pre", "save-post", "disable-pre", "disable-post", "delete-pre", "delete-post",
            "edit", "import", "template", "preview", "missing", "busy", "permission", "conflict", "cancel" })
            yield return [culture, action];
    }

    [Theory]
    [MemberData(nameof(ScheduledOperationCases))]
    public async Task ScheduledOperationFailuresAreSafeAndPreserveUnconfirmedInput(string culture, string action)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var writes = new ScheduledWriteProbe();
        var saves = new ScheduledSaveProbe();
        await using var fixture = await Fixture.CreateAsync(interceptors: [writes, saves]);
        fixture.Rule.Enabled = !action.StartsWith("delete", StringComparison.Ordinal);
        await fixture.Service.SaveAsync(fixture.Rule);
        var saved = Assert.Single(await fixture.Service.ListAsync());
        await using var page = new ScheduledPage(fixture);
        await page.StartAsync();
        await page.InvokeAsync("EditAsync", saved);
        var draft = page.Field("editing");
        var states = page.Field("states");
        var attempts = writes.Attempts;
        var failure = new InvalidOperationException("private scheduled diagnostic");
        if (action.Contains('-') || action is "permission" or "conflict" or "cancel")
        {
            Exception exception = action switch
            {
                "permission" => new UnauthorizedAccessException("private permission diagnostic"),
                "conflict" => new DbUpdateConcurrencyException("private conflict diagnostic"),
                "cancel" => new OperationCanceledException("private cancellation diagnostic"),
                _ => failure
            };
            if (action is "permission" or "conflict" or "cancel") saves.BeforeFailure = exception;
            else if (action == "save-post") saves.AfterCommitFailure = exception;
            else if (action.EndsWith("-post", StringComparison.Ordinal)) writes.AfterStatementFailure = exception;
            else writes.BeforeFailure = exception;
            var method = action.StartsWith("disable", StringComparison.Ordinal) ? "DisableAsync"
                : action.StartsWith("delete", StringComparison.Ordinal) ? "DeleteAsync" : "SaveAsync";
            if (method == "DeleteAsync") page.Set("deleteRule", saved);
            await page.InvokeAsync(method, method == "DisableAsync" ? [saved] : []);
            var key = action switch
            {
                "permission" => "AdminScheduledQueries.Errors.NotAllowed",
                "conflict" => "AdminScheduledQueries.Errors.Changed",
                _ when method == "DisableAsync" => "AdminScheduledQueries.Errors.DisableFailed",
                _ when method == "DeleteAsync" => "AdminScheduledQueries.Errors.DeleteFailed",
                _ => "AdminScheduledQueries.Errors.SaveFailed"
            };
            Assert.Equal(page.Text[key], page.Field("error"));
            var logged = Assert.Single(page.Log.Exceptions);
            if (action == "save-pre") Assert.Same(exception, Assert.IsType<DbUpdateException>(logged).InnerException);
            else Assert.Same(exception, logged);
            Assert.Same(draft, page.Field("editing"));
            if (method == "DeleteAsync") Assert.Same(saved, page.Field("deleteRule"));
            Assert.Null(page.Field("message"));
            var stored = await fixture.Service.ListAsync();
            if (action == "delete-post") Assert.Empty(stored);
            else Assert.Equal(action.EndsWith("-post", StringComparison.Ordinal) ? 2 : 1, Assert.Single(stored).Revision);
            var expectedAttempts = attempts + (action is "permission" or "conflict" or "cancel" ? 0 : 1);
            Assert.Equal(expectedAttempts, writes.Attempts);
            await page.InvokeAsync("RefreshAsync");
            Assert.Equal(expectedAttempts, writes.Attempts);
        }
        else if (action == "edit")
        {
            await RenameScheduledSource(fixture, 2, false);
            await page.InvokeAsync("EditAsync", saved);
            Assert.Same(draft, page.Field("editing"));
            Assert.Same(states, page.Field("states"));
            Assert.Equal(page.Text["AdminScheduledQueries.Errors.EditFailed"], page.Field("error"));
            Assert.IsType<SqliteException>(Assert.Single(page.Log.Exceptions));
            await RenameScheduledSource(fixture, 2, true);
        }
        else if (action == "import")
        {
            await page.InvokeAsync("ImportJsonAsync", "{");
            Assert.Equal(page.Text["QueryTemplate.Validation.InvalidJson"], page.Field("error"));
            Assert.Same(draft, page.Field("editing"));
            Assert.Equal(QueryTemplateValidationCode.InvalidJson, Assert.IsType<QueryTemplateValidationException>(
                Assert.Single(page.Log.Exceptions)).Code);
        }
        else if (action == "template")
        {
            Func<ScheduledQueryRule> create = () => throw failure;
            await page.InvokeAsync("BeginRule", create);
            Assert.Same(draft, page.Field("editing"));
            Assert.Equal(page.Text["AdminScheduledQueries.Errors.TemplateFailed"], page.Field("error"));
            Assert.Same(failure, Assert.Single(page.Log.Exceptions));
        }
        else if (action == "preview")
        {
            fixture.Query.Failures["production"] = failure;
            await page.InvokeAsync("PreviewAsync");
            Assert.Null(page.Field("error"));
            Assert.NotEmpty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(page.Field("executionPreviews")).Cast<object>());
            var preview = Assert.IsType<ScheduledQueryEvaluation>(page.Field("preview"));
            Assert.Contains("private scheduled diagnostic", Assert.Single(preview.Workspaces).Error);
            Assert.Contains(page.Text["AdminScheduledQueries.Errors.PreviewWorkspaceFailed"], await page.HtmlAsync());
        }
        else if (action == "missing")
        {
            page.Set("editing", null);
            await page.InvokeAsync("SaveAsync");
            Assert.Equal(page.Text["AdminScheduledQueries.Errors.DraftRequired"], page.Field("error"));
            await page.InvokeAsync("PreviewAsync");
            Assert.Equal(page.Text["AdminScheduledQueries.Errors.DraftRequired"], page.Field("error"));
            await page.InvokeAsync("DeleteAsync");
            Assert.Equal(page.Text["AdminScheduledQueries.Errors.DeleteRequired"], page.Field("error"));
            Assert.Equal(3, page.Log.Warnings);
        }
        else if (action == "busy")
        {
            page.Set("busy", true);
            page.Set("message", "Original status");
            page.Set("error", "Original error");
            await page.InvokeBlockedAsync(saved);
            await page.InvokeAsync("RefreshAsync");
            Assert.Same(draft, page.Field("editing"));
            Assert.Equal("Original status", page.Field("message"));
            Assert.Equal("Original error", page.Field("error"));
            Assert.Empty(page.Log.Exceptions);
            page.Set("busy", false);
        }
        else throw new ArgumentOutOfRangeException(nameof(action));
        Assert.Null(page.Field("loadError"));
        Assert.False(Assert.IsType<bool>(page.Field("busy")));
        Assert.DoesNotContain("private ", await page.HtmlAsync());
        Assert.Empty(await fixture.AlertRecordsAsync());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    private static async Task RenameScheduledSource(Fixture fixture, int source, bool restore)
    {
        await using var context = fixture.Factory.CreateDbContext();
        var sql = (source, restore) switch
        {
            (0, false) => "ALTER TABLE ScheduledQueryRules RENAME TO FailedScheduledRules",
            (0, true) => "ALTER TABLE FailedScheduledRules RENAME TO ScheduledQueryRules",
            (1, false) => "ALTER TABLE ScheduledQueryNotifications RENAME TO FailedScheduledNotifications",
            (1, true) => "ALTER TABLE FailedScheduledNotifications RENAME TO ScheduledQueryNotifications",
            (2, false) => "ALTER TABLE ScheduledQueryTargetStates RENAME TO FailedScheduledStates",
            (2, true) => "ALTER TABLE FailedScheduledStates RENAME TO ScheduledQueryTargetStates",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed class ScheduledPage : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly ServiceProvider services;
        private readonly StaticPageRenderer renderer;
        private readonly CapturingActivator activator = new();
        private Func<string>? html;
        public ScheduledPage(Fixture fixture)
        {
            services = new ServiceCollection().AddLogging().AddKqlEditorTestServices().AddUiLocalization()
                .AddSingleton<IComponentActivator>(activator)
                .AddSingleton<ILogger<Components.Pages.AdminScheduledQueries>>(Log)
                .AddSingleton(fixture.Service).AddSingleton(fixture.Options).BuildServiceProvider();
            renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public ScheduledPageLog Log { get; } = new();
        public UiText Text => services.GetRequiredService<UiText>();
        public Components.Pages.AdminScheduledQueries Page => Assert.IsType<Components.Pages.AdminScheduledQueries>(activator.Page);
        public object? Field(string name) => Page.GetType().GetField(name, Flags)!.GetValue(Page);
        public void Set(string name, object? value) => Page.GetType().GetField(name, Flags)!.SetValue(Page, value);
        public async Task StartAsync() => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Components.Pages.AdminScheduledQueries), ParameterView.Empty);
            await root.QuiescenceTask;
            html = root.ToHtmlString;
        });
        public async Task InvokeAsync(string name, params object?[] arguments) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = Page.GetType().GetMethod(name, Flags)!.Invoke(Page, arguments);
            if (result is Task task) await task;
            Page.GetType().GetMethod("StateHasChanged", Flags)!.Invoke(Page, []);
        });
        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(Assert.IsType<Func<string>>(html)()));
        public async Task InvokeBlockedAsync(ScheduledQueryRule rule)
        {
            foreach (var method in new[] { "SaveAsync", "PreviewAsync", "DeleteAsync" }) await InvokeAsync(method);
            await InvokeAsync("DisableAsync", rule);
            await InvokeAsync("EditAsync", rule);
            await InvokeAsync("BeginNew", (object?)null);
            await InvokeAsync("BeginEventExample", 4624);
            await InvokeAsync("ImportJsonAsync", "{");
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class ScheduledPageLog : ILogger<Components.Pages.AdminScheduledQueries>
    {
        public List<Exception> Exceptions { get; } = [];
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Exceptions.Add(exception);
            if (level == LogLevel.Warning) Warnings++;
        }
    }

    private sealed class ScheduledSaveProbe : SaveChangesInterceptor
    {
        public Exception? BeforeFailure { get; set; }
        public Exception? AfterCommitFailure { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken token = default)
        {
            var failure = BeforeFailure;
            BeforeFailure = null;
            if (failure is not null) throw failure;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken token = default)
        {
            var failure = AfterCommitFailure;
            AfterCommitFailure = null;
            if (failure is not null) throw failure;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ScheduledWriteProbe : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public Exception? BeforeFailure { get; set; }
        public Exception? AfterStatementFailure { get; set; }
        public Action? AfterWrite { get; set; }
        private static bool IsWrite(System.Data.Common.DbCommand command) =>
            command.CommandText.StartsWith("INSERT INTO \"ScheduledQueryRules\"", StringComparison.Ordinal) ||
            command.CommandText.StartsWith("UPDATE \"ScheduledQueryRules\"", StringComparison.Ordinal) ||
            command.CommandText.StartsWith("UPDATE \"s\"", StringComparison.Ordinal) ||
            command.CommandText.StartsWith("DELETE FROM \"ScheduledQueryRules\"", StringComparison.Ordinal);
        private void Before(System.Data.Common.DbCommand command)
        {
            if (!IsWrite(command)) return;
            Attempts++;
            var failure = BeforeFailure;
            BeforeFailure = null;
            if (failure is not null) throw failure;
        }
        private void After(System.Data.Common.DbCommand command, bool statement)
        {
            if (!IsWrite(command)) return;
            var after = AfterWrite;
            AfterWrite = null;
            after?.Invoke();
            if (statement && AfterStatementFailure is { } failure)
            {
                AfterStatementFailure = null;
                throw failure;
            }
        }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken token = default)
        {
            Before(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command, CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result, CancellationToken token = default)
        {
            After(command, false);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken token = default)
        {
            Before(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken token = default)
        {
            After(command, true);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public Components.Pages.AdminScheduledQueries? Page { get; private set; }
        public Components.QueryJsonImport? Import { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is Components.Pages.AdminScheduledQueries page) Page = page;
            if (component is Components.QueryJsonImport import) Import = import;
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

    private static LogsQueryResult EventResult(int eventId, double? value, string users, long unparsed = 0, bool truncated = false) =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            tables = new[] { new { name = "PrimaryResult",
                columns = new[]
                {
                    new { name = "Series", type = "string" }, new { name = "Value", type = "real" },
                    new { name = "EventId", type = "int" }, new { name = "Users", type = "string" },
                    new { name = "UnparsedEvents", type = "long" }, new { name = "UsersTruncated", type = "bool" }
                },
                rows = new[] { new object?[] { "Workspace", value, eventId, users, unparsed, truncated } } } }
        })))!;

    private static LogsQueryResult TargetResult(params (string Target, long Count)[] rows) =>
        ModelReaderWriter.Read<LogsQueryResult>(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            tables = new[] { new { name = "PrimaryResult",
                columns = new[] { new { name = "Target", type = "string" }, new { name = "count_", type = "long" } },
                rows = rows.Select(row => new object[] { row.Target, row.Count }) } }
        })))!;

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
        public AlertStore Store { get; private set; } = null!;

        public ScheduledQueryService NewService()
        {
            var resolver = new TargetWorkspaceResolver(Options);
            var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
            Store = new AlertStore(Factory, new(true, null, new SqlConnectionStringBuilder()), presenter,
                new(presenter), NullLogger<AlertStore>.Instance);
            return new(Factory, Options, resolver, new(Options, new Secret(), resolver, Query), Store, Clock,
                NullLogger<ScheduledQueryService>.Instance);
        }

        public static async Task<Fixture> CreateAsync(bool kerberos = false, int workspaceCount = 1, IInterceptor[]? interceptors = null, bool enabled = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, AlertInventoryLearningTests.InventoryLearningTestModelCustomizer>();
            if (interceptors is not null) builder.AddInterceptors(interceptors);
            var factory = new Factory(builder.Options);
            var options = new CrossTenantLogAnalyticsOptions
            {
                Enabled = enabled,
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
        public Action<AlertDbContext>? OnCreated { get; set; }
        public Action? BeforeNextContext { get; set; }
        public AlertDbContext CreateDbContext()
        {
            OnCreate?.Invoke();
            var before = BeforeNextContext;
            BeforeNextContext = null;
            before?.Invoke();
            var context = new AlertDbContext(options);
            OnCreated?.Invoke(context);
            return context;
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
