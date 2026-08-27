using AlertConsole.Configuration;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using PlaywrightDemo.Models;
using PlaywrightDemo.Pages;
using PlaywrightDemo.Parsing;
using PlaywrightDemo.TestCases;
using System.Net.Http.Json;

namespace PlaywrightDemo.Tests;

[TestFixture]
[Category("E2E")]
[Parallelizable(ParallelScope.Self)]
public sealed class AlertConsoleTests : PageTest
{
    private static readonly AlertConsoleTestCases Cases =
        TestCaseLoader.Load<AlertConsoleTestCases>("alert-console.json");
    private string baseUrl = null!;
    private AlertConsolePage consolePage = null!;

    [SetUp]
    public async Task OpenConsole()
    {
        try
        {
            baseUrl = AlertConsoleUrlResolver.Resolve();
        }
        catch (InvalidOperationException exception)
        {
            Assert.Ignore(exception.Message);
        }

        consolePage = new AlertConsolePage(Page);
        await consolePage.OpenAsync(baseUrl);
    }

    [Test]
    public async Task LoadsAndParsesDashboardComponents()
    {
        var summary = await consolePage.ReadResultSummaryAsync();
        var chart = await consolePage.ReadChartSummaryAsync();
        var root = await consolePage.ReadRootNodeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(summary.Shown, Is.LessThanOrEqualTo(summary.Available));
            Assert.That(chart.MaximumPerHour, Is.LessThanOrEqualTo(chart.Total));
            Assert.That(root?.Name, Is.AnyOf(null, Cases.RootNodeName));
            Assert.That(root?.Count ?? 0, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task InteractsWithViewSearchSortingAndClearControls()
    {
        await consolePage.SelectViewAsync(Cases.HistoryView);
        await Expect(consolePage.View).ToHaveValueAsync(Cases.HistoryView);

        await consolePage.SetSearchAsync(Cases.NoMatchSearch);
        await Expect(consolePage.ResultSummary).ToContainTextAsync(Cases.EmptyResultText);
        if (await consolePage.ClearFilters.IsEnabledAsync())
        {
            await consolePage.ClearAllFiltersAsync();
            await Expect(consolePage.Search).ToHaveValueAsync(string.Empty);
        }

        await consolePage.SortByAsync(Cases.AlertSortColumn);
    }

    [Test]
    public async Task SwitchesTimeZonesWhilePreservingUtcSourceTimestamp()
    {
        if (await consolePage.TimeZone.CountAsync() == 0)
        {
            Assert.Ignore("This deployment does not expose the time-zone selector yet.");
        }

        await consolePage.SelectViewAsync(Cases.HistoryView);
        var timestamp = consolePage.Table.Locator("td.timestamp").First;
        DateTimeOffset? utcSource = null;
        if (await timestamp.CountAsync() > 0)
        {
            utcSource = AlertConsoleParsers.ParseUtcTooltip(await timestamp.GetAttributeAsync("title") ?? string.Empty);
        }

        foreach (var timeZone in Cases.TimeZones)
        {
            await consolePage.SelectTimeZoneAsync(timeZone.Value);
            await Expect(consolePage.Table.GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex(timeZone.ReceivedHeader) })).ToBeVisibleAsync();
        }

        if (utcSource is not null)
        {
            var unchangedUtcSource = AlertConsoleParsers.ParseUtcTooltip(await timestamp.GetAttributeAsync("title") ?? string.Empty);
            Assert.That(unchangedUtcSource, Is.EqualTo(utcSource));
        }
    }

    [Test]
    public async Task ExpandsHierarchyAndParsesRawAlertJson()
    {
        await consolePage.SelectViewAsync(Cases.HistoryView);
        var rows = await consolePage.ReadAlertRowsAsync();
        if (rows.Count == 0)
        {
            Assert.Ignore("The environment contains no alert rows.");
        }

        Assert.That(rows.All(row => !string.IsNullOrWhiteSpace(row.SearchResult)), Is.True);

        if (await Page.GetByRole(AriaRole.Button, new() { Name = "Expand subscription" }).CountAsync() > 0)
        {
            await consolePage.ExpandFirstSubscriptionAsync();
        }

        await consolePage.OpenFirstAlertDetailsAsync();
        var payload = await consolePage.ReadExpandedPayloadAsync();
        var parsedTarget = AlertConsoleParsers.TargetNameFromPayload(payload);
        Assert.That(parsedTarget, Is.Not.Null.And.Not.Empty);

        var alertId = payload["data"]?["essentials"]?["alertId"]?.GetValue<string>();
        var correlatedCase = Cases.CorrelatedAlerts.FirstOrDefault(testCase => testCase.AlertId == alertId);
        if (correlatedCase is not null)
        {
            Assert.That(rows[0].TargetName, Is.EqualTo(correlatedCase.TargetName));
        }
        else
        {
            Assert.That(rows[0].TargetName, Is.Not.Empty);
        }
    }

    [Test]
    public async Task DisplaysCorrelatedDirectoryMonitoringAlerts()
    {
        await consolePage.SelectViewAsync(Cases.HistoryView);

        foreach (var alertCase in Cases.CorrelatedAlerts)
        {
            await consolePage.SetSearchAsync(alertCase.AlertId);
            var row = consolePage.Table.Locator("tbody > tr:not(.payload-row)")
                .Filter(new() { Has = Page.Locator("td.timestamp") });
            await Expect(row).ToHaveCountAsync(1);

            var cells = row.Locator("td");
            await Expect(cells.Nth(1)).ToContainTextAsync(alertCase.AlertName);
            await Expect(cells.Nth(2)).ToHaveTextAsync(alertCase.TargetName);

            var collapsedResult = cells.Nth(4).Locator(":scope > details.query-result-details > summary");
            if (await collapsedResult.CountAsync() > 0)
            {
                await collapsedResult.ClickAsync();
            }

            foreach (var expectedText in alertCase.ResultContains)
            {
                await Expect(cells.Nth(4)).ToContainTextAsync(expectedText);
            }
        }
    }

    [Test]
    public async Task InteractsWithNativeDateAndMobileControls()
    {
        await consolePage.SetReceivedRangeAsync(Cases.DateRange.From, Cases.DateRange.To);
        await Expect(consolePage.ReceivedFrom).ToHaveValueAsync(Cases.DateRange.From);
        await Expect(consolePage.ReceivedTo).ToHaveValueAsync(Cases.DateRange.To);

        await Page.SetViewportSizeAsync(Cases.MobileViewport.Width, Cases.MobileViewport.Height);
        var scrollWidth = await Page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        Assert.That(scrollWidth, Is.LessThanOrEqualTo(Cases.MobileViewport.Width));
    }

    [Test]
    public async Task ConfiguresGraphLayers()
    {
        await consolePage.OpenGraphAsync();
        var layer1Options = await consolePage.GraphLayer1.Locator("option").AllTextContentsAsync();
        var layer2Options = await consolePage.GraphLayer2.Locator("option").AllTextContentsAsync();
        var layer3Options = await consolePage.GraphLayer3.Locator("option").AllTextContentsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(layer1Options, Is.EqualTo(Cases.Graph.Layer1Options));
            Assert.That(layer2Options, Is.EqualTo(Cases.Graph.Layer2Options));
            Assert.That(layer3Options, Is.EqualTo(Cases.Graph.Layer3Options));
        });

        await consolePage.SelectGraphLayer2Async(Cases.Graph.SelectedLayer2);
        await Expect(consolePage.GraphLayer2).ToHaveValueAsync(Cases.Graph.SelectedLayer2);
        await Expect(consolePage.GraphLegend).ToContainTextAsync(Cases.Graph.SelectedLayer2);
        await Expect(Page.Locator(".graph-svg-resource-group")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task BuildsGraphFromPostedAlertEvents()
    {
        if (Environment.GetEnvironmentVariable("ALLOW_MUTATION_TESTS") != "true")
        {
            Assert.Ignore("Set ALLOW_MUTATION_TESTS=true only in a disposable environment.");
        }

        var runId = Guid.NewGuid().ToString("N")[..Cases.GraphTopology.RunIdLength];
        var runPrefix = ExpandText(Cases.GraphTopology.RunPrefixTemplate, new Dictionary<string, string>
        {
            ["runId"] = runId
        });
        await PostGraphTopologyAsync(baseUrl, runPrefix);
        await consolePage.OpenGraphAsync();

        var generatedSubscriptions = Page.Locator($".graph-svg-subscription[aria-label^='{runPrefix}']");
        var generatedResourceGroups = Page.Locator($".graph-svg-resource-group[aria-label^='{runPrefix}']");
        var generatedTargets = Page.Locator($".graph-svg-target[aria-label^='{runPrefix}']");
        var generatedActiveNodes = Page.Locator($".graph-svg-active[aria-label^='{runPrefix}']");

        await Expect(generatedSubscriptions).ToHaveCountAsync(Cases.GraphTopology.ExpectedSubscriptions);
        await Expect(generatedResourceGroups).ToHaveCountAsync(Cases.GraphTopology.ExpectedResourceGroups);
        await Expect(generatedTargets).ToHaveCountAsync(Cases.GraphTopology.ExpectedTargets);
        await Expect(generatedActiveNodes).ToHaveCountAsync(Cases.GraphTopology.ExpectedActiveNodes);
        await Expect(Page.GetByRole(AriaRole.Group, new() { Name = "Graph data source" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task SaveCommentWhenMutationTestsAreExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("ALLOW_MUTATION_TESTS") != "true")
        {
            Assert.Ignore("Set ALLOW_MUTATION_TESTS=true only in a disposable environment.");
        }

        await consolePage.SelectViewAsync(Cases.HistoryView);
        await consolePage.SaveFirstAlertCommentAsync($"{Cases.CommentPrefix} {DateTimeOffset.UtcNow:O}");
    }

    private static async Task PostGraphTopologyAsync(string alertConsoleUrl, string runPrefix)
    {
        var resourceGroupTargetCounts = Cases.GraphTopology.ResourceGroupTargetCounts;
        using var client = new HttpClient();
        var endpoint = new Uri(new Uri(alertConsoleUrl), "api/alerts");

        for (var subscriptionIndex = 0; subscriptionIndex < resourceGroupTargetCounts.Count; subscriptionIndex++)
        {
            var subscriptionValues = new Dictionary<string, string>
            {
                ["runPrefix"] = runPrefix,
                ["subscriptionIndex"] = (subscriptionIndex + 1).ToString()
            };
            var subscription = ExpandText(Cases.GraphTopology.SubscriptionTemplate, subscriptionValues);
            var targetCounts = resourceGroupTargetCounts[subscriptionIndex];

            for (var resourceGroupIndex = 0; resourceGroupIndex < targetCounts.Count; resourceGroupIndex++)
            {
                var resourceGroupValues = new Dictionary<string, string>(subscriptionValues)
                {
                    ["resourceGroupIndex"] = (resourceGroupIndex + 1).ToString()
                };
                var resourceGroup = ExpandText(Cases.GraphTopology.ResourceGroupTemplate, resourceGroupValues);

                for (var targetIndex = 0; targetIndex < targetCounts[resourceGroupIndex]; targetIndex++)
                {
                    var targetValues = new Dictionary<string, string>(resourceGroupValues)
                    {
                        ["targetIndex"] = (targetIndex + 1).ToString()
                    };
                    var target = ExpandText(Cases.GraphTopology.TargetTemplate, targetValues);
                    var alertId = ExpandText(Cases.GraphTopology.AlertIdTemplate, targetValues);
                    var payload = CreateCommonAlertPayload(alertId, subscription, resourceGroup, target);
                    using var response = await client.PostAsJsonAsync(endpoint, payload);
                    response.EnsureSuccessStatusCode();
                }
            }
        }
    }

    private static JsonObject CreateCommonAlertPayload(
        string alertId,
        string subscription,
        string resourceGroup,
        string target)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alertId"] = alertId,
            ["subscription"] = subscription,
            ["resourceGroup"] = resourceGroup,
            ["target"] = target,
            ["firedDateTime"] = DateTimeOffset.UtcNow.ToString("O")
        };

        return (JsonObject)ExpandTemplate(Cases.GraphTopology.AlertPayloadTemplate, values)!;
    }

    private static string ExpandText(string template, IReadOnlyDictionary<string, string> values) =>
        values.Aggregate(template, (result, entry) =>
            result.Replace($"{{{{{entry.Key}}}}}", entry.Value, StringComparison.Ordinal));

    private static JsonNode? ExpandTemplate(JsonNode? node, IReadOnlyDictionary<string, string> values) => node switch
    {
        JsonObject source => new JsonObject(source.Select(property =>
            KeyValuePair.Create(property.Key, ExpandTemplate(property.Value, values)))),
        JsonArray source => new JsonArray(source.Select(item => ExpandTemplate(item, values)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) =>
            JsonValue.Create(ExpandText(text, values)),
        _ => node?.DeepClone()
    };
}