using AlertConsole.Configuration;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace PlaywrightDemo.Tests;

[TestFixture]
[Category("E2E")]
[NonParallelizable]
public sealed class DocumentationScreenshotTests : PageTest
{
    private string directory = "";
    private string baseUrl = "";

    [SetUp]
    public async Task ConfigureCapture()
    {
        directory = Environment.GetEnvironmentVariable("ALERT_CONSOLE_SCREENSHOT_DIRECTORY") ?? "";
        if (string.IsNullOrWhiteSpace(directory))
            Assert.Ignore("Set ALERT_CONSOLE_SCREENSHOT_DIRECTORY to explicitly regenerate documentation images.");
        baseUrl = AlertConsoleUrlResolver.Resolve().TrimEnd('/');
        Assert.That(new Uri(baseUrl).IsLoopback, Is.True, "Documentation captures must use the local instance.");
        directory = Path.GetFullPath(directory!);
        Assert.That(Directory.Exists(directory), Is.True);
        await Page.SetViewportSizeAsync(1600, 1100);
        await Context.AddCookiesAsync([new Cookie
        {
            Name = ".MonitoringApp.UiCulture", Value = Uri.EscapeDataString("c=en-US|uic=en-US"), Url = baseUrl
        }]);
    }

    [Test]
    public async Task CaptureCurrentLocalApplication()
    {
        await Open("/");
        await Capture("Inbox.png");
        await Page.Locator(".inbox-results-widget").ScreenshotAsync(new()
        {
            Path = Path.Combine(directory, "InboxDetails.png"), Animations = ScreenshotAnimations.Disabled
        });
        await Open("/graph");
        await CaptureGraph();
        await Open("/admin/inventory");
        await Capture("Inventory.png");
        await Open("/analysis");
        await Page.GetByRole(AriaRole.Checkbox, new() { Name = "Edit", Exact = true }).CheckAsync();
        await Expect(Page.Locator(".analysis-setup-transfer")).ToBeVisibleAsync();
        await Capture("Analysis.png");
        await Page.Locator(".analysis-setup-transfer > summary").ClickAsync();
        await Page.Locator(".analysis-setup-transfer").ScreenshotAsync(new()
        {
            Path = Path.Combine(directory, "DashboardSetup.png"), Animations = ScreenshotAnimations.Disabled
        });
        await Page.Locator(".analysis-setup-transfer > summary").ClickAsync();
        await Page.Locator("[data-analysis-panel=queries]").ScreenshotAsync(new()
        {
            Path = Path.Combine(directory, "QueryLibrary.png"), Animations = ScreenshotAnimations.Disabled
        });

        await Open("/kql");
        await Page.Locator("textarea").FillAsync(
            "datatable(Computer:string, Status:string)\n[\"DEMO-DC-01\", \"Investigate\"]\n| where Status == \"Investigate\"\n| project Computer, Status");
        await Page.Locator("[data-kql-keyword-toggle]").ClickAsync();
        await Page.Locator("[data-kql-keyword-list]").GetByRole(AriaRole.Button, new() { Name = "where", Exact = true }).FocusAsync();
        await Expect(Page.Locator("[data-kql-keyword-help]")).ToContainTextAsync("where");
        await Capture("FreeKql.png");
        await Open("/rca");
        await Page.Locator(".rca-case-list button").First.ClickAsync();
        await Expect(Page.Locator("#rca-linked-alerts-heading")).ToBeVisibleAsync();
        await Capture("RootCauseAnalysis.png");
        await Open("/admin/analysis-queries");
        await Capture("QueryRegistry.png");
        await Open("/admin/rules");
        await Capture("Rules.png");
        await Open("/admin/rules/queries");
        await Capture("ScheduledKql.png");
        await Open("/logbook");
        await Capture("Logbook.png");
        await Open("/prompt");
        await Capture("Prompt.png");
        await Open("/queries");
        await Capture("Queries.png");
        await Open("/support/lockouts");
        await Capture("Lockouts.png");
        await Open("/admin/settings");
        await Expect(Page.Locator("[data-language-administration] input[type=file]")).ToHaveCountAsync(2);
        await Page.Locator("[data-language-administration]").ScreenshotAsync(new()
        {
            Path = Path.Combine(directory, "LanguageSettings.png"), Animations = ScreenshotAnimations.Disabled
        });
        await Open("/");
        TestContext.WriteLine("Captured 17 current local UI images. No queries executed, settings saved, demo data inserted or results simulated.");
    }

    [Test]
    public async Task CaptureAlertGraphWithoutEasterEggs()
    {
        await Open("/graph");
        await CaptureGraph();
    }

    private async Task CaptureGraph()
    {
        const string decorations = ".graph-easter-preview, .graph-offworld-sites, .graph-earth-easter-site, " +
            ".graph-easter-orbit, .graph-moon-panel, .graph-mars-panel, .easter-egg-icon";
        var style = await Page.AddStyleTagAsync(new()
        {
            Content = decorations + " { display: none !important; } " +
                ".graph-site-map-bodies { grid-template-columns: minmax(0, 1fr) !important; " +
                "gap: 0 !important; padding: 0 !important; background: transparent !important; }"
        });
        try
        {
            foreach (var decoration in await Page.Locator(decorations).AllAsync())
                await Expect(decoration).Not.ToBeVisibleAsync();
            await Expect(Page.Locator(".graph-site-map-svg")).ToBeVisibleAsync();
            foreach (var marker in await Page.Locator(".graph-site-marker").AllAsync())
                await Expect(marker).ToBeVisibleAsync();
            await Capture("Graph.png");
        }
        finally { await style.EvaluateAsync("element => element.remove()"); }
    }

    private async Task Open(string path)
    {
        await Page.GotoAsync(baseUrl + path);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator("[data-language-selector]")).ToHaveAttributeAsync("data-current-culture", "en-US");
        await Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
    }

    private async Task Capture(string file)
    {
        await Page.ScreenshotAsync(new()
        {
            Path = Path.Combine(directory, file), FullPage = false, Animations = ScreenshotAnimations.Disabled
        });
        TestContext.WriteLine($"Captured {file}: {await Page.TitleAsync()}");
    }
}
