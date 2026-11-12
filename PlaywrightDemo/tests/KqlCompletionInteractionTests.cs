using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace PlaywrightDemo.Tests;

[TestFixture]
[Category("KqlCompletion")]
public sealed class KqlCompletionInteractionTests : PageTest
{
    private ILocator Input => Page.Locator("textarea");
    private ILocator Ghost => Page.Locator(".free-kql-completion-ghost");
    private ILocator Tooltip => Page.Locator("[data-kql-info-tooltip]");
    private bool failDictionary;
    private string? dictionaryOverride;
    private int dictionaryRequests;

    [SetUp]
    public async Task SetUpRoutes()
    {
        failDictionary = false;
        dictionaryOverride = null;
        dictionaryRequests = 0;
        await Page.SetViewportSizeAsync(1000, 800);
        await Page.RouteAsync("https://fonts.googleapis.com/**",
            route => route.FulfillAsync(new() { Body = "", ContentType = "text/css" }));
        await Page.RouteAsync("http://kql.test/**", async route =>
        {
            var name = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            if (name == "kql-completion.json")
            {
                dictionaryRequests++;
                if (failDictionary)
                {
                    await route.FulfillAsync(new() { Status = 503, Body = "Unavailable" });
                    return;
                }
            }
            var content = name switch
            {
                "app.css" => await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "AnalysisAssets", name)),
                "kql-completion.js" => await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "KqlAssets", name)),
                "kql-completion.json" => dictionaryOverride ??
                    await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "KqlAssets", name)),
                _ => Fixture
            };
            await route.FulfillAsync(new()
            {
                Body = content,
                ContentType = name.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript"
                    : name.EndsWith(".json", StringComparison.Ordinal) ? "application/json"
                    : name.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html"
            });
        });
    }

    [Test]
    public async Task KeepsSchemaCachesAndTooltipsIndependentForMultipleEditors()
    {
        await OpenAsync();
        await Page.EvaluateAsync(
            """
            () => {
                const first = document.querySelector('[data-kql-editor]');
                const second = first.cloneNode(true);
                first.parentElement.after(second);
                window.schemaCounts = [0, 0];
                [first, second].forEach((root, index) => {
                    root.dataset.kqlTables = JSON.stringify(['Perf']);
                    window.kqlCompletion.connect(root, {
                        invokeMethodAsync: () => {
                            window.schemaCounts[index]++;
                            return Promise.resolve(`ColumnFromEditor${index}: string`);
                        }
                    });
                });
            }
            """);
        var inputs = Page.Locator("textarea");
        await inputs.Nth(0).FillAsync("Perf");
        await inputs.Nth(1).FillAsync("Perf");
        await inputs.Nth(0).FocusAsync();
        await inputs.Nth(0).PressAsync("F1");
        await Expect(Page.Locator("[data-kql-info-tooltip]").Nth(0)).ToHaveTextAsync("ColumnFromEditor0: string");
        await inputs.Nth(1).FocusAsync();
        await inputs.Nth(1).PressAsync("F1");
        await Expect(Page.Locator("[data-kql-info-tooltip]").Nth(1)).ToHaveTextAsync("ColumnFromEditor1: string");
        Assert.That(await Page.EvaluateAsync<int[]>("window.schemaCounts"), Is.EqualTo(new[] { 1, 1 }));
    }

    [Test]
    public async Task LoadsSchemasWhenTablesBecomeCompleteAndCachesThemAcrossTypingAndRepeatedOccurrences()
    {
        await OpenAsync();
        await ConnectSchemaBridgeAsync();
        await SetTablesAsync("Perf", "Event");
        await Input.FillAsync("Per");
        Assert.That(await Page.EvaluateAsync<int>("window.schemaRequests.length"), Is.Zero);
        await Input.PressAsync("Tab");
        await Expect(Page.Locator(".free-kql-table")).ToHaveTextAsync("Perf");
        await Input.PressAsync("F1");
        await Expect(Tooltip).ToHaveTextAsync("Perf\nCounter: int\nComputer: string\nDetails: dynamic");
        await Input.PressAsync("Escape");
        await Input.FillAsync("Perf\n| join Perf on Computer");
        await HoverWordAsync("Perf");
        await Expect(Tooltip).ToContainTextAsync("Details: dynamic");
        Assert.That(await Page.EvaluateAsync<int>("window.schemaRequests.length"), Is.EqualTo(1));
        await Input.FillAsync("Event");
        await HoverWordAsync("Event");
        await Expect(Tooltip).ToContainTextAsync("Event\n");
        Assert.That(await Page.EvaluateAsync<int>("window.schemaRequests.length"), Is.EqualTo(2));
        await Expect(Input).ToHaveValueAsync("Event");
    }

    [TestCase("Per")]
    [TestCase("PerfX")]
    [TestCase("'Perf'")]
    [TestCase("\"Perf\"")]
    [TestCase("```Perf```")]
    [TestCase("// Perf")]
    [TestCase("/* Perf */")]
    public async Task DoesNotLoadSchemasForIncompleteUnknownOrQuotedTableTokens(string query)
    {
        await OpenAsync();
        await ConnectSchemaBridgeAsync();
        await SetTablesAsync("Perf");
        await Input.FillAsync(query);
        Assert.That(await Page.EvaluateAsync<int>("window.schemaRequests.length"), Is.Zero);
        await Expect(Page.Locator(".free-kql-table")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task LoadsSchemaWhenTheCatalogArrivesAfterTheQueryAndUpdatesAnAlreadyVisibleTooltip()
    {
        await OpenAsync();
        await ConnectSchemaBridgeAsync(true);
        await Input.FillAsync("Perf");
        await SetTablesAsync("Perf");
        await Page.WaitForFunctionAsync("() => window.schemaRequests.length === 1");
        await HoverWordAsync("Perf");
        await Expect(Tooltip).ToHaveTextAsync("Loading table schema");
        await Page.EvaluateAsync("window.schemaReplies[0]('Perf\\nValue: dynamic')");
        await Expect(Tooltip).ToHaveTextAsync("Perf\nValue: dynamic");
        await Expect(Input).ToHaveValueAsync("Perf");
    }

    [Test]
    public async Task ReplacesSchemaCacheOnWorkspaceRevisionAndIgnoresLateOldResponses()
    {
        await OpenAsync();
        await ConnectSchemaBridgeAsync(true);
        await SetTablesAsync("Perf");
        await Input.FillAsync("Perf");
        await Page.WaitForFunctionAsync("() => window.schemaRequests.length === 1");
        await Page.Locator("[data-kql-editor]").EvaluateAsync("root => root.dataset.kqlCatalogVersion = '2'");
        await Page.WaitForFunctionAsync("() => window.schemaRequests.length === 2");
        await HoverWordAsync("Perf");
        await Page.EvaluateAsync("window.schemaReplies[1]('Perf\\nNewColumn: int')");
        await Expect(Tooltip).ToHaveTextAsync("Perf\nNewColumn: int");
        await Page.EvaluateAsync("window.schemaReplies[0]('Perf\\nOldColumn: string')");
        await Expect(Tooltip).ToHaveTextAsync("Perf\nNewColumn: int");
        Assert.That(await Page.EvaluateAsync<int[]>("window.schemaRequests.map(request => request.version)"), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task ReportsSchemaBridgeFailuresWithoutChangingTheQueryOrKeywordHelp()
    {
        await OpenAsync();
        await Page.EvaluateAsync(
            """
            () => window.kqlCompletion.connect(document.querySelector('[data-kql-editor]'), {
                invokeMethodAsync: () => Promise.reject(new Error('Private bridge failure'))
            })
            """);
        await SetTablesAsync("Perf");
        await Input.FillAsync("Perf | where Value > 1");
        await Expect(Page.Locator("[data-kql-schema-error]")).ToBeVisibleAsync();
        await HoverWordAsync("Perf");
        await Expect(Tooltip).ToHaveTextAsync("Schema unavailable; reload table names to retry");
        await Expect(Input).ToHaveValueAsync("Perf | where Value > 1");
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveTextAsync("where");
    }

    [Test]
    public async Task LargeSchemasAreScrollableAndRemainVisibleWhenTheMouseMovesIntoTheTooltip()
    {
        await OpenAsync();
        await ConnectSchemaBridgeAsync(true);
        await SetTablesAsync("Perf");
        await Input.FillAsync("Perf");
        await Page.WaitForFunctionAsync("() => window.schemaRequests.length === 1");
        await Page.EvaluateAsync("window.schemaReplies[0]('Perf\\n' + Array.from({length:100}, (_, i) => `Column${i}: string`).join('\\n'))");
        await HoverWordAsync("Perf");
        await Expect(Tooltip).ToContainTextAsync("Column99: string");
        Assert.That(await Tooltip.EvaluateAsync<bool>("element => element.scrollHeight > element.clientHeight"), Is.True);
        await Tooltip.HoverAsync();
        await Expect(Tooltip).ToBeVisibleAsync();
        await Page.Mouse.WheelAsync(0, 600);
        await Page.WaitForFunctionAsync("() => document.querySelector('[data-kql-info-tooltip]').scrollTop > 0");
        Assert.That(await Tooltip.EvaluateAsync<int>("element => element.scrollTop"), Is.GreaterThan(0));
        await Page.Mouse.MoveAsync(1, 1);
        await Expect(Tooltip).ToBeHiddenAsync();
    }

    [Test]
    public async Task SchemaInfoIsPlainTextAndDoesNotRunReturnedMarkup()
    {
        await OpenAsync();
        await ConnectSchemaBridgeAsync(true);
        await SetTablesAsync("Perf");
        await Input.FillAsync("Perf");
        await Page.WaitForFunctionAsync("() => window.schemaRequests.length === 1");
        await Page.EvaluateAsync("window.schemaReplies[0]('Perf\\n<img src=x onerror=alert(1)>: string')");
        await HoverWordAsync("Perf");
        await Expect(Tooltip).ToContainTextAsync("<img src=x onerror=alert(1)>: string");
        await Expect(Tooltip.Locator("img")).ToHaveCountAsync(0);
    }

    private Task ConnectSchemaBridgeAsync(bool pending = false) => Page.EvaluateAsync(
        """
        pending => {
            window.schemaRequests = [];
            window.schemaReplies = [];
            window.kqlCompletion.connect(document.querySelector('[data-kql-editor]'), {
                invokeMethodAsync: (method, table, version) => {
                    if (method !== 'GetTableSchemaAsync') throw new Error('Unexpected schema callback');
                    window.schemaRequests.push({table, version});
                    return pending ? new Promise(resolve => window.schemaReplies.push(resolve))
                        : Promise.resolve(`${table}\nCounter: int\nComputer: string\nDetails: dynamic`);
                }
            });
        }
        """, pending);

    [Test]
    public async Task CompletesWorkspaceTablesWithExactCasingAndHighlightsThemInBlackAndBold()
    {
        await OpenAsync();
        await SetTablesAsync("Perf", "Event", "DCDiagData_CL");
        await Input.FillAsync("DCD");
        await Expect(Ghost).ToHaveTextAsync("iagData_CL");
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync("DCDiagData_CL");
        Assert.That(await Page.EvaluateAsync<string>("window.boundQuery"), Is.EqualTo("DCDiagData_CL"));
        await Input.FillAsync("Perf\n| where Computer == 'Event' // Perf\n| join Event on Computer\nDCDiagData_CL");
        var tables = Page.Locator(".free-kql-table");
        await Expect(tables).ToHaveTextAsync(["Perf", "Event", "DCDiagData_CL"]);
        Assert.That(await tables.EvaluateAllAsync<bool>(
            "elements => elements.every(element => getComputedStyle(element).color === 'rgb(0, 0, 0)' && getComputedStyle(element).fontWeight === '700')"), Is.True);
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveTextAsync(["where", "join", "on"]);
        await Input.FillAsync("dcd");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Expect(tables).ToHaveCountAsync(0);
    }

    [Test]
    public async Task CatalogChangesImmediatelyReplaceSuggestionsAndHighlightingWithoutChangingTheQuery()
    {
        await OpenAsync();
        await SetTablesAsync("Perf", "Performance");
        await Input.FillAsync("Per");
        await Expect(Ghost).ToHaveCountAsync(0);
        await SetTablesAsync("Perf");
        await Expect(Ghost).ToHaveTextAsync("f");
        await SetTablesAsync();
        await Expect(Ghost).ToHaveCountAsync(0);
        await Expect(Input).ToHaveValueAsync("Per");
        await Input.FillAsync("Perf");
        await SetTablesAsync("Perf");
        await Expect(Page.Locator(".free-kql-table")).ToHaveTextAsync("Perf");
        await SetTablesAsync("Event");
        await Expect(Page.Locator(".free-kql-table")).ToHaveCountAsync(0);
        await Expect(Input).ToHaveValueAsync("Perf");
    }

    [Test]
    public async Task TablesDoNotMakeContextKeywordsAmbiguousAndDuplicateCandidatesRemainUnique()
    {
        await OpenAsync();
        await SetTablesAsync("where", "where", "byTable", "summarizeTable");
        await Input.FillAsync("wher");
        await Expect(Ghost).ToHaveTextAsync("e");
        await Input.PressAsync("Tab");
        await Expect(Page.Locator(".free-kql-table")).ToHaveTextAsync("where");
        await Input.FillAsync("| sort b");
        await Expect(Ghost).ToHaveTextAsync("y");
        await Input.FillAsync("| summ");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.FillAsync("byT");
        await Input.EvaluateAsync("input => { input.maxLength = 6; input.dispatchEvent(new Event('input')); }");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.EvaluateAsync("input => { input.maxLength = 7; input.dispatchEvent(new Event('input')); }");
        await Expect(Ghost).ToHaveTextAsync("able");
    }

    [Test]
    public async Task BoldTableTokensKeepMirrorWidthsAndFollowingTextAligned()
    {
        await OpenAsync();
        await SetTablesAsync("DCDiagData_CL");
        const string query = "DCDiagData_CL | where CounterValue > 1";
        await Input.FillAsync(query);
        await Expect(Page.Locator(".free-kql-table")).ToHaveTextAsync("DCDiagData_CL");
        Assert.That(await Page.EvaluateAsync<bool>(
            """
            () => {
                const table = document.querySelector('.free-kql-table');
                const input = document.querySelector('textarea');
                const canvas = document.createElement('canvas').getContext('2d');
                const style = getComputedStyle(input);
                canvas.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
                const expected = canvas.measureText(table.textContent).width;
                return Math.abs(table.getBoundingClientRect().width - expected) < 1;
            }
            """), Is.True);
        var before = (await Page.Locator(".free-kql-keyword").BoundingBoxAsync())!;
        await SetTablesAsync();
        await Expect(Page.Locator(".free-kql-table")).ToHaveCountAsync(0);
        var after = (await Page.Locator(".free-kql-keyword").BoundingBoxAsync())!;
        Assert.That(after.X, Is.EqualTo(before.X).Within(1));
        Assert.That(after.Y, Is.EqualTo(before.Y).Within(1));
    }

    private Task SetTablesAsync(params string[] names) => Page.Locator("[data-kql-editor]")
        .EvaluateAsync("(root, names) => root.dataset.kqlTables = JSON.stringify(names)", names);

    [Test]
    public async Task PreviewsOnlyTheMissingSuffixAndAcceptsWithTab()
    {
        await OpenAsync();
        await Input.FillAsync("Perf\n| summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Expect(Input).ToHaveValueAsync("Perf\n| summ");
        await Expect(Page.Locator(".free-kql-completion")).ToHaveAttributeAsync("aria-hidden", "true");
        Assert.That(await Ghost.EvaluateAsync<string>("element => getComputedStyle(element).color"),
            Is.EqualTo("rgb(101, 113, 109)"));
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync("Perf\n| summarize");
        await Expect(Input).ToBeFocusedAsync();
        await Expect(Ghost).ToHaveCountAsync(0);
        Assert.That(await Page.EvaluateAsync<string>("window.boundQuery"), Is.EqualTo("Perf\n| summarize"));
        Assert.That(dictionaryRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task HighlightsCompleteDictionaryWordsInGreenEvenWithoutFocus()
    {
        await OpenAsync();
        const string query =
            """
            let Data = materialize(Perf | where CounterValue > 0);
            Data | extend Value = iff(isnull(CounterValue), 0, CounterValue)
            | order by TimeGenerated desc
            """;
        await Input.FillAsync(query);
        var keywords = Page.Locator(".free-kql-keyword");
        await Expect(keywords).ToHaveTextAsync(
            ["let", "materialize", "where", "extend", "iff", "isnull", "order", "by", "desc"]);
        Assert.That(await keywords.EvaluateAllAsync<bool>(
            "elements => elements.every(element => getComputedStyle(element).color === 'rgb(19, 115, 51)')"), Is.True);
        Assert.That(await Input.EvaluateAsync<string>("input => getComputedStyle(input).caretColor"),
            Is.EqualTo("rgb(24, 34, 31)"));
        Assert.That(await Page.Locator(".free-kql-completion").EvaluateAsync<string>(
            "element => getComputedStyle(element).color"), Is.EqualTo("rgb(24, 34, 31)"));
        await Page.Locator("#execute").FocusAsync();
        await Expect(keywords).ToHaveCountAsync(9);
        await Expect(Input).ToHaveValueAsync(query);
        Assert.That(await Page.Locator(".free-kql-completion").TextContentAsync(), Is.EqualTo(query + "\n"));
    }

    [Test]
    public async Task HighlightsOnlyWholeWordsAndKeepsTheSuggestionGrey()
    {
        await OpenAsync();
        await Input.FillAsync("| summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveCountAsync(0);
        Assert.That(await Ghost.EvaluateAsync<string>("element => getComputedStyle(element).color"),
            Is.EqualTo("rgb(101, 113, 109)"));
        await Input.PressAsync("Tab");
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveTextAsync("summarize");
        await Input.PressAsync("Backspace");
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveCountAsync(0);
        await Input.FillAsync("project project-away mv-expand bag_unpack Mywhere whereX $where WHERE");
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveTextAsync(
            ["project", "project-away", "mv-expand", "bag_unpack"]);
    }

    [TestCase("'where summarize iff'")]
    [TestCase("\"where summarize iff\"")]
    [TestCase("@'where '' summarize iff'")]
    [TestCase("```where\nsummarize iff```")]
    [TestCase("// where summarize iff")]
    [TestCase("/* where summarize iff */")]
    [TestCase("'where summarize iff")]
    public async Task DoesNotHighlightWordsInsideStringsOrComments(string text)
    {
        await OpenAsync();
        await Input.FillAsync(text);
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveCountAsync(0);
        await Expect(Input).ToHaveValueAsync(text);
        Assert.That(await Page.Locator(".free-kql-completion").TextContentAsync(), Is.EqualTo(text + "\n"));
    }

    [Test]
    public async Task HighlightsNestedDictionaryWordsWithoutASeparateKeywordList()
    {
        dictionaryOverride = """[{"word":"custom","next":[{"word":"child"}]}]""";
        await Page.GotoAsync("http://kql.test/");
        await Input.FillAsync("custom child where");
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveTextAsync(["custom", "child"]);
    }

    [Test]
    public async Task WrappedGhostDoesNotMoveFollowingHighlightedQueryLines()
    {
        await OpenAsync();
        const string firstLine = "              summ";
        const string query = firstLine + "\n| where Computer == 'DC01'";
        await Input.EvaluateAsync("input => input.style.width = '190px'");
        await Input.FillAsync(query);
        await Page.Locator("#execute").FocusAsync();
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.free-kql-completion').clientWidth === document.querySelector('textarea').clientWidth");
        var before = (await Page.Locator(".free-kql-keyword").BoundingBoxAsync())!;
        await Input.FocusAsync();
        await Input.EvaluateAsync("(input, end) => input.setSelectionRange(end, end)", firstLine.Length);
        await Expect(Ghost).ToHaveTextAsync("arize");
        var after = (await Page.Locator(".free-kql-keyword").BoundingBoxAsync())!;
        Assert.That(after.X, Is.EqualTo(before.X).Within(1));
        Assert.That(after.Y, Is.EqualTo(before.Y).Within(1));
        Assert.That(await Page.Locator(".free-kql-preview").EvaluateAsync<bool>(
            """
            preview => {
                const nextKeyword = document.querySelector(".free-kql-keyword").getBoundingClientRect();
                return getComputedStyle(preview).overflow === "hidden" &&
                    preview.getBoundingClientRect().bottom <= nextKeyword.top;
            }
            """), Is.True, "The preview must be clipped before the next existing query line.");
        await Input.PressAsync("Escape");
        await Input.PressAsync("Control+a");
        Assert.That(await Input.EvaluateAsync<string>(
            "input => input.value.slice(input.selectionStart, input.selectionEnd)"), Is.EqualTo(query));
        await Expect(Input).ToHaveValueAsync(query);
    }

    [TestCase("pro")]
    [TestCase("project")]
    [TestCase("sum")]
    [TestCase("unknownkeyword")]
    [TestCase("")]
    [TestCase("SUMM")]
    [TestCase("My_summ")]
    [TestCase("$summ")]
    [TestCase("Äsumm")]
    [TestCase("123summ")]
    public async Task DoesNotSuggestAmbiguousCompleteOrNonKeywordPrefixes(string text)
    {
        await OpenAsync();
        await Input.FillAsync(text);
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.PressAsync("Tab");
        await Expect(Page.Locator("#execute")).ToBeFocusedAsync();
        await Expect(Input).ToHaveValueAsync(text);
    }

    [TestCase("Perf | mv-exp", "and")]
    [TestCase("Perf | extend x = tostr", "ing")]
    [TestCase("Perf | where x == 1\n| summ", "arize")]
    [TestCase("Perf // comment\n| summ", "arize")]
    [TestCase("Perf /* comment */ | summ", "arize")]
    [TestCase("print x = 'done' | summ", "arize")]
    [TestCase("print x = @'it''s done' | summ", "arize")]
    [TestCase("print x = ```done``` | summ", "arize")]
    public async Task SupportsFunctionsHyphenatedOperatorsAndCodeAfterLiterals(string text, string suffix)
    {
        await OpenAsync();
        await Input.FillAsync(text);
        await Expect(Ghost).ToHaveTextAsync(suffix);
    }

    [TestCase("Perf // summ")]
    [TestCase("Perf /* summ")]
    [TestCase("print x = 'summ")]
    [TestCase("print x = \"summ")]
    [TestCase("print x = 'escaped\\'summ")]
    [TestCase("print x = @'it''s summ")]
    [TestCase("print x = @\"a\"\"summ")]
    [TestCase("print x = ```multiline\nsumm")]
    public async Task DoesNotSuggestInsideCommentsOrStrings(string text)
    {
        await OpenAsync();
        await Input.FillAsync(text);
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [Test]
    public async Task EscapeDismissesUntilEditingAndEnterStillInsertsNewline()
    {
        await OpenAsync();
        await Input.FillAsync("| summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.PressAsync("Escape");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.PressAsync("Tab");
        await Expect(Page.Locator("#execute")).ToBeFocusedAsync();
        await Input.FocusAsync();
        await Input.PressAsync("End");
        await Input.PressAsync("a");
        await Expect(Ghost).ToHaveTextAsync("rize");
        await Input.PressAsync("Enter");
        await Expect(Input).ToHaveValueAsync("| summa\n");
        Assert.That(await Page.EvaluateAsync<int>("window.submissions"), Is.Zero);
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [Test]
    public async Task KeepsShiftTabNavigationAndDoesNotAcceptOnBlur()
    {
        await OpenAsync();
        await Input.FillAsync("summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.PressAsync("Shift+Tab");
        await Expect(Page.Locator("#before")).ToBeFocusedAsync();
        await Expect(Input).ToHaveValueAsync("summ");
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [Test]
    public async Task SuppressesSelectionCompositionAndDisabledInput()
    {
        await OpenAsync();
        await Input.FillAsync("summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.EvaluateAsync("input => input.setSelectionRange(0, 4)");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.EvaluateAsync("input => input.setSelectionRange(4, 4)");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.DispatchEventAsync("compositionstart");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.DispatchEventAsync("compositionend");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.EvaluateAsync("input => input.disabled = true");
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [Test]
    public async Task CompletesAtLineEndInAnExistingQueryWithoutReplacingTheRest()
    {
        await OpenAsync();
        await Input.FillAsync("Perf | summ\n| take 10");
        await Input.EvaluateAsync("input => input.setSelectionRange(11, 11)");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync("Perf | summarize\n| take 10");
        await Input.FillAsync("Perf | summ | take 10");
        await Input.EvaluateAsync("input => input.setSelectionRange(11, 11)");
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [Test]
    public async Task AlignsPreviewAfterScrollingWrappingAndResizing()
    {
        await OpenAsync();
        await Input.FillAsync(string.Join('\n', Enumerable.Repeat("Perf | take 10", 20)) + "\n| summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.EvaluateAsync(
            """
            input => {
                input.style.height = "120px";
                input.style.width = "190px";
                input.scrollTop = input.scrollHeight;
                input.dispatchEvent(new Event("scroll"));
            }
            """);
        await AssertCaretAlignmentAsync();
        await Input.FillAsync(" ".PadLeft(21) + "summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await AssertCaretAlignmentAsync();
        await Input.EvaluateAsync("input => input.style.width = '310px'");
        await AssertCaretAlignmentAsync();
    }

    [Test]
    public async Task InitializesReplacementEditorsAndReusesTheDictionary()
    {
        await OpenAsync();
        await Input.FillAsync("summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Page.Locator("[data-kql-editor]").EvaluateAsync(
            """
            root => {
                const replacement = root.cloneNode(true);
                replacement.querySelector(".free-kql-completion").replaceChildren();
                root.replaceWith(replacement);
            }
            """);
        await Input.FillAsync("tostr");
        await Expect(Ghost).ToHaveTextAsync("ing");
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync("tostring");
        Assert.That(dictionaryRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task RefreshesServerLoadedDraftsWithoutAnInputEvent()
    {
        await OpenAsync();
        await Input.FillAsync("Perf | take 10");
        await Page.Locator("#execute").FocusAsync();
        await Input.EvaluateAsync(
            """
            input => {
                input.value = "Event | summarize count() by Computer";
                input.closest("[data-kql-editor]").dataset.kqlVersion = "2";
            }
            """);
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveTextAsync(["summarize", "count", "by"]);
        Assert.That(await Page.Locator(".free-kql-completion").TextContentAsync(),
            Is.EqualTo("Event | summarize count() by Computer\n"));
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [TestCase(8, false)]
    [TestCase(9, true)]
    public async Task RespectsTheExactMaximumLengthWhenAcceptingCompletion(int maxLength, bool canAccept)
    {
        await OpenAsync();
        await Input.EvaluateAsync("(input, limit) => input.maxLength = limit", maxLength);
        await Input.FillAsync("summ");
        if (canAccept) await Expect(Ghost).ToHaveTextAsync("arize");
        else await Expect(Ghost).ToHaveCountAsync(0);
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync(canAccept ? "summarize" : "summ");
        if (canAccept) await Expect(Input).ToBeFocusedAsync();
        else await Expect(Page.Locator("#execute")).ToBeFocusedAsync();
    }

    [Test]
    public async Task RespectsInheritedFieldsetDisablingAndReadonlyFields()
    {
        await OpenAsync();
        await Input.FillAsync("summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Page.Locator("#editor-fields").EvaluateAsync("fieldset => fieldset.disabled = true");
        await Expect(Ghost).ToHaveCountAsync(0);
        Assert.That(await Input.EvaluateAsync<bool>("input => input.matches(':disabled') && !input.disabled"), Is.True);
        await Page.Locator("#editor-fields").EvaluateAsync("fieldset => fieldset.disabled = false");
        await Input.FocusAsync();
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.EvaluateAsync("input => input.readOnly = true");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.EvaluateAsync(
            """
            input => {
                input.value = "materialize";
                input.closest("[data-kql-editor]").dataset.kqlVersion = "3";
            }
            """);
        await Expect(Page.Locator(".free-kql-keyword")).ToHaveTextAsync("materialize");
        await Input.PressAsync("F1");
        await Expect(Tooltip).ToContainTextAsync("Caches a tabular expression");
    }

    [Test]
    public async Task SharesTheDictionaryButKeepsMultipleEditorsIndependent()
    {
        await OpenAsync();
        await Page.Locator("[data-kql-editor]").EvaluateAsync(
            """
            root => {
                const second = root.cloneNode(true);
                second.querySelector(".free-kql-completion").replaceChildren();
                root.parentElement.after(second);
            }
            """);
        var roots = Page.Locator("[data-kql-editor]");
        await Expect(roots).ToHaveCountAsync(2);
        var first = roots.Nth(0);
        var second = roots.Nth(1);
        await first.Locator("textarea").FillAsync("Perf | summ");
        await Expect(first.Locator(".free-kql-completion-ghost")).ToHaveTextAsync("arize");
        await second.Locator("textarea").FillAsync("Event | order ");
        await Expect(second.Locator(".free-kql-completion-ghost")).ToHaveTextAsync("by");
        await second.Locator("textarea").PressAsync("Tab");
        await Expect(second.Locator("textarea")).ToHaveValueAsync("Event | order by");
        await Expect(first.Locator("textarea")).ToHaveValueAsync("Perf | summ");
        await Expect(second.Locator(".free-kql-keyword")).ToHaveTextAsync(["order", "by"]);
        var tooltipIds = await Page.Locator("[data-kql-info-tooltip]").EvaluateAllAsync<string[]>(
            "elements => elements.map(element => element.id)");
        Assert.That(tooltipIds.Distinct().Count(), Is.EqualTo(2));
        Assert.That(dictionaryRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task PreservesTheCompactAnalysisEditorTypographyAndAlignment()
    {
        await OpenAsync();
        await Page.Locator("[data-kql-editor]").EvaluateAsync(
            "root => root.classList.add('analysis-kql-editor')");
        await Input.FillAsync("Perf | summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await AssertCaretAlignmentAsync();
        var sizes = await Page.EvaluateAsync<string[]>(
            """
            () => [
                getComputedStyle(document.querySelector("textarea")).fontSize,
                getComputedStyle(document.querySelector(".free-kql-completion")).fontSize
            ]
            """);
        Assert.That(sizes[0], Is.EqualTo(sizes[1]));
        Assert.That(float.Parse(sizes[0].Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture),
            Is.EqualTo(11.84).Within(0.01));
    }

    [Test]
    public async Task UsesTheJsonDictionaryRatherThanHardcodedCandidates()
    {
        dictionaryOverride = """["custom_keyword"]""";
        await Page.GotoAsync("http://kql.test/");
        await Input.FillAsync("custom_");
        await Expect(Ghost).ToHaveTextAsync("keyword");
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync("custom_keyword");
        await Input.FillAsync("summ");
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [TestCase("Perf | order ", "by")]
    [TestCase("Perf | sort ", "by")]
    [TestCase("Perf | order b", "y")]
    [TestCase("Perf | sort b", "y")]
    [TestCase("Perf | order\n", "by")]
    [TestCase("Perf | order /* comment */ b", "y")]
    [TestCase("Perf | order // comment\nb", "y")]
    [TestCase("Perf | order by Computer | summ", "arize")]
    [TestCase("Perf | order by tostr", "ing")]
    [TestCase("let Result = Perf; Result | sort b", "y")]
    [TestCase("print x = 'order' | summ", "arize")]
    [TestCase("Perf | where order == 1 | summ", "arize")]
    public async Task UsesNextRulesOnlyAtThePipelineOperator(string text, string suffix)
    {
        await OpenAsync();
        await Input.FillAsync(text);
        await Expect(Ghost).ToHaveTextAsync(suffix);
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync(text + suffix);
    }

    [TestCase("Perf | order summ")]
    [TestCase("Perf | sort bin")]
    [TestCase("Perf | order by")]
    [TestCase("Perf | where order ")]
    [TestCase("Perf | extend sort ")]
    [TestCase("Perf | order // b")]
    [TestCase("Perf | order 'b")]
    public async Task DoesNotLeakGlobalOrUnrelatedNextCandidates(string text)
    {
        await OpenAsync();
        await Input.FillAsync(text);
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [Test]
    public async Task SupportsNestedRulesAndAmbiguousChildrenFromJson()
    {
        dictionaryOverride =
            """
            [
              { "word": "custom", "next": [
                { "word": "branch", "next": ["leaf"] },
                "second"
              ] }
            ]
            """;
        await Page.GotoAsync("http://kql.test/");
        await Input.FillAsync("| custom ");
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.PressAsync("b");
        await Expect(Ghost).ToHaveTextAsync("ranch");
        await Input.PressAsync("Tab");
        await Input.PressAsync("Space");
        await Expect(Ghost).ToHaveTextAsync("leaf");
        await Input.PressAsync("Tab");
        await Expect(Input).ToHaveValueAsync("| custom branch leaf");
        await Input.FillAsync("| branch");
        await Expect(Ghost).ToHaveCountAsync(0);
    }

    [TestCase("materialize", "Caches a tabular expression")]
    [TestCase("evaluate", "Runs a query plugin")]
    public async Task ShowsExplanationAndExampleOnHoverWithoutEditingTheQuery(string word, string explanation)
    {
        await OpenAsync();
        await Input.FillAsync($"Perf | {word}");
        await Page.Locator("#execute").FocusAsync();
        await HoverWordAsync(word);
        await Expect(Tooltip).ToBeVisibleAsync();
        await Expect(Tooltip).ToContainTextAsync(explanation);
        await Expect(Tooltip).ToContainTextAsync("Example:");
        await Expect(Input).ToHaveValueAsync($"Perf | {word}");
        var id = await Tooltip.GetAttributeAsync("id");
        await Expect(Input).ToHaveAttributeAsync("aria-describedby", $"kql-completion-help {id}");
        await Page.Mouse.MoveAsync(1, 1);
        await Expect(Tooltip).ToBeHiddenAsync();
        await Expect(Input).ToHaveAttributeAsync("aria-describedby", "kql-completion-help");
    }

    [TestCase("mv-apply")]
    [TestCase("mv-expand")]
    [TestCase("parse")]
    [TestCase("parse-kv")]
    [TestCase("parse-where")]
    [TestCase("array_length")]
    [TestCase("array_concat")]
    [TestCase("array_index_of")]
    [TestCase("array_sort_asc")]
    [TestCase("array_sort_desc")]
    [TestCase("bag_keys")]
    [TestCase("bag_merge")]
    [TestCase("bag_pack")]
    [TestCase("bag_unpack")]
    [TestCase("dynamic")]
    [TestCase("extract")]
    [TestCase("extract_all")]
    [TestCase("extract_json")]
    [TestCase("gettype")]
    [TestCase("getschema")]
    [TestCase("make_bag")]
    [TestCase("make_list")]
    [TestCase("make_list_if")]
    [TestCase("make_set")]
    [TestCase("make_set_if")]
    [TestCase("pack_all")]
    [TestCase("parse_csv")]
    [TestCase("parse_json")]
    [TestCase("parse_url")]
    [TestCase("parse_xml")]
    [TestCase("split")]
    [TestCase("strcat_array")]
    [TestCase("todouble")]
    [TestCase("todynamic")]
    [TestCase("tolong")]
    [TestCase("tostring")]
    public async Task ShowsLocalizedDataTransformationHelpWithStandaloneExamples(string word)
    {
        using var dictionary = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "KqlAssets", "kql-completion.json")));
        var entry = dictionary.RootElement.EnumerateArray().Single(item => item.GetProperty("word").GetString() == word);
        await OpenAsync();
        foreach (var culture in new[] { "en", "de-DE" })
        {
            var language = culture.Split('-')[0];
            var info = entry.GetProperty("info").GetProperty(language).GetString()!;
            var exampleLabel = language == "de" ? "\nBeispiel: " : "\nExample: ";
            Assert.That(info.Contains(exampleLabel + "print ") || info.Contains(exampleLabel + "datatable("),
                Is.True, "Each transformation hint must include an example independent of workspace tables.");
            await Page.EvaluateAsync("culture => document.documentElement.lang = culture", culture);
            await Input.FillAsync(word);
            await Input.PressAsync("F1");
            await Expect(Tooltip).ToHaveTextAsync(info);
            await Input.PressAsync("Escape");
            await Expect(Tooltip).ToBeHiddenAsync();
            await HoverWordAsync(word);
            await Expect(Tooltip).ToHaveTextAsync(info);
            await Expect(Input).ToHaveValueAsync(word);
            await Page.Mouse.MoveAsync(1, 1);
        }
    }

    [TestCase("iff", "iff(condition, then, else)")]
    [TestCase("iif", "iif(condition, then, else)")]
    [TestCase("substring", "substring(source, startingIndex [, length])")]
    [TestCase("indexof", "indexof(string, match [, start [, length [, occurrence]]])")]
    [TestCase("case", "case(predicate_1, then_1 [, predicate_2, then_2, ...], else)")]
    [TestCase("round", "round(number [, precision])")]
    [TestCase("strcat", "strcat(arg1 [, arg2, ...])")]
    [TestCase("trim", "trim(regex, source)")]
    [TestCase("datetime_add", "datetime_add(period, amount, datetime)")]
    [TestCase("sumif", "sumif(expr, predicate)")]
    [TestCase("row_number", "row_number([startingIndex [, restart]])")]
    [TestCase("todatetime", "todatetime(value)")]
    public async Task ShowsCompactParameterSignaturesWithoutChangingTheFunctionCall(string word, string signature)
    {
        await OpenAsync();
        const string prefix = "print Result = ";
        var query = prefix + word + "()";
        foreach (var culture in new[] { "en", "de-DE" })
        {
            await Page.EvaluateAsync("culture => document.documentElement.lang = culture", culture);
            await Input.FillAsync(query);
            await HoverWordAsync(word);
            await Expect(Tooltip).ToHaveTextAsync(signature);
            await Page.Mouse.MoveAsync(1, 1);
            await Expect(Tooltip).ToBeHiddenAsync();
            await Input.EvaluateAsync("(input, end) => input.setSelectionRange(end, end)", prefix.Length + word.Length);
            await Input.PressAsync("F1");
            await Expect(Tooltip).ToHaveTextAsync(signature);
            await Input.PressAsync("Escape");
            await Expect(Tooltip).ToBeHiddenAsync();
            await Expect(Input).ToHaveValueAsync(query);
        }
    }

    [Test]
    public async Task ShowsInfoOnTheGreySuggestionAndViaKeyboard()
    {
        await OpenAsync();
        await Input.FillAsync("let Cached = materi");
        await Expect(Ghost).ToHaveTextAsync("alize");
        await HoverWordAsync("alize");
        await Expect(Tooltip).ToContainTextAsync("Caches a tabular expression");
        await Page.Mouse.MoveAsync(1, 1);
        await Expect(Tooltip).ToBeHiddenAsync();
        await Input.PressAsync("F1");
        await Expect(Tooltip).ToBeVisibleAsync();
        await Input.PressAsync("Escape");
        await Expect(Tooltip).ToBeHiddenAsync();
        await Expect(Ghost).ToHaveCountAsync(0);
        await Input.FillAsync("materialize");
        await Input.PressAsync("F1");
        await Expect(Tooltip).ToContainTextAsync("Caches a tabular expression");
        await Input.PressAsync("Escape");
        await Expect(Tooltip).ToBeHiddenAsync();
    }

    [Test]
    public async Task UsesLocalizedInfoAndFallsBackToEnglish()
    {
        await OpenAsync();
        await Page.EvaluateAsync("document.documentElement.lang = 'de-DE'");
        await Input.FillAsync("materialize");
        await HoverWordAsync("materialize");
        await Expect(Tooltip).ToContainTextAsync("Speichert einen tabellarischen Ausdruck");
        await Expect(Tooltip).ToContainTextAsync("Beispiel:");
        await Page.EvaluateAsync("document.documentElement.lang = 'fr-FR'");
        await Input.FillAsync("evaluate");
        await HoverWordAsync("evaluate");
        await Expect(Tooltip).ToContainTextAsync("Runs a query plugin");
    }

    [Test]
    public async Task TreatsInfoAsPlainTextAndAllowsEntriesWithoutInfo()
    {
        dictionaryOverride =
            """
            [
              { "word": "custom", "info": "Example: <img src=x onerror=alert(1)>" },
              { "word": "plain" }
            ]
            """;
        await Page.GotoAsync("http://kql.test/");
        await Input.FillAsync("cust");
        await Expect(Ghost).ToHaveTextAsync("om");
        await Input.PressAsync("Tab");
        await Input.PressAsync("F1");
        await Expect(Tooltip).ToHaveTextAsync("Example: <img src=x onerror=alert(1)>");
        await Expect(Tooltip.Locator("img")).ToHaveCountAsync(0);
        await Input.FillAsync("plain");
        await HoverWordAsync("plain");
        await Expect(Tooltip).ToBeHiddenAsync();
    }

    [TestCase("print x = 'materialize'")]
    [TestCase("print x = ```materialize```")]
    [TestCase("Perf // materialize")]
    [TestCase("Perf /* materialize */")]
    public async Task DoesNotShowWordInfoInsideCommentsOrStrings(string text)
    {
        await OpenAsync();
        await Input.FillAsync(text);
        await Expect(Page.Locator(".free-kql-completion [data-kql-info]")).ToHaveCountAsync(0);
        await Expect(Tooltip).ToBeHiddenAsync();
    }

    [Test]
    public async Task KeepsHoverAlignedAfterScrollAndResizeAndDoesNotBlockSelection()
    {
        await OpenAsync();
        await Input.FillAsync(string.Join('\n', Enumerable.Repeat("Perf | take 10", 20)) + "\n| evaluate");
        await Input.EvaluateAsync(
            """
            input => {
                input.style.height = "120px";
                input.style.width = "190px";
                input.scrollTop = input.scrollHeight;
                input.dispatchEvent(new Event("scroll"));
            }
            """);
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.free-kql-completion').clientHeight === 120 - 2");
        await HoverWordAsync("evaluate");
        await Expect(Tooltip).ToContainTextAsync("Runs a query plugin");
        await Input.DblClickAsync(new() { Position = new() { X = 60, Y = 100 } });
        Assert.That(await Input.EvaluateAsync<bool>("input => input.selectionEnd > input.selectionStart"), Is.True);
    }

    [TestCase(null)]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("""["summarize", "summarize"]""")]
    [TestCase("""["<script>"]""")]
    [TestCase("""[{"word":"order","next":[]} ]""")]
    [TestCase("""[{"word":"order","next":["by","by"]}]""")]
    [TestCase("""[{"word":"order","next":"by"}]""")]
    [TestCase("""[{"word":"materialize","info":42}]""")]
    [TestCase("""[{"word":"materialize","info":""}]""")]
    [TestCase("""[{"word":"materialize","info":{"en":42}}]""")]
    [TestCase("""[{"word":"materialize","info":{}}]""")]
    [TestCase("""[{"word":"order"},"order"]""")]
    public async Task ReportsDictionaryFailuresWithoutDisablingTheEditor(string? invalidDictionary)
    {
        failDictionary = invalidDictionary is null;
        dictionaryOverride = invalidDictionary;
        var messages = new List<string>();
        Page.Console += (_, message) => { if (message.Type == "error") messages.Add(message.Text); };
        await Page.GotoAsync("http://kql.test/");
        await Expect(Page.Locator("[data-kql-completion-error]")).ToBeVisibleAsync();
        await Expect(Page.Locator(".free-kql-highlighted")).ToHaveCountAsync(0);
        await Input.FillAsync("Perf | take 10");
        await Expect(Input).ToHaveValueAsync("Perf | take 10");
        Assert.That(await Input.EvaluateAsync<string>("input => getComputedStyle(input).color"),
            Is.EqualTo("rgb(24, 34, 31)"));
        await Expect(Ghost).ToHaveCountAsync(0);
        Assert.That(messages.Any(message => message.Contains("Could not initialize KQL completion.")), Is.True);
    }

    private async Task OpenAsync()
    {
        await Page.GotoAsync("http://kql.test/");
        await Input.FillAsync("summ");
        await Expect(Ghost).ToHaveTextAsync("arize");
        await Input.FillAsync("");
    }

    private async Task HoverWordAsync(string word)
    {
        await using var coordinates = await Page.WaitForFunctionAsync(
            """
            word => {
                const element = [...document.querySelectorAll(".free-kql-completion span")]
                    .find(item => item.textContent === word);
                const bounds = element?.getClientRects()[0];
                return bounds ? [bounds.left + bounds.width / 2, bounds.top + bounds.height / 2] : false;
            }
            """, word);
        var position = await coordinates.JsonValueAsync<float[]>();
        await Page.Mouse.MoveAsync(position[0], position[1]);
    }

    private async Task AssertCaretAlignmentAsync()
    {
        await Page.WaitForFunctionAsync(
            """
            () => {
                const input = document.querySelector("textarea");
                const overlay = document.querySelector(".free-kql-completion");
                return overlay.clientWidth === input.clientWidth &&
                    overlay.clientHeight === input.clientHeight &&
                    overlay.scrollTop === input.scrollTop;
            }
            """);
        Assert.That(await Input.EvaluateAsync<bool>(
            """
            input => {
                const overlay = document.querySelector(".free-kql-completion");
                const ghost = overlay.querySelector(".free-kql-completion-ghost");
                const style = getComputedStyle(input);
                const mirror = document.createElement("div");
                for (const property of ["font", "letter-spacing", "line-height", "padding", "white-space",
                    "overflow-wrap", "word-break", "tab-size", "box-sizing"]) {
                    mirror.style.setProperty(property, style.getPropertyValue(property));
                }
                mirror.style.position = "absolute";
                mirror.style.width = input.clientWidth + "px";
                mirror.style.visibility = "hidden";
                const prefix = input.value.slice(0, input.selectionStart);
                mirror.textContent = prefix;
                const marker = document.createElement("span");
                marker.textContent = ghost.textContent;
                mirror.append(marker);
                document.body.append(mirror);
                const expected = marker.getClientRects()[0];
                const origin = mirror.getBoundingClientRect();
                const actual = ghost.getClientRects()[0];
                const bounds = overlay.getBoundingClientRect();
                const aligned = Math.abs((expected.x - origin.x) - (actual.x - bounds.x + input.scrollLeft)) < 1 &&
                    Math.abs((expected.y - origin.y) - (actual.y - bounds.y + input.scrollTop)) < 1;
                mirror.remove();
                return aligned;
            }
            """), Is.True, "Ghost text must align with the suffix at the textarea caret.");
    }

    private const string Fixture =
        """
        <!doctype html><html><head><link rel="stylesheet" href="/app.css"></head><body>
        <form class="admin-form" style="width:800px">
            <button type="button" id="before">Before</button>
            <fieldset id="editor-fields" style="display:contents">
            <label>KQL
                <span class="free-kql-editor" data-kql-editor data-kql-version="1" data-kql-catalog-version="1"
                      data-kql-schema-loading="Loading table schema" data-kql-schema-failed="Schema unavailable; reload table names to retry">
                    <textarea rows="12" spellcheck="false" aria-describedby="kql-completion-help"></textarea>
                    <span class="free-kql-completion" aria-hidden="true"></span>
                    <span class="free-kql-info" data-kql-info-tooltip role="tooltip" hidden></span>
                    <small data-kql-completion-error role="status" hidden>Completion unavailable</small>
                    <small data-kql-schema-error role="status" hidden>Schema unavailable; reload table names to retry</small>
                </span>
                <small id="kql-completion-help">Tab accepts, Escape dismisses, Enter inserts a new line.</small>
            </label>
            </fieldset>
            <button type="submit" id="execute">Run query</button>
        </form>
        <script>
            window.boundQuery = "";
            window.submissions = 0;
            document.querySelector("textarea").addEventListener("change", event => window.boundQuery = event.target.value);
            document.querySelector("form").addEventListener("submit", event => { event.preventDefault(); window.submissions++; });
        </script>
        <script src="/kql-completion.js"></script>
        </body></html>
        """;
}
