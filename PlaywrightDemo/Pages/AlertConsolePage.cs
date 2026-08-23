using Microsoft.Playwright;
using PlaywrightDemo.Models;
using PlaywrightDemo.Parsing;

namespace PlaywrightDemo.Pages;

public sealed class AlertConsolePage(IPage page)
{
    public ILocator Filters => page.GetByRole(AriaRole.Region, new() { Name = "Alert filters" });
    public ILocator View => Filters.Locator("select").First;
    public ILocator Search => page.GetByRole(AriaRole.Searchbox, new() { Name = "Search alerts" });
    public ILocator TimeZone => page.GetByLabel("Time zone");
    public ILocator ReceivedFrom => page.GetByLabel("Received from");
    public ILocator ReceivedTo => page.GetByLabel("Received to");
    public ILocator ClearFilters => page.GetByRole(AriaRole.Button, new() { Name = "Clear filters" });
    public ILocator ResultSummary => page.Locator(".result-summary");
    public ILocator Table => page.GetByRole(AriaRole.Table);
    public ILocator Chart => page.GetByRole(AriaRole.Img, new() { NameRegex = new Regex("Alerts received per hour") });
    public ILocator GraphLayer1 => page.Locator(".graph-layer-controls select").Nth(0);
    public ILocator GraphLayer2 => page.Locator(".graph-layer-controls select").Nth(1);
    public ILocator GraphLayer3 => page.Locator(".graph-layer-controls select").Nth(2);
    public ILocator GraphLegend => page.GetByLabel("Count legend");

    public async Task OpenAsync(string baseUrl)
    {
        await page.GotoAsync(baseUrl);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Alert inbox" }).WaitForAsync();
        await Filters.WaitForAsync();
    }

    public async Task OpenGraphAsync()
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Graph" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Alert graph" }).WaitForAsync();
        await page.Locator("#alert-graph-canvas").WaitForAsync();
    }

    public Task SelectGraphLayer2Async(string layer) => SelectOptionAndConfirmAsync(GraphLayer2, layer);

    public Task SelectViewAsync(string mode) => SelectOptionAndConfirmAsync(View, mode);

    public Task SelectTimeZoneAsync(string zone) => SelectOptionAndConfirmAsync(TimeZone, zone);

    public async Task SetSearchAsync(string value)
    {
        await Search.ClickAsync();
        await Search.PressAsync("ControlOrMeta+A");
        await Search.PressSequentiallyAsync(value);
    }

    public async Task SetReceivedRangeAsync(string? from = null, string? to = null)
    {
        if (from is not null)
        {
            await ReceivedFrom.FillAsync(from);
            await ReceivedFrom.BlurAsync();
        }

        if (to is not null)
        {
            await ReceivedTo.FillAsync(to);
            await ReceivedTo.BlurAsync();
        }
    }

    public Task ClearAllFiltersAsync() => ClearFilters.ClickAsync();

    public async Task<ResultSummary> ReadResultSummaryAsync() =>
        AlertConsoleParsers.ParseResultSummary(await ResultSummary.InnerTextAsync());

    public async Task<ChartSummary> ReadChartSummaryAsync() =>
        AlertConsoleParsers.ParseChartDescription(await Chart.GetAttributeAsync("aria-label") ?? string.Empty);

    public async Task<IReadOnlyList<AlertRow>> ReadAlertRowsAsync()
    {
        var rows = Table.Locator("tbody > tr:not(.payload-row)").Filter(new() { Has = page.Locator("td.timestamp") });
        var result = new List<AlertRow>();

        for (var index = 0; index < await rows.CountAsync(); index++)
        {
            var cells = rows.Nth(index).Locator("td");
            result.Add(new AlertRow(
                (await cells.Nth(0).InnerTextAsync()).Trim(),
                (await cells.Nth(1).InnerTextAsync()).Trim(),
                (await cells.Nth(2).InnerTextAsync()).Trim(),
                (await cells.Nth(3).InnerTextAsync()).Trim(),
                (await cells.Nth(4).InnerTextAsync()).Trim(),
                (await cells.Nth(5).InnerTextAsync()).Trim(),
                await cells.Nth(6).Locator("a").CountAsync() > 0));
        }

        return result;
    }

    public async Task<NavigationNode?> ReadRootNodeAsync()
    {
        var button = page.Locator(".tree-node-root");
        if (await button.CountAsync() == 0)
        {
            return null;
        }

        return AlertConsoleParsers.ParseNavigationNode(await button.InnerTextAsync());
    }

    public async Task ExpandFirstSubscriptionAsync()
    {
        var toggle = page.GetByRole(AriaRole.Button, new() { Name = "Expand subscription" }).First;
        await toggle.ClickAsync();
        await toggle.WaitForAsync();
    }

    public async Task ExpandFirstResourceGroupAsync()
    {
        var toggle = page.GetByRole(AriaRole.Button, new() { Name = "Expand resource group" }).First;
        await toggle.ClickAsync();
        await toggle.WaitForAsync();
    }

    public Task SortByAsync(string column) =>
        Table.GetByRole(AriaRole.Button, new() { NameRegex = new Regex($"^{Regex.Escape(column)}") }).ClickAsync();

    public async Task OpenFirstAlertDetailsAsync()
    {
        await Table.Locator(".alert-name-button").First.ClickAsync();
        await Table.GetByText("Raw payload", new() { Exact = true }).WaitForAsync();
    }

    public async Task<JsonObject> ReadExpandedPayloadAsync() =>
        AlertConsoleParsers.ParseCommonAlertPayload(await Table.Locator("tr.payload-row pre").InnerTextAsync());

    public async Task SaveFirstAlertCommentAsync(string comment)
    {
        await OpenFirstAlertDetailsAsync();
        await Table.GetByLabel("Worker comments").FillAsync(comment);
        await Table.GetByRole(AriaRole.Button, new() { Name = "Save comments" }).ClickAsync();
        await Table.GetByText(comment, new() { Exact = true }).WaitForAsync();
    }

    private async Task SelectOptionAndConfirmAsync(ILocator select, string value)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await select.SelectOptionAsync(value);
            await page.WaitForTimeoutAsync(300);
            if (await select.InputValueAsync() == value)
            {
                return;
            }
        }

        throw new TimeoutException($"The Blazor control did not retain option '{value}'.");
    }
}