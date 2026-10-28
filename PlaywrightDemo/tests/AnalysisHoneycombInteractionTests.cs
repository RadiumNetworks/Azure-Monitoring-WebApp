using Microsoft.Playwright.NUnit;

namespace PlaywrightDemo.Tests;

[TestFixture]
[Category("AnalysisDashboard")]
public sealed class AnalysisHoneycombInteractionTests : PageTest
{
    [SetUp]
    public async Task OpenIsolatedHoneycomb()
    {
        await Page.SetViewportSizeAsync(1200, 900);
        await Page.RouteAsync("https://fonts.googleapis.com/**",
            route => route.FulfillAsync(new() { Body = "", ContentType = "text/css" }));
        await Page.RouteAsync("http://analysis.test/**", async route =>
        {
            var name = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            var content = name is "app.css" or "analysis-honeycomb.js"
                ? await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "AnalysisAssets", name))
                : Fixture;
            await route.FulfillAsync(new()
            {
                Body = content,
                ContentType = name.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript"
                    : name.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html"
            });
        });
        await Page.GotoAsync("http://analysis.test/");
        await Expect(Page.Locator(".analysis-honeycomb")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("height:"));
    }

    [TestCase(480, 300)]
    [TestCase(320, 240)]
    [TestCase(700, 220)]
    [TestCase(240, 400)]
    public async Task FitsTwelveCellsWithoutScrollingAndKeepsCellsInsideTheViewBox(int width, int height)
    {
        await ResizeAsync(width, height);
        await AssertFitsAsync();
        await Expect(Page.Locator(".analysis-honeycomb-cell")).ToHaveCountAsync(12);
        Assert.That(await Page.Locator(".analysis-honeycomb").EvaluateAsync<bool>(
            """
            svg => {
                const bounds = svg.viewBox.baseVal;
                return [...svg.querySelectorAll(".analysis-honeycomb-cell")].every(cell => {
                    const position = cell.transform.baseVal.getItem(0).matrix;
                    return position.e - 70 >= 0 && position.e + 70 <= bounds.width &&
                        position.f - 60 >= 0 && position.f + 60 <= bounds.height;
                });
            }
            """), Is.True);
    }

    [Test]
    public async Task ReflowsWhenWidgetChangesShape()
    {
        await ResizeAsync(700, 220);
        await AssertFitsAsync();
        var wide = await Page.Locator(".analysis-honeycomb").GetAttributeAsync("viewBox");
        await ResizeAsync(240, 400);
        await AssertFitsAsync();
        var narrow = await Page.Locator(".analysis-honeycomb").GetAttributeAsync("viewBox");
        Assert.That(narrow, Is.Not.EqualTo(wide));
    }

    [Test]
    public async Task ZoomsInAndOutAndFitsAgainWithKeyboardAccessibleControls()
    {
        var chart = Page.Locator(".analysis-honeycomb");
        var initialWidth = (await chart.BoundingBoxAsync())!.Width;
        var zoomIn = Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Zoom in honeycomb" });
        await zoomIn.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.Locator("output")).ToHaveTextAsync("125%");
        Assert.That((await chart.BoundingBoxAsync())!.Width, Is.EqualTo(initialWidth * 1.25).Within(1));
        for (var index = 0; index < 3; index++) await zoomIn.ClickAsync();
        await Expect(zoomIn).ToBeDisabledAsync();
        await Expect(Page.Locator("output")).ToHaveTextAsync("200%");
        Assert.That(await Page.Locator(".analysis-honeycomb-viewport").EvaluateAsync<bool>(
            "element => element.scrollWidth > element.clientWidth || element.scrollHeight > element.clientHeight"), Is.True);

        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Fit", Exact = true }).ClickAsync();
        await Expect(Page.Locator("output")).ToHaveTextAsync("100%");
        await AssertFitsAsync();

        var zoomOut = Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Zoom out honeycomb" });
        await zoomOut.ClickAsync();
        await zoomOut.ClickAsync();
        await Expect(zoomOut).ToBeDisabledAsync();
        await Expect(Page.Locator("output")).ToHaveTextAsync("50%");
        Assert.That((await chart.BoundingBoxAsync())!.Width, Is.EqualTo(initialWidth * 0.5).Within(1));
    }

    [Test]
    public async Task UpdatesCellsAfterRefreshAndInitializesNewWidgets()
    {
        await Page.Locator(".analysis-honeycomb").EvaluateAsync(
            """
            svg => {
                const original = svg.querySelector(".analysis-honeycomb-cell");
                for (let index = 0; index < 12; index++) svg.append(original.cloneNode(true));
            }
            """);
        await Expect(Page.Locator(".analysis-honeycomb-cell")).ToHaveCountAsync(24);
        await Page.WaitForFunctionAsync(
            """
            () => new Set([...document.querySelectorAll(".analysis-honeycomb-cell")]
                .map(cell => cell.getAttribute("transform"))).size === 24
            """);
        await AssertFitsAsync();
        await Page.Locator("article").EvaluateAsync(
            """
            widget => {
                const replacement = widget.cloneNode(true);
                widget.remove();
                document.body.append(replacement);
            }
            """);
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Zoom in honeycomb" }).ClickAsync();
        await Expect(Page.Locator("output")).ToHaveTextAsync("125%");
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Fit", Exact = true }).ClickAsync();
        await AssertFitsAsync();
    }

    private async Task ResizeAsync(int width, int height)
    {
        await Page.Locator("article").EvaluateAsync(
            "(element, size) => { element.style.width = size.width + 'px'; element.style.height = size.height + 'px'; }",
            new { width, height });
    }

    private async Task AssertFitsAsync()
    {
        await Page.WaitForFunctionAsync(
            """
            () => {
                const body = document.querySelector(".analysis-widget-body");
                const viewport = document.querySelector(".analysis-honeycomb-viewport");
                const svg = document.querySelector(".analysis-honeycomb");
                const available = viewport.getBoundingClientRect();
                const actual = svg.getBoundingClientRect();
                return viewport.clientHeight > 0 && actual.height <= available.height &&
                    actual.width <= available.width && viewport.scrollHeight <= viewport.clientHeight &&
                    viewport.scrollWidth <= viewport.clientWidth && body.scrollHeight <= body.clientHeight;
            }
            """);
    }

    private static string Fixture =>
        """
        <!doctype html><html><head><link rel="stylesheet" href="/app.css"></head><body>
        <article class="inbox-dashboard-widget analysis-dashboard-widget" style="width:480px;height:300px">
            <header class="inbox-widget-header"><div><strong>LDAP latency by site</strong><small>Last hour</small></div></header>
            <div class="inbox-widget-body analysis-widget-body">
                <div class="analysis-honeycomb-view" data-analysis-honeycomb>
                    <div class="analysis-honeycomb-controls" role="group" aria-label="Honeycomb zoom">
                        <button type="button" data-honeycomb-zoom="out" aria-label="Zoom out honeycomb">-</button>
                        <output aria-label="Honeycomb zoom level">100%</output>
                        <button type="button" data-honeycomb-zoom="in" aria-label="Zoom in honeycomb">+</button>
                        <button type="button" data-honeycomb-zoom="fit">Fit</button>
                    </div>
                    <p class="cell-secondary analysis-honeycomb-description">LDAP latency: green 0 ms to red 2000 ms. Colors are not alert thresholds.</p>
                    <div class="analysis-honeycomb-viewport" tabindex="0" role="region" aria-label="Honeycomb chart">
                        <svg class="analysis-honeycomb" viewBox="0 0 500 431" role="img">
        """ +
        string.Concat(Enumerable.Range(0, 12).Select(index =>
            $"""
            <g class="analysis-honeycomb-cell" tabindex="0" aria-label="Site {index}: 1000 ms">
                <title>Site {index}: 1000 ms</title>
                <polygon points="-70,0 -35,-60 35,-60 70,0 35,60 -35,60" fill="hsl(60 65% 38%)" />
                <text x="0" y="-10" text-anchor="middle">Site {index}</text>
                <text x="0" y="14" text-anchor="middle">1000 ms</text>
                <text x="0" y="34" text-anchor="middle">Workspace A</text>
            </g>
            """)) +
        """
                        </svg>
                    </div>
                </div>
            </div>
        </article>
        <script src="/analysis-honeycomb.js"></script>
        </body></html>
        """;
}
