using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace PlaywrightDemo.Tests;

[TestFixture]
[Category("AnalysisDashboard")]
public sealed class AnalysisDashboardInteractionTests : PageTest
{
    [SetUp]
    public async Task OpenIsolatedDashboard()
    {
        await Page.SetViewportSizeAsync(1200, 900);
        await Page.RouteAsync("http://analysis.test/**", async route =>
        {
            var name = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            var content = name switch
            {
                "app.css" or "analysis-dashboard.js" or "inbox-dashboard.js" =>
                    await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "AnalysisAssets", name)),
                _ => Fixture
            };
            await route.FulfillAsync(new()
            {
                Body = content,
                ContentType = name.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript"
                    : name.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html"
            });
        });
        await Page.GotoAsync("http://analysis.test/");
        await Page.WaitForFunctionAsync("() => window.dashboardReady === true");
    }

    [Test]
    public async Task DropsNewQueryAtChosenCellAndMovesExistingWidget()
    {
        var grid = Page.Locator("[data-inbox-dashboard-grid]");
        var query = Page.Locator("[data-analysis-query-id]");
        await query.DragToAsync(grid, new() { TargetPosition = new() { X = 650, Y = 130 } });

        var widget = Page.Locator("[data-inbox-widget-id]");
        await Expect(widget).ToHaveCSSAsync("grid-column-start", "3");
        await Expect(widget).ToHaveCSSAsync("grid-row-start", "2");
        await query.DragToAsync(grid, new() { TargetPosition = new() { X = 50, Y = 400 } });
        await Expect(widget).ToHaveCountAsync(1);
        await Expect(widget).ToHaveCSSAsync("grid-column-start", "1");
        await Expect(widget).ToHaveCSSAsync("grid-row-start", "4");
        await Expect(Page.Locator(".analysis-query-drop-preview")).ToHaveCountAsync(0);

        var handle = widget.Locator("[data-inbox-drag-handle]");
        var box = (await handle.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(box.X + 40, box.Y + 12);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(box.X + 330, box.Y + 109, new() { Steps = 5 });
        await Page.Mouse.UpAsync();
        await Expect(widget).ToHaveCSSAsync("grid-column-start", "2");
        await Expect(widget).ToHaveCSSAsync("grid-row-start", "5");
        Assert.That(await Page.EvaluateAsync<int>("() => window.calls.filter(call => call[0] === 'CommitDashboardMove').length"), Is.EqualTo(1));
    }

    [Test]
    public async Task ResizesBothSelectionPanelsAndRetainsCompactQueryCards()
    {
        var cardBox = (await Page.Locator("[data-analysis-query-id]").BoundingBoxAsync())!;
        Assert.That(cardBox.Width * cardBox.Height, Is.LessThanOrEqualTo(150 * 70));
        foreach (var name in new[] { "systems", "queries" })
        {
            var panel = Page.Locator($"[data-analysis-panel='{name}']");
            var handle = panel.Locator("[data-analysis-panel-resize]");
            var box = (await handle.BoundingBoxAsync())!;
            await Page.Mouse.MoveAsync(box.X + 8, box.Y + 8);
            await Page.Mouse.DownAsync();
            await Page.Mouse.MoveAsync(box.X - 92, box.Y + 68, new() { Steps = 5 });
            await Page.Mouse.UpAsync();
            await Expect(panel).ToHaveCSSAsync("width", "460px");
            await Expect(panel).ToHaveCSSAsync("height", "340px");
            await handle.FocusAsync();
            await Page.Keyboard.PressAsync("ArrowDown");
            await Expect(panel).ToHaveCSSAsync("height", "360px");
        }
        Assert.That(await Page.EvaluateAsync<int>("() => window.calls.filter(call => call[0] === 'CommitAnalysisPanelResize').length"), Is.EqualTo(4));
    }

    [Test]
    public async Task SupportsKeyboardPlacementAndWidgetResize()
    {
        await Page.Locator("[data-analysis-query-id]").PressAsync("Enter");
        var widget = Page.Locator("[data-inbox-widget-id]");
        await Expect(widget).ToHaveCountAsync(1);
        await Expect(widget).ToHaveCSSAsync("grid-column-start", "1");
        await Expect(widget).ToHaveCSSAsync("grid-row-start", "1");
        var handle = widget.Locator("[data-inbox-resize-handle]");
        var box = (await handle.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(box.X + 6, box.Y + 6);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(box.X + 296, box.Y + 103, new() { Steps = 5 });
        await Page.Mouse.UpAsync();
        await Expect(widget).ToHaveAttributeAsync("data-column-span", "3");
        await Expect(widget).ToHaveAttributeAsync("data-row-span", "5");

        var grip = widget.Locator(".inbox-widget-header > span");
        var gripBox = (await grip.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(gripBox.X + 6, gripBox.Y + 6);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(gripBox.X + 296, gripBox.Y + 103, new() { Steps = 5 });
        await Page.Mouse.UpAsync();
        await Expect(widget).ToHaveCSSAsync("grid-column-start", "2");
        await Expect(widget).ToHaveCSSAsync("grid-row-start", "2");
    }

    [Test]
    public async Task HeaderButtonsRemainClickableWithoutStartingAMove()
    {
        await Page.Locator("[data-analysis-query-id]").PressAsync("Enter");
        await Page.Locator(".inbox-widget-header button").ClickAsync();

        Assert.That(await Page.EvaluateAsync<int>("() => window.calls.filter(call => call[0] === 'CommitDashboardMove').length"), Is.Zero);
        await Expect(Page.Locator(".is-moving")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task DisposalRemovesAllInteractionListeners()
    {
        await Page.EvaluateAsync("() => window.disposeDashboard()");
        await Page.Locator("[data-analysis-query-id]").PressAsync("Enter");

        Assert.That(await Page.EvaluateAsync<int>("() => window.calls.length"), Is.Zero);
    }

    [Test]
    public async Task FitsResizedPanelsOnNarrowScreensAndKeepsDashboardScrollable()
    {
        await Page.SetViewportSizeAsync(360, 800);

        foreach (var name in new[] { "systems", "queries" })
        {
            var box = (await Page.Locator($"[data-analysis-panel='{name}']").BoundingBoxAsync())!;
            Assert.That(box.Width, Is.LessThanOrEqualTo(360));
            Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(360));
        }
        var canvas = Page.Locator(".analysis-dashboard-canvas");
        await Expect(canvas).ToHaveCSSAsync("overflow-x", "auto");
        Assert.That(await canvas.EvaluateAsync<bool>("element => element.scrollWidth > element.clientWidth"), Is.True);
    }

    [Test]
    public async Task AdminDraftAndTestOutputUseTwoColumnsAndStackOnSmallScreens()
    {
        await Page.SetContentAsync(
            """
            <html><head><link rel="stylesheet" href="http://analysis.test/app.css"></head><body>
            <div class="admin-workspace admin-analysis-workspace editing-draft">
                <section class="admin-rule-list"><h2>Query registry</h2><div class="admin-rule-cards" hidden>Queries</div></section>
                <section class="admin-editor"><h2>Draft</h2></section>
                <section class="admin-analysis-test-output"><h2>Test execution</h2></section>
            </div></body></html>
            """);
        await Expect(Page.Locator(".admin-rule-cards")).ToBeHiddenAsync();
        await Expect(Page.Locator(".admin-analysis-workspace")).ToHaveCSSAsync("display", "grid");
        var editor = (await Page.Locator(".admin-editor").BoundingBoxAsync())!;
        var output = (await Page.Locator(".admin-analysis-test-output").BoundingBoxAsync())!;
        Assert.That(output.X, Is.GreaterThanOrEqualTo(editor.X + editor.Width));
        Assert.That(output.Y, Is.EqualTo(editor.Y).Within(1));

        await Page.SetViewportSizeAsync(600, 800);
        editor = (await Page.Locator(".admin-editor").BoundingBoxAsync())!;
        output = (await Page.Locator(".admin-analysis-test-output").BoundingBoxAsync())!;
        Assert.That(output.X, Is.EqualTo(editor.X).Within(1));
        Assert.That(output.Y, Is.GreaterThanOrEqualTo(editor.Y + editor.Height));
    }

    private const string Fixture =
        """
        <!DOCTYPE html>
        <html><head><link rel="stylesheet" href="/app.css"></head><body>
        <div id="root" class="analysis-workspace">
          <div class="analysis-selection-panels">
            <section class="analysis-system-picker analysis-resizable-panel" data-analysis-panel="systems" style="width:560px;height:280px">
              <div class="admin-section-heading"><h2>Systems</h2></div><div class="analysis-system-grid">SERVER01</div>
              <button class="analysis-panel-resize-handle" data-analysis-panel-resize aria-label="Resize Systems"></button>
            </section>
            <section class="analysis-query-library analysis-resizable-panel" data-analysis-panel="queries" style="width:560px;height:280px">
              <div class="admin-section-heading"><h2>Query library</h2></div>
              <div class="analysis-query-library-grid">
                <article draggable="true" tabindex="0" role="button" data-analysis-query-id="cpu" data-column-span="2" data-row-span="4">
                  <span>Performance</span><h3>CPU</h3><small>Percent</small>
                </article>
              </div>
              <button class="analysis-panel-resize-handle" data-analysis-panel-resize aria-label="Resize Query library"></button>
            </section>
          </div>
          <section class="analysis-dashboard-canvas">
            <div class="inbox-dashboard-grid analysis-dashboard-grid show-grid" data-inbox-dashboard-grid
                 data-columns="4" data-rows="12" data-row-height="88" style="--columns:4;--rows:12;--row-height:88px"></div>
          </section>
        </div>
        <script type="module">
          import { initialize, dispose } from "/analysis-dashboard.js";
          const root = document.querySelector("#root");
          const grid = root.querySelector("[data-inbox-dashboard-grid]");
          window.calls = [];
          const reference = { async invokeMethodAsync(method, ...args) {
            window.calls.push([method, ...args]);
            if (method === "CommitAnalysisPanelResize") {
              const panel = root.querySelector(`[data-analysis-panel="${args[0]}"]`);
              panel.style.width = `${args[1]}px`;
              panel.style.height = `${args[2]}px`;
              return;
            }
            let widget = grid.querySelector("[data-inbox-widget-id]");
            if (method === "PlaceDashboardQuery" && !widget) {
              widget = document.createElement("article");
              widget.className = "inbox-dashboard-widget analysis-dashboard-widget edit-enabled";
              widget.dataset.inboxWidgetId = "widget";
              widget.dataset.analysisWidgetQueryId = args[0];
              widget.dataset.columnSpan = "2";
              widget.dataset.rowSpan = "4";
              widget.innerHTML = '<header class="inbox-widget-header" data-inbox-drag-handle><span aria-hidden="true">&#x283F;</span><div><strong>CPU</strong><small>60 minutes</small></div><button type="button">Open</button></header><button class="inbox-resize-handle" data-inbox-resize-handle></button>';
              grid.append(widget);
            }
            if (method === "CommitDashboardResize") {
              widget.dataset.columnSpan = args[1];
              widget.dataset.rowSpan = args[2];
            }
            const column = method === "CommitDashboardResize" ? parseInt(widget.style.gridColumn) : args[1];
            const row = method === "CommitDashboardResize" ? parseInt(widget.style.gridRow) : args[2];
            widget.style.cssText = `grid-column:${column}/span ${widget.dataset.columnSpan};grid-row:${row}/span ${widget.dataset.rowSpan};`;
          }};
          initialize(reference, root);
          window.disposeDashboard = () => dispose(root);
          window.dashboardReady = true;
        </script></body></html>
        """;
}
