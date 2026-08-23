using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AlertWebAgent;

public sealed class Worker : BackgroundService
{
    private readonly AgentOptions options;
    private readonly AlertStateStore stateStore;
    private readonly TeamsNotifier notifier;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly ILogger<Worker> logger;

    public Worker(
        IOptions<AgentOptions> options,
        AlertStateStore stateStore,
        TeamsNotifier notifier,
        IHostApplicationLifetime applicationLifetime,
        ILogger<Worker> logger)
    {
        this.options = options.Value;
        this.stateStore = stateStore;
        this.notifier = notifier;
        this.applicationLifetime = applicationLifetime;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless
        });

        var contextOptions = new BrowserNewContextOptions();
        if (!string.IsNullOrWhiteSpace(options.BrowserStorageStatePath) &&
            File.Exists(options.BrowserStorageStatePath))
        {
            contextOptions.StorageStatePath = options.BrowserStorageStatePath;
        }

        await using var context = await browser.NewContextAsync(contextOptions);
        var page = await context.NewPageAsync();
        var state = await stateStore.LoadAsync(options.StateFilePath, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var observations = await ReadAlertsAsync(page);
                await ProcessAlertsAsync(observations, state, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Alert page poll failed. The next poll will retry.");
            }

            if (options.RunOnce)
            {
                applicationLifetime.StopApplication();
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task<IReadOnlyList<AlertObservation>> ReadAlertsAsync(IPage page)
    {
        logger.LogInformation("Polling {PageUrl}", options.PageUrl);
        var response = await page.GotoAsync(options.PageUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = options.NavigationTimeoutSeconds * 1000
        });

        var viewSelector = page.Locator(".controls select").First;
        try
        {
            await viewSelector.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = options.NavigationTimeoutSeconds * 1000
            });
        }
        catch (TimeoutException)
        {
            var bodyText = await page.Locator("body").InnerTextAsync();
            logger.LogError(
                "Alert controls were not found. HTTP={StatusCode}, FinalUrl={FinalUrl}, Title={Title}, Body={BodyExcerpt}",
                response?.Status,
                page.Url,
                await page.TitleAsync(),
                bodyText[..Math.Min(bodyText.Length, 500)]);
            throw;
        }
        await page.WaitForTimeoutAsync(2000);
        await viewSelector.SelectOptionAsync("active");
        await viewSelector.SelectOptionAsync("history");
        await page.Locator("tbody").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = options.NavigationTimeoutSeconds * 1000
        });
        await page.WaitForTimeoutAsync(1000);

        var observations = new List<AlertObservation>();
        var rows = page.Locator("tbody > tr");
        for (var index = 0; index < await rows.CountAsync(); index++)
        {
            var row = rows.Nth(index);
            var title = row.Locator("strong.alert-title-fired, strong.alert-title-resolved");
            if (await title.CountAsync() == 0)
            {
                continue;
            }

            var cells = row.Locator("td");
            if (await cells.CountAsync() < 7)
            {
                continue;
            }

            var titleClass = await title.First.GetAttributeAsync("class") ?? string.Empty;
            var condition = titleClass.Contains("alert-title-resolved", StringComparison.Ordinal)
                ? "Resolved"
                : "Fired";
            var receivedAt = await cells.Nth(0).GetAttributeAsync("title")
                ?? await cells.Nth(0).InnerTextAsync();
            var searchLink = cells.Nth(6).Locator("a");

            observations.Add(AlertObservation.Create(
                receivedAt,
                condition,
                await title.First.InnerTextAsync(),
                await GetOptionalTextAsync(cells.Nth(1).Locator(".cell-secondary")),
                await cells.Nth(2).InnerTextAsync(),
                await cells.Nth(3).InnerTextAsync(),
                await cells.Nth(4).InnerTextAsync(),
                await cells.Nth(5).InnerTextAsync(),
                await searchLink.CountAsync() > 0
                    ? await searchLink.First.GetAttributeAsync("href") ?? string.Empty
                    : string.Empty));
        }

        logger.LogInformation("Found {AlertCount} Fired/Resolved events in full history.", observations.Count);
        return observations;
    }

    private async Task ProcessAlertsAsync(
        IReadOnlyList<AlertObservation> observations,
        AlertState state,
        CancellationToken cancellationToken)
    {
        if (!state.Existed && !options.NotifyExistingOnFirstRun)
        {
            foreach (var observation in observations)
            {
                state.SeenIds.Add(observation.Id);
            }

            await stateStore.SaveAsync(options.StateFilePath, state.SeenIds, cancellationToken);
            state.Existed = true;
            logger.LogInformation(
                "Initialized baseline with {AlertCount} existing events; no Teams messages were sent.",
                observations.Count);
            return;
        }

        foreach (var observation in observations.Reverse().Where(item => !state.SeenIds.Contains(item.Id)))
        {
            if (!await notifier.SendAsync(observation, options, cancellationToken))
            {
                continue;
            }

            state.SeenIds.Add(observation.Id);
            await stateStore.SaveAsync(options.StateFilePath, state.SeenIds, cancellationToken);
        }

        state.Existed = true;
    }

    private static async Task<string> GetOptionalTextAsync(ILocator locator) =>
        await locator.CountAsync() > 0 ? (await locator.First.InnerTextAsync()).Trim() : string.Empty;
}
