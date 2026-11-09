using System.Security.Claims;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class RcaTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReconciliationServiceRollsBackSnapshotFailureAndPreservesFollowUpBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("worker", "DC01", Start);
        var reconciliation = fixture.Services.GetRequiredService<RcaReconciliationService>();
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailRcaSnapshot BEFORE INSERT ON RcaPrompts BEGIN SELECT RAISE(ABORT, 'snapshot failed'); END;");

        await Assert.ThrowsAsync<DbUpdateException>(() => reconciliation.ReconcileAsync());
        await using (var context = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(await context.RootCauseAnalyses.ToArrayAsync());
            Assert.Empty(await context.RcaAlertLinks.ToArrayAsync());
            Assert.Empty(await context.RcaPrompts.ToArrayAsync());
            Assert.Empty(await context.RcaChanges.ToArrayAsync());
            Assert.Single(await context.Alerts.ToArrayAsync());
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailRcaSnapshot;");
        }

        await reconciliation.ReconcileAsync();
        var original = Assert.Single(await fixture.Service.ListAsync());
        var initial = Assert.Single(await fixture.Service.GetPromptsAsync(original.Id));
        Assert.Equal(Start, original.CreatedAt);
        Assert.Equal(Start.AddMinutes(15), original.FollowUpDueAt);
        Assert.Equal(0, initial.Sequence);
        var clock = Assert.IsType<FixedTime>(fixture.Services.GetRequiredService<TimeProvider>());
        clock.Now = Start.AddMinutes(15).AddTicks(-1);
        await reconciliation.ReconcileAsync();
        Assert.Single(await fixture.Service.GetPromptsAsync(original.Id));
        Assert.Equal(original.Revision, Assert.Single(await fixture.Service.ListAsync()).Revision);

        clock.Now = Start.AddMinutes(15);
        await reconciliation.ReconcileAsync();
        await reconciliation.ReconcileAsync();
        var prompts = await fixture.Service.GetPromptsAsync(original.Id);
        Assert.Equal(new[] { 0, 1 }, prompts.Select(prompt => prompt.Sequence));
        Assert.Equal(initial.RawPrompt, prompts[0].RawPrompt);
        Assert.Equal(clock.Now, prompts[1].CreatedAt);
        Assert.Equal(original.Revision + 1, Assert.Single(await fixture.Service.ListAsync()).Revision);
        Assert.Single(await fixture.Service.GetLinkedAlertsAsync(original.Id));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[\"not-a-guid\"]")]
    public async Task InvalidEvidenceFailsConsistentlyInNoticesAssignmentsAndReconciliation(string evidence)
    {
        await using var fixture = await Fixture.CreateAsync(admin: true);
        await fixture.AddAlertAsync("invalid-evidence", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var stored = await context.RootCauseAnalyses.SingleAsync();
            stored.EvidenceEventIdsJson = evidence;
            await context.SaveChangesAsync();
        }

        var notices = await Record.ExceptionAsync(() => fixture.Service.GetOpenNoticesAsync());
        var assignments = await Record.ExceptionAsync(() => fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        var reconciliation = await Record.ExceptionAsync(() =>
            fixture.Services.GetRequiredService<RcaReconciliationService>().ReconcileAsync());
        Assert.NotNull(notices);
        if (evidence == "null")
        {
            Assert.Equal(original.Id, Assert.IsType<RcaEvidenceException>(notices).RcaId);
            Assert.IsAssignableFrom<InvalidOperationException>(notices);
            Assert.Contains(original.Id.ToString(), notices.Message);
        }
        else
        {
            var json = Assert.IsType<RcaEvidenceJsonException>(notices);
            Assert.Equal(original.Id, json.RcaId);
            var parser = Assert.IsAssignableFrom<JsonException>(json.InnerException);
            Assert.Equal(parser.Message, json.Message);
            Assert.Equal(parser.Path, json.Path);
            Assert.Equal(parser.LineNumber, json.LineNumber);
            Assert.Equal(parser.BytePositionInLine, json.BytePositionInLine);
            Assert.False(typeof(InvalidOperationException).IsInstanceOfType(json));
        }
        Assert.NotNull(assignments);
        Assert.NotNull(reconciliation);
        Assert.Equal(notices.GetType(), assignments.GetType());
        Assert.Equal(notices.GetType(), reconciliation.GetType());
        Assert.Equal(notices.Message, assignments.Message);
        Assert.Equal(notices.Message, reconciliation.Message);
        var unchanged = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(evidence, unchanged.EvidenceEventIdsJson);
        Assert.Equal(original.Revision, unchanged.Revision);
        Assert.Single(await fixture.Service.GetLinkedAlertsAsync(original.Id));
        Assert.Single(await fixture.Service.GetPromptsAsync(original.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerRunsImmediatelyRetriesAfter30SecondsAndCancelsDatabaseWork(bool failFirst)
    {
        await using var fixture = await Fixture.CreateAsync();
        var factory = new WaitingFactory(failFirst);
        var clock = new AnalysisRefreshLoopTests.ManualTimeProvider();
        var logger = new WorkerLogger();
        var reconciliation = new RcaReconciliationService(factory,
            fixture.Services.GetRequiredService<AlertRuleEvaluator>(),
            fixture.Services.GetRequiredService<QueryResultPresenter>(), clock);
        using var worker = new RcaWorker(reconciliation, clock, logger);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            if (failFirst)
            {
                await logger.FirstError.Task.WaitAsync(TimeSpan.FromSeconds(5));
                clock.Advance(TimeSpan.FromSeconds(29));
                Assert.Equal(1, factory.Attempts);
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(failFirst ? 2 : 1, factory.Attempts);
            Assert.Equal(TimeSpan.FromSeconds(30), clock.Period);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.True(factory.Token.IsCancellationRequested);
        Assert.True(clock.Disposed);
        Assert.Equal(failFirst ? 1 : 0, logger.ErrorCount);
    }

    [Fact]
    public async Task AssignableAlertsUseLatestLifecycleStateAndInclusive24HourBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("old-active", "DC01", Start.AddDays(-3));
        await fixture.AddAlertAsync("recent-resolved", "DC02", Start.AddHours(-23), "Resolved");
        await fixture.AddAlertAsync("boundary", "DC03", Start.AddHours(-24), "Resolved");
        await fixture.AddAlertAsync("too-old", "DC04", Start.AddHours(-24).AddTicks(-1), "Resolved");
        await fixture.AddAlertAsync("old-resolved", "DC05", Start.AddDays(-3));
        await fixture.AddAlertAsync("old-resolved", "DC05", Start.AddHours(-25), "Resolved");
        await fixture.AddAlertAsync("manual-resolved", "DC06", Start.AddDays(-2));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var alert = await context.Alerts.SingleAsync(alert => alert.AlertId == "manual-resolved");
            context.Entry(alert).CurrentValues.SetValues(alert with { Comments = "Resolved by operator" });
            await context.SaveChangesAsync();
        }
        var candidates = await fixture.Service.GetAssignableAlertsAsync(User("Admin"));
        Assert.Equal(new[] { "boundary", "old-active", "recent-resolved" },
            candidates.Select(candidate => candidate.Alert.AlertId).Order());
        Assert.True(candidates.Single(candidate => candidate.Alert.AlertId == "old-active").IsActive);
        Assert.False(candidates.Single(candidate => candidate.Alert.AlertId == "recent-resolved").IsActive);
    }

    [Fact]
    public async Task ManualCreationAndConfirmedMovesKeepWholeLifecyclesAuditAndAutomationConsistent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start.AddHours(-2));
        await fixture.AddAlertAsync("first", "DC01", Start.AddHours(-1), "Resolved");
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.AddAlertAsync("second", "DC02", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        var initial = Assert.Single(await fixture.Service.GetPromptsAsync(original.Id));
        var candidates = await fixture.Service.GetAssignableAlertsAsync(User("Admin"));
        var first = candidates.Single(candidate => candidate.Alert.AlertId == "first").Selection;
        await Assert.ThrowsAsync<RcaValidationException>(() =>
            fixture.Service.CreateAsync(User("Admin"), "Manual investigation", [first], false));
        Assert.Single(await fixture.Service.ListAsync());

        var id = await fixture.Service.CreateAsync(User("Admin"), " Manual investigation ", [first], true);
        var manual = (await fixture.Service.ListAsync()).Single(rca => rca.Id == id);
        Assert.Equal("Manual investigation", manual.CategoryName);
        Assert.Equal(Guid.Empty, manual.RuleId);
        Assert.Equal(Start.AddMinutes(15), manual.FollowUpDueAt);
        Assert.True(manual.IsActive);
        Assert.Equal(3, (await fixture.Service.GetLinkedAlertsAsync(id)).Count);
        Assert.Equal("second", Assert.Single(await fixture.Service.GetLinkedAlertsAsync(original.Id)).AlertId);
        var manualInitial = Assert.Single(await fixture.Service.GetPromptsAsync(id));
        Assert.Contains("manually created", manualInitial.RawPrompt);
        Assert.Contains("DC01", manualInitial.RawPrompt);
        Assert.DoesNotContain("DC02", manualInitial.RawPrompt);
        candidates = await fixture.Service.GetAssignableAlertsAsync(User("Admin"));
        var second = candidates.Single(candidate => candidate.Alert.AlertId == "second").Selection;
        await fixture.Service.AssignAlertsAsync(User("Admin"), id, manual.Revision, [second], true);
        Assert.Empty(await fixture.Service.GetLinkedAlertsAsync(original.Id));
        original = (await fixture.Service.ListAsync()).Single(rca => rca.Id == original.Id);
        Assert.Empty(JsonSerializer.Deserialize<Guid[]>(original.EvidenceEventIdsJson)!);
        Assert.False(original.IsActive);
        Assert.Contains(await fixture.Service.GetChangesAsync(original.Id), change =>
            change.Field == "AlertReassignment" && change.After.Contains(id.ToString()) && change.Actor != "System");
        Assert.Contains(await fixture.Service.GetChangesAsync(id), change => change.Field == "AlertAssignment");
        await fixture.ReconcileAsync(Start.AddMinutes(15));
        await fixture.ReconcileAsync(Start.AddMinutes(16));
        Assert.Empty(await fixture.Service.GetLinkedAlertsAsync(original.Id));
        Assert.Equal(4, (await fixture.Service.GetLinkedAlertsAsync(id)).Count);
        var originalPrompts = await fixture.Service.GetPromptsAsync(original.Id);
        Assert.Equal(initial.RawPrompt, originalPrompts[0].RawPrompt);
        Assert.Contains("No alert events are currently assigned", originalPrompts[1].RawPrompt);
        var manualPrompts = await fixture.Service.GetPromptsAsync(id);
        Assert.Equal(manualInitial.RawPrompt, manualPrompts[0].RawPrompt);
        Assert.Contains("DC02", manualPrompts[1].RawPrompt);
    }

    [Fact]
    public async Task CreationRechecksEligibilityAtSaveAndRejectsStaleAssignmentsAndTargetRevisions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("expiring", "DC01", Start.AddHours(-24).AddSeconds(1), "Resolved");
        var selected = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        ((FixedTime)fixture.Services.GetRequiredService<TimeProvider>()).Now = Start.AddSeconds(2);
        await Assert.ThrowsAsync<RcaValidationException>(() =>
            fixture.Service.CreateAsync(User("Admin"), "Expired", [selected], false));
        Assert.Empty(await fixture.Service.ListAsync());
        await fixture.AddAlertAsync("first", "DC02", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        selected = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        await fixture.Service.SaveAsync(User("Admin"), original.Id, original.Revision, "New analysis", "", "Open", "");
        await Assert.ThrowsAsync<RcaValidationException>(() =>
            fixture.Service.CreateAsync(User("Admin"), "Stale source", [selected], true));
        selected = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        await Assert.ThrowsAsync<RcaValidationException>(() =>
            fixture.Service.AssignAlertsAsync(User("Admin"), original.Id, original.Revision, [selected], true));
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Single(await fixture.Service.GetLinkedAlertsAsync(original.Id));
    }

    [Fact]
    public async Task ManualCaseDoesNotRequireACriticalRuleAndInitialPromptIncludesAutomaticallyJoinedAlerts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var context = fixture.Factory.CreateDbContext())
            await context.AlertRules.ExecuteDeleteAsync();
        await fixture.AddAlertAsync("noncritical", "DC01", Start);
        var selection = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        await fixture.Service.CreateAsync(User("Admin"), "Noncritical investigation", [selection], false);
        Assert.False(Assert.Single(await fixture.Service.ListAsync()).IsActive);
        await using var critical = await Fixture.CreateAsync();
        await critical.AddAlertAsync("first-critical", "DC01", Start);
        await critical.AddAlertAsync("second-critical", "DC01", Start);
        selection = (await critical.Service.GetAssignableAlertsAsync(User("Admin")))
            .Single(candidate => candidate.Alert.AlertId == "first-critical").Selection;
        var id = await critical.Service.CreateAsync(User("Admin"), "Critical investigation", [selection], false);
        Assert.Single(await critical.Service.ListAsync());
        Assert.Equal(2, (await critical.Service.GetLinkedAlertsAsync(id)).Count);
        var prompt = Assert.Single(await critical.Service.GetPromptsAsync(id));
        Assert.Contains("Selected alerts: 2", prompt.RawPrompt);
        Assert.Equal(2, prompt.RawPrompt.Split("- Target: DC01", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task ManualCreationRollsBackCaseLinksAndAuditWhenInitialPromptCannotBeSaved()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        var selected = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER FailPrompt BEFORE INSERT ON RcaPrompts BEGIN SELECT RAISE(ABORT, 'Prompt failed'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Service.CreateAsync(User("Admin"), "Rollback", [selected], false));
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Empty(await check.RootCauseAnalyses.ToArrayAsync());
        Assert.Empty(await check.RcaAlertLinks.ToArrayAsync());
        Assert.Empty(await check.RcaChanges.ToArrayAsync());
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Operator")]
    public async Task ManualCreationAndAssignmentAreAdministratorOnly(string role)
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = User(role);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.GetAssignableAlertsAsync(user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.CreateAsync(user, "Case", [], false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.AssignAlertsAsync(user, Guid.NewGuid(), 1, [], false));
    }

    [Fact]
    public async Task ManualCreationRequiresANameAndAlertAndClosedCasesMustBeReopened()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        var selected = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.CreateAsync(User("Admin"), "", [selected], false));
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.CreateAsync(User("Admin"), new string('x', 257), [selected], false));
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.CreateAsync(User("Admin"), "Name", [], false));
        var id = await fixture.Service.CreateAsync(User("Admin"), "Name", [selected], false);
        var manual = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), id, manual.Revision, "", "", RcaStatuses.Closed, "");
        manual = Assert.Single(await fixture.Service.ListAsync());
        selected = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        await Assert.ThrowsAsync<RcaValidationException>(() =>
            fixture.Service.AssignAlertsAsync(User("Admin"), id, manual.Revision, [selected], false));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task AdminCanCreateFromPickerAndMovingAlertsRequiresVisibleConfirmation(string culture)
    {
        using var cultures = new PageCultures(culture);
        string Local(string english, string german) => culture == "de-DE" ? german : english;
        await using var fixture = await Fixture.CreateAsync(admin: true);
        await fixture.AddAlertAsync("old-active", "DC01", Start.AddDays(-2));
        await fixture.AddAlertAsync("old-inactive", "DC02", Start.AddDays(-2), "Resolved");
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Rca), ParameterView.Empty);
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(Rca);
        await InvokeAsync("OpenAlertPickerAsync", true);
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains(Local("Create RCA", "RCA erstellen"), html);
        Assert.Contains("old-active", html);
        Assert.DoesNotContain("old-inactive", html);
        type.GetField("newCaseName", flags)!.SetValue(page, "Manual UI case");
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        await InvokeAsync("AlertSelectionChanged", candidate.Alert.Id, new ChangeEventArgs { Value = true });
        await InvokeAsync("SubmitAlertAssignmentAsync");
        html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains("Manual UI case", html);
        Assert.Contains(Local("Associated alerts (1)", "Zugeordnete Alarme (1)"), html);
        Assert.Contains(Local("RCA and alert assignments saved", "RCA und Alarmzuordnungen gespeichert"), html);
        foreach (var statusCode in RcaStatuses.All)
            Assert.Contains($"value=\"{statusCode}\"", html);
        await fixture.AddAlertAsync("recent-resolved", "DC03", Start.AddHours(-1), "Resolved");
        await InvokeAsync("OpenAlertPickerAsync", false);
        var addition = (await fixture.Service.GetAssignableAlertsAsync(User("Admin")))
            .Single(candidate => candidate.Alert.AlertId == "recent-resolved");
        await InvokeAsync("AlertSelectionChanged", addition.Alert.Id, new ChangeEventArgs { Value = true });
        await InvokeAsync("SubmitAlertAssignmentAsync");
        Assert.Contains(Local("Associated alerts (2)", "Zugeordnete Alarme (2)"),
            WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
        await InvokeAsync("OpenAlertPickerAsync", true);
        await InvokeAsync("AlertSelectionChanged", candidate.Alert.Id, new ChangeEventArgs { Value = true });
        html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains(Local("I confirm moving these alerts", "Ich bestätige das Verschieben dieser Alarme"), html);
        Assert.Contains("Manual UI case", html);
        Assert.Contains(Local("Previous prompt snapshots remain unchanged", "Frühere Prompt-Snapshots bleiben unverändert"), html);
        type.GetField("newCaseName", flags)!.SetValue(page, "Confirmed UI move");
        type.GetField("confirmReassignment", flags)!.SetValue(page, true);
        await InvokeAsync("SubmitAlertAssignmentAsync");
        Assert.Contains(Local("RCA and alert assignments saved", "RCA und Alarmzuordnungen gespeichert"),
            WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
        var cases = await fixture.Service.ListAsync();
        Assert.Equal(2, cases.Count);
        var moved = cases.Single(rca => rca.CategoryName == "Confirmed UI move");
        Assert.Equal("old-active", Assert.Single(await fixture.Service.GetLinkedAlertsAsync(moved.Id)).AlertId);

        async Task InvokeAsync(string method, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (type.GetMethod(method, flags)!.Invoke(page, arguments) is Task task)
                    await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
        }
    }

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
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, rca.Revision, "", "", "Open", ""));
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, saved.Revision, "", "missing-user", "Open", ""));
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.SaveAsync(
            User("Admin"), rca.Id, saved.Revision, "", "", "Invalid", ""));
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.SaveAsync(
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
        await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.SaveAsync(
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
        await Assert.ThrowsAsync<RcaValidationException>(() =>
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

    public static IEnumerable<object[]> RcaValidationCases() =>
        new[] { "en-US", "de-DE" }.SelectMany(culture =>
            Enum.GetValues<RcaValidationCode>().Select(code => new object[] { culture, code }));

    [Theory]
    [MemberData(nameof(RcaValidationCases))]
    public async Task RcaStableValidationCodesAreLocalizedWithoutMessageMatching(string culture, RcaValidationCode code)
    {
        using var cultures = new PageCultures(culture);
        var writes = new RcaWriteInterceptor();
        await using var fixture = await Fixture.CreateAsync(admin: true, interceptors: [writes]);
        await fixture.AddAlertAsync("validation-ui", "DC01", Start);
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        var id = await fixture.Service.CreateAsync(User("Admin"), "Custom RCA", [candidate.Selection], false);
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(SelectedRcaPage),
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["SelectedCaseId"] = id }));
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var text = fixture.Services.GetRequiredService<UiText>();
        var log = fixture.Services.GetRequiredService<RcaPageLogger>();
        var original = Assert.IsType<RootCauseAnalysis>(typeof(Rca).GetField("selected", flags)!.GetValue(page));
        typeof(Rca).GetField("analysis", flags)!.SetValue(page, "Custom pending <analysis> &");
        async Task<string> Save(Exception failure)
        {
            writes.Failure = failure;
            try
            {
                await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    await Assert.IsAssignableFrom<Task>(typeof(Rca).GetMethod("SaveAsync", flags)!.Invoke(page, []));
                    typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
                });
            }
            finally { writes.Failure = null; }
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        }
        var exception = new RcaValidationException(code, "Technical <validation> details.");
        var html = await Save(exception);
        var key = code == RcaValidationCode.Changed ? "Rca.Errors.Changed" : $"Rca.Validation.{code}";
        Assert.Contains(text[key], html);
        Assert.DoesNotContain(exception.Message, html);
        Assert.Same(exception, log.Entries[^1].Exception);
        Assert.Equal(LogLevel.Warning, log.Entries[^1].Level);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal(original.Revision, Assert.Single(await fixture.Service.ListAsync()).Revision);
        Assert.Equal("Custom pending <analysis> &", typeof(Rca).GetField("analysis", flags)!.GetValue(page));
        foreach (var unknown in new Exception[]
        {
            new RcaValidationException((RcaValidationCode)999, "Unknown code details"),
            new InvalidOperationException("Unknown RCA status.")
        })
        {
            html = await Save(unknown);
            Assert.Contains(text["Rca.SaveError"], html);
            Assert.DoesNotContain(unknown.Message, html);
            Assert.Same(unknown, log.Entries[^1].Exception);
            Assert.Equal(LogLevel.Error, log.Entries[^1].Level);
        }
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", "null")]
    [InlineData("de-DE", "null")]
    [InlineData("en-US", "{")]
    [InlineData("de-DE", "{")]
    [InlineData("en-US", "[\"not-a-guid\"]")]
    [InlineData("de-DE", "[\"not-a-guid\"]")]
    public async Task RcaEvidenceReadErrorsReachPickerWithoutChangingStoredCaseOrPendingEditor(string culture, string evidence)
    {
        using var cultures = new PageCultures(culture);
        var writes = new RcaWriteInterceptor();
        await using var fixture = await Fixture.CreateAsync(admin: true, interceptors: [writes]);
        await fixture.AddAlertAsync("evidence-boundary", "DC01", Start.AddMinutes(-1));
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        var id = await fixture.Service.CreateAsync(User("Admin"), "Original <RCA> &", [candidate.Selection], false);
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(SelectedRcaPage),
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["SelectedCaseId"] = id }));
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        var log = fixture.Services.GetRequiredService<RcaPageLogger>();
        var text = fixture.Services.GetRequiredService<UiText>();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Get(string name) => typeof(Rca).GetField(name, flags)!.GetValue(page);
        void Set(string name, object? value) => typeof(Rca).GetField(name, flags)!.SetValue(page, value);
        async Task<string> Invoke(string method, params object?[] args)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (typeof(Rca).GetMethod(method, flags)!.Invoke(page, args) is Task task) await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        }
        await Invoke("OpenAlertPickerAsync", false);
        Set("analysis", "Original pending analysis");
        Set("comment", "Original pending comment");
        var selected = Get("selected");
        var original = Assert.Single(await fixture.Service.ListAsync());
        var originalEvidence = original.EvidenceEventIdsJson;
        await using (var context = fixture.Factory.CreateDbContext())
            await context.RootCauseAnalyses.Where(rca => rca.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rca => rca.EvidenceEventIdsJson, evidence));
        var attempts = writes.Attempts;
        var html = await Invoke("RefreshAssignableAlertsAsync");
        Assert.Equal(text["Rca.Errors.EvidenceInvalid"], Get("error"));
        Assert.Contains(text["Rca.Errors.EvidenceInvalid"], html);
        Assert.DoesNotContain("not-a-guid", html);
        Assert.DoesNotContain("invalid evidence event IDs", html);
        Assert.Equal(true, Get("candidateLoadFailed"));
        Assert.Equal(false, Get("busy"));
        Assert.Same(selected, Get("selected"));
        Assert.Equal("Original pending analysis", Get("analysis"));
        Assert.Equal("Original pending comment", Get("comment"));
        var failure = log.Entries[^1];
        Assert.Equal(LogLevel.Error, failure.Level);
        if (evidence == "null") Assert.Equal(id, Assert.IsType<RcaEvidenceException>(failure.Exception).RcaId);
        else Assert.Equal(id, Assert.IsType<RcaEvidenceJsonException>(failure.Exception).RcaId);
        await Invoke("SubmitAlertAssignmentAsync");
        Assert.Equal(attempts, writes.Attempts);
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(evidence, stored.EvidenceEventIdsJson);
        Assert.Equal(original.Revision, stored.Revision);
        original.EvidenceEventIdsJson = evidence;
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(stored));
        Assert.Single(await fixture.Service.GetLinkedAlertsAsync(id));
        await using (var context = fixture.Factory.CreateDbContext())
            await context.RootCauseAnalyses.Where(rca => rca.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rca => rca.EvidenceEventIdsJson, originalEvidence));
        await Invoke("RefreshAssignableAlertsAsync");
        Assert.Null(Get("error"));
        Assert.Equal(false, Get("candidateLoadFailed"));
        Assert.Equal("Original pending comment", Get("comment"));
        Assert.Equal(attempts, writes.Attempts);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RcaEvidencePresentationDoesNotTranslateEnglishLookalikesOrUnrelatedJson(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var fixture = await Fixture.CreateAsync(admin: true);
        var page = new Rca();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var text = fixture.Services.GetRequiredService<UiText>();
        typeof(Rca).GetProperty("Text", flags)!.SetValue(page, text);
        typeof(Rca).GetProperty("Logger", flags)!.SetValue(page, fixture.Services.GetRequiredService<RcaPageLogger>());
        foreach (var exception in new Exception[]
        {
            new InvalidOperationException($"RCA {Guid.NewGuid()} has invalid evidence event IDs."),
            new JsonException("private JSON diagnostic"),
            new InvalidOperationException("wrapper", new RcaEvidenceException(Guid.NewGuid(), "private evidence diagnostic"))
        })
        {
            typeof(Rca).GetMethod("ReportError", flags)!.Invoke(page, [exception, "Rca.LoadCandidatesError"]);
            Assert.Equal(text["Rca.LoadCandidatesError"], typeof(Rca).GetField("error", flags)!.GetValue(page));
        }
    }

    [Fact]
    public void RcaEvidenceReadKeepsEmptyDuplicateAndEmptyGuidSemantics()
    {
        var id = Guid.NewGuid();
        Assert.Empty(RcaEvidence.Read(id, "[]"));
        var evidence = new[] { Guid.Empty, id, id };
        Assert.Equal(evidence, RcaEvidence.Read(id, JsonSerializer.Serialize(evidence)));
        var exception = Assert.Throws<RcaEvidenceException>(() => RcaEvidence.Read(id, "null"));
        Assert.Equal($"RCA {id} has invalid evidence event IDs.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RcaValidationLimitsAreExactAndPreserveStoredText(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("validation-limits", "DC01", Start);
        var selection = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin"))).Selection;
        foreach (var name in new[] { "", " ", new string('x', 257), "Invalid\u0001name" })
        {
            var exception = await Assert.ThrowsAsync<RcaValidationException>(() =>
                fixture.Service.CreateAsync(User("Admin"), name, [selection], false));
            Assert.Equal(RcaValidationCode.NameInvalid, exception.Code);
            Assert.Equal("Enter an RCA name of 1-256 characters without control characters.", exception.Message);
            Assert.Empty(await fixture.Service.ListAsync());
        }
        foreach (var selections in new[] { Array.Empty<RcaAlertSelection>(), new[] { selection, selection } })
            Assert.Equal(RcaValidationCode.SelectionsInvalid,
                (await Assert.ThrowsAsync<RcaValidationException>(() =>
                    fixture.Service.CreateAsync(User("Admin"), "Name", selections, false))).Code);
        var id = await fixture.Service.CreateAsync(User("Admin"), "  " + new string('x', 256) + "  ", [selection], false);
        var original = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(new string('x', 256), original.CategoryName);
        var account = new string('u', 256);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.AuthenticationUsers.Add(new() { Username = account, PasswordHash = "unused" });
            await context.SaveChangesAsync();
        }
        await fixture.Service.SaveAsync(User("Admin"), id, original.Revision, new string('a', 100000), account, "Open", new string('c', 4000));
        var saved = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(new string('a', 100000), saved.Analysis);
        Assert.Equal(account, saved.AssignedTo);
        Assert.Equal(new string('c', 4000), Assert.Single(await fixture.Service.GetChangesAsync(id), change => change.Field == "Comment").After);
        foreach (var values in new[]
        {
            (Analysis: new string('a', 100001), Comment: "", Assignee: ""),
            (Analysis: "", Comment: new string('c', 4001), Assignee: ""),
            (Analysis: "", Comment: "", Assignee: new string('u', 257))
        })
            Assert.Equal(RcaValidationCode.LengthExceeded,
                (await Assert.ThrowsAsync<RcaValidationException>(() => fixture.Service.SaveAsync(User("Admin"), id,
                    saved.Revision, values.Analysis, values.Assignee, "Open", values.Comment))).Code);
        Assert.Equal(RcaValidationCode.StatusInvalid,
            (await Assert.ThrowsAsync<RcaValidationException>(() =>
                fixture.Service.SaveAsync(User("Admin"), id, saved.Revision, "", "", "INVALID", ""))).Code);
        Assert.Equal(RcaValidationCode.AssigneeInvalid,
            (await Assert.ThrowsAsync<RcaValidationException>(() =>
                fixture.Service.SaveAsync(User("Admin"), id, saved.Revision, "", "unknown account", "Open", ""))).Code);
        Assert.Equal(RcaValidationCode.Changed,
            (await Assert.ThrowsAsync<RcaValidationException>(() =>
                fixture.Service.SaveAsync(User("Admin"), id, saved.Revision - 1, "", "", "Open", ""))).Code);
        Assert.Equal(RcaValidationCode.CaseMissing,
            (await Assert.ThrowsAsync<RcaValidationException>(() =>
                fixture.Service.SaveAsync(User("Admin"), Guid.NewGuid(), 1, "", "", "Open", ""))).Code);
        Assert.Equal(saved.Revision, Assert.Single(await fixture.Service.ListAsync()).Revision);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    public static IEnumerable<object[]> RcaReadBoundaryCases() =>
        new[] { "en-US", "de-DE" }.SelectMany(culture =>
            new[] { "RootCauseAnalyses", "AuthenticationUsers", "RcaPrompts", "RcaChanges", "RcaAlertLinks", "Alerts" }
                .Select(table => new object[] { culture, table }));

    private static (string Break, string Restore) RcaReadFault(string table) => table switch
    {
        "RootCauseAnalyses" => ("ALTER TABLE RootCauseAnalyses RENAME TO HiddenCases;", "ALTER TABLE HiddenCases RENAME TO RootCauseAnalyses;"),
        "AuthenticationUsers" => ("ALTER TABLE AuthenticationUsers RENAME TO HiddenAccounts;", "ALTER TABLE HiddenAccounts RENAME TO AuthenticationUsers;"),
        "RcaPrompts" => ("ALTER TABLE RcaPrompts RENAME TO HiddenPrompts;", "ALTER TABLE HiddenPrompts RENAME TO RcaPrompts;"),
        "RcaChanges" => ("ALTER TABLE RcaChanges RENAME TO HiddenChanges;", "ALTER TABLE HiddenChanges RENAME TO RcaChanges;"),
        "RcaAlertLinks" => ("ALTER TABLE RcaAlertLinks RENAME TO HiddenLinks;", "ALTER TABLE HiddenLinks RENAME TO RcaAlertLinks;"),
        "Alerts" => ("ALTER TABLE Alerts RENAME TO HiddenAlerts;", "ALTER TABLE HiddenAlerts RENAME TO Alerts;"),
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };

    [Theory]
    [MemberData(nameof(RcaReadBoundaryCases))]
    public async Task RcaReadFailuresAreSafeAtomicAndRecoverable(string culture, string table)
    {
        using var cultures = new PageCultures(culture);
        await using var fixture = await Fixture.CreateAsync(admin: true);
        await fixture.AddAlertAsync("rca-read", "DC01", Start.AddMinutes(-1));
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        var id = await fixture.Service.CreateAsync(User("Admin"), "Custom <RCA> &", [candidate.Selection], false);
        var fault = RcaReadFault(table);
        async Task Sql(string sql)
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync(sql);
        }
        await Sql(fault.Break);
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(SelectedRcaPage),
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["SelectedCaseId"] = id }));
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        var log = fixture.Services.GetRequiredService<RcaPageLogger>();
        var text = fixture.Services.GetRequiredService<UiText>();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Get(string name) => typeof(Rca).GetField(name, flags)!.GetValue(page);
        void Set(string name, object? value) => typeof(Rca).GetField(name, flags)!.SetValue(page, value);
        async Task<string> Invoke(string method, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (typeof(Rca).GetMethod(method, flags)!.Invoke(page, arguments) is Task task) await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        }
        void AssertFailure(string key, string html)
        {
            Assert.Equal(text[key], Get("error"));
            Assert.Contains(text[key], html);
            Assert.IsType<SqliteException>(log.Entries[^1].Exception);
            Assert.DoesNotContain(log.Entries[^1].Exception!.Message, html);
            Assert.Equal(false, Get("busy"));
        }
        var initial = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        AssertFailure("Rca.LoadCasesError", initial);
        Assert.Null(Get("selected"));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RootCauseAnalysis>>(Get("cases")));
        Assert.DoesNotContain(text["Rca.Empty"], initial);
        await Sql(fault.Restore);
        await Invoke("RefreshAsync");
        Assert.Null(Get("error"));
        Assert.Equal(id, Assert.IsType<RootCauseAnalysis>(Get("selected")).Id);
        var names = new[] { "cases", "selected", "assignees", "prompts", "changes", "linkedAlerts" };
        var before = names.ToDictionary(name => name, Get);
        Set("analysis", "Custom pending <analysis> &");
        Set("comment", "Custom pending comment");
        Set("assignedTo", "owner");
        Set("status", "InAnalysis");
        await Sql(fault.Break);
        AssertFailure("Rca.LoadCasesError", await Invoke("RefreshAsync"));
        foreach (var name in names) Assert.Same(before[name], Get(name));
        Assert.Equal("Custom pending <analysis> &", Get("analysis"));
        Assert.Equal("Custom pending comment", Get("comment"));
        Assert.Equal("owner", Get("assignedTo"));
        Assert.Equal("InAnalysis", Get("status"));
        if (table is "RcaPrompts" or "RcaChanges" or "RcaAlertLinks" or "Alerts")
        {
            var copy = Assert.IsType<RootCauseAnalysis>(before["selected"]);
            AssertFailure("Rca.LoadCaseError", await Invoke("SelectAsync", copy));
            foreach (var name in names) Assert.Same(before[name], Get(name));
            Assert.Equal("Custom pending comment", Get("comment"));
        }
        await Sql(fault.Restore);
        await Invoke("RefreshAsync");
        Assert.Null(Get("error"));
        Assert.Equal("", Get("comment"));
        Assert.Equal(id, Assert.IsType<RootCauseAnalysis>(Get("selected")).Id);
        if (table is "RootCauseAnalyses" or "RcaAlertLinks" or "Alerts")
        {
            await Invoke("OpenAlertPickerAsync", true);
            await Sql(fault.Break);
            var failedCandidates = await Invoke("RefreshAssignableAlertsAsync");
            AssertFailure("Rca.LoadCandidatesError", failedCandidates);
            Assert.Equal(true, Get("candidateLoadFailed"));
            Assert.DoesNotContain(text["Rca.NoCandidates"], failedCandidates);
            Set("newCaseName", "Blocked creation");
            await Invoke("SubmitAlertAssignmentAsync");
            await Sql(fault.Restore);
            Assert.Single(await fixture.Service.ListAsync());
            await Invoke("RefreshAssignableAlertsAsync");
            Assert.Null(Get("error"));
            Assert.Equal(false, Get("candidateLoadFailed"));
            Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<RcaAlertCandidate>>(Get("alertCandidates")));
        }
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [MemberData(nameof(RcaReadBoundaryCases))]
    public async Task ConfirmedRcaSaveRetainsSuccessAndBlocksRepeatUntilReload(string culture, string table)
    {
        using var cultures = new PageCultures(culture);
        var writes = new RcaWriteInterceptor();
        await using var fixture = await Fixture.CreateAsync(admin: true, interceptors: [writes]);
        await fixture.AddAlertAsync("rca-save", "DC01", Start.AddMinutes(-1));
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        var id = await fixture.Service.CreateAsync(User("Admin"), "Custom saved RCA", [candidate.Selection], false);
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(SelectedRcaPage),
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["SelectedCaseId"] = id }));
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        var log = fixture.Services.GetRequiredService<RcaPageLogger>();
        var text = fixture.Services.GetRequiredService<UiText>();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Get(string name) => typeof(Rca).GetField(name, flags)!.GetValue(page);
        void Set(string name, object? value) => typeof(Rca).GetField(name, flags)!.SetValue(page, value);
        async Task<string> Invoke(string method, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (typeof(Rca).GetMethod(method, flags)!.Invoke(page, arguments) is Task task) await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        }
        async Task Sql(string sql)
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync(sql);
        }
        var fault = RcaReadFault(table);
        var before = Assert.IsType<RootCauseAnalysis>(Get("selected"));
        Set("analysis", "Confirmed <analysis> &");
        Set("comment", "Confirmed comment");
        var attempts = writes.Attempts;
        writes.AfterWrite = () => fixture.Factory.BeforeNextContext = () =>
        {
            using var context = fixture.Factory.CreateDbContext();
            context.Database.ExecuteSqlRaw(fault.Break);
        };
        string html;
        try { html = await Invoke("SaveAsync"); }
        finally { writes.AfterWrite = null; }
        Assert.Equal(text["Rca.Saved"], Get("message"));
        Assert.Equal(text["Rca.LoadCasesError"], Get("error"));
        Assert.Contains(text["Rca.Saved"], html);
        Assert.Contains(text["Rca.LoadCasesError"], html);
        Assert.DoesNotContain(text["Rca.SaveError"], html);
        Assert.DoesNotContain(Assert.IsType<SqliteException>(log.Entries[^1].Exception).Message, html);
        Assert.Same(before, Get("selected"));
        Assert.Equal("", Get("comment"));
        Assert.Equal(true, Get("reloadRequired"));
        Assert.Equal(false, Get("busy"));
        Assert.Equal(attempts + 1, writes.Attempts);
        Assert.Contains("disabled", System.Text.RegularExpressions.Regex.Match(html,
            @"<button[^>]*type=""submit""[^>]*>").Value);
        await Invoke("SaveAsync");
        await Invoke("OpenAlertPickerAsync", true);
        await Invoke("SubmitAlertAssignmentAsync");
        Assert.Equal(attempts + 1, writes.Attempts);
        await Sql(fault.Restore);
        var saved = (await fixture.Service.ListAsync()).Single(rca => rca.Id == id);
        Assert.Equal(before.Revision + 1, saved.Revision);
        Assert.Equal("Confirmed <analysis> &", saved.Analysis);
        Assert.Single(await fixture.Service.GetChangesAsync(id), change => change.Field == "Comment");
        await Invoke("RefreshAsync");
        Assert.Null(Get("error"));
        Assert.Equal(false, Get("reloadRequired"));
        Assert.Equal(saved.Revision, Assert.IsType<RootCauseAnalysis>(Get("selected")).Revision);
        Assert.Equal(id, Assert.IsType<RootCauseAnalysis>(Get("selected")).Id);
        Assert.Equal(attempts + 1, writes.Attempts);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task ConfirmedRcaAssignmentsSelectCommittedCaseAfterReloadRecovery(string culture, bool create)
    {
        using var cultures = new PageCultures(culture);
        var writes = new RcaWriteInterceptor();
        await using var fixture = await Fixture.CreateAsync(admin: true, interceptors: [writes]);
        await fixture.AddAlertAsync("rca-assignment", "DC01", Start.AddMinutes(-1));
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        Guid? originalId = create ? null
            : await fixture.Service.CreateAsync(User("Admin"), "Original RCA", [candidate.Selection], false);
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(SelectedRcaPage),
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["SelectedCaseId"] = originalId ?? Guid.Empty }));
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        var text = fixture.Services.GetRequiredService<UiText>();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Get(string name) => typeof(Rca).GetField(name, flags)!.GetValue(page);
        async Task Invoke(string method, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (typeof(Rca).GetMethod(method, flags)!.Invoke(page, arguments) is Task task) await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
        }
        if (!create) await fixture.AddAlertAsync("additional-assignment", "DC02", Start.AddMinutes(-1));
        await Invoke("OpenAlertPickerAsync", create);
        typeof(Rca).GetField("newCaseName", flags)!.SetValue(page, "Confirmed new <RCA> &");
        candidate = (await fixture.Service.GetAssignableAlertsAsync(User("Admin")))
            .Single(item => item.Alert.AlertId == (create ? "rca-assignment" : "additional-assignment"));
        await Invoke("AlertSelectionChanged", candidate.Alert.Id, new ChangeEventArgs { Value = true });
        writes.AfterWrite = () => fixture.Factory.BeforeNextContext = () =>
        {
            using var context = fixture.Factory.CreateDbContext();
            context.Database.ExecuteSqlRaw("ALTER TABLE RcaPrompts RENAME TO HiddenPrompts;");
        };
        try { await Invoke("SubmitAlertAssignmentAsync"); }
        finally { writes.AfterWrite = null; }
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Equal(text["Rca.AssignmentsSaved"], Get("message"));
        Assert.Equal(text["Rca.LoadCasesError"], Get("error"));
        Assert.Contains(text["Rca.AssignmentsSaved"], html);
        Assert.Contains(text["Rca.LoadCasesError"], html);
        Assert.DoesNotContain(text["Rca.SaveAssignmentsError"], html);
        Assert.Equal(true, Get("reloadRequired"));
        var pendingId = Assert.IsType<Guid>(Get("pendingReloadCaseId"));
        if (!create) Assert.Equal(originalId, pendingId);
        var attempts = writes.Attempts;
        await Invoke("SubmitAlertAssignmentAsync");
        Assert.Equal(attempts, writes.Attempts);
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE HiddenPrompts RENAME TO RcaPrompts;");
        var cases = await fixture.Service.ListAsync();
        Assert.Single(cases);
        Assert.Equal(pendingId, cases[0].Id);
        Assert.Equal(create ? "Confirmed new <RCA> &" : "Original RCA", cases[0].CategoryName);
        Assert.Equal(create ? 1 : 2, (await fixture.Service.GetLinkedAlertsAsync(pendingId)).Count);
        await Invoke("RefreshAsync");
        Assert.Null(Get("error"));
        Assert.Equal(false, Get("reloadRequired"));
        Assert.Equal(pendingId, Assert.IsType<RootCauseAnalysis>(Get("selected")).Id);
        Assert.Equal(attempts, writes.Attempts);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RcaSaveErrorsLogOriginalCauseAndPreserveUnconfirmedDrafts(string culture)
    {
        using var cultures = new PageCultures(culture);
        var writes = new RcaWriteInterceptor();
        var commits = new RcaCommitInterceptor();
        await using var fixture = await Fixture.CreateAsync(admin: true, interceptors: [writes, commits]);
        await fixture.AddAlertAsync("rca-errors", "DC01", Start.AddMinutes(-1));
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        var id = await fixture.Service.CreateAsync(User("Admin"), "Original RCA", [candidate.Selection], false);
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(SelectedRcaPage),
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["SelectedCaseId"] = id }));
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        var log = fixture.Services.GetRequiredService<RcaPageLogger>();
        var text = fixture.Services.GetRequiredService<UiText>();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Get(string name) => typeof(Rca).GetField(name, flags)!.GetValue(page);
        void Set(string name, object? value) => typeof(Rca).GetField(name, flags)!.SetValue(page, value);
        async Task<string> Invoke(string method)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await Assert.IsAssignableFrom<Task>(typeof(Rca).GetMethod(method, flags)!.Invoke(page, []));
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        }
        foreach (var phase in new[] { "before", "after", "provider", "concurrency", "cancellation" })
        {
            await Invoke("RefreshAsync");
            var before = Assert.IsType<RootCauseAnalysis>(Get("selected"));
            var historyBefore = (await fixture.Service.GetChangesAsync(id)).Count;
            var attempts = writes.Attempts;
            Set("analysis", "Pending <analysis> & " + phase);
            Set("comment", "Pending comment " + phase);
            Exception failure = phase switch
            {
                "concurrency" => new DbUpdateConcurrencyException("Sensitive RCA concurrency detail."),
                "cancellation" => new OperationCanceledException("Sensitive unexpected RCA cancellation."),
                "provider" => new InvalidOperationException("Sensitive RCA provider detail"),
                _ => new InvalidOperationException("Sensitive RCA write <details> & " + phase)
            };
            if (phase == "after") commits.Failure = failure;
            else if (phase == "provider")
            {
                await using var context = fixture.Factory.CreateDbContext();
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER FailRcaSave BEFORE UPDATE ON RootCauseAnalyses BEGIN SELECT RAISE(ABORT, 'Sensitive RCA provider detail'); END;");
            }
            else writes.Failure = failure;
            string html;
            try { html = await Invoke("SaveAsync"); }
            finally
            {
                writes.Failure = null;
                commits.Failure = null;
                if (phase == "provider")
                {
                    await using var context = fixture.Factory.CreateDbContext();
                    await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailRcaSave;");
                }
            }
            var key = phase == "concurrency" ? "Rca.Errors.Changed" : "Rca.SaveError";
            Assert.Equal(text[key], Get("error"));
            Assert.Contains(text[key], html);
            Assert.Null(Get("message"));
            Assert.DoesNotContain(failure.Message, html);
            Assert.Contains(failure.Message, log.Entries[^1].Exception!.ToString());
            if (phase == "provider")
                Assert.IsType<SqliteException>(Assert.IsType<DbUpdateException>(log.Entries[^1].Exception).InnerException);
            Assert.Equal(phase == "concurrency" ? LogLevel.Warning : LogLevel.Error, log.Entries[^1].Level);
            Assert.Same(before, Get("selected"));
            Assert.Equal("Pending <analysis> & " + phase, Get("analysis"));
            Assert.Equal("Pending comment " + phase, Get("comment"));
            Assert.Equal(false, Get("busy"));
            Assert.Equal(attempts + 1, writes.Attempts);
            var stored = (await fixture.Service.ListAsync()).Single(rca => rca.Id == id);
            Assert.Equal(before.Revision + (phase == "after" ? 1 : 0), stored.Revision);
            Assert.Equal(phase == "after" ? "Pending <analysis> & after" : before.Analysis, stored.Analysis);
            Assert.Equal(historyBefore + (phase == "after" ? 2 : 0), (await fixture.Service.GetChangesAsync(id)).Count);
        }
        await Invoke("RefreshAsync");
        Set("analysis", "Denied pending analysis");
        var beforeDenied = writes.Attempts;
        typeof(Rca).GetProperty("AuthenticationStateTask", flags)!.SetValue(page,
            Task.FromResult(new AuthenticationState(User("Reader"))));
        var denied = await Invoke("SaveAsync");
        Assert.Equal(text["Rca.Errors.NotAllowed"], Get("error"));
        Assert.Contains(text["Rca.Errors.NotAllowed"], denied);
        Assert.IsType<UnauthorizedAccessException>(log.Entries[^1].Exception);
        Assert.Equal(LogLevel.Warning, log.Entries[^1].Level);
        Assert.Equal(beforeDenied, writes.Attempts);
        Assert.Equal("Denied pending analysis", Get("analysis"));
        Set("busy", true);
        await Invoke("SaveAsync");
        Assert.Equal(beforeDenied, writes.Attempts);
        Set("busy", false);
        denied = await Invoke("RefreshAssignableAlertsAsync");
        Assert.Contains(text["Rca.Errors.NotAllowed"], denied);
        Assert.IsType<UnauthorizedAccessException>(log.Entries[^1].Exception);
        Assert.Equal(true, Get("candidateLoadFailed"));
        Assert.Equal(beforeDenied, writes.Attempts);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task UnconfirmedRcaCreationKeepsPickerAndDoesNotClaimSuccess(string culture, bool afterCommit)
    {
        using var cultures = new PageCultures(culture);
        var writes = new RcaWriteInterceptor();
        var commits = new RcaCommitInterceptor();
        await using var fixture = await Fixture.CreateAsync(admin: true, interceptors: [writes, commits]);
        await fixture.AddAlertAsync("rca-create-error", "DC01", Start.AddMinutes(-1));
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Rca), ParameterView.Empty);
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().Page!;
        var text = fixture.Services.GetRequiredService<UiText>();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        async Task Invoke(string method, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                if (typeof(Rca).GetMethod(method, flags)!.Invoke(page, arguments) is Task task) await task;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, []);
            });
        }
        await Invoke("OpenAlertPickerAsync", true);
        var candidate = Assert.Single(await fixture.Service.GetAssignableAlertsAsync(User("Admin")));
        typeof(Rca).GetField("newCaseName", flags)!.SetValue(page, "Pending new <RCA> &");
        await Invoke("AlertSelectionChanged", candidate.Alert.Id, new ChangeEventArgs { Value = true });
        var failure = new InvalidOperationException("Sensitive assignment provider detail.");
        if (afterCommit) commits.Failure = failure;
        else writes.Failure = failure;
        var attempts = writes.Attempts;
        try { await Invoke("SubmitAlertAssignmentAsync"); }
        finally { writes.Failure = null; commits.Failure = null; }
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains(text["Rca.SaveAssignmentsError"], html);
        Assert.DoesNotContain(text["Rca.AssignmentsSaved"], html);
        Assert.DoesNotContain(failure.Message, html);
        Assert.Same(failure, fixture.Services.GetRequiredService<RcaPageLogger>().Entries[^1].Exception);
        Assert.InRange(writes.Attempts, attempts + 1, attempts + (afterCommit ? 2 : 1));
        Assert.Equal("Pending new <RCA> &", typeof(Rca).GetField("newCaseName", flags)!.GetValue(page));
        Assert.Equal(true, typeof(Rca).GetField("creatingCase", flags)!.GetValue(page));
        Assert.Contains(candidate.Alert.Id, Assert.IsType<HashSet<Guid>>(typeof(Rca).GetField("selectedAlertIds", flags)!.GetValue(page)));
        if (afterCommit)
        {
            var saved = Assert.Single(await fixture.Service.ListAsync());
            Assert.Equal("Pending new <RCA> &", saved.CategoryName);
            Assert.Single(await fixture.Service.GetLinkedAlertsAsync(saved.Id));
            var afterWrites = writes.Attempts;
            await Invoke("SubmitAlertAssignmentAsync");
            Assert.Single(await fixture.Service.ListAsync());
            Assert.Equal(afterWrites, writes.Attempts);
            Assert.DoesNotContain(text["Rca.AssignmentsSaved"],
                WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
            await Invoke("RefreshAsync");
            Assert.Null(typeof(Rca).GetField("error", flags)!.GetValue(page));
        }
        else
        {
            Assert.Empty(await fixture.Service.ListAsync());
            await Invoke("SubmitAlertAssignmentAsync");
            Assert.Single(await fixture.Service.ListAsync());
            Assert.Contains(text["Rca.AssignmentsSaved"], WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString)));
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RendersRcaNavigationPage(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var fixture = await Fixture.CreateAsync();
        await using var renderer = new StaticPageRenderer(fixture.Services,
            fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(Rca), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return WebUtility.HtmlDecode(rendered.ToHtmlString());
        });
        var text = fixture.Services.GetRequiredService<UiText>();
        Assert.Contains(culture == "de-DE" ? "<h1>Ursachenanalyse</h1>" : "<h1>Root cause analysis</h1>", html);
        Assert.Contains(text["Rca.Empty"], html);
        Assert.Contains(culture == "de-DE" ? "Aktualisieren" : "Refresh", html);
        Assert.DoesNotContain(text["Common.SaveChanges"], html);
        Assert.DoesNotContain(text["Rca.New"], html);
        Assert.DoesNotContain($">{text["Rca.AssignAlerts"]}</button>", html);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", false)]
    [InlineData("de-DE", true)]
    public async Task RendersCompactGroupsAndSelectedCaseAlertsAboveAnalysis(string culture, bool selectClosed)
    {
        using var cultures = new PageCultures(culture);
        const string raw = "New RCA <script>Original ä\r\nAnalysis & Refresh</script>";
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
                new() { Id = openId, RuleId = ruleId, CategoryName = "Open domain outage",
                    CreatedAt = Start, Analysis = raw, AssignedTo = "Refresh" },
                new() { Id = closedId, RuleId = ruleId, CategoryName = "Closed site outage",
                    CreatedAt = Start, Status = RcaStatuses.Closed, Analysis = raw, AssignedTo = "Refresh" });
            context.RcaPrompts.Add(new()
            {
                Id = Guid.NewGuid(), RcaId = selectClosed ? closedId : openId,
                Sequence = 0, CreatedAt = Start, RawPrompt = raw
            });
            context.RcaChanges.Add(new()
            {
                Id = Guid.NewGuid(), RcaId = selectClosed ? closedId : openId, CreatedAt = Start,
                Actor = "New RCA", Field = "Analysis", Before = raw, After = raw
            });
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
            return WebUtility.HtmlDecode(rendered.ToHtmlString());
        });
        var text = fixture.Services.GetRequiredService<UiText>();
        Assert.Contains(text.Format("Rca.OpenCount", 1), html);
        Assert.Contains(text.Format("Rca.ClosedCount", 1), html);
        var closedGroupStart = html.IndexOf("<details class=\"rca-closed-group\"", StringComparison.Ordinal);
        var closedGroupTag = html[closedGroupStart..(html.IndexOf('>', closedGroupStart) + 1)];
        Assert.DoesNotContain(" open", closedGroupTag);
        Assert.True(html.IndexOf("Open domain outage", StringComparison.Ordinal) < closedGroupStart);
        Assert.True(html.IndexOf("Closed site outage", StringComparison.Ordinal) > closedGroupStart);
        Assert.Contains("--rca-color:#a64b00", html);
        Assert.Contains("--rca-color:#137547", html);
        Assert.DoesNotContain("admin-rule-card", html);
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.Contains("aria-pressed=\"false\"", html);
        Assert.Contains(text.Format("Rca.LinkedCount", 1), html);
        Assert.Contains("linked-alert", html);
        Assert.Contains(eventId.ToString(), html);
        Assert.Contains("DC01", html);
        Assert.DoesNotContain("unrelated-alert", html);
        Assert.DoesNotContain("DC02", html);
        Assert.True(html.IndexOf(text.Format("Rca.LinkedCount", 1), StringComparison.Ordinal) <
            html.IndexOf($"<span>{text["Rca.Analysis"]}</span>", StringComparison.Ordinal));
        Assert.DoesNotContain(text["Common.SaveChanges"], html);
        Assert.Contains(Start.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"), html);
        Assert.Contains(raw, html);
        Assert.Contains(text["Rca.RawPromptHelp"], html);
        Assert.Contains(text["Rca.Changelog"], html);
        Assert.Contains(" / New RCA / Analysis", html);
        Assert.Contains(">Refresh</span>", html);
        Assert.Equal(raw, Assert.Single(await fixture.Service.GetPromptsAsync(selectClosed ? closedId : openId)).RawPrompt);
        Assert.Equal(raw, (await fixture.Service.ListAsync()).Single(rca => rca.Id == (selectClosed ? closedId : openId)).Analysis);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
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
        await Assert.ThrowsAsync<RcaEvidenceException>(() => fixture.Service.GetOpenNoticesAsync());
    }

    [Fact]
    public async Task SuppressionOfRecoveredEvidenceEndsCriticalStateButPreservesRcaLinksAndInitialPrompt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("recovered", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        var original = Assert.Single(await fixture.Service.ListAsync());
        var prompt = Assert.Single(await fixture.Service.GetPromptsAsync(original.Id));
        var linked = Assert.Single(await fixture.Service.GetLinkedAlertsAsync(original.Id));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var alert = await context.Alerts.SingleAsync();
            context.Entry(alert).CurrentValues.SetValues(alert with
            {
                Name = "DCDiag",
                RawJson = """{"queryResult":{"type":"DCDiag","columns":[],"rows":[]}}"""
            });
            context.AlertRules.Add(DefaultAlertRules.Create().Single(rule => rule.ConditionType == AlertRuleConditionTypes.NoFailedItems));
            await context.SaveChangesAsync();
        }
        await fixture.ReconcileAsync(Start.AddMinutes(1));
        var updated = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(original.Id, updated.Id);
        Assert.False(updated.IsActive);
        Assert.Equal(Start.AddMinutes(1), updated.CriticalEndedAt);
        Assert.Equal(linked.Id, Assert.Single(await fixture.Service.GetLinkedAlertsAsync(original.Id)).Id);
        Assert.Equal(prompt.RawPrompt, (await fixture.Service.GetPromptsAsync(original.Id))[0].RawPrompt);
        await using var finalContext = fixture.Factory.CreateDbContext();
        Assert.Equal("Fired", (await finalContext.Alerts.SingleAsync()).MonitorCondition);
    }

    [Fact]
    public async Task InboxRendersConfiguredInventorySiteInTargetColumnAndTargetOutput()
    {
        await using var fixture = await Fixture.CreateAsync(inboxQueries: true);
        await fixture.AddAlertAsync("without-payload-site", "DC01", Start);
        await fixture.LinkInventoryAsync("DC01", "example.org");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await context.AlertRules.ExecuteDeleteAsync();
            var computer = await context.ComputerInventory.SingleAsync();
            computer.Site = "Configured site";
            await context.SaveChangesAsync();
        }
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(Home), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return rendered.ToHtmlString();
        });
        Assert.Contains("class=\"target\" title=\"DC01 (Configured site)\">DC01 (Configured site)</td>", html);
        Assert.Contains("<span class=\"heatmap-site\">Configured site</span>", html);
    }

    [Fact]
    public async Task InboxRendersOpenRcaDomainLinkAndHidesClosedCases()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAlertAsync("first", "DC01", Start);
        await fixture.ReconcileAsync(Start);
        await fixture.LinkInventoryAsync("DC01", "example.org");
        var rca = Assert.Single(await fixture.Service.ListAsync());
        var text = fixture.Services.GetRequiredService<UiText>();
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
        Assert.Contains(text.Format("Inbox.RcaNoticeWithDomains", "Outage", "example.org"), html);
        Assert.Contains($"/rca?caseId={rca.Id}", html);
        Assert.Contains($"aria-label=\"{text["Inbox.OpenRcaNotices"]}\"", html);
        Assert.Contains(text.Format("Inbox.RcaNoticeDetails", text["Inbox.RcaStatus.Open"], text["Inbox.RcaUnassigned"]), html);
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", "admin@example.org", "InAnalysis", "");
        html = await RenderInboxAsync();
        Assert.Contains(text.Format("Inbox.RcaNoticeDetails", text["Inbox.RcaStatus.InAnalysis"], "admin@example.org"), html);
        rca = Assert.Single(await fixture.Service.ListAsync());
        await fixture.Service.SaveAsync(User("Admin"), rca.Id, rca.Revision, "", "admin@example.org", "Closed", "");
        html = await RenderInboxAsync();
        Assert.DoesNotContain($"/rca?caseId={rca.Id}", html);
        Assert.DoesNotContain($"aria-label=\"{text["Inbox.OpenRcaNotices"]}\"", html);
    }

    private static ClaimsPrincipal User(string? role) => new(new ClaimsIdentity(
        role is null ? [] : new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, role) },
        role is null ? null : "test"));

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RcaSummaryTranslatesOnlyKnownCodesAndMissingAssignees(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var fixture = await Fixture.CreateAsync();
        await using var renderer = new StaticPageRenderer(fixture.Services,
            fixture.Services.GetRequiredService<ILoggerFactory>());
        var text = fixture.Services.GetRequiredService<UiText>();
        foreach (var status in RcaStatuses.All.Append("Future <status>"))
        foreach (var active in new[] { false, true })
        foreach (var assignee in new[] { "", " \t", "Open <owner>" })
        {
            var item = new RootCauseAnalysis
            {
                Id = Guid.NewGuid(), CategoryName = "New RCA <name>", Status = status,
                AssignedTo = assignee, IsActive = active, CreatedAt = Start
            };
            var rawHtml = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(Components.RcaCaseSummary),
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(Components.RcaCaseSummary.Case)] = item,
                        [nameof(Components.RcaCaseSummary.Selected)] = true,
                        [nameof(Components.RcaCaseSummary.Busy)] = true
                    }));
                await root.QuiescenceTask;
                return root.ToHtmlString();
            });
            var html = WebUtility.HtmlDecode(rawHtml);
            Assert.Contains($"title=\"{text.Format("Rca.OpenCase", item.Id)}\"", html);
            Assert.Contains("New RCA <name>", html);
            Assert.Contains(RcaStatuses.All.Contains(status) ? text[$"Inbox.RcaStatus.{status}"] : status, html);
            Assert.Contains(text[active ? "Rules.Critical" : "Rca.NoLongerCritical"], html);
            Assert.Contains(string.IsNullOrWhiteSpace(assignee) ? text["Inbox.RcaUnassigned"] : assignee, html);
            Assert.Contains("aria-pressed=\"true\" disabled", html);
            Assert.Contains(Start.ToString("O"), html);
            Assert.Contains(Start.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"), html);
            Assert.DoesNotContain("<name>", rawHtml);
            Assert.Equal(status, item.Status);
            Assert.Equal(assignee, item.AssignedTo);
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", false)]
    [InlineData("de-DE", true)]
    public async Task AnalysisWorkspaceLocalizesActualPageAndKeepsPromptClipboardAndResultContracts(
        string culture, bool admin)
    {
        using var cultures = new PageCultures(culture);
        var jobWrites = new AgentJobWriteInterceptor();
        await using var fixture = await Fixture.CreateAsync(admin: admin, promptPage: true, interceptors: [jobWrites]);
        var now = DateTimeOffset.UtcNow;
        ((FixedTime)fixture.Services.GetRequiredService<TimeProvider>()).Now = now;
        var text = fixture.Services.GetRequiredService<UiText>();
        await using var renderer = new StaticPageRenderer(fixture.Services,
            fixture.Services.GetRequiredService<ILoggerFactory>());
        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = renderer.BeginRenderingComponent(typeof(Prompt), ParameterView.Empty);
            await root.QuiescenceTask;
            return root;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().PromptPage!;
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(Prompt);
        var html = await HtmlAsync();
        Assert.Contains(culture == "de-DE" ? "<h1>Analysearbeitsbereich</h1>" : "<h1>Analysis workspace</h1>", html);
        Assert.Contains(culture == "de-DE" ? ">Prompt kopieren" : ">Copy prompt", html);
        Assert.Contains(culture == "de-DE" ? ">Analyseauftrag erstellen" : ">Create analysis job", html);
        Assert.Contains(text["Prompt.NoAlerts"], html);
        Assert.Contains(text["Prompt.SelectAlert"], html);
        Assert.Contains(text[admin ? "Prompt.NoJobs" : "Prompt.AccessHelp"], html);
        Assert.Equal("Select at least one alert to prepare an incident analysis prompt.", Get<string>("generatedPrompt"));
        foreach (var value in new[] { "Incident", "Target", "GraphSubtree", "active", "recent", "1", "4", "24", "168" })
            Assert.Contains($"value=\"{value}\"", html);
        Assert.Contains("value=\"GraphSubtree\" disabled", html);

        await fixture.AddAlertAsync("New RCA", "Refresh <target>", now.AddMinutes(-1));
        await InvokeAsync("ReloadAlertsAsync", true);
        html = await HtmlAsync();
        Assert.Contains("Refresh <target>", html);
        Assert.DoesNotContain("<target>", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        Assert.Contains(text["Prompt.IncludedEvidence"], html);
        var snapshot = Get<IncidentAnalysisSnapshot>("previewSnapshot");
        var generated = Get<string>("generatedPrompt");
        var orchestrator = fixture.Services.GetRequiredService<AgentAnalysisOrchestrator>();
        Assert.Equal(orchestrator.Prepare(snapshot).Prompt, generated);
        using (new PageCultures("en-US"))
            Assert.Equal(generated, orchestrator.Prepare(snapshot).Prompt);
        var clipboard = fixture.Services.GetRequiredService<RecordingJavaScript>().Clipboard;
        await InvokeAsync("CopyPromptAsync");
        Assert.Equal(generated, Assert.Single(clipboard));
        Assert.Contains(text["Prompt.Copied"], await HtmlAsync());
        await InvokeAsync("ToggleSystemHistory", new ChangeEventArgs { Value = false });
        await InvokeAsync("ToggleRelatedContext", new ChangeEventArgs { Value = false });
        Assert.Contains(text["Prompt.ExcludedMessage"], await HtmlAsync());
        await InvokeAsync("ClearSelection");
        Assert.Contains(text["Prompt.SelectAlert"], await HtmlAsync());
        Assert.Equal("Select at least one alert to prepare an incident analysis prompt.", Get<string>("generatedPrompt"));
        await InvokeAsync("SelectAll");
        await InvokeAsync("ChangeAnalysisScope", new ChangeEventArgs { Value = "Target" });
        Assert.Equal(IncidentAnalysisScopeType.Target, Get<IncidentAnalysisSnapshot>("previewSnapshot").Scope.Type);

        if (!admin)
        {
            await InvokeAsync("CreateJobAsync");
            Assert.Contains(text["Prompt.AccessHelp"], await HtmlAsync());
            Assert.Empty(await fixture.Services.GetRequiredService<AgentAnalysisJobStore>().ListAsync("owner", true));
            return;
        }

        var promptBeforeStart = Get<string>("generatedPrompt");
        type.GetField("analysisScopeType", flags)!.SetValue(page, "GraphSubtree");
        await InvokeAsync("CreateJobAsync");
        Assert.Equal(text["Prompt.Job.RequestInvalid"], Get<string>("jobError"));
        Assert.Equal(promptBeforeStart, Get<string>("generatedPrompt"));
        Assert.False(Get<bool>("jobReloadRequired"));
        Assert.Empty(await fixture.Services.GetRequiredService<AgentAnalysisJobStore>().ListAsync("owner", true));
        type.GetField("analysisScopeType", flags)!.SetValue(page, "Target");
        foreach (var code in Enum.GetValues<AgentAnalysisScopeDeniedCode>())
            Assert.Equal(text[$"Prompt.Job.{code}"], type.GetMethod("JobStartError", flags)!.Invoke(page,
                [new AgentAnalysisScopeDeniedException("SECRET <scope>", code)]));
        foreach (var code in Enum.GetValues<AgentAnalysisJobValidationCode>())
            Assert.Equal(text[$"Prompt.Job.{code}"], type.GetMethod("JobStartError", flags)!.Invoke(page,
                [new AgentAnalysisJobValidationException(code, "SECRET <validation>")]));
        foreach (var failure in new Exception[]
        {
            new InvalidOperationException("CreatedBy is required and may contain at most 256 characters."),
            new AgentAnalysisScopeDeniedException("The authenticated tenant is not allowed to start agent analysis jobs."),
            new AgentAnalysisScopeDeniedException("SECRET", (AgentAnalysisScopeDeniedCode)999),
            new AgentAnalysisJobValidationException((AgentAnalysisJobValidationCode)999, "SECRET"),
            new IOException("Wrapper", new AgentAnalysisJobValidationException(AgentAnalysisJobValidationCode.EvidenceRequired, "SECRET"))
        })
            Assert.Equal(text["Prompt.Job.StartFailed"], type.GetMethod("JobStartError", flags)!.Invoke(page, [failure]));
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <start>");
        await InvokeAsync("CreateJobAsync");
        Assert.Equal(text["Prompt.Job.StartFailed"], Get<string>("jobError"));
        Assert.True(Get<bool>("jobReloadRequired"));
        Assert.False(Get<bool>("isSavingJob"));
        Assert.Equal(promptBeforeStart, Get<string>("generatedPrompt"));
        Assert.Contains(text["Prompt.Job.RefreshHistory"], await HtmlAsync());
        Assert.DoesNotContain(text["Prompt.NoJobs"], await HtmlAsync());
        await InvokeAsync("CreateJobAsync");
        Assert.Equal(0, jobWrites.Added);
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <recovery>");
        await InvokeAsync("RefreshJobHistoryAsync");
        Assert.True(Get<bool>("jobReloadRequired"));
        await InvokeAsync("RefreshJobHistoryAsync");
        Assert.False(Get<bool>("jobReloadRequired"));
        jobWrites.AfterWrite = () =>
        {
            jobWrites.AfterWrite = null;
            fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <postcommit>");
        };
        await InvokeAsync("CreateJobAsync");
        Assert.Equal(text["Prompt.Job.CreatedReloadFailed"], Get<string>("jobError"));
        Assert.Equal(text["Prompt.JobCreated"], Get<string>("copyStatus"));
        Assert.True(Get<bool>("jobReloadRequired"));
        Assert.Equal(1, jobWrites.Added);
        await InvokeAsync("CreateJobAsync");
        Assert.Equal(1, jobWrites.Added);
        await InvokeAsync("RefreshJobHistoryAsync");
        Assert.False(Get<bool>("jobReloadRequired"));
        var activeJob = Get<AgentAnalysisJobResponse>("activeJob");
        Assert.Contains(text["Prompt.JobCreated"], await HtmlAsync());
        var store = fixture.Services.GetRequiredService<AgentAnalysisJobStore>();
        var job = (await store.GetAsync(activeJob.Id))!;
        var originalJobDiagnostics = (job.StatusReason, job.ErrorCode, job.ErrorMessage);
        foreach (var message in new[]
        {
            "Analysis queued.",
            "Log Analytics authentication failed.",
            "Original <stored diagnostic> &",
            "Eigene <Fehlermeldung> &"
        })
        {
            await using (var context = fixture.Factory.CreateDbContext())
                await context.AgentAnalysisJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.StatusReason, message)
                    .SetProperty(item => item.ErrorCode, "Original <code>")
                    .SetProperty(item => item.ErrorMessage, message));
            await InvokeAsync("RefreshJobHistoryAsync");
            var storedDiagnostic = Get<AgentAnalysisJobResponse>("activeJob");
            var originalResponse = JsonSerializer.Serialize(storedDiagnostic);
            var raw = await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);
            var decoded = WebUtility.HtmlDecode(raw);
            Assert.Contains($"<p class=\"agent-job-reason\">{message}</p>", decoded);
            Assert.Contains(message, decoded);
            Assert.Contains("Original <code>", decoded);
            Assert.DoesNotContain("<stored diagnostic>", raw);
            Assert.DoesNotContain("<Fehlermeldung>", raw);
            Assert.Equal(message, storedDiagnostic.StatusReason);
            Assert.Equal(message, storedDiagnostic.ErrorMessage);
            Assert.Equal(originalResponse, JsonSerializer.Serialize(Get<AgentAnalysisJobResponse>("activeJob")));
            var unchangedDiagnostics = (await store.GetAsync(job.Id))!;
            Assert.Equal(message, unchangedDiagnostics.StatusReason);
            Assert.Equal(message, unchangedDiagnostics.ErrorMessage);
        }
        await using (var context = fixture.Factory.CreateDbContext())
            await context.AgentAnalysisJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.StatusReason, originalJobDiagnostics.StatusReason)
                .SetProperty(item => item.ErrorCode, originalJobDiagnostics.ErrorCode)
                .SetProperty(item => item.ErrorMessage, originalJobDiagnostics.ErrorMessage));
        await InvokeAsync("RefreshJobHistoryAsync");
        Assert.Equal("Refresh <target>", job.ScopeValue);
        var storedSnapshot = job.SnapshotJson;
        job = await store.TransitionAsync(job.Id, job.Revision, AgentAnalysisJobStatuses.Running);
        var source = fixture.Services.GetRequiredService<MicrosoftSourceCatalog>().ApprovedSources.First();
        var original = AgentAnalysisJobStateMachineTests.CreateResult(job.AnalysisId, job.ResultVersion);
        var result = original with
        {
            DetailedAnalysis = original.DetailedAnalysis with
            {
                ExecutiveSummary = new[] { "ObservedFact", "RuleAssessment", "GraphCorrelation",
                        "HistoricalPattern", "Contradiction", "MissingEvidence" }
                    .Select((kind, index) => new AgentExecutiveStatement($"executive-{index}", kind,
                        $"New RCA <{kind}>", [], [], [source.Id])).ToArray(),
                Hypotheses = [new("hypothesis-1", "Refresh <hypothesis>", "High", [], [], "New RCA <confirm>")]
            }
        };
        job = await store.CompleteAsync(job.Id, job.Revision, result);
        var storedResult = job.ResultJson;
        await InvokeAsync("RefreshActiveJobAsync");
        html = await HtmlAsync();
        foreach (var key in new[] { "Prompt.FullAnalysis", "Prompt.TeamsSummary", "Prompt.ObservedFacts",
            "Prompt.RuleAssessments", "Prompt.Correlations", "Prompt.OpenEvidence", "Prompt.Hypotheses",
            "Prompt.Checks", "Prompt.Handoff", "Prompt.Review", "Prompt.Accept", "Prompt.Reject",
            "Prompt.CopyHandoff", "Prompt.Download" })
            Assert.Contains(text[key], html);
        Assert.Contains(text.Format("Prompt.Confidence", "High"), html);
        Assert.Contains("Refresh <hypothesis>", html);
        Assert.Contains("New RCA <ObservedFact>", html);
        Assert.Contains("ObservedFact", html);
        Assert.Contains("Completed", html);
        Assert.Contains(source.Title, html);
        Assert.Contains($"/api/agent-analysis/jobs/{job.Id:D}/attachment", html);
        Assert.Contains(job.CreatedAt.ToLocalTime().ToString("g"), html);
        Assert.DoesNotContain("<hypothesis>", await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        await InvokeAsync("CopyHandoffAsync");
        var handoff = result.DetailedAnalysis.OperatorHandoff;
        Assert.Equal(string.Join(Environment.NewLine,
            $"Decision: {handoff.CurrentDecision}",
            $"Unresolved risk: {handoff.UnresolvedRisk}",
            $"Missing evidence: {handoff.MissingEvidence}",
            $"Audience: {handoff.Audience}"), clipboard.Last());
        Assert.Contains(text.Format("Prompt.ReviewRecorded", AgentAnalysisReviewActions.HandoffCopied, "1"), await HtmlAsync());
        type.GetField("reviewComment", flags)!.SetValue(page, "Refresh <original review>");
        await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Commented, null);
        Assert.Contains("Refresh <original review>", await HtmlAsync());
        var reviews = await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id);
        Assert.Equal(2, reviews.Count);
        Assert.Contains(reviews, review => review.Action == AgentAnalysisReviewActions.Commented &&
            review.Comment == "Refresh <original review>");
        var previousHistory = Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents");
        foreach (var (action, comment, code) in new[]
        {
            ("custom <action>", "Retain <draft>", AgentAnalysisReviewValidationCode.ActionInvalid),
            (AgentAnalysisReviewActions.Commented, new string('c', 4001), AgentAnalysisReviewValidationCode.CommentTooLong),
            (AgentAnalysisReviewActions.Rejected, " ", AgentAnalysisReviewValidationCode.CommentRequired)
        })
        {
            type.GetField("reviewComment", flags)!.SetValue(page, comment);
            await InvokeAsync("SubmitReviewAsync", action, null);
            Assert.Equal(text[$"Prompt.Review.{code}"], Get<string>("reviewError"));
            Assert.Equal(comment, Get<string>("reviewComment"));
            Assert.Same(previousHistory, Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents"));
            Assert.False(Get<bool>("isSavingReview"));
            Assert.False(Get<bool>("reviewReloadRequired"));
            Assert.Equal(2, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
        }
        foreach (var code in Enum.GetValues<AgentAnalysisReviewValidationCode>())
        {
            var projected = type.GetMethod("ReviewError", flags)!.Invoke(page,
                [new AgentAnalysisReviewValidationException(code, "SECRET <diagnostic>")]);
            Assert.Equal(text[$"Prompt.Review.{code}"], projected);
        }
        foreach (var failure in new Exception[]
        {
            new AgentAnalysisReviewValidationException((AgentAnalysisReviewValidationCode)999, "SECRET <diagnostic>"),
            new InvalidOperationException("Comment and rejection actions require a review comment."),
            new System.Text.Json.JsonException("SECRET <diagnostic>"),
            new InvalidOperationException("Wrapper", new AgentAnalysisReviewValidationException(
                AgentAnalysisReviewValidationCode.CommentRequired, "SECRET <diagnostic>"))
        })
            Assert.Equal(text["Prompt.Review.SaveFailed"], type.GetMethod("ReviewError", flags)!.Invoke(page, [failure]));

        type.GetField("reviewComment", flags)!.SetValue(page, "Retain <draft>");
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <database>");
        await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Commented, null);
        Assert.Equal(text["Prompt.Review.SaveFailed"], Get<string>("reviewError"));
        Assert.True(Get<bool>("reviewReloadRequired"));
        Assert.Equal("Retain <draft>", Get<string>("reviewComment"));
        Assert.Same(previousHistory, Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents"));
        await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Commented, null);
        Assert.Equal(2, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
        var previousResult = Get<AgentAnalysisResult>("activeResult");
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <refresh>");
        await InvokeAsync("RefreshActiveJobAsync");
        Assert.Equal(text["Prompt.Review.HistoryLoadFailed"], Get<string>("jobError"));
        Assert.True(Get<bool>("reviewReloadRequired"));
        Assert.Same(previousResult, Get<AgentAnalysisResult>("activeResult"));
        Assert.Same(previousHistory, Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents"));
        Assert.Equal("Retain <draft>", Get<string>("reviewComment"));
        Assert.False(Get<bool>("isRefreshingJob"));
        await InvokeAsync("RefreshActiveJobAsync");
        Assert.False(Get<bool>("reviewReloadRequired"));

        var attempt = 0;
        void FailHistory()
        {
            if (++attempt == 3) throw new IOException("SECRET <history>");
            fixture.Factory.BeforeNextContext = FailHistory;
        }
        fixture.Factory.BeforeNextContext = FailHistory;
        previousHistory = Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents");
        await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Commented, null);
        Assert.Equal(text["Prompt.Review.ReloadFailed"], Get<string>("reviewError"));
        Assert.Equal(text.Format("Prompt.ReviewRecorded", AgentAnalysisReviewActions.Commented, "1"), Get<string>("reviewStatus"));
        Assert.Equal(string.Empty, Get<string>("reviewComment"));
        Assert.True(Get<bool>("reviewReloadRequired"));
        Assert.Same(previousHistory, Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents"));
        Assert.Equal(3, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
        await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Commented, "Duplicate");
        Assert.Equal(3, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
        await InvokeAsync("RefreshActiveJobAsync");
        Assert.Equal(3, Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents").Count);

        var js = fixture.Services.GetRequiredService<RecordingJavaScript>();
        js.Failure = new Microsoft.JSInterop.JSException("SECRET <clipboard>");
        var copies = clipboard.Count;
        await InvokeAsync("CopyHandoffAsync");
        Assert.Equal(text["Prompt.Review.ClipboardFailed"], Get<string>("reviewError"));
        Assert.Equal(copies, clipboard.Count);
        Assert.False(Get<bool>("isSavingReview"));
        Assert.Equal(3, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
        js.Failure = null;
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <audit>");
        await InvokeAsync("CopyHandoffAsync");
        Assert.Equal(text["Prompt.Review.CopyAuditFailed"], Get<string>("reviewError"));
        Assert.Equal(copies + 1, clipboard.Count);
        Assert.DoesNotContain("SECRET", await HtmlAsync());
        var reviewLogs = fixture.Services.GetRequiredService<PromptPageLogger>().Entries;
        Assert.Contains(reviewLogs, entry => entry.Level == LogLevel.Warning &&
            entry.Exception is AgentAnalysisReviewValidationException);
        Assert.Contains(reviewLogs, entry => entry.Level == LogLevel.Error &&
            entry.Exception is IOException);
        Assert.Contains(reviewLogs, entry => entry.Level == LogLevel.Error &&
            entry.Exception is Microsoft.JSInterop.JSException);
        await InvokeAsync("RefreshActiveJobAsync");
        type.GetField("reviewComment", flags)!.SetValue(page, "Explicit retry");
        await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Commented, null);
        Assert.Equal(4, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
        type.GetField("isSavingReview", flags)!.SetValue(page, true);
        await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Accepted, null);
        await InvokeAsync("CopyHandoffAsync");
        Assert.Equal(copies + 1, clipboard.Count);
        Assert.Equal(4, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
        type.GetField("isSavingReview", flags)!.SetValue(page, false);
        foreach (var (json, code) in new[]
        {
            ("null", AgentAnalysisResultCode.NullResponse),
            ("{", AgentAnalysisResultCode.SchemaMismatch),
            ("{}", AgentAnalysisResultCode.SchemaMismatch),
            (storedResult!.Replace("\"contractVersion\":", "\"customSecret\":", StringComparison.Ordinal),
                AgentAnalysisResultCode.SchemaMismatch)
        })
        {
            var savedView = Get<AgentAnalysisResult>("activeResult");
            var savedReviews = Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents");
            type.GetField("reviewComment", flags)!.SetValue(page, "Keep <result draft>");
            await SetResultJsonAsync(json);
            await InvokeAsync("RefreshActiveJobAsync");
            Assert.Equal(text[$"Prompt.Result.{code}"], Get<string>("jobError"));
            Assert.Same(savedView, Get<AgentAnalysisResult>("activeResult"));
            Assert.Same(savedReviews, Get<IReadOnlyList<AgentAnalysisReviewEvent>>("reviewEvents"));
            Assert.Equal("Keep <result draft>", Get<string>("reviewComment"));
            Assert.True(Get<bool>("reviewReloadRequired"));
            Assert.False(Get<bool>("isRefreshingJob"));
            Assert.DoesNotContain("customSecret", await HtmlAsync());
            await InvokeAsync("SubmitReviewAsync", AgentAnalysisReviewActions.Accepted, null);
            Assert.Equal(4, (await fixture.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(job.Id)).Count);
            Assert.Equal(json, (await store.GetAsync(job.Id))!.ResultJson);
            await SetResultJsonAsync(storedResult);
            await InvokeAsync("RefreshActiveJobAsync");
            Assert.False(Get<bool>("reviewReloadRequired"));
        }
        foreach (var code in Enum.GetValues<AgentAnalysisResultCode>())
            Assert.Equal(text[$"Prompt.Result.{code}"], type.GetMethod("ResultError", flags)!.Invoke(page,
                [new AgentAnalysisResultException(code, "SECRET <result>")]));
        foreach (var failure in new Exception[]
        {
            new AgentAnalysisResultException((AgentAnalysisResultCode)999, "SECRET <result>"),
            new InvalidOperationException("Agent analysis response contains JSON null."),
            new System.Text.Json.JsonException("SECRET <result>"),
            new IOException("Wrapper", new AgentAnalysisResultException(AgentAnalysisResultCode.SchemaMismatch, "SECRET"))
        })
            Assert.Equal(text["Prompt.Review.HistoryLoadFailed"], type.GetMethod("ResultError", flags)!.Invoke(page, [failure]));
        Assert.Contains(reviewLogs, entry => entry.Level == LogLevel.Error && entry.Exception is AgentAnalysisResultException);
        async Task SetResultJsonAsync(string json)
        {
            await using var context = fixture.Factory.CreateDbContext();
            var saved = await context.AgentAnalysisJobs.SingleAsync(item => item.Id == job.Id);
            saved.ResultJson = json;
            await context.SaveChangesAsync();
        }
        type.GetField("analysisScopeType", flags)!.SetValue(page, "Unsupported <scope>");
        await InvokeAsync("GeneratePromptAsync");
        Assert.Equal(text["Prompt.Job.ScopeUnsupported"], Get<string>("generationError"));
        Assert.DoesNotContain("Unsupported <scope>", Get<string>("generationError"));
        type.GetField("analysisScopeType", flags)!.SetValue(page, "Target");
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <generator>");
        await InvokeAsync("GeneratePromptAsync");
        Assert.Equal(text["Prompt.GenerationFailed"], Get<string>("generationError"));
        Assert.DoesNotContain("SECRET", await HtmlAsync());
        await InvokeAsync("GeneratePromptAsync");
        var unchanged = (await store.GetAsync(job.Id))!;
        Assert.Equal(storedSnapshot, unchanged.SnapshotJson);
        Assert.Equal(storedResult, unchanged.ResultJson);
        Assert.Equal(result.ToCanonicalJson(), Get<AgentAnalysisResult>("activeResult").ToCanonicalJson());
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);

        T Get<T>(string field) => Assert.IsAssignableFrom<T>(type.GetField(field, flags)!.GetValue(page));
        async Task<string> HtmlAsync() =>
            WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        async Task InvokeAsync(string method, params object?[] arguments)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await Assert.IsAssignableFrom<Task>(type.GetMethod(method, flags)!.Invoke(page, arguments));
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, null);
            });
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SnapshotValidationReadsAndLateFailuresKeepUiAndJobBoundaries(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var fixture = await Fixture.CreateAsync(admin: true, promptPage: true);
        var now = DateTimeOffset.UtcNow;
        ((FixedTime)fixture.Services.GetRequiredService<TimeProvider>()).Now = now;
        await fixture.AddAlertAsync("snapshot", "Target", now.AddMinutes(-1));
        AlertRecord savedAlert;
        await using (var context = fixture.Factory.CreateDbContext())
            savedAlert = await context.Alerts.AsNoTracking().SingleAsync();
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(Prompt), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return rendered;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().PromptPage!;
        var type = typeof(Prompt);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var text = fixture.Services.GetRequiredService<UiText>();
        var service = fixture.Services.GetRequiredService<IncidentAnalysisSnapshotService>();
        Task<IncidentAnalysisSnapshot> Build(IReadOnlyCollection<Guid> ids, IncidentAnalysisSnapshotOptions? options = null) =>
            service.BuildAsync(ids, new(IncidentAnalysisScopeType.Incident), AlertGraphLayer.Subscription,
                AlertGraphLayer.ResourceGroup, AlertGraphLayer.Target, now, options);
        fixture.Factory.BeforeNextContext = () => throw new IOException("Preflight must not read.");
        var empty = await Assert.ThrowsAsync<IncidentAnalysisSnapshotArgumentException>(() => Build([]));
        Assert.Equal(IncidentAnalysisSnapshotCode.PrimaryIdsRequired, empty.Code);
        Assert.Equal("primaryEventIds", empty.ParamName);
        Assert.Equal(new ArgumentException("At least one primary event ID is required.", "primaryEventIds").Message, empty.Message);
        Assert.NotNull(fixture.Factory.BeforeNextContext);
        var range = await Assert.ThrowsAsync<IncidentAnalysisSnapshotRangeException>(() =>
            Build([savedAlert.Id], new(MaxAlerts: 0)));
        Assert.Equal(IncidentAnalysisSnapshotCode.AlertLimitInvalid, range.Code);
        Assert.NotNull(fixture.Factory.BeforeNextContext);
        fixture.Factory.BeforeNextContext = null;
        foreach (var code in Enum.GetValues<IncidentAnalysisSnapshotCode>())
        {
            Exception failure = code switch
            {
                IncidentAnalysisSnapshotCode.SystemHistoryDaysInvalid or IncidentAnalysisSnapshotCode.ContextHoursInvalid or
                    IncidentAnalysisSnapshotCode.AlertLimitInvalid or IncidentAnalysisSnapshotCode.CharacterLimitInvalid =>
                    new IncidentAnalysisSnapshotRangeException(code, "SECRET <field>"),
                IncidentAnalysisSnapshotCode.CorrelationLayersInvalid or IncidentAnalysisSnapshotCode.PrimaryIdsRequired =>
                    new IncidentAnalysisSnapshotArgumentException(code, "SECRET <argument>", "field"),
                _ => new IncidentAnalysisSnapshotException(code, "SECRET <snapshot>")
            };
            Assert.Equal(text[$"Prompt.Snapshot.{code}"],
                type.GetMethod("SnapshotError", flags)!.Invoke(page, [failure, "Prompt.GenerationFailed"]));
            Assert.Equal(text[$"Prompt.Snapshot.{code}"], type.GetMethod("JobStartError", flags)!.Invoke(page, [failure]));
        }
        foreach (var failure in new Exception[]
        {
            new IncidentAnalysisSnapshotRangeException((IncidentAnalysisSnapshotCode)999, "SECRET"),
            new IncidentAnalysisSnapshotArgumentException((IncidentAnalysisSnapshotCode)999, "SECRET", "field"),
            new IncidentAnalysisSnapshotException((IncidentAnalysisSnapshotCode)999, "SECRET"),
            new ArgumentOutOfRangeException("MaxAlerts"),
            new InvalidOperationException("At least one primary alert is required."),
            new System.Text.Json.JsonException("SECRET"),
            new IOException("Wrapper", new IncidentAnalysisSnapshotException(IncidentAnalysisSnapshotCode.PrimaryEventsMissing, "SECRET"))
        })
        {
            Assert.Equal(text["Prompt.GenerationFailed"],
                type.GetMethod("SnapshotError", flags)!.Invoke(page, [failure, "Prompt.GenerationFailed"]));
            Assert.Equal(text["Prompt.Job.StartFailed"], type.GetMethod("JobStartError", flags)!.Invoke(page, [failure]));
        }
        var fields = fixture.Services.GetRequiredService<AgentAnalysisContextOptions>().InventoryCorrelationLayers;
        var originalField = fields[0];
        fields[0] = AlertGraphLayer.Subscription;
        await Invoke("GeneratePromptAsync");
        Assert.Equal(text["Prompt.Snapshot.CorrelationLayersInvalid"], Get<string>("generationError"));
        await Invoke("CreateJobAsync");
        Assert.Equal(text["Prompt.Snapshot.CorrelationLayersInvalid"], Get<string>("jobError"));
        Assert.False(Get<bool>("jobReloadRequired"));
        fields[0] = originalField;
        await Invoke("GeneratePromptAsync");
        Assert.Null(Value("generationError"));
        await using (var context = fixture.Factory.CreateDbContext())
            await context.Alerts.Where(alert => alert.Id == savedAlert.Id).ExecuteDeleteAsync();
        var missing = await Assert.ThrowsAsync<IncidentAnalysisSnapshotException>(() => Build([savedAlert.Id]));
        Assert.Equal(IncidentAnalysisSnapshotCode.PrimaryEventsMissing, missing.Code);
        Assert.Equal($"Primary alert events were not found: {savedAlert.Id}", missing.Message);
        await Invoke("GeneratePromptAsync");
        Assert.Equal(text["Prompt.Snapshot.PrimaryEventsMissing"], Get<string>("generationError"));
        Assert.Contains(savedAlert.Id, Get<HashSet<Guid>>("selectedAlertIds"));
        await Invoke("CreateJobAsync");
        Assert.Equal(text["Prompt.Snapshot.PrimaryEventsMissing"], Get<string>("jobError"));
        Assert.False(Get<bool>("jobReloadRequired"));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.Alerts.Add(savedAlert);
            await context.SaveChangesAsync();
        }
        await Invoke("GeneratePromptAsync");
        Assert.Null(Value("generationError"));
        for (var slot = 1; slot <= 4; slot++)
        {
            var failure = new IOException($"SECRET <read {slot}>");
            var count = 0;
            void FailRead()
            {
                if (++count == slot) throw failure;
                fixture.Factory.BeforeNextContext = FailRead;
            }
            fixture.Factory.BeforeNextContext = FailRead;
            await Invoke("GeneratePromptAsync");
            Assert.Equal(slot, count);
            Assert.Equal(text["Prompt.GenerationFailed"], Get<string>("generationError"));
            Assert.Null(Value("previewSnapshot"));
            Assert.Equal(string.Empty, Get<string>("generatedPrompt"));
            Assert.False(Get<bool>("isGenerating"));
            Assert.Contains(savedAlert.Id, Get<HashSet<Guid>>("selectedAlertIds"));
            Assert.Contains(fixture.Services.GetRequiredService<PromptPageLogger>().Entries,
                entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
            Assert.DoesNotContain("SECRET", WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString)));
            await Invoke("GeneratePromptAsync");
            Assert.Null(Value("generationError"));
        }
        var cachedAlerts = Get<IReadOnlyList<AlertRecord>>("alerts");
        var cachedPrompt = Get<string>("generatedPrompt");
        var cachedSnapshot = Get<IncidentAnalysisSnapshot>("previewSnapshot");
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <alert reload>");
        await Invoke("ReloadAlertsAsync", false);
        Assert.Equal(text["Prompt.AlertLoadFailed"], Get<string>("alertLoadError"));
        Assert.Same(cachedAlerts, Get<IReadOnlyList<AlertRecord>>("alerts"));
        Assert.Same(cachedSnapshot, Get<IncidentAnalysisSnapshot>("previewSnapshot"));
        Assert.Equal(cachedPrompt, Get<string>("generatedPrompt"));
        Assert.Contains(savedAlert.Id, Get<HashSet<Guid>>("selectedAlertIds"));
        await Invoke("CreateJobAsync");
        Assert.Equal(text["Prompt.AlertLoadFailed"], Get<string>("jobError"));
        var copies = fixture.Services.GetRequiredService<RecordingJavaScript>().Clipboard.Count;
        await Invoke("CopyPromptAsync");
        Assert.Equal(copies, fixture.Services.GetRequiredService<RecordingJavaScript>().Clipboard.Count);
        await Invoke("ReloadAlertsAsync", false);
        Assert.Null(Value("alertLoadError"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.BeforeNextAsyncContext = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var older = renderer.Dispatcher.InvokeAsync(() =>
            Assert.IsAssignableFrom<Task>(type.GetMethod("GeneratePromptAsync", flags)!.Invoke(page, null)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Invoke("GeneratePromptAsync");
        var currentPrompt = Get<string>("generatedPrompt");
        var currentSnapshot = Get<IncidentAnalysisSnapshot>("previewSnapshot");
        release.SetException(new IOException("SECRET <late old generation>"));
        await older;
        Assert.Equal(currentPrompt, Get<string>("generatedPrompt"));
        Assert.Same(currentSnapshot, Get<IncidentAnalysisSnapshot>("previewSnapshot"));
        Assert.Null(Value("generationError"));
        Assert.False(Get<bool>("isGenerating"));
        Assert.Equal(fixture.Services.GetRequiredService<AgentAnalysisOrchestrator>().Prepare(currentSnapshot).Prompt, currentPrompt);
        Assert.Empty(await fixture.Services.GetRequiredService<AgentAnalysisJobStore>().ListAsync("owner", true));
        Assert.Contains(fixture.Services.GetRequiredService<PromptPageLogger>().Entries,
            entry => entry.Level == LogLevel.Warning && entry.Exception is IncidentAnalysisSnapshotArgumentException);
        var sourceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.BeforeNextAsyncContext = _ =>
        {
            sourceEntered.TrySetResult();
            return sourceRelease.Task;
        };
        var oldReload = renderer.Dispatcher.InvokeAsync(() =>
            Assert.IsAssignableFrom<Task>(type.GetMethod("ReloadAlertsAsync", flags)!.Invoke(page, [false])));
        await sourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Invoke("ReloadAlertsAsync", false);
        var freshAlerts = Get<IReadOnlyList<AlertRecord>>("alerts");
        sourceRelease.SetException(new IOException("SECRET <late old reload>"));
        await oldReload;
        Assert.Same(freshAlerts, Get<IReadOnlyList<AlertRecord>>("alerts"));
        Assert.Null(Value("alertLoadError"));
        object? Value(string field) => type.GetField(field, flags)!.GetValue(page);
        T Get<T>(string field) => Assert.IsAssignableFrom<T>(Value(field));
        Task Invoke(string method, params object?[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            await Assert.IsAssignableFrom<Task>(type.GetMethod(method, flags)!.Invoke(page, arguments));
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(page, null);
        });
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task SnapshotInitialAlertReadFailureIsNotNormalEmptyState(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var fixture = await Fixture.CreateAsync(admin: true, promptPage: true);
        await fixture.AddAlertAsync("source", "Target", DateTimeOffset.UtcNow.AddMinutes(-1));
        fixture.Factory.BeforeNextContext = () => throw new IOException("SECRET <initial alert read>");
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(Prompt), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return rendered;
        });
        var text = fixture.Services.GetRequiredService<UiText>();
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains(text["Prompt.AlertLoadFailed"], html);
        Assert.DoesNotContain(text["Prompt.NoAlerts"], html);
        Assert.DoesNotContain("SECRET", html);
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().PromptPage!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(async () =>
            await Assert.IsAssignableFrom<Task>(typeof(Prompt).GetMethod("ReloadAlertsAsync", flags)!.Invoke(page, [true])));
        Assert.Null(typeof(Prompt).GetField("alertLoadError", flags)!.GetValue(page));
        Assert.NotNull(typeof(Prompt).GetField("previewSnapshot", flags)!.GetValue(page));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task WorkspaceUsesDisabledCodeWithoutChangingExecutionOrGeneratingPrompt(string culture)
    {
        using var cultures = new PageCultures(culture);
        var state = AgentAnalysisOrchestratorTests.CreateDisabledState(9);
        await using var fixture = await Fixture.CreateAsync(admin: true, promptPage: true, executionState: state);
        await fixture.AddAlertAsync("disabled", "Target", DateTimeOffset.UtcNow);
        await using var renderer = new StaticPageRenderer(fixture.Services, fixture.Services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(Prompt), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return rendered;
        });
        var page = fixture.Services.GetRequiredService<CapturingRcaActivator>().PromptPage!;
        var text = fixture.Services.GetRequiredService<UiText>();
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains(text["AgentAnalysis.Disabled.PreparationOnly"], System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Hidden:Endpoint", html);
        Assert.False(state.IsEnabled);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(async () =>
            await Assert.IsAssignableFrom<Task>(typeof(Prompt).GetMethod("CreateJobAsync", flags)!.Invoke(page, null)));
        Assert.Equal(text["AgentAnalysis.Disabled.PreparationOnly"], typeof(Prompt).GetField("jobError", flags)!.GetValue(page));
        Assert.Equal(string.Empty, typeof(Prompt).GetField("generatedPrompt", flags)!.GetValue(page));
        Assert.Empty(await fixture.Services.GetRequiredService<AgentAnalysisJobStore>().ListAsync("owner", true));
        Assert.Same(state, fixture.Services.GetRequiredService<AgentAnalysisOrchestrator>().ExecutionState);
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, Factory factory) : IAsyncDisposable
    {
        public Factory Factory => factory;
        public ServiceProvider Services => services;
        public RcaService Service => services.GetRequiredService<RcaService>();

        public static async Task<Fixture> CreateAsync(bool admin = false, bool inboxQueries = false, bool promptPage = false,
            AgentAnalysisExecutionState? executionState = null,
            params IInterceptor[] interceptors)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection);
            options.AddInterceptors(interceptors);
            if (inboxQueries)
                options.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer,
                    InboxWorkspaceLocalizationTests.InboxTimestampModelCustomizer>();
            if (promptPage)
                options.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer, PromptTimestampModelCustomizer>();
            var factory = new Factory(options.Options);
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
            collection.AddUiLocalization();
            collection.AddCascadingAuthenticationState();
            var services = collection
                .AddSingleton<RcaPageLogger>()
                .AddSingleton<ILogger<Rca>>(services => services.GetRequiredService<RcaPageLogger>())
                .AddSingleton<AuthenticationStateProvider>(new ReaderAuthenticationState(admin ? "Admin" : "Reader"))
                .AddSingleton<CapturingRcaActivator>()
                .AddSingleton<IComponentActivator>(services => services.GetRequiredService<CapturingRcaActivator>())
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
                .AddSingleton(new AlertGraphOptions())
                .AddSingleton(new AgentAnalysisContextOptions())
                .AddSingleton(new AgentAnalysisAccessOptions())
                .AddSingleton(executionState ?? AgentAnalysisExecutionState.Create(new AgentAnalysisExecutionOptions()))
                .AddSingleton(MicrosoftSourceCatalog.CreateDefault())
                .AddSingleton<IncidentAnalysisSnapshotBuilder>()
                .AddSingleton<IncidentAnalysisSnapshotService>()
                .AddSingleton<AgentAnalysisOrchestrator>()
                .AddSingleton<AgentAnalysisScopePolicy>()
                .AddSingleton<AgentAnalysisJobStore>()
                .AddSingleton<AgentAnalysisReviewStore>()
                .AddSingleton<AgentAnalysisJobService>()
                .AddSingleton<PromptPageLogger>()
                .AddSingleton<ILogger<Prompt>>(services => services.GetRequiredService<PromptPageLogger>())
                .AddSingleton<InboxDashboardState>()
                .AddSingleton<InboxLayoutRepository>()
                .AddSingleton<RecordingJavaScript>()
                .AddSingleton<Microsoft.JSInterop.IJSRuntime>(services => promptPage
                    ? services.GetRequiredService<RecordingJavaScript>() : new UnusedJavaScript())
                .AddSingleton<RcaReconciliationService>()
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
        public Action? BeforeNextContext { get; set; }
        public Func<CancellationToken, Task>? BeforeNextAsyncContext { get; set; }
        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var before = BeforeNextAsyncContext;
            BeforeNextAsyncContext = null;
            if (before is not null) await before(cancellationToken);
            return CreateDbContext();
        }
        public AlertDbContext CreateDbContext()
        {
            var before = BeforeNextContext;
            BeforeNextContext = null;
            before?.Invoke();
            return new(options);
        }
    }

    private sealed class RcaPageLogger : ILogger<Rca>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }

    private sealed class PromptPageLogger : ILogger<Prompt>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }

    private sealed class RcaWriteInterceptor : SaveChangesInterceptor
    {
        private readonly HashSet<DbContext> pending = [];
        public int Attempts { get; private set; }
        public Exception? Failure { get; set; }
        public Action? AfterWrite { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && context.ChangeTracker.Entries<RootCauseAnalysis>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                Attempts++;
                if (Failure is { } failure) throw failure;
                pending.Add(context);
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && pending.Remove(context)) AfterWrite?.Invoke();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AgentJobWriteInterceptor : SaveChangesInterceptor
    {
        private readonly HashSet<DbContext> pending = [];
        public int Added { get; private set; }
        public Action? AfterWrite { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && context.ChangeTracker.Entries<AgentAnalysisJob>()
                .Any(entry => entry.State == EntityState.Added))
                pending.Add(context);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && pending.Remove(context))
            {
                Added++;
                AfterWrite?.Invoke();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RcaCommitInterceptor : DbTransactionInterceptor
    {
        public Exception? Failure { get; set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Failure is { } failure) throw failure;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WaitingFactory(bool failFirst) : IDbContextFactory<AlertDbContext>
    {
        private readonly TaskCompletionSource<AlertDbContext> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AlertDbContext CreateDbContext() =>
            throw new InvalidOperationException("The worker must create contexts asynchronously.");

        public async Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref attempts) == 1 && failFirst)
                throw new InvalidOperationException("Database temporarily unavailable.");
            Token = cancellationToken;
            Started.TrySetResult();
            return await pending.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class WorkerLogger : ILogger<RcaWorker>
    {
        private int errorCount;
        public int ErrorCount => Volatile.Read(ref errorCount);
        public TaskCompletionSource FirstError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Interlocked.Increment(ref errorCount);
                FirstError.TrySetResult();
            }
        }
    }

    private sealed class ReaderAuthenticationState(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(User(role)));
    }

    private sealed class CapturingRcaActivator : IComponentActivator
    {
        public Rca? Page { get; private set; }
        public Prompt? PromptPage { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is Rca page)
                Page = page;
            if (component is Prompt prompt)
                PromptPage = prompt;
            return component;
        }
    }

    private sealed class PageCultures : IDisposable
    {
        private readonly CultureInfo ui = CultureInfo.CurrentUICulture;
        private readonly CultureInfo format = CultureInfo.CurrentCulture;

        public PageCultures(string culture)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        }

        public void Dispose()
        {
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.CurrentCulture = format;
        }
    }

    public sealed class PromptTimestampModelCustomizer(
        Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizerDependencies dependencies)
        : Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AlertRecord>().Property(alert => alert.ReceivedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<AgentAnalysisJob>().Property(job => job.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<AgentAnalysisJob>().Property(job => job.ExpiresAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<AgentAnalysisReviewEvent>().Property(review => review.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task PromptClipboardFailuresKeepPreparedEvidenceAndNeverClaimSuccess(string culture, bool afterCopy)
    {
        using var cultures = new PageCultures(culture);
        await using var page = await ClipboardPageFixture.CreateAsync();
        var prompt = page.Get<string>("generatedPrompt");
        var snapshot = page.Get<IncidentAnalysisSnapshot>("previewSnapshot");
        foreach (var failure in new Exception[]
        {
            new Microsoft.JSInterop.JSException("PRIVATE_CLIPBOARD_DETAIL"),
            new Microsoft.JSInterop.JSDisconnectedException("PRIVATE_CLIPBOARD_DETAIL"),
            new TaskCanceledException("PRIVATE_CLIPBOARD_DETAIL"),
            new InvalidOperationException("PRIVATE_CLIPBOARD_DETAIL")
        })
        {
            page.Javascript.Failure = failure;
            page.Javascript.FailAfterCopy = afterCopy;
            var count = page.Javascript.Clipboard.Count;
            await page.Invoke("CopyPromptAsync");
            Assert.Equal(page.Text["Prompt.CopyFailed"], page.Get<string>("copyError"));
            Assert.Equal(string.Empty, page.Get<string>("copyStatus"));
            Assert.False(page.Get<bool>("isCopyingClipboard"));
            Assert.Equal(count + (afterCopy ? 1 : 0), page.Javascript.Clipboard.Count);
            Assert.Equal(prompt, page.Get<string>("generatedPrompt"));
            Assert.Same(snapshot, page.Get<IncidentAnalysisSnapshot>("previewSnapshot"));
            Assert.Contains(page.Log.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
            Assert.DoesNotContain("PRIVATE_CLIPBOARD_DETAIL", page.Html);
            Assert.Empty(await page.JobStore.ListAsync("owner", true));
        }
        page.Javascript.Failure = null;
        await page.Invoke("CopyPromptAsync");
        Assert.Equal(prompt, page.Javascript.Clipboard.Last());
        Assert.Equal(page.Text["Prompt.Copied"], page.Get<string>("copyStatus"));
        Assert.Null(page.Get<string?>("copyError"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PromptCopyValidatesActualSelectionAndGenerationFailuresBeforeCallingBrowser(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var page = await ClipboardPageFixture.CreateAsync();
        await page.Invoke("ClearSelection");
        await page.Invoke("CopyPromptAsync");
        Assert.Equal(page.Text["Prompt.CopyUnavailable"], page.Get<string>("copyError"));
        Assert.Equal(0, page.Javascript.Attempts);
        await page.Invoke("SelectAll");
        page.Storage.Factory.BeforeNextContext = () => throw new IOException("PRIVATE_SNAPSHOT_DETAIL");
        await page.Invoke("GeneratePromptAsync");
        await page.Invoke("CopyPromptAsync");
        Assert.Equal(page.Text["Prompt.CopyUnavailable"], page.Get<string>("copyError"));
        Assert.Equal(0, page.Javascript.Attempts);
        await page.Invoke("GeneratePromptAsync");
        page.Storage.Factory.BeforeNextContext = () => throw new IOException("PRIVATE_SOURCE_DETAIL");
        await page.Invoke("ReloadAlertsAsync", false);
        await page.Invoke("CopyPromptAsync");
        Assert.Equal(page.Text["Prompt.AlertLoadFailed"], page.Get<string>("copyError"));
        Assert.Equal(0, page.Javascript.Attempts);
        await page.Invoke("ReloadAlertsAsync", false);
        var prompt = page.Get<string>("generatedPrompt");
        await page.Set("generatedPrompt", " ");
        await page.Invoke("CopyPromptAsync");
        Assert.Equal(page.Text["Prompt.CopyUnavailable"], page.Get<string>("copyError"));
        Assert.Equal(0, page.Javascript.Attempts);
        await page.Set("generatedPrompt", prompt);
        await page.Invoke("CopyPromptAsync");
        Assert.Equal(prompt, Assert.Single(page.Javascript.Clipboard));
        Assert.DoesNotContain("PRIVATE_", page.Html);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PendingPromptCopyBlocksOverlapsAndReportsTheCapturedOlderPromptAfterSelectionChanges(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var page = await ClipboardPageFixture.CreateAsync();
        var original = page.Get<string>("generatedPrompt");
        page.Javascript.BlockNext = true;
        var pending = page.Invoke("CopyPromptAsync");
        await page.Javascript.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await page.Invoke("CopyPromptAsync");
            await page.Invoke("CopyHandoffAsync");
            await page.Invoke("CreateJobAsync");
            Assert.Equal(1, page.Javascript.Attempts);
            Assert.Empty(await page.JobStore.ListAsync("owner", true));
            Assert.Contains(page.Text["Prompt.Copying"], WebUtility.HtmlDecode(page.Html));
            await page.Invoke("ToggleSystemHistory", new ChangeEventArgs { Value = false });
            Assert.NotEqual(original, page.Get<string>("generatedPrompt"));
        }
        finally
        {
            page.Javascript.Release.SetResult();
            await pending;
        }
        Assert.Equal(original, Assert.Single(page.Javascript.Clipboard));
        Assert.Equal(page.Text["Prompt.CopiedPrevious"], page.Get<string>("copyStatus"));
        await page.Invoke("CopyPromptAsync");
        Assert.Equal(page.Get<string>("generatedPrompt"), page.Javascript.Clipboard.Last());
        Assert.Equal(page.Text["Prompt.Copied"], page.Get<string>("copyStatus"));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task DisposingPromptCancelsClipboardAndPreventsLateSuccessAndHandoffReview(string culture, bool handoff)
    {
        using var cultures = new PageCultures(culture);
        foreach (var late in new[] { false, true })
        {
            await using var page = await ClipboardPageFixture.CreateAsync(handoff);
            var prompt = page.Get<string>("generatedPrompt");
            page.Javascript.BlockNext = true;
            page.Javascript.IgnoreCancellation = late;
            var pending = page.Invoke(handoff ? "CopyHandoffAsync" : "CopyPromptAsync");
            await page.Javascript.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.DisposePage();
            if (late) page.Javascript.Release.SetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(page.Get<bool>("isCopyingClipboard"));
            Assert.Equal(prompt, page.Get<string>("generatedPrompt"));
            Assert.Null(page.Get<string?>("copyError"));
            Assert.Null(page.Get<string?>("reviewError"));
            Assert.DoesNotContain(page.Text["Prompt.Copied"], page.Get<string>("copyStatus"));
            Assert.Equal(late ? 1 : 0, page.Javascript.Clipboard.Count);
            Assert.Empty(page.Log.Entries);
            if (handoff)
                Assert.Empty(await page.Reviews());
            var attempts = page.Javascript.Attempts;
            await page.Invoke("CopyPromptAsync");
            await page.Invoke("CopyHandoffAsync");
            Assert.Equal(attempts, page.Javascript.Attempts);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task HandoffClipboardFailuresAndChangedResultNeverCreateCopyReview(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var page = await ClipboardPageFixture.CreateAsync(handoff: true);
        var result = page.Get<AgentAnalysisResult>("activeResult");
        foreach (var failure in new Exception[]
        {
            new Microsoft.JSInterop.JSException("PRIVATE_CLIPBOARD_DETAIL"),
            new Microsoft.JSInterop.JSDisconnectedException("PRIVATE_CLIPBOARD_DETAIL"),
            new TaskCanceledException("PRIVATE_CLIPBOARD_DETAIL"),
            new InvalidOperationException("PRIVATE_CLIPBOARD_DETAIL")
        })
        {
            page.Javascript.Failure = failure;
            page.Javascript.FailAfterCopy = true;
            await page.Invoke("CopyHandoffAsync");
            Assert.Equal(page.Text["Prompt.Review.ClipboardFailed"], page.Get<string>("reviewError"));
            Assert.Empty(await page.Reviews());
            Assert.False(page.Get<bool>("isSavingReview"));
            Assert.Contains(page.Log.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
        }
        page.Javascript.Failure = null;
        page.Javascript.BlockNext = true;
        var pending = page.Invoke("CopyHandoffAsync");
        await page.Javascript.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await page.Set("activeResult", result with { ResultVersion = result.ResultVersion + 1 });
        }
        finally
        {
            page.Javascript.Release.SetResult();
            await pending;
        }
        Assert.Equal(page.Text["Prompt.Review.CopySelectionChanged"], page.Get<string>("reviewError"));
        Assert.Empty(await page.Reviews());
        Assert.DoesNotContain("PRIVATE_CLIPBOARD_DETAIL", page.Html);
        await page.Set("activeResult", result);
        await page.Invoke("CopyHandoffAsync");
        Assert.Single(await page.Reviews(), review => review.Action == AgentAnalysisReviewActions.HandoffCopied);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PendingHandoffCopyBlocksOtherCopiesAndWritesThenRecordsExactlyOneReview(string culture)
    {
        using var cultures = new PageCultures(culture);
        await using var page = await ClipboardPageFixture.CreateAsync(handoff: true);
        var handoff = page.Get<AgentAnalysisResult>("activeResult").DetailedAnalysis.OperatorHandoff;
        var expected = string.Join(Environment.NewLine, $"Decision: {handoff.CurrentDecision}",
            $"Unresolved risk: {handoff.UnresolvedRisk}", $"Missing evidence: {handoff.MissingEvidence}",
            $"Audience: {handoff.Audience}");
        page.Javascript.BlockNext = true;
        var pending = page.Invoke("CopyHandoffAsync");
        await page.Javascript.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await page.Invoke("CopyPromptAsync");
            await page.Invoke("CopyHandoffAsync");
            await page.Invoke("CreateJobAsync");
            await page.Invoke("SubmitReviewAsync", AgentAnalysisReviewActions.Accepted, null);
            Assert.Equal(1, page.Javascript.Attempts);
            Assert.Empty(await page.Reviews());
            Assert.Single(await page.JobStore.ListAsync("owner", true));
        }
        finally
        {
            page.Javascript.Release.SetResult();
            await pending;
        }
        Assert.Equal(expected, Assert.Single(page.Javascript.Clipboard));
        Assert.Single(await page.Reviews(), review => review.Action == AgentAnalysisReviewActions.HandoffCopied);
        Assert.False(page.Get<bool>("isSavingReview"));
    }

    private sealed class ClipboardPageFixture(Fixture storage, StaticPageRenderer renderer, Prompt component,
        Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root) : IAsyncDisposable
    {
        private const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        public Fixture Storage => storage;
        public RecordingJavaScript Javascript => storage.Services.GetRequiredService<RecordingJavaScript>();
        public UiText Text => storage.Services.GetRequiredService<UiText>();
        public PromptPageLogger Log => storage.Services.GetRequiredService<PromptPageLogger>();
        public AgentAnalysisJobStore JobStore => storage.Services.GetRequiredService<AgentAnalysisJobStore>();
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public T Get<T>(string name) => (T)typeof(Prompt).GetField(name, Flags)!.GetValue(component)!;
        public Task Set(string name, object? value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(Prompt).GetField(name, Flags)!.SetValue(component, value);
            Changed();
        });
        public Task Invoke(string name, params object?[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(Prompt).GetMethod(name, Flags)!.Invoke(component, arguments);
            if (result is Task task) await task;
            Changed();
        });
        private void Changed() => typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(component, null);
        public Task DisposePage() => renderer.Dispatcher.InvokeAsync(component.Dispose);
        public Task<IReadOnlyList<AgentAnalysisReviewEvent>> Reviews() =>
            storage.Services.GetRequiredService<AgentAnalysisReviewStore>().ListAsync(Get<AgentAnalysisJobResponse>("activeJob").Id);
        public static async Task<ClipboardPageFixture> CreateAsync(bool handoff = false)
        {
            var storage = await Fixture.CreateAsync(admin: true, promptPage: true);
            var now = DateTimeOffset.UtcNow;
            ((FixedTime)storage.Services.GetRequiredService<TimeProvider>()).Now = now;
            await storage.AddAlertAsync("Clipboard evidence", "<original target>", now.AddMinutes(-1));
            var renderer = new StaticPageRenderer(storage.Services, storage.Services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(Prompt), ParameterView.Empty);
                await root.QuiescenceTask;
                return root;
            });
            var page = new ClipboardPageFixture(storage, renderer,
                storage.Services.GetRequiredService<CapturingRcaActivator>().PromptPage!, root);
            if (handoff)
            {
                await page.Invoke("CreateJobAsync");
                var job = (await page.JobStore.GetAsync(page.Get<AgentAnalysisJobResponse>("activeJob").Id))!;
                job = await page.JobStore.TransitionAsync(job.Id, job.Revision, AgentAnalysisJobStatuses.Running);
                await page.JobStore.CompleteAsync(job.Id, job.Revision,
                    AgentAnalysisJobStateMachineTests.CreateResult(job.AnalysisId, job.ResultVersion));
                await page.Invoke("RefreshActiveJobAsync");
            }
            return page;
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await storage.DisposeAsync();
        }
    }

    private sealed class RecordingJavaScript : Microsoft.JSInterop.IJSRuntime
    {
        public List<string> Clipboard { get; } = [];
        public Exception? Failure { get; set; }
        public bool FailAfterCopy { get; set; }
        public int Attempts { get; private set; }
        public bool BlockNext { get; set; }
        public bool IgnoreCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("navigator.clipboard.writeText", identifier);
            Attempts++;
            if (BlockNext)
            {
                BlockNext = false;
                Started.SetResult();
                await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
            }
            if (!FailAfterCopy && Failure is { } before) throw before;
            Clipboard.Add(Assert.IsType<string>(Assert.Single(args!)));
            if (Failure is { } after) throw after;
            return default!;
        }
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
