using System.Text.Json;
using AlertConsoleCli;
using Microsoft.Playwright;

const string help = """
AlertConsoleCli - reads alerts from the Alert Console with Playwright.

Usage:
  dotnet run --project AlertConsoleCli -- [options]

Options:
  -m, --minutes <x>  Only alerts received within the last x minutes.
      --include-resolved
                      Include Fired and Resolved alerts from Full History.
      --hours <x>     Required with --include-resolved; limits history to x hours.
    -u, --url <url>    Alert Console URL. Overrides ALERT_CONSOLE_URL and local JSON.
      --headed       Show the Chromium browser while scraping.
  -h, --help         Show this help.

The JSON output contains alertName, subscription, target, searchResultLink and status.
""";

try
{
    var options = CliOptions.Parse(args);
    if (options.ShowHelp)
    {
        Console.WriteLine(help);
        return 0;
    }

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new()
    {
        Headless = !options.Headed
    });
    var page = await browser.NewPageAsync();
    var scraper = new AlertConsoleScraper(page);
    var alerts = await scraper.ReadAlertsAsync(options.Url, options.IncludeResolved);
    var cutoff = options.Hours is not null
        ? DateTimeOffset.UtcNow.AddHours(-options.Hours.Value)
        : options.Minutes is not null
            ? DateTimeOffset.UtcNow.AddMinutes(-options.Minutes.Value)
            : (DateTimeOffset?)null;
    var output = alerts
        .Where(alert => cutoff is null || alert.ReceivedAtUtc >= cutoff.Value)
        .Select(alert => alert.Alert)
        .ToArray();

    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    }));
    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Use --help for usage information.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Failed to read active alerts: {exception.Message}");
    return 1;
}
