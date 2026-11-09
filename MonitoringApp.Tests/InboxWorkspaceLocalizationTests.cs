using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class InboxWorkspaceLocalizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 34, 56, TimeSpan.Zero);
    private const string Owner = "workspace-test";
    private const string OriginalTitle = "Original <widget> & title";

    [Theory]
    [InlineData("en-US", 1, "2026-11-06 12:34:56")]
    [InlineData("de-DE", 1, "06.11.2026 12:34:56")]
    [InlineData("en-US", 7, "2026-11-06 12:34:56")]
    [InlineData("de-DE", 7, "06.11.2026 12:34:56")]
    public async Task InboxShellTranslatesWithoutChangingTimeZoneInputsTechnicalValuesOrDefaultNames(
        string culture, int days, string clock)
    {
        using var selection = new UiSelection(culture);
        var formatCulture = CultureInfo.CurrentCulture.Name;
        var catalog = JsonSerializer.Serialize(InboxDashboardCatalog.Widgets);
        await using var fixture = await Fixture.Create(days);
        await fixture.Render<Home>();
        await fixture.Set("isEditingDateTime", true);
        await fixture.Set("currentTimeUtc", Now);
        foreach (var key in new[] { "Inbox.Title", "Inbox.Description", "Inbox.CommandsLabel",
            "Inbox.DesignerDescription", "Inbox.MinimumRowsHelp", "Inbox.WorkspaceReady", "Inbox.HideGrid" })
            Assert.Contains(fixture.Text[key], fixture.Decoded);
        Assert.Contains(clock, fixture.Decoded);
        Assert.Contains($"datetime=\"{Now:O}\"", fixture.Decoded);
        Assert.Contains(fixture.Text.Plural("Inbox.ReceivedWindow", days), fixture.Decoded);
        Assert.Contains(fixture.Text.Format(days == 1 ? "Inbox.ReceivedCount.One" : "Inbox.ReceivedCount.Other", 0, days), fixture.Decoded);
        foreach (var value in new[] { "active", "history", "history-fired", "utc" })
            Assert.Contains($"value=\"{value}\"", fixture.Html);
        Assert.Equal(2, Regex.Matches(fixture.Html, "type=\"datetime-local\"").Count);
        await fixture.Invoke("ChangeReceivedFrom", new ChangeEventArgs { Value = "2026-11-06T12:00" });
        Assert.Equal(new DateTime(2026, 11, 6, 12, 0, 0), fixture.Get<DateTime?>("receivedFrom"));
        Assert.Contains("value=\"2026-11-06T12:00\"", fixture.Html);
        Assert.Equal("utc", fixture.Get<string>("selectedTimeZoneId"));
        foreach (var template in InboxDashboardCatalog.Widgets)
        {
            Assert.Contains(template.Name, fixture.Decoded);
            Assert.Contains(fixture.Text.Format("Inbox.WidgetAlreadyVisible", template.Name), fixture.Decoded);
        }
        Assert.Contains("data-language-dirty=\"false\"", fixture.Html);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
        Assert.Equal(catalog, JsonSerializer.Serialize(InboxDashboardCatalog.Widgets));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RestoredCustomAndDefaultLookingTitlesAreEncodedNotTranslatedOrRewritten(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var document = InboxDashboardCatalog.CreateDefault();
        document.Name = "Original workspace";
        document.Widgets[1].Title = OriginalTitle;
        await fixture.Repository.SaveAsync(Owner, document);
        var original = await fixture.StoredJson();
        await fixture.Render<Home>();
        Assert.Contains(fixture.Text["Inbox.WorkspaceRestored"], fixture.Decoded);
        Assert.Contains("Time graphs", fixture.Decoded);
        Assert.Contains("Original &lt;widget&gt; &amp; title", fixture.Html);
        Assert.Contains(fixture.Text.Format("Widgets.Resize", OriginalTitle), fixture.Decoded);
        Assert.Equal(original, await fixture.StoredJson());
        await fixture.Set("isDashboardEditEnabled", true);
        await fixture.Set("selectedDashboardWidgetId", document.Widgets[1].Id);
        Assert.Contains(fixture.Text.Format("Widgets.ActionsFor", OriginalTitle), fixture.Decoded);
        Assert.Contains(fixture.Text["Widgets.ReduceWidthSymbol"], fixture.Decoded);
        Assert.Equal(original, await fixture.StoredJson());
        await fixture.Invoke("ChangeDashboardRowHeightAsync", new ChangeEventArgs { Value = "120" });
        var stored = (await fixture.Repository.LoadAsync(Owner))!;
        Assert.Equal("Original workspace", stored.Name);
        Assert.Equal(OriginalTitle, stored.Widgets[1].Title);
        Assert.Equal("Time graphs", stored.Widgets[0].Title);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task WorkspaceEditsPersistNeutralDocumentsWithLocalizedWholeRevisionFeedback(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.Set("isDashboardEditEnabled", true);
        await fixture.Invoke("ChangeDashboardRowsAsync", new ChangeEventArgs { Value = "26" });
        await AssertSaved("Inbox.RowsSaved", 1);
        Assert.Equal(26, (await fixture.Repository.LoadAsync(Owner))!.Rows);
        await fixture.Invoke("ChangeDashboardRowHeightAsync", new ChangeEventArgs { Value = "120" });
        await AssertSaved("Inbox.RowSizeSaved", 2);
        Assert.Equal(120, (await fixture.Repository.LoadAsync(Owner))!.RowHeight);
        var navigation = fixture.Dashboard.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Navigation);
        await fixture.Invoke("CommitDashboardMove", navigation.Id, 2, 8);
        await AssertSaved("Inbox.PositionSaved", 3);
        await fixture.Invoke("CommitDashboardResize", navigation.Id, 1, 4);
        await AssertSaved("Inbox.SizeSaved", 4);
        await fixture.Invoke("HandleDashboardWidgetActionAsync", navigation.Id, "right");
        await AssertSaved("Inbox.PositionSaved", 5);
        var current = (await fixture.Repository.LoadAsync(Owner))!.Widgets.Single(widget => widget.Id == navigation.Id);
        Assert.Equal((3, 1, 4), (current.Column, current.ColumnSpan, current.RowSpan));
        await fixture.Invoke("RemoveDashboardWidgetAsync", navigation.Id);
        await AssertSaved("Inbox.SectionRemoved", 6);
        Assert.DoesNotContain(fixture.Dashboard.Document.Widgets, widget => widget.Kind == InboxWidgetKind.Navigation);
        Assert.Contains(fixture.Text.Format("Inbox.AddWidget", "Alert navigation"), fixture.Decoded);
        await fixture.Invoke("AddDashboardWidgetAsync", InboxWidgetKind.Navigation);
        await AssertSaved("Inbox.SectionAdded", 7);
        Assert.Equal("Alert navigation", fixture.Dashboard.Document.Widgets.Single(widget => widget.Kind == InboxWidgetKind.Navigation).Title);
        await fixture.Invoke("UndoDashboardAsync");
        await AssertSaved("Inbox.ChangeUndone", 8);
        Assert.DoesNotContain(fixture.Dashboard.Document.Widgets, widget => widget.Kind == InboxWidgetKind.Navigation);
        await fixture.Invoke("RedoDashboardAsync");
        await AssertSaved("Inbox.ChangeRestored", 9);
        Assert.Contains(fixture.Dashboard.Document.Widgets, widget => widget.Kind == InboxWidgetKind.Navigation);
        await fixture.Invoke("ToggleDashboardGridAsync");
        await AssertSaved("Inbox.GridSaved", 10);
        Assert.False((await fixture.Repository.LoadAsync(Owner))!.ShowGrid);
        Assert.Contains("data-language-dirty=\"true\"", fixture.Html);
        await fixture.Invoke("ResetDashboardAsync");
        await AssertSaved("Inbox.DefaultRestored", 11);
        Assert.Equal(5, fixture.Dashboard.Document.Widgets.Count);
        Assert.Equal("Alert operations workspace", fixture.Dashboard.Document.Name);
        Assert.All(fixture.Dashboard.Document.Widgets, widget => Assert.Equal(
            InboxDashboardCatalog.Widgets.Single(template => template.Kind == widget.Kind).Name, widget.Title));
        Assert.Equal(11, fixture.Interceptor.Attempts);
        Assert.Empty(fixture.Log.Entries);

        async Task AssertSaved(string key, int revision)
        {
            Assert.Contains(fixture.Text.Format(key, revision), fixture.Decoded);
            Assert.False(fixture.Get<bool>("dashboardSaveFailed"));
            var json = await fixture.StoredJson();
            Assert.Contains("\"version\":3", json);
            Assert.Contains("\"kind\":\"Trends\"", json);
            Assert.DoesNotContain(fixture.Text["Inbox.CommandsLabel"], json);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DisabledEditModeDoesNotWriteButGridPreferenceStillWorks(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        var original = JsonSerializer.Serialize(fixture.Dashboard.Document);
        var id = fixture.Dashboard.Document.Widgets[0].Id;
        await fixture.Invoke("CommitDashboardMove", id, 2, 5);
        await fixture.Invoke("CommitDashboardResize", id, 1, 1);
        await fixture.Invoke("RemoveDashboardWidgetAsync", id);
        await fixture.Invoke("AddDashboardWidgetAsync", InboxWidgetKind.Trends);
        await fixture.Invoke("ChangeDashboardRowsAsync", new ChangeEventArgs { Value = "30" });
        await fixture.Invoke("ChangeDashboardRowHeightAsync", new ChangeEventArgs { Value = "128" });
        await fixture.Invoke("UndoDashboardAsync");
        await fixture.Invoke("RedoDashboardAsync");
        await fixture.Invoke("ResetDashboardAsync");
        Assert.Equal(original, JsonSerializer.Serialize(fixture.Dashboard.Document));
        Assert.Equal(0, fixture.Interceptor.Attempts);
        await fixture.Invoke("ToggleDashboardGridAsync");
        Assert.Contains(fixture.Text.Format("Inbox.GridSaved", 1), fixture.Decoded);
        Assert.Equal(1, fixture.Interceptor.Attempts);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task SaveFailureBeforeOrAfterCommitIsLoggedUnconfirmedNotRetriedAndNotSuccessShaped(
        string culture, bool afterCommit)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        fixture.Interceptor.FailBefore = !afterCommit;
        fixture.Interceptor.FailAfter = afterCommit;
        await fixture.Invoke("ToggleDashboardGridAsync");
        Assert.Equal(1, fixture.Interceptor.Attempts);
        Assert.True(fixture.Get<bool>("dashboardSaveFailed"));
        Assert.Contains("inbox-save-status-error", fixture.Html);
        Assert.Contains("role=\"alert\"", fixture.Html);
        Assert.Contains(fixture.Text["Inbox.WorkspaceSaveUnconfirmed"], fixture.Decoded);
        Assert.DoesNotContain("Sensitive database detail", fixture.Decoded);
        Assert.DoesNotContain(fixture.Text.Format("Inbox.GridSaved", 1), fixture.Decoded);
        var logged = Assert.Single(fixture.Log.Entries);
        Assert.NotNull(logged);
        Assert.Contains("Sensitive database detail", logged.Message);
        Assert.Equal(afterCommit, await fixture.Repository.LoadAsync(Owner) is not null);
        fixture.Interceptor.FailBefore = fixture.Interceptor.FailAfter = false;
        await fixture.Invoke("ToggleDashboardGridAsync");
        Assert.Equal(2, fixture.Interceptor.Attempts);
        Assert.False(fixture.Get<bool>("dashboardSaveFailed"));
        Assert.DoesNotContain("inbox-save-status-error", fixture.Html);
        Assert.Contains(fixture.Text.Format("Inbox.GridSaved", afterCommit ? 2 : 1), fixture.Decoded);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task WidgetActionsTranslateCaptionsWithoutChangingNineCallbackTokensOrWidget(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var widget = InboxDashboardCatalog.CreateWidget(InboxWidgetKind.Navigation);
        widget.Title = OriginalTitle;
        var original = JsonSerializer.Serialize(widget);
        var tokens = new List<string>();
        await fixture.Render<InboxWidgetActions>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(InboxWidgetActions.Widget)] = widget,
            [nameof(InboxWidgetActions.OnAction)] = EventCallback.Factory.Create<string>(tokens, (string token) => tokens.Add(token))
        }));
        Assert.Contains("Original &lt;widget&gt; &amp; title", fixture.Html);
        foreach (var name in new[] { "MoveLeft", "MoveUp", "MoveDown", "MoveRight", "ReduceWidth",
            "IncreaseWidth", "ReduceHeight", "IncreaseHeight", "Remove" })
            Assert.Contains(fixture.Text.Format("Widgets." + name + "Label", OriginalTitle), fixture.Decoded);
        foreach (var method in new[] { "MoveLeftAsync", "MoveUpAsync", "MoveDownAsync", "MoveRightAsync",
            "MakeNarrowerAsync", "MakeWiderAsync", "MakeShorterAsync", "MakeTallerAsync", "RemoveAsync" })
            await fixture.InvokeComponent(fixture.Actions, method);
        Assert.Equal(new[] { "left", "up", "down", "right", "narrower", "wider", "shorter", "taller", "remove" }, tokens);
        Assert.Equal(original, JsonSerializer.Serialize(widget));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task FilterLabelsTranslateWhileViewSearchAndTimeRangeSemanticsRemainUnchanged(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        foreach (var key in new[] { "Inbox.FiltersLabel", "Inbox.View", "Inbox.View.Active",
            "Inbox.View.History", "Inbox.View.FiredHistory", "Inbox.Search", "Inbox.SearchPlaceholder",
            "Common.TimeZone", "Inbox.ReceivedFrom", "Inbox.ReceivedTo", "Inbox.ClearFilters" })
            Assert.Contains(fixture.Text[key], fixture.Decoded);
        Assert.Matches("class=\"clear-filters\"[^>]*disabled", fixture.Html);

        var live = FilterAlert("live", Now.AddMinutes(-5), "Raw <rule> & original");
        var resolved = FilterAlert("resolved", Now.AddMinutes(-10), "Original history") with { MonitorCondition = "Resolved" };
        var historical = FilterAlert("old-fired", Now.AddDays(-10), "Original Fired history");
        AlertRecord[] records = [live, resolved];
        var original = JsonSerializer.Serialize(records);
        await fixture.Set("alerts", records);
        await fixture.Set("firedHistoryAlerts", new[] { historical });
        await fixture.Set("isFiredHistoryLoaded", true);
        Assert.Equal(["live"], Ids());
        await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history" });
        Assert.Equal(["live", "resolved"], Ids());
        await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history-fired" });
        Assert.Equal(["old-fired"], Ids());
        Assert.Equal("history-fired", fixture.Get<string>("viewMode"));
        await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history" });
        await fixture.Set("searchText", "<RULE>");
        await fixture.Invoke("ResetPagination");
        Assert.Equal(["live"], Ids());
        Assert.Contains("Raw &lt;rule&gt; &amp; original", fixture.Html);
        await fixture.Set("searchText", culture == "de-DE" ? "Aktive Alarme" : "Active alerts");
        Assert.Empty(Ids());
        await fixture.Set("searchText", string.Empty);
        await fixture.Invoke("ChangeReceivedFrom", new ChangeEventArgs { Value = "2026-11-06T12:29:56" });
        await fixture.Invoke("ChangeReceivedTo", new ChangeEventArgs { Value = "2026-11-06T12:29:56" });
        Assert.Equal(["live"], Ids());
        Assert.DoesNotContain("class=\"filter-error\"", fixture.Html);
        await fixture.Invoke("ChangeReceivedFrom", new ChangeEventArgs { Value = "2026-11-06T12:29:57" });
        Assert.Empty(Ids());
        Assert.Contains($"class=\"filter-error\" role=\"alert\">{fixture.Text["Inbox.InvalidTimeRange"]}", fixture.Decoded);
        await fixture.Set("domainFilter", "Unknown domain");
        await fixture.Set("roleFilter", "Unknown role");
        await fixture.Set("targetNameFilter", "Original target");
        await fixture.Set("searchText", "Original query");
        await fixture.Invoke("ClearFilters");
        foreach (var field in new[] { "searchText", "domainFilter", "roleFilter", "targetNameFilter", "receivedFromInput", "receivedToInput" })
            Assert.Equal(string.Empty, fixture.Get<string>(field));
        Assert.Null(fixture.Get<DateTime?>("receivedFrom"));
        Assert.Null(fixture.Get<DateTime?>("receivedTo"));
        Assert.Equal(["live", "resolved"], Ids());
        Assert.Equal(1, fixture.Get<int>("currentPage"));
        Assert.Equal("history", fixture.Get<string>("viewMode"));
        Assert.Equal("utc", fixture.Get<string>("selectedTimeZoneId"));
        Assert.Equal(original, JsonSerializer.Serialize(records));
        Assert.Null(await fixture.Repository.LoadAsync(Owner));

        string[] Ids() => fixture.Property<IEnumerable<AlertRecord>>("FilteredAlerts").Select(alert => alert.AlertId).ToArray();
    }

    [Theory]
    [InlineData("en-US", "utc", 0)]
    [InlineData("de-DE", "utc", 0)]
    [InlineData("en-US", "germany", 1)]
    [InlineData("de-DE", "germany", 1)]
    [InlineData("en-US", "singapore", 8)]
    [InlineData("de-DE", "singapore", 8)]
    [InlineData("en-US", "new-york", -5)]
    [InlineData("de-DE", "new-york", -5)]
    public async Task TimeZoneLabelsAndChartTicksUseUiCultureWithoutChangingIdsOffsetsOrInputConversion(
        string culture, string zone, int offset)
    {
        using var selection = new UiSelection(culture, culture == "de-DE" ? "en-US" : "de-DE");
        var formatCulture = CultureInfo.CurrentCulture.Name;
        var original = AlertTimeZones.Options.Select(option => (option.Id, option.Label, option.TimeZone.Id)).ToArray();
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        await fixture.Invoke("ChangeReceivedFrom", new ChangeEventArgs { Value = "2026-11-06T12:00" });
        await fixture.Invoke("ChangeReceivedTo", new ChangeEventArgs { Value = "2026-11-06T13:00" });
        await fixture.Invoke("ChangeTimeZone", new ChangeEventArgs { Value = zone });
        foreach (var option in AlertTimeZones.Options)
        {
            Assert.Contains($"value=\"{option.Id}\"", fixture.Html);
            Assert.Contains($">{fixture.Text["Inbox.TimeZone." + option.Id]}</option>", fixture.Decoded);
        }
        var zoneLabel = fixture.Text["Inbox.TimeZone." + zone];
        Assert.Equal(zone, fixture.Get<string>("selectedTimeZoneId"));
        Assert.Equal(new DateTime(2026, 11, 6, 12, 0, 0).AddHours(offset), fixture.Get<DateTime?>("receivedFrom"));
        Assert.Equal(new DateTime(2026, 11, 6, 13, 0, 0).AddHours(offset), fixture.Get<DateTime?>("receivedTo"));
        var expectedFrom = new DateTime(2026, 11, 6, 12, 0, 0).AddHours(offset).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        Assert.Contains($"value=\"{expectedFrom}\"", fixture.Html);
        Assert.Contains(fixture.Text.Format("Inbox.CurrentTimeLabel", zoneLabel), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.ChartWindow", zoneLabel), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.CriticalChartSummary", 0, 0, zoneLabel), fixture.Decoded);
        Assert.Contains($"UTC{(offset < 0 ? "-" : "+")}{Math.Abs(offset):00}:00", fixture.Decoded);
        Assert.Contains($"datetime=\"{Now:O}\"", fixture.Decoded);
        var firstTick = new DateTime(2026, 11, 4, 13, 0, 0).AddHours(offset);
        Assert.Contains(firstTick.ToString("ddd HH:mm", CultureInfo.GetCultureInfo(culture)), fixture.Decoded);
        Assert.Equal(14, Regex.Matches(fixture.Html, "style=\"--chart-tick:").Count);
        Assert.Equal(original, AlertTimeZones.Options.Select(option => (option.Id, option.Label, option.TimeZone.Id)).ToArray());
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 3)]
    [InlineData("de-DE", 3)]
    public async Task ChartCaptionsAndAriaTranslateWithExactCountsAndInvariantGeometry(string culture, int count)
    {
        using var selection = new UiSelection(culture, culture == "de-DE" ? "en-US" : "de-DE");
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var fired = Enumerable.Range(0, count)
            .Select(index => FilterAlert($"chart-{index}", Now.AddHours(-2), $"Original chart {index}")).ToArray();
        var records = count == 3
            ? fired.Append(fired[2] with { Id = Guid.NewGuid(), ReceivedAt = Now.AddHours(-1), MonitorCondition = "Resolved" }).ToArray()
            : fired;
        var lifecycles = fired.Select((alert, index) => new CriticalAlertLifecycle(
            alert.AlertId, Now.AddHours(-3), index == 2 ? Now.AddHours(-1) : null)).ToArray();
        var original = JsonSerializer.Serialize(records);
        await fixture.Set("alerts", records);
        await fixture.Set("criticalAlertLifecycles", lifecycles);
        var current = count == 3 ? 2 : count;
        Assert.Contains(culture == "de-DE" ? "Alarme pro Stunde" : "Alerts per hour", fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.ActiveCriticalAlerts"], fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.ChartDescription", count, count), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.CriticalChartDescription", current, count), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.CriticalChartSummary", current, count, "UTC"), fixture.Decoded);
        Assert.Contains($"title=\"{fixture.Text["Inbox.CriticalScaleHelp"]}\"", fixture.Decoded);
        var descriptions = Regex.Matches(fixture.Html, "role=\"img\" aria-label=\"([^\"]+)\"");
        Assert.Equal(2, descriptions.Count);
        Assert.Equal(fixture.Text.Format("Inbox.ChartDescription", count, count), WebUtility.HtmlDecode(descriptions[0].Groups[1].Value));
        Assert.Equal(fixture.Text.Format("Inbox.CriticalChartDescription", current, count), WebUtility.HtmlDecode(descriptions[1].Groups[1].Value));
        var points = Regex.Matches(fixture.Html, "<polyline[^>]*points=\"([^\"]+)\"");
        Assert.Equal(2, points.Count);
        Assert.Equal(ExpectedPoints(index => index == 45 ? count : 0), points[0].Groups[1].Value);
        Assert.Equal(ExpectedPoints(index => index < 44 ? 0 : count == 3 && index >= 46 ? 2 : count), points[1].Groups[1].Value);
        Assert.Equal(14, Regex.Matches(fixture.Html, "style=\"--chart-tick:").Count);
        Assert.Equal(original, JsonSerializer.Serialize(records));
        Assert.Same(lifecycles, fixture.Get<IReadOnlyList<CriticalAlertLifecycle>>("criticalAlertLifecycles"));

        string ExpectedPoints(Func<int, int> value) => string.Join(' ', Enumerable.Range(0, 48).Select(index =>
            FormattableString.Invariant($"{index * 640d / 47:0.##},{110 - value(index) * 100d / Math.Max(1, count):0.##}")));
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    public async Task NavigationTranslatesCountsAndExpansionWithoutChangingNamesKeysOrSelection(string culture, int count)
    {
        using var selection = new UiSelection(culture);
        const string domain = "Original <domain> & name";
        const string role = "Original <role>";
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var records = Enumerable.Range(0, count).Select(index =>
            FilterAlert($"navigation-{index}", Now.AddMinutes(-5), "Original rule") with { Target = $"target-{index}" }).ToArray();
        var inventory = records.Select(alert => new ComputerInventoryEntry
        {
            Computer = alert.Target, SubscriptionId = alert.SubscriptionId, Domain = domain,
            Role = role, Site = "Original <site>", LastHeartbeatAt = Now
        }).ToArray();
        var original = JsonSerializer.Serialize(inventory);
        await fixture.Set("computerInventory", inventory);
        await fixture.Set("alerts", records);
        Assert.Contains(fixture.Text["Inbox.NavigationLabel"], fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.NavigationTitle"], fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.NavigationCountsLabel"], fixture.Decoded);
        Assert.Contains(fixture.Text["Filters.AllDomains"], fixture.Decoded);
        Assert.Contains(fixture.Text.Format(count == 1 ? "Inbox.NavigationCounts.One" : "Inbox.NavigationCounts.Other", count, count), fixture.Decoded);
        if (count == 0)
        {
            Assert.Contains(fixture.Text["Inbox.NoTargets"], fixture.Decoded);
            Assert.DoesNotContain("class=\"heatmap-legend\"", fixture.Html);
        }
        else
        {
            Assert.Contains("Original &lt;domain&gt; &amp; name", fixture.Html);
            Assert.Contains(fixture.Text["Inbox.ExpandDomain"], fixture.Decoded);
            await fixture.Invoke("ToggleDomain", domain);
            Assert.Contains(fixture.Text["Inbox.CollapseDomain"], fixture.Decoded);
            Assert.Contains(fixture.Text["Inbox.ExpandRole"], fixture.Decoded);
            await fixture.Invoke("ToggleRole", domain + "\u001f" + role);
            Assert.Contains(fixture.Text["Inbox.CollapseRole"], fixture.Decoded);
            Assert.Contains("Original &lt;role&gt;", fixture.Html);
            Assert.Contains("target-0 (Original &lt;site&gt;)", fixture.Html);
            await fixture.Invoke("SelectTreeNode", domain, role, "target-0");
            Assert.Equal(domain, fixture.Get<string>("domainFilter"));
            Assert.Equal(role, fixture.Get<string>("roleFilter"));
            Assert.Equal("target-0", fixture.Get<string>("targetNameFilter"));
            Assert.Equal(["navigation-0"], fixture.Property<IEnumerable<AlertRecord>>("FilteredAlerts").Select(alert => alert.AlertId));
            await fixture.Invoke("ToggleRole", domain + "\u001f" + role);
            await fixture.Invoke("ToggleDomain", domain);
            Assert.Empty(fixture.Get<HashSet<string>>("expandedDomains"));
            Assert.Empty(fixture.Get<HashSet<string>>("expandedRoles"));
            Assert.Equal("target-0", fixture.Get<string>("targetNameFilter"));
            await fixture.Invoke("SelectTreeNode", "", "", "");
            Assert.Equal(count, fixture.Property<IEnumerable<AlertRecord>>("FilteredAlerts").Count());
        }
        Assert.Equal(original, JsonSerializer.Serialize(inventory));
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MissingAndCustomDefaultLookingDomainRoleNamesRetainCanonicalFilterIdentities(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var records = new[] { "missing", "custom" }.Select(name =>
            FilterAlert(name, Now.AddMinutes(-1), "Original rule") with { Target = name }).ToArray();
        ComputerInventoryEntry[] inventory =
        [
            new() { Computer = "missing", SubscriptionId = "Original subscription", LastHeartbeatAt = Now },
            new() { Computer = "custom", SubscriptionId = "Original subscription", Domain = "Unknown domain", Role = "Unknown role", LastHeartbeatAt = Now }
        ];
        var original = JsonSerializer.Serialize(inventory);
        await fixture.Set("computerInventory", inventory);
        await fixture.Set("alerts", records);
        await fixture.Invoke("ToggleDomain", "Unknown domain");
        await fixture.Invoke("ToggleRole", "Unknown domain\u001fUnknown role");
        Assert.Contains(">Unknown domain</span>", fixture.Html);
        Assert.Contains(">Unknown role</span>", fixture.Html);
        foreach (var target in new[] { "missing", "custom" })
        {
            await fixture.Invoke("SelectTreeNode", "Unknown domain", "Unknown role", target);
            Assert.Equal([target], fixture.Property<IEnumerable<AlertRecord>>("FilteredAlerts").Select(alert => alert.AlertId));
            Assert.Equal("Unknown domain", fixture.Get<string>("domainFilter"));
            Assert.Equal("Unknown role", fixture.Get<string>("roleFilter"));
        }
        Assert.Equal(original, JsonSerializer.Serialize(inventory));
    }

    [Theory]
    [InlineData("en-US", true, true)]
    [InlineData("de-DE", true, true)]
    [InlineData("en-US", false, true)]
    [InlineData("de-DE", false, true)]
    [InlineData("en-US", true, false)]
    [InlineData("de-DE", true, false)]
    [InlineData("en-US", false, false)]
    [InlineData("de-DE", false, false)]
    public async Task TargetOverviewProjectsTypedLegendWithConfiguredRangesAndUnchangedColors(
        string culture, bool showDomain, bool critical)
    {
        using var selection = new UiSelection(culture);
        var options = new InboxTargetOutputDisplayOptions
        {
            ShowDomain = showDomain, CriticalOutageAlwaysRed = critical, SuppressedColor = "green",
            CountColors = [new() { MinimumCount = 5, Color = "gray" }, new() { MinimumCount = 1, Color = "orange" }, new() { MinimumCount = 3, Color = "yellow" }]
        };
        var original = JsonSerializer.Serialize(options);
        var neutralLabels = options.Legend().Select(entry => entry.Label).ToArray();
        await using var fixture = await Fixture.Create(targetOptions: options);
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var records = new[] { 1, 3, 5 }.SelectMany(count => Enumerable.Range(0, count)
            .Select(index => FilterAlert($"target-{count}-{index}", Now.AddMinutes(-1), "Original rule")
                with { Target = $"target-{count}" })).ToArray();
        await fixture.Set("alerts", records);
        Assert.Contains(fixture.Text[showDomain ? "Inbox.TargetsDomainsSites" : "Inbox.TargetsSites"], fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.HeatmapDescription"], fixture.Decoded);
        var expected = culture == "de-DE"
            ? "Unterdrückt: grün; 1-2: orange; 3-4: gelb; 5+: grau; " +
                (critical ? "Kritischer Ausfall: rot; " : "") + "Dienstzustandsalarm: violett; Fehlender Heartbeat: grau"
            : "Suppressed: green; 1-2: orange; 3-4: yellow; 5+: gray; " +
                (critical ? "Critical outage: red; " : "") + "Service health alert: purple; Missing heartbeat: gray";
        var legend = Regex.Match(fixture.Html, "class=\"heatmap-legend\" aria-label=\"([^\"]+)\"");
        Assert.True(legend.Success);
        Assert.Equal(expected, WebUtility.HtmlDecode(legend.Groups[1].Value));
        foreach (var entry in options.Legend())
            Assert.Contains(InboxTargetOutputDisplayOptions.ColorStyle(entry.Color), fixture.Html);
        foreach (var (count, color) in new[] { (1, InboxTargetColor.Orange), (3, InboxTargetColor.Yellow), (5, InboxTargetColor.Gray) })
        {
            Assert.Contains($">{count}</strong>", fixture.Html);
            Assert.Contains($"style=\"{InboxTargetOutputDisplayOptions.ColorStyle(color)}\" title=", fixture.Html);
        }
        Assert.Equal(showDomain ? 3 : 0, Regex.Matches(fixture.Html, "class=\"heatmap-domain\"").Count);
        Assert.Equal(neutralLabels, options.Legend().Select(entry => entry.Label));
        Assert.Equal(InboxTargetLegendKind.Suppressed, options.Legend()[0].Kind);
        Assert.All(options.Legend().Skip(1).Take(3), entry => Assert.Equal(InboxTargetLegendKind.CountBand, entry.Kind));
        Assert.Equal(InboxTargetLegendKind.ServiceHealthAlert, options.Legend()[^1].Kind);
        if (critical) Assert.Equal(InboxTargetLegendKind.CriticalOutage, options.Legend()[^2].Kind);
        Assert.Equal(original, JsonSerializer.Serialize(options));
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US", false, false)]
    [InlineData("de-DE", false, false)]
    [InlineData("en-US", false, true)]
    [InlineData("de-DE", false, true)]
    [InlineData("en-US", true, false)]
    [InlineData("de-DE", true, false)]
    [InlineData("en-US", true, true)]
    [InlineData("de-DE", true, true)]
    public async Task TargetStatusLabelsPreserveHeartbeatServiceHealthPriorityWithLocalizedCellDescriptions(
        string culture, bool stale, bool serviceHealth)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        ComputerInventoryEntry[] inventory =
        [
            new()
            {
                Computer = "Original <target>", SubscriptionId = "Original subscription", Domain = "Original <domain>",
                Site = "Original <site>", LastHeartbeatAt = stale ? null : Now,
                ServicesJson = serviceHealth ? """[{"Name":"Original <service> \" &","Status":1},{"Name":"Running service","Status":4},{"Name":"DNS","Status":null}]""" : "[]"
            }
        ];
        var original = JsonSerializer.Serialize(inventory);
        await fixture.Set("computerInventory", inventory);
        if (!stale && !serviceHealth)
        {
            Assert.Contains(fixture.Text["Inbox.NoTargets"], fixture.Decoded);
            Assert.DoesNotContain("class=\"heatmap-grid\"", fixture.Html);
        }
        else
        {
            Assert.Contains($"class=\"heatmap-heartbeat-status\">{fixture.Text[serviceHealth ? "Inbox.ServiceHealthAlert" : "Inbox.MissingHeartbeat"]}", fixture.Decoded);
            Assert.Contains("Original &lt;target&gt;", fixture.Html);
            Assert.Contains("Original &lt;domain&gt;", fixture.Html);
            Assert.Contains("Original &lt;site&gt;", fixture.Html);
            Assert.Equal(stale, fixture.Html.Contains("heatmap-cell-missing-heartbeat"));
            Assert.Equal(serviceHealth, fixture.Html.Contains("heatmap-cell-service-health-alert"));
            if (serviceHealth) Assert.Contains(InboxTargetOutputDisplayOptions.ColorStyle(InboxTargetColor.Purple), fixture.Html);
            var cell = Regex.Match(fixture.Html, "<div class=\"heatmap-cell[^\"]*\"[^>]*title=\"([^\"]+)\" aria-label=\"([^\"]+)\"");
            Assert.True(cell.Success);
            Assert.Equal(cell.Groups[1].Value, cell.Groups[2].Value);
            var description = WebUtility.HtmlDecode(cell.Groups[1].Value);
            var expectedCell = new InboxTargetOutputCell("Original subscription", "Original <target>", "Original <site>",
                0, stale, "Original <domain>", HasServiceHealthAlert: serviceHealth);
            var expected = ExpectedCellDescription(culture, true, expectedCell,
                fixture.Get<TimeSpan>("currentHeartbeatAge").TotalMinutes);
            if (serviceHealth)
            {
                expected += culture == "de-DE" ? "; Nicht laufende Dienste: " : "; Non-running services: ";
                expected += "DNS, Original <service> \" &";
                Assert.Contains("Original &lt;service&gt; &quot; &amp;", cell.Groups[1].Value);
            }
            Assert.Equal(expected, description);
            Assert.DoesNotContain("Running service", description);
        }
        Assert.Equal(original, JsonSerializer.Serialize(inventory));
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    public async Task WholeTargetDescriptionsCoverEverySelectorWithoutChangingCellDataOrEnglishCopy(string culture, bool showDomain)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var formatCulture = CultureInfo.CurrentCulture.Name;
        var options = new InboxTargetOutputDisplayOptions { ShowDomain = showDomain };
        var originalOptions = JsonSerializer.Serialize(options);
        await using var fixture = await Fixture.Create(targetOptions: options);
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var method = typeof(Home).GetMethod("HeatmapCellDescription", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var manager = new ResourceManager("MonitoringApp.Resources.UiResources", typeof(UiResources).Assembly);
        var resources = manager.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, true)!;
        Assert.Equal(72, resources.Cast<DictionaryEntry>().Count(entry => ((string)entry.Key).StartsWith("Inbox.TargetCell.", StringComparison.Ordinal)));
        var checkedCases = 0;
        foreach (var minutes in new[] { 1d, 12.6 })
        {
            await fixture.Set("currentHeartbeatAge", TimeSpan.FromMinutes(minutes));
            foreach (var count in new[] { 0, 1, 2, 1234 })
            foreach (var serviceHealth in new[] { false, true })
            foreach (var suppressed in new[] { false, true })
            foreach (var outage in Enumerable.Range(0, 4))
            foreach (var missing in new[] { false, true })
            {
                var cell = new InboxTargetOutputCell("Original <subscription> \" & value", "Original <target> \" & value",
                    "Original <site> \" & value", count, missing, "Original <domain> \" & value",
                    count == 2 ? 1 : count, suppressed, (outage & 1) != 0, (outage & 2) != 0, serviceHealth);
                var original = JsonSerializer.Serialize(cell);
                var actual = Assert.IsType<string>(method.Invoke(fixture.Page, [cell]));
                Assert.Equal(ExpectedCellDescription(culture, showDomain, cell, minutes), actual);
                Assert.Equal(showDomain, actual.Contains(cell.Domain, StringComparison.Ordinal));
                Assert.Equal(original, JsonSerializer.Serialize(cell));
                checkedCases++;
            }
        }
        Assert.Equal(256, checkedCases);
        Assert.Equal(originalOptions, JsonSerializer.Serialize(options));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US", true, 1)]
    [InlineData("de-DE", true, 1)]
    [InlineData("en-US", false, 1)]
    [InlineData("de-DE", false, 1)]
    [InlineData("en-US", true, 2)]
    [InlineData("de-DE", true, 2)]
    [InlineData("en-US", false, 2)]
    [InlineData("de-DE", false, 2)]
    public async Task RenderedTargetTitleAndAriaUseWholeLocalizedTemplatesAndEncodeOriginalIdentities(
        string culture, bool showDomain, int count)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create(targetOptions: new() { ShowDomain = showDomain });
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        const string target = "Original <target> \" & value";
        const string domain = "Original <domain> \" & value";
        const string site = "Original <site> \" & value";
        const string subscription = "Original <subscription> \" & value";
        var records = Enumerable.Range(0, count).Select(index =>
            FilterAlert($"encoded-{index}", Now.AddMinutes(-1), "Original rule")
                with { Target = target, SubscriptionId = subscription }).ToArray();
        ComputerInventoryEntry[] inventory =
        [
            new() { Computer = target, Domain = domain, Site = site, SubscriptionId = subscription, LastHeartbeatAt = Now }
        ];
        var originalRecords = JsonSerializer.Serialize(records);
        var originalInventory = JsonSerializer.Serialize(inventory);
        await fixture.Set("computerInventory", inventory);
        await fixture.Set("alerts", records);
        var cell = Regex.Match(fixture.Html, "<div class=\"heatmap-cell[^\"]*\"[^>]*title=\"([^\"]+)\" aria-label=\"([^\"]+)\"");
        Assert.True(cell.Success);
        Assert.Equal(cell.Groups[1].Value, cell.Groups[2].Value);
        var description = WebUtility.HtmlDecode(cell.Groups[1].Value);
        Assert.Equal(ExpectedCellDescription(culture, showDomain,
            new(subscription, target, site, count, false, domain, count),
            fixture.Get<TimeSpan>("currentHeartbeatAge").TotalMinutes), description);
        Assert.Contains("Original &lt;target&gt;", cell.Groups[1].Value);
        Assert.Contains("&amp;", cell.Groups[1].Value);
        Assert.DoesNotContain("<target>", fixture.Html);
        Assert.DoesNotContain("<subscription>", fixture.Html);
        Assert.Equal(originalRecords, JsonSerializer.Serialize(records));
        Assert.Equal(originalInventory, JsonSerializer.Serialize(inventory));
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    private static string ExpectedCellDescription(string culture, bool showDomain, InboxTargetOutputCell cell, double minutes)
    {
        var english = culture == "en-US";
        var identity = english
            ? $"{cell.Target}, " + (showDomain ? $"domain {cell.Domain}, " : "") +
                $"site {cell.Site}, subscription {cell.SubscriptionId}: {cell.Count} {(cell.Count == 1 ? "alert" : "alerts")}"
            : $"{cell.Target}, " + (showDomain ? $"Domäne {cell.Domain}, " : "") +
                $"Standort {cell.Site}, Abonnement {cell.SubscriptionId}: {cell.Count} {(cell.Count == 1 ? "Alarm" : "Alarme")}";
        var serviceHealth = cell.HasServiceHealthAlert && cell.Count == 0
            ? english ? "; Service health alert: at least one monitored service is not running"
                : "; Dienstzustandsalarm: mindestens ein überwachter Dienst läuft nicht"
            : "";
        var suppression = cell.SuppressedOnly
            ? english ? "; Suppressed alerts only" : "; nur unterdrückte Alarme"
            : english ? $"; {cell.NonSuppressedCount} non-suppressed alerts" : $"; nicht unterdrückte Alarme: {cell.NonSuppressedCount}";
        var outage = cell.IsCriticalOutage
            ? english ? "; Critical outage" : "; Kritischer Ausfall"
            : cell.IsOutage ? english ? "; Outage" : "; Ausfall" : "";
        var heartbeat = cell.MissingHeartbeat
            ? english ? FormattableString.Invariant($"; Missing heartbeat (older than {minutes:0} minutes or never received)")
                : FormattableString.Invariant($"; Fehlender Heartbeat (älter als {minutes:0} min oder nie empfangen)")
            : "";
        return identity + serviceHealth + suppression + outage + heartbeat;
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    public async Task ResultsChromeTranslatesWhileColumnContractsOriginalRowsUrlsAndEmptyStatesRemainIntact(string culture, int count)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var records = Enumerable.Range(0, count).Select(index =>
            FilterAlert($"result-{index}", Now.AddMinutes(-index), $"Original <alert> {index}") with
            {
                Comments = "Original <comment> & value",
                SearchResultsUrl = "https://portal.azure.com/#blade/original?x=1&target=%3Coriginal%3E",
                RawJson = JsonSerializer.Serialize(new
                {
                    searchQuery = "Heartbeat | where Computer == '<original>'",
                    queryResult = new { type = "DCPort", columns = Array.Empty<string>(), rows = Array.Empty<string[]>() }
                }),
                QueryResultRefreshedAt = Now
            }).ToArray();
        var original = JsonSerializer.Serialize(records);
        await fixture.Set("alerts", records);
        await fixture.Invoke("ChangeTimeZone", new ChangeEventArgs { Value = "germany" });
        foreach (var column in new[] { "Received", "Alert", "Target", "Query", "Result", "Comments", "Dashboard",
            "ResizeAlert", "ResizeTarget", "ResizeQuery", "ResizeResult", "StorageFailed", "BrowserFailed", "ReloadBrowser" })
            Assert.Contains(fixture.Text["Inbox.Columns." + column], fixture.Decoded);
        Assert.Contains("data-table-storage-error hidden role=\"alert\"", fixture.Html);
        Assert.Contains("data-table-browser-error hidden role=\"alert\"", fixture.Html);
        Assert.Contains("data-table-retry", fixture.Html);
        Assert.Contains($"class=\"column-zone\">{fixture.Text["Inbox.TimeZone.germany"]}", fixture.Decoded);
        foreach (var (index, key, minimum, width) in new[] { (1, "alert", 140, 190), (2, "target", 120, 150),
            (3, "query", 220, 340), (4, "search-result", 240, 320) })
        {
            var handle = Regex.Match(fixture.Html, $"<span class=\"column-resizer\"[^>]*data-column-index=\"{index}\"[^>]*>").Value;
            Assert.Contains("role=\"separator\"", handle);
            Assert.Contains("aria-orientation=\"vertical\"", handle);
            Assert.Contains($"data-column-key=\"{key}\"", handle);
            Assert.Contains($"data-min-width=\"{minimum}\"", handle);
            Assert.Contains($"aria-valuemin=\"{minimum}\"", handle);
            Assert.Contains($"aria-valuenow=\"{width}\"", handle);
            Assert.Contains("tabindex=\"0\"", handle);
        }
        Assert.Contains(fixture.Text.Format(count == 1 ? "Inbox.ResultsSummary.One" : "Inbox.ResultsSummary.Other",
            count == 0 ? "0" : $"1-{count}", count, count), fixture.Decoded);
        var plural = Assert.IsType<string>(typeof(Home).GetMethod("AlertCountLabel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Page, [count]));
        Assert.Equal(culture == "de-DE" ? $"{count} {(count == 1 ? "Alarm" : "Alarme")}" : $"{count} {(count == 1 ? "alert" : "alerts")}", plural);
        Assert.Equal(count, ResultNames(fixture).Length);
        if (count == 0)
        {
            Assert.Contains(fixture.Text["Inbox.Waiting"], fixture.Decoded);
            Assert.Contains(fixture.Text["Inbox.IngestionHelp"], fixture.Decoded);
            Assert.Contains("/api/alerts", fixture.Decoded);
        }
        else
        {
            Assert.Contains("Original &lt;alert&gt; 0", ResultBody(fixture));
            Assert.Contains("Original &lt;comment&gt; &amp; value", ResultBody(fixture));
            Assert.Contains(records[0].SearchQuery, fixture.Decoded);
            Assert.Contains($"title=\"{fixture.Text["Inbox.ShowDetails"]}\"", fixture.Decoded);
            Assert.Contains($"href=\"{records[0].SearchResultsUrl}\" target=\"_blank\" rel=\"noopener noreferrer\">{fixture.Text["Common.Open"]}</a>", fixture.Decoded);
            Assert.Contains(fixture.Text.Format("Inbox.Refreshed", culture == "de-DE" ? "06.11.2026 13:34:56" : "2026-11-06 13:34:56"), fixture.Decoded);
            Assert.Contains($"{Now:O} UTC", fixture.Decoded);
            await fixture.Set("searchText", "No such original alert");
            Assert.Empty(ResultNames(fixture));
            Assert.Contains(fixture.Text["Inbox.NoMatches"], fixture.Decoded);
            Assert.Contains(fixture.Text["Inbox.FilterHelp"], fixture.Decoded);
            Assert.Contains(fixture.Text.Format("Inbox.ResultsSummary.Other", "0", 0, count), fixture.Decoded);
            Assert.DoesNotContain(fixture.Text["Inbox.Waiting"], ResultBody(fixture));
        }
        Assert.DoesNotContain("class=\"inbox-pagination\"", fixture.Html);
        Assert.Equal(original, JsonSerializer.Serialize(records));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ResultsPaginationAndSortingKeepExactRowsPageWindowAndDetailResetSemantics(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var records = Enumerable.Range(0, 83).Select(index =>
            FilterAlert($"page-{index}", Now.AddMinutes(-index), $"Alert {index:00}")
                with { Target = $"target-{82 - index:00}", Comments = "Original saved comment" }).ToArray();
        var original = JsonSerializer.Serialize(records);
        await fixture.Set("alerts", records);
        Assert.Equal(records.Take(10).Select(alert => alert.Name), ResultNames(fixture));
        AssertPage("1-10", Enumerable.Range(1, 7), previousDisabled: true, nextDisabled: false);
        await fixture.Invoke("ToggleDetails", records[0]);
        await fixture.Set("commentDraft", "Unsaved original comment");
        await fixture.Set("commentSaveError", "Original test error");
        await fixture.Invoke("ChangePage", 5);
        Assert.Equal(records.Skip(40).Take(10).Select(alert => alert.Name), ResultNames(fixture));
        AssertPage("41-50", Enumerable.Range(2, 7), previousDisabled: false, nextDisabled: false);
        Assert.Null(fixture.Get<Guid?>("expandedAlertId"));
        Assert.Equal(string.Empty, fixture.Get<string>("commentDraft"));
        Assert.Equal(string.Empty, fixture.Get<string>("commentSaveError"));
        await fixture.Invoke("ChangePage", 9);
        Assert.Equal(records.Skip(80).Select(alert => alert.Name), ResultNames(fixture));
        AssertPage("81-83", Enumerable.Range(3, 7), previousDisabled: false, nextDisabled: true);
        await fixture.Invoke("SortBy", nameof(AlertRecord.Name));
        Assert.Equal(nameof(AlertRecord.Name), fixture.Get<string>("sortColumn"));
        Assert.True(fixture.Get<bool>("sortAscending"));
        Assert.Equal(1, fixture.Get<int>("currentPage"));
        Assert.Equal(records.Take(10).Select(alert => alert.Name), ResultNames(fixture));
        Assert.Contains(fixture.Text["Inbox.SortAscending"], fixture.Decoded);
        await fixture.Invoke("SortBy", nameof(AlertRecord.Name));
        Assert.False(fixture.Get<bool>("sortAscending"));
        Assert.Equal(records.Reverse().Take(10).Select(alert => alert.Name), ResultNames(fixture));
        Assert.Contains(fixture.Text["Inbox.SortDescending"], fixture.Decoded);
        await fixture.Invoke("SortBy", nameof(AlertRecord.TargetName));
        Assert.Equal(nameof(AlertRecord.TargetName), fixture.Get<string>("sortColumn"));
        Assert.True(fixture.Get<bool>("sortAscending"));
        Assert.Equal(records.Reverse().Take(10).Select(alert => alert.Name), ResultNames(fixture));
        await fixture.Invoke("SortBy", nameof(AlertRecord.ReceivedAt));
        Assert.Equal(nameof(AlertRecord.ReceivedAt), fixture.Get<string>("sortColumn"));
        Assert.True(fixture.Get<bool>("sortAscending"));
        Assert.Equal(records.Reverse().Take(10).Select(alert => alert.Name), ResultNames(fixture));
        await fixture.Set("searchText", "Alert 01");
        await fixture.Invoke("ResetPagination");
        Assert.Equal(["Alert 01"], ResultNames(fixture));
        Assert.Contains(fixture.Text.Format("Inbox.ResultsSummary.One", "1-1", 1, 83), fixture.Decoded);
        Assert.DoesNotContain("class=\"inbox-pagination\"", fixture.Html);
        Assert.Equal(original, JsonSerializer.Serialize(records));
        Assert.Null(await fixture.Repository.LoadAsync(Owner));

        void AssertPage(string range, IEnumerable<int> pages, bool previousDisabled, bool nextDisabled)
        {
            Assert.Contains(fixture.Text.Format("Inbox.ResultsSummary.Other", range, 83, 83), fixture.Decoded);
            var pagination = Regex.Match(fixture.Html, "<nav class=\"inbox-pagination\"[^>]*>(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;
            Assert.Contains(fixture.Text["Inbox.PagesLabel"], fixture.Decoded);
            Assert.Equal(pages, Regex.Matches(pagination, ">\\s*(\\d+)\\s*</button>")
                .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
            var buttons = Regex.Matches(pagination, "<button([^>]*)>(.*?)</button>", RegexOptions.Singleline);
            Assert.Equal(previousDisabled, buttons[0].Groups[1].Value.Contains("disabled"));
            Assert.Equal(nextDisabled, buttons[^1].Groups[1].Value.Contains("disabled"));
            Assert.Equal(fixture.Text["Common.Previous"], WebUtility.HtmlDecode(buttons[0].Groups[2].Value).Trim());
            Assert.Equal(fixture.Text["Common.Next"], WebUtility.HtmlDecode(buttons[^1].Groups[2].Value).Trim());
            Assert.Contains($"aria-current=\"page\">{fixture.Get<int>("currentPage")}</button>", pagination);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task CategoryHeadingsTranslateOwnedCountsButPreserveNamesMergingOrderAndCollapseState(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var records = new[] { "Outage member", "Named A", "Named B", "Uncategorized member", "Suppressed member" }
            .Select((name, index) => FilterAlert($"category-{index}", Now.AddMinutes(-index), name)).ToArray();
        const string custom = "Custom <category> & value";
        AlertRule[] rules =
        [
            CategoryRule("Outage member", "System Outage", 90, classification: AlertRuleAnalysisClassifications.SystemOutage),
            CategoryRule("Named A", custom, 10),
            CategoryRule("Named B", custom.ToUpperInvariant(), 20),
            CategoryRule("Suppressed member", "Suppressed alerts", 100, true, AlertRuleAnalysisClassifications.Suppressed)
        ];
        var originalRules = JsonSerializer.Serialize(rules);
        var originalRecords = JsonSerializer.Serialize(records);
        await fixture.Set("alertRules", rules);
        await fixture.Set("alerts", records);
        Assert.Equal(["System Outage", custom, fixture.Text["Inbox.Uncategorized"], "Suppressed alerts"], CategoryNames(fixture));
        Assert.Equal(records.Take(4).Select(alert => alert.Name), ResultNames(fixture));
        Assert.Contains("Custom &lt;category&gt; &amp; value", ResultBody(fixture));
        Assert.Contains(fixture.Text.Plural("Inbox.AlertCount", 1), WebUtility.HtmlDecode(ResultBody(fixture)));
        Assert.Contains(fixture.Text.Plural("Inbox.AlertCount", 2), WebUtility.HtmlDecode(ResultBody(fixture)));
        var evaluator = new AlertRuleEvaluator(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")));
        var groups = InboxCategories.Build(evaluator.Categorize(records, rules).Categories, records);
        var state = fixture.Get<InboxCategoryState>("categoryState");
        var merged = groups.Single(group => group.Name == custom);
        state.Toggle(merged);
        await fixture.Refresh();
        Assert.Equal(["Outage member", "Uncategorized member"], ResultNames(fixture));
        Assert.Contains(fixture.Text.Plural("Inbox.AlertCount", 2), WebUtility.HtmlDecode(ResultBody(fixture)));
        Assert.True(state.IsCollapsed(merged with { Name = custom.ToUpperInvariant() }));
        state.Toggle(merged);
        state.Toggle(groups.Single(group => group.IsSuppressed));
        await fixture.Refresh();
        Assert.Equal(records.Select(alert => alert.Name), ResultNames(fixture));
        await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history" });
        Assert.Empty(CategoryNames(fixture));
        Assert.Equal(records.Select(alert => alert.Name), ResultNames(fixture));
        Assert.Equal(originalRules, JsonSerializer.Serialize(rules));
        Assert.Equal(originalRecords, JsonSerializer.Serialize(records));
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task CategoryCountsAndUncategorizedHeaderReferToTheVisiblePageNotTheWholeMatchingSet(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var records = Enumerable.Range(0, 12).Select(index =>
            FilterAlert($"subset-{index}", Now.AddMinutes(-index), index < 11 ? $"Grouped {index:00}" : "Separate original")).ToArray();
        await fixture.Set("alertRules", new[] { CategoryRule("Grouped", "Original category", 10) });
        await fixture.Set("alerts", records);
        Assert.Equal(["Original category"], CategoryNames(fixture));
        Assert.Equal(10, ResultNames(fixture).Length);
        Assert.Contains(fixture.Text.Plural("Inbox.AlertCount", 10), WebUtility.HtmlDecode(ResultBody(fixture)));
        await fixture.Invoke("ChangePage", 2);
        Assert.Equal(["Original category", fixture.Text["Inbox.Uncategorized"]], CategoryNames(fixture));
        Assert.Equal(records.Skip(10).Select(alert => alert.Name), ResultNames(fixture));
        Assert.Equal(2, Regex.Matches(WebUtility.HtmlDecode(ResultBody(fixture)), Regex.Escape(fixture.Text.Plural("Inbox.AlertCount", 1))).Count);
        Assert.Contains(fixture.Text.Format("Inbox.ResultsSummary.Other", "11-12", 12, 12), fixture.Decoded);
    }

    [Theory]
    [InlineData("en-US", "utc", 0)]
    [InlineData("de-DE", "utc", 0)]
    [InlineData("en-US", "germany", 1)]
    [InlineData("de-DE", "germany", 1)]
    [InlineData("en-US", "singapore", 8)]
    [InlineData("de-DE", "singapore", 8)]
    [InlineData("en-US", "new-york", -5)]
    [InlineData("de-DE", "new-york", -5)]
    public async Task DetailEditorAndVisibleTimestampsTranslateWithoutChangingPayloadUtcInputsOrNeutralPresenterValues(
        string culture, string zone, int offset)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var iso = Now.ToString("O", CultureInfo.InvariantCulture);
        var raw = JsonSerializer.Serialize(new
        {
            searchQuery = "Heartbeat | where Computer == '<original>'",
            queryResult = new
            {
                type = "Heartbeat", columns = new[] { "Computer", "Site", "TimeGenerated", "Status", "DelayMinutes" }.Select(name => new { name }),
                rows = new object[][] { ["Original target", "Original site", iso, "Current", 0] }
            }
        });
        var alert = FilterAlert("Original <id> & value", Now, "Original alert") with
        {
            Comments = "Original <comment> & value", RawJson = raw, QueryResultRefreshedAt = Now
        };
        var original = JsonSerializer.Serialize(alert);
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var presentation = presenter.Parse(raw)!;
        var neutral = JsonSerializer.Serialize(presentation);
        await fixture.Set("alerts", new[] { alert });
        await fixture.Invoke("ChangeTimeZone", new ChangeEventArgs { Value = zone });
        await fixture.Invoke("ToggleDetails", alert);
        var displayed = Now.UtcDateTime.AddHours(offset).ToString(
            culture == "de-DE" ? "dd.MM.yyyy HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Contains($"class=\"timestamp\" title=\"{iso} UTC\">{displayed}", fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.Refreshed", displayed), fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.QueryRefreshed", displayed), fixture.Decoded);
        Assert.Matches(Regex.Escape(fixture.Text["QueryResults.Observed"]) + ":</strong>\\s*" + Regex.Escape(displayed), fixture.Decoded);
        Assert.Contains($"aria-label=\"{fixture.Text["QueryResults.HeartbeatSummary"]}\"", fixture.Decoded);
        Assert.Contains(fixture.Text.Plural("QueryResults.HeartbeatRecords", 1), fixture.Decoded);
        Assert.Contains(fixture.Text["QueryResults.HeartbeatMinutes"], fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.WorkerComments"], fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.SaveComments"], fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.RawPayload"], fixture.Decoded);
        Assert.Contains(fixture.Text.Format("Inbox.AlertId", alert.AlertId), fixture.Decoded);
        Assert.Contains("Original &lt;id&gt; &amp; value", fixture.Html);
        Assert.Contains($"for=\"comment-{alert.Id}\"", fixture.Html);
        Assert.Contains($"id=\"comment-{alert.Id}\" maxlength=\"4000\" rows=\"3\"", fixture.Html);
        Assert.Contains("Original &lt;comment&gt; &amp; value", fixture.Html);
        Assert.Equal(raw, WebUtility.HtmlDecode(Regex.Match(ResultBody(fixture), "<pre>(.*?)</pre>", RegexOptions.Singleline).Groups[1].Value));
        Assert.Contains($"datetime=\"{iso}\"", fixture.Decoded);
        Assert.Contains("type=\"datetime-local\"", fixture.Html);
        Assert.Equal("Original <comment> & value", fixture.Get<string>("commentDraft"));
        var format = typeof(Home).GetMethod("FormatPresentationValue", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(displayed, format.Invoke(fixture.Page, [new QueryResultValue("Original", iso, "DATETIME", "", "")]));
        foreach (var value in new[] { new QueryResultValue("Original", iso, "text", "", ""),
            new QueryResultValue("Original", "invalid original date", "datetime", "", ""),
            new QueryResultValue("Original", "1,234.50", "number", "", "") })
            Assert.Equal(value.Value, format.Invoke(fixture.Page, [value]));
        Assert.Equal(iso, presentation.Rows[0].Metadata.Single(value => value.Format == "datetime").Value);
        Assert.Equal(neutral, JsonSerializer.Serialize(presentation));
        Assert.Equal(neutral, JsonSerializer.Serialize(presenter.Parse(raw)));
        await fixture.Set("isSavingComment", true);
        Assert.Contains(fixture.Text["Common.Saving"], fixture.Decoded);
        var actions = Regex.Match(fixture.Html, "<div class=\"comment-actions\">(.*?)</button>", RegexOptions.Singleline).Value;
        Assert.Contains("disabled", actions);
        await fixture.Set("isSavingComment", false);
        await fixture.Invoke("ToggleDetails", alert);
        Assert.DoesNotContain("class=\"payload-row\"", fixture.Html);
        Assert.Equal(string.Empty, fixture.Get<string>("commentDraft"));
        Assert.Equal(original, JsonSerializer.Serialize(alert));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task CommentSaveKeepsTrimmingActorNeutralLogbookIdempotencyAndClearSemantics(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var alert = FilterAlert("comment-success", Now, "Original alert") with { Comments = "Original previous comment" };
        await using (var context = fixture.CreateContext())
        {
            context.Alerts.Add(alert);
            await context.SaveChangesAsync();
        }
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        await fixture.Invoke("ToggleDetails", alert);
        const string draft = "  Original <comment> & value  ";
        await fixture.Set("commentDraft", draft);
        await fixture.Invoke("SaveCommentsAsync", alert.Id);
        Assert.False(fixture.Get<bool>("isSavingComment"));
        Assert.Equal(string.Empty, fixture.Get<string>("commentSaveError"));
        Assert.Equal(draft, fixture.Get<string>("commentDraft"));
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(draft.Trim(), (await context.Alerts.SingleAsync()).Comments);
            var log = await context.LogbookEntries.SingleAsync();
            Assert.Equal(Owner, log.User);
            Assert.Equal($"Alert comment: {draft.Trim()}\nAlert: Original alert\nTarget: Original target", log.Comment);
            Assert.Equal(alert.RawJson, (await context.Alerts.SingleAsync()).RawJson);
        }
        Assert.Equal(1, fixture.Interceptor.CommentAttempts);
        await fixture.Invoke("SaveCommentsAsync", alert.Id);
        Assert.Equal(1, fixture.Interceptor.CommentAttempts);
        await fixture.Set("commentDraft", "   ");
        await fixture.Invoke("SaveCommentsAsync", alert.Id);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(string.Empty, (await context.Alerts.SingleAsync()).Comments);
            Assert.Single(await context.LogbookEntries.ToArrayAsync());
        }
        Assert.DoesNotContain("class=\"comment-error\"", fixture.Html);
        Assert.Empty(fixture.Log.Entries);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MissingCommentAlertShowsKnownLocalizedFailureWithoutWritingOrDiscardingDraft(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        var alert = FilterAlert("missing-comment-alert", Now, "Original missing alert");
        await fixture.Set("alerts", new[] { alert });
        await fixture.Invoke("ToggleDetails", alert);
        await fixture.Set("commentDraft", "Original unsaved draft");
        await fixture.Invoke("SaveCommentsAsync", alert.Id);
        Assert.Contains($"class=\"comment-error\" role=\"alert\">{fixture.Text["Inbox.CommentAlertMissing"]}", fixture.Decoded);
        Assert.Equal("Original unsaved draft", fixture.Get<string>("commentDraft"));
        Assert.False(fixture.Get<bool>("isSavingComment"));
        Assert.Equal(0, fixture.Interceptor.CommentAttempts);
        await using var context = fixture.CreateContext();
        Assert.Empty(await context.Alerts.ToArrayAsync());
        Assert.Empty(await context.LogbookEntries.ToArrayAsync());
        Assert.Empty(fixture.Log.Entries);
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task CommentFailuresBeforeOrAfterCommitShowUnconfirmedOutcomeLogDetailsAndNeverRetry(
        string culture, bool afterCommit)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var alert = FilterAlert("comment-failure", Now, "Original alert") with { Comments = "Original previous comment" };
        await using (var context = fixture.CreateContext())
        {
            context.Alerts.Add(alert);
            await context.SaveChangesAsync();
        }
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        await fixture.Invoke("ToggleDetails", alert);
        const string draft = "Original attempted <comment> & value";
        await fixture.Set("commentDraft", draft);
        var notifications = 0;
        var failure = new InvalidOperationException("Sensitive subscriber detail after comment commit.");
        void ThrowAfterCommit()
        {
            notifications++;
            throw failure;
        }
        if (afterCommit) fixture.Store.Changed += ThrowAfterCommit;
        else fixture.Interceptor.FailCommentBeforeCommit = true;
        await fixture.Invoke("SaveCommentsAsync", alert.Id);
        Assert.Equal(1, fixture.Interceptor.CommentAttempts);
        Assert.Equal(afterCommit ? 1 : 0, notifications);
        Assert.False(fixture.Get<bool>("isSavingComment"));
        Assert.Equal(draft, fixture.Get<string>("commentDraft"));
        Assert.Contains($"class=\"comment-error\" role=\"alert\">{fixture.Text["Inbox.CommentSaveUnconfirmed"]}", fixture.Decoded);
        Assert.DoesNotContain("Sensitive", fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["Inbox.CommentAlertMissing"], fixture.Decoded);
        Assert.Single(fixture.Log.Entries);
        Assert.Contains("Sensitive", fixture.Log.Entries[0]!.Message);
        if (afterCommit) Assert.Same(failure, fixture.Log.Entries[0]);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(afterCommit ? draft : "Original previous comment", (await context.Alerts.SingleAsync()).Comments);
            Assert.Equal(afterCommit ? 1 : 0, await context.LogbookEntries.CountAsync());
        }
        fixture.Store.Changed -= ThrowAfterCommit;
        fixture.Interceptor.FailCommentBeforeCommit = false;
        await fixture.Set("commentDraft", "Explicit later original comment");
        await fixture.Invoke("SaveCommentsAsync", alert.Id);
        Assert.Equal(2, fixture.Interceptor.CommentAttempts);
        Assert.Equal(string.Empty, fixture.Get<string>("commentSaveError"));
        Assert.DoesNotContain("class=\"comment-error\"", fixture.Html);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal("Explicit later original comment", (await context.Alerts.SingleAsync()).Comments);
            Assert.Equal(afterCommit ? 2 : 1, await context.LogbookEntries.CountAsync());
        }
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    public async Task RcaNoticeProjectionTranslatesOnlyOwnedLabelsAndKeepsNamesLinksUnknownStatusesAndNeutralContracts(
        string culture, int domainCount)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        var formatCulture = CultureInfo.CurrentCulture.Name;
        var domains = new[] { "example.org", "Original <domain> & value" }.Take(domainCount).ToArray();
        OpenRcaNotice[] notices =
        [
            new(Guid.NewGuid(), "Outage", domains, "Open", ""),
            new(Guid.NewGuid(), "Original <category> & value", domains, "InAnalysis", "Unassigned"),
            new(Guid.NewGuid(), "Open RCA", domains, "Actions", "   "),
            new(Guid.NewGuid(), "Unassigned", domains, "Future <status> & value", "  Original <actor> & value  "),
            new(Guid.NewGuid(), "Case-sensitive status", domains, "open", "Original actor")
        ];
        var original = JsonSerializer.Serialize(notices);
        var statuses = JsonSerializer.Serialize(RcaStatuses.All);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        Assert.DoesNotContain("class=\"admin-message admin-message-warning\"", fixture.Html);
        await fixture.Set("openRcaNotices", notices);
        Assert.Contains($"role=\"status\" aria-label=\"{(culture == "de-DE" ? "Offene Ursachenanalysen" : "Open root cause analyses")}\"", fixture.Decoded);
        var rendered = Regex.Matches(fixture.Html, "<a href=\"/rca\\?caseId=([^\"]+)\">(.*?)</a>\\s*<span>(.*?)</span>", RegexOptions.Singleline);
        Assert.Equal(notices.Length, rendered.Count);
        var translatedStatuses = new[] { "Offen", "In Analyse", "Maßnahmen", notices[3].Status, "open" };
        for (var index = 0; index < notices.Length; index++)
        {
            var notice = notices[index];
            var label = culture == "de-DE"
                ? $"Offene RCA {notice.CategoryName}" + (domainCount == 0 ? "" : $" {string.Join(", ", domains)}")
                : notice.Label;
            var status = culture == "de-DE" ? translatedStatuses[index] : notice.StatusLabel;
            var assignee = string.IsNullOrWhiteSpace(notice.AssignedTo)
                ? culture == "de-DE" ? "Nicht zugewiesen" : "Unassigned"
                : notice.AssignedTo;
            var detail = culture == "de-DE"
                ? $" / Status: {status} / Zugewiesen: {assignee}"
                : $" / Status: {status} / Assigned: {assignee}";
            Assert.Equal(notice.Id.ToString(), rendered[index].Groups[1].Value);
            Assert.Equal(label, WebUtility.HtmlDecode(rendered[index].Groups[2].Value));
            Assert.Equal(detail, WebUtility.HtmlDecode(rendered[index].Groups[3].Value));
        }
        Assert.Contains("Original &lt;category&gt; &amp; value", fixture.Html);
        Assert.Contains("Future &lt;status&gt; &amp; value", fixture.Html);
        Assert.Contains("Original &lt;actor&gt; &amp; value", fixture.Html);
        if (domainCount == 2) Assert.Contains("Original &lt;domain&gt; &amp; value", fixture.Html);
        var statusLabel = typeof(Home).GetMethod("RcaStatusLabel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(culture == "de-DE" ? "Geschlossen" : "Closed", statusLabel.Invoke(fixture.Page, [RcaStatuses.Closed]));
        Assert.Equal(original, JsonSerializer.Serialize(notices));
        Assert.Equal(statuses, JsonSerializer.Serialize(RcaStatuses.All));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
        await fixture.Set("openRcaNotices", Array.Empty<OpenRcaNotice>());
        Assert.DoesNotContain("class=\"admin-message admin-message-warning\"", fixture.Html);
        Assert.Empty(fixture.Log.Entries);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RcaNoticesUseRealReadOnlyServiceOrderingAndClosedCaseFilterWithoutChangingPersistedData(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var cases = new[] { "Open", "InAnalysis", "Actions", "Future status", "Closed" }
            .Select((status, index) => new RootCauseAnalysis
            {
                Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CategoryName = $"Original category {index}",
                CreatedAt = Now.AddMinutes(index), FollowUpDueAt = Now.AddMinutes(15 + index),
                IsActive = false, CriticalEndedAt = Now, Status = status, AssignedTo = index == 1 ? "Unassigned" : "",
                Analysis = "Original analysis <value>", EvidenceEventIdsJson = "[]", Revision = 7
            }).ToArray();
        var originals = JsonSerializer.Serialize(cases.OrderBy(rca => rca.Id));
        await using (var context = fixture.CreateContext())
        {
            context.RootCauseAnalyses.AddRange(cases);
            await context.SaveChangesAsync();
        }
        var neutral = await fixture.Rca.GetOpenNoticesAsync();
        var originalNotices = JsonSerializer.Serialize(neutral);
        Assert.Equal(cases.Take(4).Reverse().Select(rca => rca.Id), neutral.Select(notice => notice.Id));
        await fixture.Render<Home>();
        Assert.Equal(originalNotices, JsonSerializer.Serialize(fixture.Get<IReadOnlyList<OpenRcaNotice>>("openRcaNotices")));
        var links = Regex.Matches(fixture.Html, "<a href=\"/rca\\?caseId=([^\"]+)\"");
        Assert.Equal(neutral.Select(notice => notice.Id.ToString()), links.Select(link => link.Groups[1].Value));
        Assert.DoesNotContain($"/rca?caseId={cases[4].Id}", fixture.Html);
        Assert.Contains(culture == "de-DE" ? "Status: In Analyse / Zugewiesen: Unassigned" : "Status: In analysis / Assigned: Unassigned", fixture.Decoded);
        Assert.Contains("Future status", fixture.Decoded);
        Assert.Equal(originalNotices, JsonSerializer.Serialize(await fixture.Rca.GetOpenNoticesAsync()));
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(originals, JsonSerializer.Serialize((await context.RootCauseAnalyses.ToArrayAsync()).OrderBy(rca => rca.Id)));
            Assert.Empty(await context.RcaPrompts.ToArrayAsync());
            Assert.Empty(await context.RcaChanges.ToArrayAsync());
            Assert.Empty(await context.LogbookEntries.ToArrayAsync());
        }
        Assert.Empty(fixture.Log.Entries);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US", "null")]
    [InlineData("de-DE", "null")]
    [InlineData("en-US", "{")]
    [InlineData("de-DE", "{")]
    [InlineData("en-US", "[\"not-a-guid\"]")]
    [InlineData("de-DE", "[\"not-a-guid\"]")]
    public async Task RcaReadErrorsShowSafeLocalizedFeedbackLogOriginalExceptionsRetainStaleNoticesAndRecover(
        string culture, string evidence)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var rca = new RootCauseAnalysis
        {
            Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CategoryName = "Original category",
            CreatedAt = Now, FollowUpDueAt = Now.AddMinutes(15), EvidenceEventIdsJson = evidence
        };
        await using (var context = fixture.CreateContext())
        {
            context.RootCauseAnalyses.Add(rca);
            await context.SaveChangesAsync();
        }
        async Task ChangeEvidence(string value)
        {
            await using var context = fixture.CreateContext();
            (await context.RootCauseAnalyses.SingleAsync()).EvidenceEventIdsJson = value;
            await context.SaveChangesAsync();
        }
        void AssertFailure(int count)
        {
            Assert.Equal(count, fixture.Log.Entries.Count);
            var failure = fixture.Log.Entries[^1]!;
            if (evidence == "null") Assert.Equal(rca.Id, Assert.IsType<RcaEvidenceException>(failure).RcaId);
            else Assert.Equal(rca.Id, Assert.IsType<RcaEvidenceJsonException>(failure).RcaId);
            Assert.Contains($"class=\"admin-message admin-message-error\" role=\"alert\">{fixture.Text["Rca.Errors.EvidenceInvalid"]}", fixture.Decoded);
            Assert.DoesNotContain(failure.Message, fixture.Decoded);
            Assert.DoesNotContain("invalid evidence event IDs", fixture.Decoded);
            Assert.DoesNotContain("not-a-guid", fixture.Decoded);
            Assert.Contains(fixture.Text["Inbox.Title"], fixture.Decoded);
        }
        await fixture.Render<Home>();
        AssertFailure(1);
        Assert.Empty(fixture.Get<IReadOnlyList<OpenRcaNotice>>("openRcaNotices"));
        await ChangeEvidence("[]");
        await fixture.Invoke("RefreshRcaNoticesAsync");
        Assert.Null(fixture.Get<string?>("rcaNoticeError"));
        Assert.DoesNotContain("class=\"admin-message admin-message-error\"", fixture.Html);
        var notices = fixture.Get<IReadOnlyList<OpenRcaNotice>>("openRcaNotices");
        Assert.Equal(rca.Id, Assert.Single(notices).Id);
        Assert.Contains($"/rca?caseId={rca.Id}", fixture.Html);
        var originalNotices = JsonSerializer.Serialize(notices);
        await ChangeEvidence(evidence);
        await fixture.Invoke("RefreshRcaNoticesAsync");
        AssertFailure(2);
        Assert.Same(notices, fixture.Get<IReadOnlyList<OpenRcaNotice>>("openRcaNotices"));
        Assert.Equal(originalNotices, JsonSerializer.Serialize(notices));
        Assert.Contains($"/rca?caseId={rca.Id}", fixture.Html);
        await ChangeEvidence("[]");
        await fixture.Invoke("RefreshRcaNoticesAsync");
        Assert.Null(fixture.Get<string?>("rcaNoticeError"));
        Assert.Equal(originalNotices, JsonSerializer.Serialize(fixture.Get<IReadOnlyList<OpenRcaNotice>>("openRcaNotices")));
        Assert.Equal(2, fixture.Log.Entries.Count);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal("[]", (await context.RootCauseAnalyses.SingleAsync()).EvidenceEventIdsJson);
            Assert.Empty(await context.RcaChanges.ToArrayAsync());
            Assert.Empty(await context.RcaPrompts.ToArrayAsync());
            Assert.Empty(await context.LogbookEntries.ToArrayAsync());
        }
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task CanceledRcaRefreshPreservesPreviousNoticeAndErrorWithoutLoggingNormalShutdown(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        OpenRcaNotice[] notices = [new(Guid.NewGuid(), "Original category", [], "Open", "")];
        await fixture.Set("openRcaNotices", notices);
        await fixture.Set("rcaNoticeError", fixture.Text["Inbox.RcaRefreshFailed"]);
        await fixture.FreezeClock();
        var before = fixture.Html;
        await fixture.Invoke("RefreshRcaNoticesAsync");
        Assert.Same(notices, fixture.Get<IReadOnlyList<OpenRcaNotice>>("openRcaNotices"));
        Assert.Equal(fixture.Text["Inbox.RcaRefreshFailed"], fixture.Get<string>("rcaNoticeError"));
        Assert.Equal(before, fixture.Html);
        Assert.Empty(fixture.Log.Entries);
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("de-DE", 0)]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    public async Task InboxReadFailuresRetainTheCompletePreviousSnapshotAndRecover(string culture, int count)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        await using var fixture = await Fixture.Create();
        var records = Enumerable.Range(0, count).Select(index =>
            FilterAlert($"read-{index}", Now.AddMinutes(-index), $"Original alert {index}")).ToArray();
        var rule = CategoryRule("Original", "Original category", 0);
        rule.IsCritical = true;
        await using (var context = fixture.CreateContext())
        {
            context.Alerts.AddRange(records);
            context.AlertHistory.AddRange(records.Select(AlertHistoryRecord.Create));
            context.AlertRules.Add(rule);
            context.ComputerInventory.Add(new()
            {
                SubscriptionId = "Original subscription", Computer = "Original target",
                Domain = "Original <domain> &", Site = "Original <site> &"
            });
            await context.SaveChangesAsync();
        }
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history-fired" });
        await fixture.Invoke("ReloadAlertsAsync");
        Assert.Empty(fixture.Log.Entries);
        Assert.Equal(count, fixture.Get<IReadOnlyList<AlertRecord>>("alerts").Count);
        Assert.Equal(count, fixture.Get<IReadOnlyList<AlertRecord>>("firedHistoryAlerts").Count);
        Assert.Equal(count, fixture.Get<IReadOnlyList<CriticalAlertLifecycle>>("criticalAlertLifecycles").Count);
        await fixture.Set("commentDraft", "Original unsaved <draft> &");
        await fixture.Set("currentPage", 1);
        var fields = new[] { "alerts", "firedHistoryAlerts", "computerInventory", "alertRules", "criticalAlertLifecycles" };
        var prior = fields.ToDictionary(field => field, fixture.Get<object>);
        var snapshot = JsonSerializer.Serialize(prior);
        var heartbeatAge = fixture.Get<TimeSpan>("currentHeartbeatAge");
        for (var source = 1; source <= 5; source++)
        {
            var failure = new InvalidOperationException($"Sensitive inbox source {source} <detail> &");
            var requests = 0;
            fixture.ReadFactory.OnCreate = () => { if (++requests == source) throw failure; };
            try { await fixture.Invoke("ReloadAlertsAsync"); }
            finally { fixture.ReadFactory.OnCreate = null; }
            Assert.Same(failure, fixture.Log.Entries[^1]);
            Assert.Equal(fixture.Text["Inbox.LoadFailed"], fixture.Get<string>("inboxLoadError"));
            Assert.Contains($"role=\"alert\">{fixture.Text["Inbox.LoadFailed"]}", fixture.Decoded);
            Assert.DoesNotContain(failure.Message, fixture.Decoded);
            foreach (var field in fields) Assert.Same(prior[field], fixture.Get<object>(field));
            Assert.Equal(snapshot, JsonSerializer.Serialize(fields.ToDictionary(field => field, fixture.Get<object>)));
            Assert.Equal(heartbeatAge, fixture.Get<TimeSpan>("currentHeartbeatAge"));
            Assert.Equal("Original unsaved <draft> &", fixture.Get<string>("commentDraft"));
            Assert.Equal("history-fired", fixture.Get<string>("viewMode"));
            Assert.Equal(1, fixture.Get<int>("currentPage"));
        }
        await fixture.Invoke("ReloadAlertsAsync");
        Assert.Null(fixture.Get<string?>("inboxLoadError"));
        Assert.DoesNotContain(fixture.Text["Inbox.LoadFailed"], fixture.Decoded);
        Assert.Equal(count, fixture.Get<IReadOnlyList<AlertRecord>>("alerts").Count);
        Assert.Equal(count, fixture.Get<IReadOnlyList<AlertRecord>>("firedHistoryAlerts").Count);
        Assert.Equal(5, fixture.Log.Entries.Count);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        Assert.Null(await fixture.Repository.LoadAsync(Owner));
        await using var verification = fixture.CreateContext();
        Assert.Equal(records.Select(alert => alert.Id).Order(), (await verification.Alerts.ToArrayAsync()).Select(alert => alert.Id).Order());
        Assert.Empty(await verification.LogbookEntries.ToArrayAsync());
    }

    [Theory]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    [InlineData("en-US", 3)]
    [InlineData("de-DE", 3)]
    [InlineData("en-US", 4)]
    [InlineData("de-DE", 4)]
    public async Task InboxInitialReadFailureKeepsThePageAvailableAndCanRecover(string culture, int source)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var failure = new InvalidOperationException("Sensitive initial inbox <failure> &");
        var requests = 0;
        fixture.ReadFactory.OnCreate = () => { if (++requests == source + 1) throw failure; };
        try { await fixture.Render<Home>(); }
        finally { fixture.ReadFactory.OnCreate = null; }
        Assert.Same(failure, Assert.Single(fixture.Log.Entries));
        Assert.Contains(fixture.Text["Inbox.Title"], fixture.Decoded);
        Assert.Contains(fixture.Text["Inbox.LoadFailed"], fixture.Decoded);
        Assert.DoesNotContain(failure.Message, fixture.Decoded);
        Assert.False(fixture.Get<bool>("isFiredHistoryLoaded"));
        await fixture.FreezeClock();
        await fixture.Invoke("ReloadAlertsAsync");
        Assert.Null(fixture.Get<string?>("inboxLoadError"));
        Assert.DoesNotContain(fixture.Text["Inbox.LoadFailed"], fixture.Decoded);
        Assert.Single(fixture.Log.Entries);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task FailedLazyHistoryLoadRetainsViewAndCanRetryWithoutClearingAnUnrelatedReadError(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var alert = FilterAlert("history-read", Now, "Original history <alert> &");
        await using (var context = fixture.CreateContext())
        {
            context.AlertHistory.Add(AlertHistoryRecord.Create(alert));
            context.Alerts.AddRange(Enumerable.Range(0, 11).Select(index =>
                FilterAlert($"current-history-{index}", Now.AddMinutes(-index), $"Original current alert {index}")));
            await context.SaveChangesAsync();
        }
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        await fixture.Set("viewMode", "history");
        await fixture.Set("currentPage", 2);
        await fixture.Set("commentDraft", "Original unsaved history <draft> &");
        var failure = new InvalidOperationException("Sensitive lazy history <failure> &");
        fixture.ReadFactory.OnCreate = () => throw failure;
        try { await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history-fired" }); }
        finally { fixture.ReadFactory.OnCreate = null; }
        Assert.Same(failure, Assert.Single(fixture.Log.Entries));
        Assert.Equal("history", fixture.Get<string>("viewMode"));
        Assert.Equal(2, fixture.Get<int>("currentPage"));
        Assert.Equal("Original unsaved history <draft> &", fixture.Get<string>("commentDraft"));
        Assert.False(fixture.Get<bool>("isFiredHistoryLoaded"));
        Assert.Contains($"role=\"alert\">{fixture.Text["Inbox.FiredHistoryLoadFailed"]}", fixture.Decoded);
        Assert.DoesNotContain(failure.Message, fixture.Decoded);
        await fixture.Set("inboxLoadError", fixture.Text["Inbox.LoadFailed"]);
        await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history-fired" });
        Assert.True(fixture.Get<bool>("isFiredHistoryLoaded"));
        Assert.Equal("history-fired", fixture.Get<string>("viewMode"));
        Assert.Equal(1, fixture.Get<int>("currentPage"));
        Assert.Equal(alert.Id, Assert.Single(fixture.Get<IReadOnlyList<AlertRecord>>("firedHistoryAlerts")).Id);
        Assert.Null(fixture.Get<string?>("firedHistoryLoadError"));
        Assert.Equal(fixture.Text["Inbox.LoadFailed"], fixture.Get<string>("inboxLoadError"));
        Assert.Single(fixture.Log.Entries);
        await fixture.Invoke("ReloadAlertsAsync");
        Assert.Null(fixture.Get<string?>("inboxLoadError"));
    }

    [Theory]
    [InlineData("en-US", 1)]
    [InlineData("de-DE", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 2)]
    [InlineData("en-US", 3)]
    [InlineData("de-DE", 3)]
    [InlineData("en-US", 4)]
    [InlineData("de-DE", 4)]
    [InlineData("en-US", 5)]
    [InlineData("de-DE", 5)]
    public async Task ConfirmedCommentSaveAndNotificationReadFailureRemainSeparate(string culture, int source)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var alert = FilterAlert("comment-read", Now, "Original alert") with { Comments = "Original comment" };
        await using (var context = fixture.CreateContext())
        {
            context.Alerts.Add(alert);
            await context.SaveChangesAsync();
        }
        var failure = new InvalidOperationException("Sensitive notification read <failure> &");
        void FailNotificationRead()
        {
            var requests = 0;
            fixture.ReadFactory.OnCreate = () => { if (++requests == source) throw failure; };
        }
        fixture.Store.Changed += FailNotificationRead;
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        await fixture.Invoke("ChangeViewModeAsync", new ChangeEventArgs { Value = "history-fired" });
        await fixture.Invoke("ToggleDetails", alert);
        await fixture.Set("commentDraft", "  Confirmed <comment> &  ");
        try { await fixture.Invoke("SaveCommentsAsync", alert.Id); }
        finally
        {
            fixture.ReadFactory.OnCreate = null;
            fixture.Store.Changed -= FailNotificationRead;
        }
        Assert.Equal(1, fixture.Interceptor.CommentAttempts);
        Assert.False(fixture.Get<bool>("isSavingComment"));
        Assert.Equal("", fixture.Get<string>("commentSaveError"));
        Assert.Equal(fixture.Text["Inbox.LoadFailed"], fixture.Get<string>("inboxLoadError"));
        Assert.Same(failure, Assert.Single(fixture.Log.Entries));
        Assert.DoesNotContain(failure.Message, fixture.Decoded);
        Assert.Equal("Original comment", Assert.Single(fixture.Get<IReadOnlyList<AlertRecord>>("alerts")).Comments);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal("Confirmed <comment> &", (await context.Alerts.SingleAsync()).Comments);
            var entry = Assert.Single(await context.LogbookEntries.ToArrayAsync());
            Assert.Equal(Owner, entry.User);
            Assert.Contains("Confirmed <comment> &", entry.Comment);
        }
        await fixture.Invoke("ReloadAlertsAsync");
        Assert.Null(fixture.Get<string?>("inboxLoadError"));
        Assert.Equal("Confirmed <comment> &", Assert.Single(fixture.Get<IReadOnlyList<AlertRecord>>("alerts")).Comments);
        Assert.Equal(1, fixture.Interceptor.CommentAttempts);
    }

    [Fact]
    public async Task RequiredCriticalLifecycleReadsPreserveLegacyDataAndFailureContracts()
    {
        await using var fixture = await Fixture.Create();
        var alert = FilterAlert("critical-read", Now.AddMinutes(-1), "Original critical alert");
        var rule = CategoryRule("Original", "Original critical category", 0);
        rule.IsCritical = true;
        await using (var context = fixture.CreateContext())
        {
            context.Alerts.Add(alert);
            context.AlertRules.Add(rule);
            await context.SaveChangesAsync();
        }
        var legacy = await fixture.Store.GetCriticalAlertLifecyclesAsync(Now.AddDays(-7), Now);
        Assert.Equal(alert.AlertId, Assert.Single(legacy).AlertId);
        var required = await fixture.Store.GetCriticalAlertLifecyclesRequiredAsync(Now.AddDays(-7), Now);
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(required));
        await using (var context = fixture.CreateContext())
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertRules\" RENAME TO \"UnavailableRules\";");
        try
        {
            Assert.Empty(await fixture.Store.GetCriticalAlertLifecyclesAsync(Now.AddDays(-7), Now));
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Store.GetCriticalAlertLifecyclesRequiredAsync(Now.AddDays(-7), Now));
        }
        finally
        {
            await using var context = fixture.CreateContext();
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableRules\" RENAME TO \"AlertRules\";");
        }
        Assert.Equal(JsonSerializer.Serialize(legacy),
            JsonSerializer.Serialize(await fixture.Store.GetCriticalAlertLifecyclesRequiredAsync(Now.AddDays(-7), Now)));
        var presenter = new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions"));
        var configuration = DatabaseConfigurationStatus.Parse("", requireManagedIdentity: false);
        var invalidStore = new AlertStore(fixture.ReadFactory, configuration, presenter, new(presenter),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertStore>.Instance);
        var requests = 0;
        fixture.ReadFactory.OnCreate = () => requests++;
        try
        {
            Assert.Empty(await invalidStore.GetCriticalAlertLifecyclesAsync(Now.AddDays(-7), Now));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                invalidStore.GetCriticalAlertLifecyclesRequiredAsync(Now.AddDays(-7), Now));
            Assert.Equal(configuration.Error, failure.Message);
            Assert.Equal(0, requests);
        }
        finally { fixture.ReadFactory.OnCreate = null; }
    }

    [Theory]
    [InlineData("en-US", "database")]
    [InlineData("de-DE", "database")]
    [InlineData("en-US", "{")]
    [InlineData("de-DE", "{")]
    [InlineData("en-US", "null")]
    [InlineData("de-DE", "null")]
    [InlineData("en-US", "[]")]
    [InlineData("de-DE", "[]")]
    public async Task WorkspaceLoadFailureShowsSafeFeedbackProtectsTheStoredLayoutAndCanRetry(string culture, string source)
    {
        using var selection = new UiSelection(culture, "fr-FR");
        await using var fixture = await Fixture.Create();
        var document = InboxDashboardCatalog.CreateDefault();
        document.Name = "Original <workspace> &";
        document.Widgets[0].Title = OriginalTitle;
        document.Version = 1;
        await fixture.Repository.SaveAsync(Owner, document);
        var validJson = await fixture.StoredJson();
        await using (var context = fixture.CreateContext())
        {
            var stored = await context.InboxLayouts.SingleAsync();
            stored.Revision = 7;
            if (source != "database") stored.DocumentJson = source;
            await context.SaveChangesAsync();
        }
        async Task<string> Stamp()
        {
            await using var context = fixture.CreateContext();
            return JsonSerializer.Serialize(await context.InboxLayouts.AsNoTracking().SingleAsync());
        }
        var original = await Stamp();
        var attempts = fixture.Interceptor.Attempts;
        var failure = new InvalidOperationException("Sensitive workspace database <failure> &");
        if (source == "database")
        {
            var requests = 0;
            fixture.ReadFactory.OnCreate = () => { if (++requests == 1) throw failure; };
        }
        try { await fixture.Render<Home>(); }
        finally { fixture.ReadFactory.OnCreate = null; }
        await fixture.FreezeClock();
        var logged = Assert.Single(fixture.Log.Entries)!;
        if (source == "database") Assert.Same(failure, logged);
        else if (source == "null") Assert.IsType<InvalidOperationException>(logged);
        else Assert.IsAssignableFrom<JsonException>(logged);
        Assert.True(fixture.Get<bool>("dashboardLoadFailed"));
        Assert.False(fixture.Get<bool>("dashboardSaveFailed"));
        Assert.False(fixture.Get<bool>("isLoadingDashboard"));
        Assert.False(fixture.Get<bool>("isDashboardEditEnabled"));
        Assert.Contains(fixture.Text["Inbox.WorkspaceLoadFailed"], fixture.Decoded);
        Assert.Contains(fixture.Text["Common.Refresh"], fixture.Decoded);
        Assert.DoesNotContain(logged.Message, fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["Inbox.WorkspaceSaveUnconfirmed"], fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["Inbox.WorkspaceReady"], fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["Inbox.WorkspaceRestored"], fixture.Decoded);
        Assert.Contains("disabled", Regex.Match(fixture.Html, "<input type=\"checkbox\"[^>]*>").Value);
        var displayed = JsonSerializer.Serialize(fixture.Dashboard.Document);
        await fixture.Invoke("ToggleDashboardGridAsync");
        await fixture.Invoke("ChangeDashboardRowsAsync", new ChangeEventArgs { Value = "30" });
        await fixture.Invoke("ResetDashboardAsync");
        await fixture.Invoke("CommitDashboardMove", fixture.Dashboard.Document.Widgets[0].Id, 2, 5);
        Assert.Equal(displayed, JsonSerializer.Serialize(fixture.Dashboard.Document));
        await fixture.Invoke("SaveDashboardAsync", "Inbox.GridSaved");
        Assert.Equal(attempts, fixture.Interceptor.Attempts);
        Assert.Equal(original, await Stamp());
        Assert.True(fixture.Get<bool>("dashboardLoadFailed"));
        Assert.Equal(fixture.Text["Inbox.WorkspaceLoadFailed"], fixture.Get<string>("dashboardStatus"));
        await fixture.Set("inboxLoadError", fixture.Text["Inbox.LoadFailed"]);
        if (source != "database")
        {
            await using var context = fixture.CreateContext();
            (await context.InboxLayouts.SingleAsync()).DocumentJson = validJson;
            await context.SaveChangesAsync();
        }
        var repaired = await Stamp();
        await fixture.Invoke("LoadDashboardAsync");
        Assert.False(fixture.Get<bool>("dashboardLoadFailed"));
        Assert.False(fixture.Get<bool>("isLoadingDashboard"));
        Assert.Contains(fixture.Text["Inbox.WorkspaceRestored"], fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["Inbox.WorkspaceLoadFailed"], fixture.Decoded);
        Assert.Equal(fixture.Text["Inbox.LoadFailed"], fixture.Get<string>("inboxLoadError"));
        Assert.Equal(document.Name, fixture.Dashboard.Document.Name);
        Assert.Contains("Original &lt;widget&gt; &amp; title", fixture.Html);
        Assert.Equal(repaired, await Stamp());
        await fixture.Invoke("ToggleDashboardGridAsync");
        Assert.NotNull(await fixture.Repository.LoadRequiredAsync(Owner));
        await using (var context = fixture.CreateContext())
            Assert.Equal(8, (await context.InboxLayouts.SingleAsync()).Revision);
        Assert.False(fixture.Get<bool>("dashboardSaveFailed"));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MissingWorkspaceIsReadyAndCanSaveWithoutAFalseLoadError(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        Assert.False(fixture.Get<bool>("dashboardLoadFailed"));
        Assert.False(fixture.Get<bool>("isLoadingDashboard"));
        Assert.Contains(fixture.Text["Inbox.WorkspaceReady"], fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["Inbox.WorkspaceLoadFailed"], fixture.Decoded);
        Assert.Null(await fixture.Repository.LoadRequiredAsync(Owner));
        await fixture.Invoke("ToggleDashboardGridAsync");
        Assert.Equal(1, fixture.Interceptor.Attempts);
        Assert.NotNull(await fixture.Repository.LoadRequiredAsync(Owner));
        Assert.Empty(fixture.Log.Entries);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PendingWorkspaceRetryDisablesControlsAndBlocksDuplicateReadsAndWrites(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        var document = InboxDashboardCatalog.CreateDefault();
        document.Name = "Original recovered workspace";
        await fixture.Repository.SaveAsync(Owner, document);
        var original = await fixture.StoredJson();
        var requests = 0;
        fixture.ReadFactory.OnCreate = () => { if (++requests == 1) throw new InvalidOperationException("Initial workspace failure."); };
        try { await fixture.Render<Home>(); }
        finally { fixture.ReadFactory.OnCreate = null; }
        await fixture.FreezeClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<AlertDbContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.ReadFactory.OnCreateAsync = _ =>
        {
            reads++;
            started.TrySetResult();
            return pending.Task;
        };
        var loading = fixture.Invoke("LoadDashboardAsync");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(fixture.Get<bool>("isLoadingDashboard"));
            await fixture.Refresh();
            var refreshButton = Regex.Match(fixture.Html,
                $"<button type=\"button\"[^>]*>{Regex.Escape(fixture.Text["Common.Refresh"])}</button>").Value;
            Assert.Contains("disabled", refreshButton);
            await fixture.Invoke("LoadDashboardAsync");
            await fixture.Invoke("ToggleDashboardGridAsync");
            await fixture.Invoke("SaveDashboardAsync", "Inbox.GridSaved");
            Assert.Equal(1, reads);
            Assert.Equal(1, fixture.Interceptor.Attempts);
            Assert.Equal(original, await fixture.StoredJson());
        }
        finally
        {
            fixture.ReadFactory.OnCreateAsync = null;
            pending.TrySetResult(fixture.CreateContext());
            await loading;
        }
        Assert.False(fixture.Get<bool>("isLoadingDashboard"));
        Assert.False(fixture.Get<bool>("dashboardLoadFailed"));
        Assert.Equal(document.Name, fixture.Dashboard.Document.Name);
        Assert.Equal(original, await fixture.StoredJson());
    }

    private static AlertRule CategoryRule(string match, string name, int priority, bool collapsed = false,
        string classification = AlertRuleAnalysisClassifications.None) => new()
    {
        Id = Guid.NewGuid(), Name = "Original rule", AlertNameContains = match,
        CategoryName = name, Priority = priority, Collapsed = collapsed,
        ConditionType = AlertRuleConditionTypes.AnyMatchingAlert, AnalysisClassification = classification
    };

    private static string ResultBody(Fixture fixture) =>
        Regex.Match(fixture.Html, "<table class=\"alert-table\">.*?<tbody>(.*?)</tbody>", RegexOptions.Singleline).Groups[1].Value;

    private static string[] ResultNames(Fixture fixture) => Regex.Matches(ResultBody(fixture),
        "class=\"alert-name-button[^>]*>\\s*<span[^>]*>\\s*<span>([^<]*)</span>", RegexOptions.Singleline)
        .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();

    private static string[] CategoryNames(Fixture fixture) => Regex.Matches(ResultBody(fixture),
        "<tr class=\"alert-(?:category|uncategorized)-row[^>]*>.*?<strong>(.*?)</strong>", RegexOptions.Singleline)
        .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();

    private static AlertRecord FilterAlert(string id, DateTimeOffset received, string name) =>
        TestAlertFactory.FromFixture(new AlertRecordFixtureCase
        {
            AlertId = id, Target = "Original target", Name = name, MonitorCondition = "Fired",
            Status = "Fired", ReceivedAt = received, FiredAt = received, Severity = "Sev0", SubscriptionId = "Original subscription"
        });

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
    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Owner), new Claim(ClaimTypes.Role, "Reader")], "test"))));
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
    }
    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task InboxBrowserFailuresPreserveActualDataLayoutAndWritesAndRecoverOnlyOnExplicitRetry(string culture, bool importing)
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
            var browser = new BrowserJavascript();
            await using var fixture = await CreateBrowserFixture(browser);
            var document = JsonSerializer.Serialize(fixture.Dashboard.Document);
            var stored = await fixture.StoredJson();
            var inventory = fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory");
            var writes = fixture.Interceptor.Attempts;
            if (importing) browser.ImportFailure = failure;
            else browser.InitializeFailure = failure;
            await fixture.Invoke("OnAfterRenderAsync", true);
            Assert.Contains(fixture.Text[importing ? "Inbox.Browser.ImportFailed" : "Inbox.Browser.InitializeFailed"], fixture.Decoded);
            Assert.Contains(fixture.Text["Inbox.Browser.Retry"], fixture.Decoded);
            Assert.Contains("Original &lt;widget&gt; &amp; title", fixture.Html);
            Assert.DoesNotContain("PRIVATE_BROWSER_DETAIL", fixture.Html);
            Assert.Contains(fixture.Log.Entries, entry => ReferenceEquals(entry, failure));
            Assert.Same(inventory, fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory"));
            await AssertBrowserCallbacksBlocked(fixture, document, writes);
            Assert.Equal(stored, await fixture.StoredJson());
            var calls = browser.Calls.Count;
            await fixture.Invoke("OnAfterRenderAsync", false);
            Assert.Equal(calls, browser.Calls.Count);
            var clock = fixture.Get<Task>("clockTask");
            browser.ImportFailure = browser.InitializeFailure = null;
            await fixture.Invoke("RetryDashboardBrowserAsync");
            Assert.Null(fixture.Property<string?>("DashboardBrowserError"));
            Assert.True(fixture.Property<bool>("CanUseDashboardBrowser"));
            Assert.Same(clock, fixture.Get<Task>("clockTask"));
            Assert.Equal(document, JsonSerializer.Serialize(fixture.Dashboard.Document));
            Assert.Equal(stored, await fixture.StoredJson());
            Assert.Equal(writes, fixture.Interceptor.Attempts);
            Assert.False(browser.DuplicateHandlers);
            var module = Assert.Single(browser.Modules, module => module.HandlersActive);
            Assert.Same(fixture.Page, module.Reference!.Value);
            Assert.IsType<ElementReference>(module.Root);
            Assert.All(browser.Calls.Where(call => call.Name == "import"),
                call => Assert.Equal("./js/inbox-dashboard.js", Assert.Single(call.Arguments)));
            Assert.Equal(5, Regex.Matches(fixture.Html, "data-inbox-drag-handle").Count);
            Assert.Equal(5, Regex.Matches(fixture.Html, "data-inbox-resize-handle").Count);
        }
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("de-DE", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", true)]
    public async Task InboxBrowserCleanupFailurePreventsDuplicateInitializationAndRetriesCompletedPhasesSafely(string culture, bool moduleRelease)
    {
        using var selection = new UiSelection(culture);
        foreach (var afterRelease in moduleRelease ? new[] { false, true } : new[] { false })
        {
            var browser = new BrowserJavascript { InitializeFailure = new JSException("PRIVATE_INIT_DETAIL") };
            await using var fixture = await CreateBrowserFixture(browser);
            await fixture.Invoke("OnAfterRenderAsync", true);
            var original = JsonSerializer.Serialize(fixture.Dashboard.Document);
            var module = Assert.Single(browser.Modules);
            var reference = module.Reference!;
            var failure = new InvalidOperationException("PRIVATE_CLEANUP_DETAIL");
            browser.InitializeFailure = null;
            if (moduleRelease) browser.ReleaseFailure = failure;
            else browser.CleanupFailure = failure;
            browser.ReleaseThenThrow = afterRelease;
            await fixture.Invoke("RetryDashboardBrowserAsync");
            Assert.Contains(fixture.Text["Inbox.Browser.CleanupFailed"], fixture.Decoded);
            Assert.Contains(fixture.Log.Entries, entry => ReferenceEquals(entry, failure));
            Assert.Single(browser.Modules);
            Assert.Same(fixture.Page, reference.Value);
            await AssertBrowserCallbacksBlocked(fixture, original, fixture.Interceptor.Attempts);
            var handlerCleanups = browser.Calls.Count(call => call.Name == "dispose");
            browser.ReleaseFailure = browser.CleanupFailure = null;
            await fixture.Invoke("RetryDashboardBrowserAsync");
            Assert.Null(fixture.Property<string?>("DashboardBrowserError"));
            Assert.True(module.Released);
            Assert.Throws<ObjectDisposedException>(() => reference.Value);
            Assert.False(browser.DuplicateHandlers);
            Assert.Single(browser.Modules, item => item.HandlersActive);
            if (moduleRelease) Assert.Equal(handlerCleanups, browser.Calls.Count(call => call.Name == "dispose"));
        }
    }

    [Theory]
    [InlineData("en-US", "import")]
    [InlineData("de-DE", "import")]
    [InlineData("en-US", "initialize")]
    [InlineData("de-DE", "initialize")]
    public async Task BusyInboxBrowserSetupBlocksCallbacksButManualDesignerEditsRemainAvailable(string culture, string operation)
    {
        using var selection = new UiSelection(culture);
        var browser = new BrowserJavascript { BlockOperation = operation };
        await using var fixture = await CreateBrowserFixture(browser);
        var document = JsonSerializer.Serialize(fixture.Dashboard.Document);
        var writes = fixture.Interceptor.Attempts;
        var pending = fixture.Invoke("OnAfterRenderAsync", true);
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var calls = browser.Calls.Count;
            await fixture.Invoke("RetryDashboardBrowserAsync");
            await fixture.Invoke("OnAfterRenderAsync", false);
            await AssertBrowserCallbacksBlocked(fixture, document, writes);
            Assert.Equal(calls, browser.Calls.Count);
            Assert.True(fixture.Property<bool>("IsInitializingBrowser"));
            await fixture.Invoke("ChangeDashboardRowHeightAsync", new ChangeEventArgs { Value = "120" });
            Assert.Equal(120, (await fixture.Repository.LoadAsync(Owner))!.RowHeight);
        }
        finally
        {
            browser.Release.SetResult();
            await pending;
        }
        Assert.Equal(120, fixture.Dashboard.Document.RowHeight);
        Assert.Equal(OriginalTitle, fixture.Dashboard.Document.Widgets[1].Title);
        Assert.True(fixture.Property<bool>("CanUseDashboardBrowser"));
        Assert.Single(browser.Modules);
        var completedCalls = browser.Calls.Count;
        await fixture.Invoke("OnAfterRenderAsync", false);
        Assert.Equal(completedCalls, browser.Calls.Count);
    }

    [Theory]
    [InlineData("en-US", "import")]
    [InlineData("de-DE", "import")]
    [InlineData("en-US", "initialize")]
    [InlineData("de-DE", "initialize")]
    public async Task InboxDisposalCancelsBrowserSetupAndDisposesPartialModuleAndCallbackOnce(string culture, string operation)
    {
        using var selection = new UiSelection(culture);
        var browser = new BrowserJavascript { BlockOperation = operation };
        await using var fixture = await CreateBrowserFixture(browser);
        var pending = fixture.Invoke("OnAfterRenderAsync", true);
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reference = browser.Modules.SingleOrDefault()?.Reference;
        await fixture.DisposePage();
        await pending;
        Assert.Null(fixture.Property<string?>("DashboardBrowserError"));
        Assert.Empty(fixture.Log.Entries);
        Assert.All(browser.Modules, module => Assert.True(module.Released));
        if (reference is not null) Assert.Throws<ObjectDisposedException>(() => reference.Value);
        var calls = browser.Calls.Count;
        await fixture.Invoke("OnAfterRenderAsync", false);
        await fixture.Invoke("RetryDashboardBrowserAsync");
        await fixture.DisposePage();
        Assert.Equal(calls, browser.Calls.Count);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task LateInboxImportAfterPageCancellationIsReleasedWithoutInitializingNewHandlers(string culture)
    {
        using var selection = new UiSelection(culture);
        var browser = new BrowserJavascript { BlockOperation = "import", IgnoreCancellation = true };
        await using var fixture = await CreateBrowserFixture(browser);
        var pending = fixture.Invoke("OnAfterRenderAsync", true);
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposing = fixture.DisposePage();
        browser.Release.SetResult();
        await Task.WhenAll(pending, disposing);
        var module = Assert.Single(browser.Modules);
        Assert.True(module.Released);
        Assert.Null(module.Reference);
        Assert.DoesNotContain(browser.Calls, call => call.Name == "initialize");
        Assert.Null(fixture.Property<string?>("DashboardBrowserError"));
        Assert.Empty(fixture.Log.Entries);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task InboxTeardownAttemptsEveryReleaseDespiteErrorsAndIgnoresExpectedDisconnect(string culture)
    {
        using var selection = new UiSelection(culture);
        foreach (var disconnected in new[] { false, true })
        {
            var browser = new BrowserJavascript();
            await using var fixture = await CreateBrowserFixture(browser);
            await fixture.Invoke("OnAfterRenderAsync", true);
            var reference = Assert.Single(browser.Modules).Reference!;
            var failure = disconnected ? (Exception)new JSDisconnectedException("PRIVATE_CLEANUP_DETAIL")
                : new InvalidOperationException("PRIVATE_CLEANUP_DETAIL");
            browser.CleanupFailure = browser.ReleaseFailure = failure;
            await fixture.DisposePage();
            Assert.Contains(browser.Calls, call => call.Name == "module-release");
            Assert.Throws<ObjectDisposedException>(() => reference.Value);
            if (disconnected) Assert.Empty(fixture.Log.Entries);
            else Assert.Equal(2, fixture.Log.Entries.Count(entry => ReferenceEquals(entry, failure)));
            Assert.DoesNotContain("PRIVATE_CLEANUP_DETAIL", fixture.Html);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task InboxHeartbeatRefreshContinuesOnRealClockWhileBrowserSetupHasFailed(string culture)
    {
        using var selection = new UiSelection(culture);
        var browser = new BrowserJavascript { InitializeFailure = new JSException("PRIVATE_BROWSER_DETAIL") };
        await using var fixture = await CreateBrowserFixture(browser);
        await fixture.Invoke("OnAfterRenderAsync", true);
        var stored = await fixture.StoredJson();
        var clock = fixture.Get<Task>("clockTask");
        var fresh = DateTimeOffset.UtcNow;
        await using (var context = fixture.CreateContext())
            await context.ComputerInventory.ExecuteUpdateAsync(update => update.SetProperty(entry => entry.LastHeartbeatAt, fresh));
        await fixture.Set("lastInboxDataRefresh", fresh.AddMinutes(-1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory").Any(entry => entry.LastHeartbeatAt == fresh))
            await Task.Delay(10, timeout.Token);
        Assert.Contains(fixture.Text["Inbox.Browser.InitializeFailed"], fixture.Decoded);
        Assert.Same(clock, fixture.Get<Task>("clockTask"));
        Assert.True(fixture.Get<DateTimeOffset>("lastInboxDataRefresh") >= fresh);
        Assert.Equal(stored, await fixture.StoredJson());
        Assert.Single(browser.Calls, call => call.Name == "initialize");
        Assert.Null(fixture.Get<string?>("inboxLoadError"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task NullInboxBrowserModuleIsAnExplicitImportFailureAndDoesNotProduceSuccess(string culture)
    {
        using var selection = new UiSelection(culture);
        var browser = new BrowserJavascript { ReturnNullModule = true };
        await using var fixture = await CreateBrowserFixture(browser);
        await fixture.Invoke("OnAfterRenderAsync", true);
        Assert.Contains(fixture.Text["Inbox.Browser.ImportFailed"], fixture.Decoded);
        Assert.Contains(fixture.Log.Entries, entry => entry is InvalidOperationException);
        Assert.False(fixture.Property<bool>("CanUseDashboardBrowser"));
        Assert.DoesNotContain(browser.Calls, call => call.Name == "initialize");
    }

    private static async Task<Fixture> CreateBrowserFixture(BrowserJavascript browser)
    {
        var fixture = await Fixture.Create(javascript: browser);
        var document = InboxDashboardCatalog.CreateDefault();
        document.Widgets[1].Title = OriginalTitle;
        await fixture.Repository.SaveAsync(Owner, document);
        await using (var context = fixture.CreateContext())
        {
            context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "DC01", LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1) });
            await context.SaveChangesAsync();
        }
        await fixture.Render<Home>();
        await fixture.Set("isDashboardEditEnabled", true);
        return fixture;
    }

    private static async Task AssertBrowserCallbacksBlocked(Fixture fixture, string document, int writes)
    {
        var widget = fixture.Dashboard.Document.Widgets[0];
        await fixture.Invoke("CommitDashboardMove", widget.Id, 2, 2);
        await fixture.Invoke("CommitDashboardResize", widget.Id, 2, 2);
        Assert.Equal(document, JsonSerializer.Serialize(fixture.Dashboard.Document));
        Assert.Equal(writes, fixture.Interceptor.Attempts);
        Assert.DoesNotContain("data-inbox-drag-handle", fixture.Html);
        Assert.DoesNotContain("data-inbox-resize-handle", fixture.Html);
    }

    private sealed class BrowserJavascript : IJSRuntime
    {
        public List<(string Name, object?[] Arguments)> Calls { get; } = [];
        public List<BrowserModule> Modules { get; } = [];
        public Exception? ImportFailure { get; set; }
        public Exception? InitializeFailure { get; set; }
        public Exception? CleanupFailure { get; set; }
        public Exception? ReleaseFailure { get; set; }
        public bool ReturnNullModule { get; set; }
        public bool ReleaseThenThrow { get; set; }
        public bool DuplicateHandlers { get; set; }
        public string? BlockOperation { get; set; }
        public bool IgnoreCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(string operation, CancellationToken cancellationToken)
        {
            if (BlockOperation != operation) return;
            BlockOperation = null;
            Started.SetResult();
            await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            Calls.Add((identifier, args ?? []));
            await WaitAsync(identifier, cancellationToken);
            if (ImportFailure is { } failure) throw failure;
            if (ReturnNullModule) return default!;
            var module = new BrowserModule(this);
            Modules.Add(module);
            return Assert.IsAssignableFrom<T>(module);
        }
    }

    private sealed class BrowserModule(BrowserJavascript browser) : IJSObjectReference
    {
        public bool HandlersActive { get; private set; }
        public bool Released { get; private set; }
        public DotNetObjectReference<Home>? Reference { get; private set; }
        public object? Root { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            browser.Calls.Add((identifier, args ?? []));
            if (identifier == "initialize")
            {
                browser.DuplicateHandlers |= browser.Modules.Any(module => module.HandlersActive);
                Reference = Assert.IsType<DotNetObjectReference<Home>>(args![0]);
                Root = args[1];
                HandlersActive = true;
                await browser.WaitAsync(identifier, cancellationToken);
                if (browser.InitializeFailure is { } failure) throw failure;
            }
            else
            {
                Assert.Equal("dispose", identifier);
                Assert.Equal(Root, Assert.Single(args!));
                if (browser.CleanupFailure is { } failure) throw failure;
                HandlersActive = false;
            }
            return default!;
        }
        public ValueTask DisposeAsync()
        {
            browser.Calls.Add(("module-release", []));
            if (Released) return ValueTask.CompletedTask;
            if (browser.ReleaseThenThrow) Released = true;
            if (browser.ReleaseFailure is { } failure) throw failure;
            Released = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnusedJavascript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("Static render must not invoke JavaScript.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
    private sealed class SaveInterceptor : SaveChangesInterceptor
    {
        public bool FailBefore { get; set; }
        public bool FailAfter { get; set; }
        public int Attempts { get; private set; }
        public bool FailCommentBeforeCommit { get; set; }
        public int CommentAttempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (data.Context!.ChangeTracker.Entries<LogbookEntry>().Any(entry => entry.State == EntityState.Added))
            {
                CommentAttempts++;
                if (FailCommentBeforeCommit) throw new InvalidOperationException("Sensitive database detail before comment commit.");
            }
            if (data.Context!.ChangeTracker.Entries<InboxLayout>().Any(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                Attempts++;
                if (FailBefore) throw new InvalidOperationException("Sensitive database detail before save.");
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default) =>
            FailAfter && data.Context!.ChangeTracker.Entries<InboxLayout>().Any()
                ? throw new InvalidOperationException("Sensitive database detail after commit.")
                : ValueTask.FromResult(result);
    }
    private sealed class HomeLog : ILogger<Home>
    {
        public List<Exception?> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add(exception);
    }
    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public Action? OnCreate { get; set; }
        public Func<CancellationToken, Task<AlertDbContext>>? OnCreateAsync { get; set; }
        public Task<AlertDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            OnCreateAsync?.Invoke(cancellationToken) ?? Task.FromResult(CreateDbContext());
        public AlertDbContext CreateDbContext()
        {
            OnCreate?.Invoke();
            return new(options);
        }
    }
    public sealed class InboxTimestampModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AlertRecord>().Property(alert => alert.ReceivedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            modelBuilder.Entity<AlertHistoryRecord>().Property(alert => alert.ReceivedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            var nullableTimestamp = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : (DateTimeOffset?)null);
            modelBuilder.Entity<ParsedAlertRecord>().Property(alert => alert.FiredDateTime).HasConversion(nullableTimestamp);
            modelBuilder.Entity<ParsedAlertRecord>().Property(alert => alert.ResolvedAt).HasConversion(nullableTimestamp);
        }
    }
    private sealed class Activator(IServiceProvider services) : IComponentActivator
    {
        public Home Page { get; private set; } = default!;
        public InboxWidgetActions Actions { get; private set; } = default!;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is Home page) Page = page;
            if (component is InboxWidgetActions actions) Actions = actions;
            return component;
        }
    }
    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type type, int? parentId, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type)
                : throw new NotSupportedException($"Unsupported test render mode: {mode.GetType().Name}");
    }
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task PollingRefreshesHeartbeatWithoutStoreNotificationAndRetainsLayoutAndFilters(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = await Fixture.Create();
        await using (var context = fixture.CreateContext())
        {
            context.ComputerInventory.Add(new() { SubscriptionId = "sub", Computer = "DC01", LastHeartbeatAt = Now.AddHours(-1) });
            await context.SaveChangesAsync();
        }
        await fixture.Render<Home>();
        await fixture.FreezeClock();
        await fixture.Set("lastInboxDataRefresh", Now);
        await fixture.Set("domainFilter", "Original filter");
        var document = JsonSerializer.Serialize(fixture.Dashboard.Document);
        var attempts = fixture.Interceptor.Attempts;
        await using (var context = fixture.CreateContext())
            await context.ComputerInventory.ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.LastHeartbeatAt, Now));
        await fixture.Set("currentTimeUtc", Now.AddSeconds(29));
        await fixture.Invoke("RefreshLiveDataAsync");
        Assert.Equal(Now.AddHours(-1), Assert.Single(fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory")).LastHeartbeatAt);
        await fixture.Set("currentTimeUtc", Now.AddSeconds(30));
        await fixture.Invoke("RefreshLiveDataAsync");
        Assert.Equal(Now, Assert.Single(fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory")).LastHeartbeatAt);
        Assert.Equal("Original filter", fixture.Get<string>("domainFilter"));
        Assert.Equal(document, JsonSerializer.Serialize(fixture.Dashboard.Document));
        Assert.Equal(attempts, fixture.Interceptor.Attempts);
        var cached = fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory");
        fixture.ReadFactory.OnCreate = () => throw new InvalidOperationException("private refresh failure");
        try
        {
            await fixture.Set("currentTimeUtc", Now.AddSeconds(60));
            await fixture.Invoke("RefreshLiveDataAsync");
            Assert.Same(cached, fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory"));
            Assert.Equal(fixture.Text["Inbox.LoadFailed"], fixture.Get<string>("inboxLoadError"));
        }
        finally { fixture.ReadFactory.OnCreate = null; }
        await fixture.Set("currentTimeUtc", Now.AddSeconds(90));
        await fixture.Invoke("RefreshLiveDataAsync");
        Assert.Null(fixture.Get<string?>("inboxLoadError"));
        Assert.Equal(Now, Assert.Single(fixture.Get<IReadOnlyList<ComputerInventoryEntry>>("computerInventory")).LastHeartbeatAt);
    }

    private sealed class Fixture(SqliteConnection connection, WebApplication app, AsyncServiceScope scope, Renderer renderer) : IAsyncDisposable
    {
        public InboxDashboardState Dashboard => scope.ServiceProvider.GetRequiredService<InboxDashboardState>();
        public InboxLayoutRepository Repository => scope.ServiceProvider.GetRequiredService<InboxLayoutRepository>();
        public AlertStore Store => scope.ServiceProvider.GetRequiredService<AlertStore>();
        public RcaService Rca => scope.ServiceProvider.GetRequiredService<RcaService>();
        public Factory ReadFactory => (Factory)scope.ServiceProvider.GetRequiredService<IDbContextFactory<AlertDbContext>>();
        public AlertDbContext CreateContext() => scope.ServiceProvider.GetRequiredService<IDbContextFactory<AlertDbContext>>().CreateDbContext();
        public SaveInterceptor Interceptor => scope.ServiceProvider.GetRequiredService<SaveInterceptor>();
        public HomeLog Log => scope.ServiceProvider.GetRequiredService<HomeLog>();
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        public Home Page => scope.ServiceProvider.GetRequiredService<Activator>().Page;
        public InboxWidgetActions Actions => scope.ServiceProvider.GetRequiredService<Activator>().Actions;
        private HtmlRootComponent root;
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);
        public Task Refresh() => renderer.Dispatcher.InvokeAsync(() => Changed(Page));
        public T Get<T>(string field) => (T)typeof(Home).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page)!;
        public T Property<T>(string property) => (T)typeof(Home).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page)!;
        public async Task FreezeClock()
        {
            Get<CancellationTokenSource>("clockCancellation").Cancel();
            await Get<Task>("clockTask");
            await Set("currentTimeUtc", Now);
        }
        public Task Set(string field, object value) => renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(Home).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);
            Changed(Page);
        });
        public Task Invoke(string method, params object[] arguments) => InvokeComponent(Page, method, arguments);
        public Task DisposePage() => renderer.Dispatcher.InvokeAsync(async () =>
            await Page.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        public Task InvokeComponent(ComponentBase component, string method, params object[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(component, arguments);
            if (result is Task task) await task;
            Changed(component);
        });
        private static void Changed(ComponentBase component) =>
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
        public async Task Render<T>(ParameterView? parameters = null) where T : IComponent =>
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                root = renderer.BeginRenderingComponent(typeof(T), parameters ?? ParameterView.Empty);
                await root.QuiescenceTask;
            });
        public async Task<string> StoredJson()
        {
            await using var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AlertDbContext>>().CreateDbContext();
            return (await context.InboxLayouts.SingleAsync()).DocumentJson;
        }
        public static async Task<Fixture> Create(int days = 7, InboxTargetOutputDisplayOptions? targetOptions = null, IJSRuntime? javascript = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new SaveInterceptor();
            var options = new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, InboxTimestampModelCustomizer>()
                .AddInterceptors(interceptor).Options;
            var factory = new Factory(options);
            await using (var context = factory.CreateDbContext())
            {
                foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("{}", "{{}}", StringComparison.Ordinal)
                        .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                        .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal)
                        .Replace("N'", "'", StringComparison.Ordinal));
            }
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents();
            builder.Services.AddUiLocalization();
            builder.Services.AddAuthorization(options => options.AddPolicy("AdminAccess", policy => policy.RequireRole("Admin")));
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddScoped<AuthenticationStateProvider, TestAuthentication>();
            builder.Services.AddScoped<NavigationManager, TestNavigation>();
            builder.Services.AddSingleton<IDbContextFactory<AlertDbContext>>(factory);
            builder.Services.AddSingleton(interceptor);
            builder.Services.AddSingleton<HomeLog>();
            builder.Services.AddSingleton<ILogger<Home>>(services => services.GetRequiredService<HomeLog>());
            builder.Services.AddSingleton(new DatabaseConfigurationStatus(true, null, new SqlConnectionStringBuilder()));
            builder.Services.AddSingleton(new CurrentUserIdentity(new HttpContextAccessor()));
            builder.Services.AddSingleton(new ApplicationAuthenticationOptions { Type = "sql" });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new QueryResultPresenter(Path.Combine(AppContext.BaseDirectory, "AlertDefinitions")));
            builder.Services.AddSingleton<AlertRuleEvaluator>();
            builder.Services.AddSingleton<AlertStore>();
            builder.Services.AddSingleton(new AlertHistoryOptions { Days = days });
            builder.Services.AddSingleton(new AlertSeverityDisplayOptions());
            builder.Services.AddSingleton(targetOptions ?? new InboxTargetOutputDisplayOptions());
            builder.Services.AddScoped<InboxDashboardState>();
            builder.Services.AddScoped<InboxLayoutRepository>();
            builder.Services.AddScoped<RcaService>();
            builder.Services.AddScoped<IJSRuntime>(_ => javascript ?? new UnusedJavascript());
            builder.Services.AddScoped<Activator>();
            builder.Services.AddScoped<IComponentActivator>(services => services.GetRequiredService<Activator>());
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            return new Fixture(connection, app, scope, new Renderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()));
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
