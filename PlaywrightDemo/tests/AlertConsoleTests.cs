using AlertConsole.Configuration;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using PlaywrightDemo.Pages;
using PlaywrightDemo.Parsing;
using System.Net.Http.Json;

namespace PlaywrightDemo.Tests;

[TestFixture]
[Category("E2E")]
[Parallelizable(ParallelScope.Self)]
public sealed class AlertConsoleTests : PageTest
{
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
            Assert.That(root?.Name, Is.AnyOf(null, "All subscriptions"));
            Assert.That(root?.Count ?? 0, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task InteractsWithViewSearchSortingAndClearControls()
    {
        await consolePage.SelectViewAsync("history");
        await Expect(consolePage.View).ToHaveValueAsync("history");

        await consolePage.SetSearchAsync("__playwright_no_match__");
        await Expect(consolePage.ResultSummary).ToContainTextAsync("Showing 0 of");
        if (await consolePage.ClearFilters.IsEnabledAsync())
        {
            await consolePage.ClearAllFiltersAsync();
            await Expect(consolePage.Search).ToHaveValueAsync(string.Empty);
        }

        await consolePage.SortByAsync("Alert");
    }

    [Test]
    public async Task SwitchesTimeZonesWhilePreservingUtcSourceTimestamp()
    {
        if (await consolePage.TimeZone.CountAsync() == 0)
        {
            Assert.Ignore("This deployment does not expose the time-zone selector yet.");
        }

        await consolePage.SelectViewAsync("history");
        var timestamp = consolePage.Table.Locator("td.timestamp").First;
        DateTimeOffset? utcSource = null;
        if (await timestamp.CountAsync() > 0)
        {
            utcSource = AlertConsoleParsers.ParseUtcTooltip(await timestamp.GetAttributeAsync("title") ?? string.Empty);
        }

        await consolePage.SelectTimeZoneAsync("germany");
        await Expect(consolePage.Table.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Received Germany") })).ToBeVisibleAsync();
        await consolePage.SelectTimeZoneAsync("singapore");
        await Expect(consolePage.Table.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Received Singapore") })).ToBeVisibleAsync();
        await consolePage.SelectTimeZoneAsync("new-york");
        await Expect(consolePage.Table.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Received New York") })).ToBeVisibleAsync();

        if (utcSource is not null)
        {
            var unchangedUtcSource = AlertConsoleParsers.ParseUtcTooltip(await timestamp.GetAttributeAsync("title") ?? string.Empty);
            Assert.That(unchangedUtcSource, Is.EqualTo(utcSource));
        }
    }

    [Test]
    public async Task ExpandsHierarchyAndParsesRawAlertJson()
    {
        await consolePage.SelectViewAsync("history");
        var rows = await consolePage.ReadAlertRowsAsync();
        if (rows.Count == 0)
        {
            Assert.Ignore("The environment contains no alert rows.");
        }

        Assert.That(rows, Has.All.Property("SearchResult").EqualTo("tbd"));

        if (await Page.GetByRole(AriaRole.Button, new() { Name = "Expand subscription" }).CountAsync() > 0)
        {
            await consolePage.ExpandFirstSubscriptionAsync();
            if (await Page.GetByRole(AriaRole.Button, new() { Name = "Expand resource group" }).CountAsync() > 0)
            {
                await consolePage.ExpandFirstResourceGroupAsync();
            }
        }

        await consolePage.OpenFirstAlertDetailsAsync();
        var payload = await consolePage.ReadExpandedPayloadAsync();
        var parsedTarget = AlertConsoleParsers.TargetNameFromPayload(payload);
        if (parsedTarget is not null)
        {
            Assert.That(rows[0].TargetName, Is.EqualTo(parsedTarget));
        }
    }

    [Test]
    public async Task InteractsWithNativeDateAndMobileControls()
    {
        await consolePage.SetReceivedRangeAsync("2026-01-01T00:00", "2026-12-31T23:59");
        await Expect(consolePage.ReceivedFrom).ToHaveValueAsync("2026-01-01T00:00");
        await Expect(consolePage.ReceivedTo).ToHaveValueAsync("2026-12-31T23:59");

        await Page.SetViewportSizeAsync(390, 844);
        var scrollWidth = await Page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        Assert.That(scrollWidth, Is.LessThanOrEqualTo(390));
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
            Assert.That(layer1Options, Is.EqualTo(new[] { "Subscription" }));
            Assert.That(layer2Options, Is.EqualTo(new[] { "AlertName", "Resourcegroup" }));
            Assert.That(layer3Options, Is.EqualTo(new[] { "Target" }));
        });

        await consolePage.SelectGraphLayer2Async("AlertName");
        await Expect(consolePage.GraphLayer2).ToHaveValueAsync("AlertName");
        await Expect(consolePage.GraphLegend).ToContainTextAsync("AlertName");
        await Expect(Page.Locator(".graph-svg-resource-group")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task BuildsGraphFromPostedAlertEvents()
    {
        if (Environment.GetEnvironmentVariable("ALLOW_MUTATION_TESTS") != "true")
        {
            Assert.Ignore("Set ALLOW_MUTATION_TESTS=true only in a disposable environment.");
        }

        var runPrefix = $"e2e-{Guid.NewGuid():N}"[..12];
        await PostGraphTopologyAsync(baseUrl, runPrefix);
        await consolePage.OpenGraphAsync();

        var generatedSubscriptions = Page.Locator($".graph-svg-subscription[aria-label^='{runPrefix}']");
        var generatedResourceGroups = Page.Locator($".graph-svg-resource-group[aria-label^='{runPrefix}']");
        var generatedTargets = Page.Locator($".graph-svg-target[aria-label^='{runPrefix}']");
        var generatedActiveNodes = Page.Locator($".graph-svg-active[aria-label^='{runPrefix}']");

        await Expect(generatedSubscriptions).ToHaveCountAsync(3);
        await Expect(generatedResourceGroups).ToHaveCountAsync(8);
        await Expect(generatedTargets).ToHaveCountAsync(28);
        await Expect(generatedActiveNodes).ToHaveCountAsync(39);
        await Expect(Page.GetByRole(AriaRole.Group, new() { Name = "Graph data source" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task SaveCommentWhenMutationTestsAreExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("ALLOW_MUTATION_TESTS") != "true")
        {
            Assert.Ignore("Set ALLOW_MUTATION_TESTS=true only in a disposable environment.");
        }

        await consolePage.SelectViewAsync("history");
        await consolePage.SaveFirstAlertCommentAsync($"Playwright demo {DateTimeOffset.UtcNow:O}");
    }

    private static async Task PostGraphTopologyAsync(string alertConsoleUrl, string runPrefix)
    {
        int[][] resourceGroupTargetCounts =
        [
            [1],
            [2, 4, 6],
            [1, 3, 5, 6]
        ];
        using var client = new HttpClient();
        var endpoint = new Uri(new Uri(alertConsoleUrl), "api/alerts");

        for (var subscriptionIndex = 0; subscriptionIndex < resourceGroupTargetCounts.Length; subscriptionIndex++)
        {
            var subscription = $"{runPrefix}-sub-{subscriptionIndex + 1}";
            var targetCounts = resourceGroupTargetCounts[subscriptionIndex];

            for (var resourceGroupIndex = 0; resourceGroupIndex < targetCounts.Length; resourceGroupIndex++)
            {
                var resourceGroup = $"{runPrefix}-rg-{subscriptionIndex + 1}-{resourceGroupIndex + 1}";

                for (var targetIndex = 0; targetIndex < targetCounts[resourceGroupIndex]; targetIndex++)
                {
                    var target = $"{runPrefix}-target-{subscriptionIndex + 1}-{resourceGroupIndex + 1}-{targetIndex + 1}";
                    var alertId = $"{runPrefix}-alert-{subscriptionIndex + 1}-{resourceGroupIndex + 1}-{targetIndex + 1}";
                    var payload = CreateCommonAlertPayload(alertId, subscription, resourceGroup, target);
                    using var response = await client.PostAsJsonAsync(endpoint, payload);
                    response.EnsureSuccessStatusCode();
                }
            }
        }
    }

    private static object CreateCommonAlertPayload(
        string alertId,
        string subscription,
        string resourceGroup,
        string target) => new
        {
            schemaId = "azureMonitorCommonAlertSchema",
            data = new
            {
                essentials = new
                {
                    alertId,
                    alertRule = "E2E graph topology",
                    severity = "Sev2",
                    signalType = "Log",
                    monitorCondition = "Fired",
                    targetResourceGroup = resourceGroup,
                    targetSubscriptionId = subscription,
                    alertTargetIDs = new[]
                    {
                        $"/subscriptions/{subscription}/resourceGroups/{resourceGroup}/providers/Microsoft.HybridCompute/machines/{target}"
                    },
                    firedDateTime = DateTimeOffset.UtcNow
                },
                alertContext = new
                {
                    condition = new
                    {
                        allOf = new[]
                        {
                            new
                            {
                                searchQuery = "Heartbeat | summarize LastSeen=max(TimeGenerated) by Computer",
                                dimensions = new[]
                                {
                                    new { name = "Computer", value = target }
                                }
                            }
                        }
                    }
                }
            }
        };
}