using System.Globalization;
using Microsoft.Playwright;

namespace AlertConsoleCli;

internal sealed class AlertConsoleScraper(IPage page)
{
    public async Task<IReadOnlyList<ScrapedAlert>> ReadAlertsAsync(string url, bool includeResolved)
    {
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByRole(AriaRole.Heading, new() { Name = "Alert inbox" }).WaitForAsync();

        var view = page.GetByLabel("View");
        await SelectOptionAndConfirmAsync(view, includeResolved ? "history" : "active");
        if (includeResolved)
        {
            await WaitForFullHistoryAsync();
        }

        var table = page.GetByRole(AriaRole.Table);
        var rows = table.Locator("tbody > tr:not(.payload-row)").Filter(new()
        {
            Has = page.Locator("td.timestamp")
        });
        var alerts = new List<ScrapedAlert>();

        for (var index = 0; index < await rows.CountAsync(); index++)
        {
            var cells = rows.Nth(index).Locator("td");
            var timestampCell = cells.Nth(0);
            var receivedAtUtc = ParseUtcTimestamp(
                await timestampCell.GetAttributeAsync("title") ?? await timestampCell.InnerTextAsync());
            var searchLink = cells.Nth(6).Locator("a");
            var alertTitle = cells.Nth(1).Locator("strong");

            alerts.Add(new ScrapedAlert(
                new ActiveAlert(
                    (await alertTitle.InnerTextAsync()).Trim(),
                    (await cells.Nth(2).InnerTextAsync()).Trim(),
                    (await cells.Nth(3).InnerTextAsync()).Trim(),
                    await searchLink.CountAsync() == 0
                        ? null
                        : await searchLink.GetAttributeAsync("href"),
                    ParseStatus(await alertTitle.GetAttributeAsync("class"))),
                receivedAtUtc));
        }

        return alerts;
    }

    private async Task WaitForFullHistoryAsync()
    {
        var totalText = await page.Locator(".alert-count strong").InnerTextAsync();
        if (!int.TryParse(totalText.Trim(), out var totalAlerts))
        {
            throw new FormatException($"Unexpected total alert count: {totalText}");
        }

        var expectedSummaryPart = $"of {totalAlerts} alerts";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var summary = await page.Locator(".result-summary").InnerTextAsync();
            if (summary.Contains(expectedSummaryPart, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await page.WaitForTimeoutAsync(500);
            await page.GetByLabel("View").SelectOptionAsync("history");
        }

        throw new TimeoutException($"Full History did not render all {totalAlerts} stored alerts.");
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

    private static string ParseStatus(string? className)
    {
        if (className?.Contains("alert-title-resolved", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Resolved";
        }

        if (className?.Contains("alert-title-fired", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Fired";
        }

        return "Unknown";
    }

    private static DateTimeOffset ParseUtcTimestamp(string value)
    {
        var normalized = value.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
        return DateTimeOffset.Parse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
