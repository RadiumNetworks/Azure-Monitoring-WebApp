using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class QueryResultLocalizationTests
{
    private static readonly string DefinitionsPath = Path.Combine(AppContext.BaseDirectory, "AlertDefinitions");
    private static readonly QueryResultPresenter Presenter = new(DefinitionsPath);
    private static readonly JsonSerializerOptions DefinitionOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly IReadOnlyDictionary<string, string> GermanLabels = new Dictionary<string, string>
    {
        ["*"] = "Standard-Alarmidentität",
        ["DCDiag"] = "DCDiag-Testübersicht",
        ["DCPort"] = "Übersicht der DC-Portprüfungen",
        ["Heartbeat"] = "Heartbeat-Abfrageübersicht",
        ["Replication"] = "Replikationsabfrageübersicht",
        ["InventoryUnknownTarget"] = "Unbekanntes Inventarziel",
        ["ScheduledKql"] = "Schwellenwert der geplanten KQL-Abfrage",
        ["ScheduledKqlWorkspace"] = "Workspace-KQL-Schwellenwert"
    };

    public static IEnumerable<object[]> DisplayCases =>
        from path in Directory.GetFiles(DefinitionsPath, "*.json").Order()
        from culture in new[] { "en-US", "de-DE" }
        from count in new[] { 0, 1, 2 }
        select new object[] { Path.GetFileName(path), culture, count };

    [Theory]
    [MemberData(nameof(DisplayCases))]
    public async Task AllDefinitionsLocalizeOnlyDisplayLabelsAndKeepLegacyDefinitionsCompatible(
        string filename, string culture, int count)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        var json = File.ReadAllText(Path.Combine(DefinitionsPath, filename));
        var definition = JsonSerializer.Deserialize<QueryResultDefinition>(json, DefinitionOptions)!;
        var raw = Payload(definition.Type, count);
        var neutral = Assert.IsType<QueryResultPresentation>(Presenter.Parse(raw));
        var original = JsonSerializer.Serialize(neutral);
        var display = Assert.IsType<QueryResultPresentation>(Presenter.ParseForDisplay(raw, text));
        Assert.Equal(count, display.Rows.Count);
        Assert.Equal(culture == "de-DE" ? GermanLabels[definition.Type] : definition.Label, display.Label);
        Assert.Equal(JsonSerializer.Serialize(WithoutDisplayLabels(neutral)), JsonSerializer.Serialize(WithoutDisplayLabels(display)));
        if (culture == "en-US")
        {
            var expected = definition.Type == "Replication" && count == 1
                ? neutral with { Summary = [neutral.Summary[0] with { Text = "1 failure" }, neutral.Summary[1]] }
                : neutral;
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(display));
        }
        if (definition.Type != "Replication")
            Assert.Equal(neutral.Rows.Select(row => row.Title), display.Rows.Select(row => row.Title));

        foreach (var row in display.Rows)
        {
            Assert.Equal(definition.Row.Metadata.Select(field => text[field.LabelKey]), row.Metadata.Select(value => value.Label));
            Assert.Equal(definition.Row.Facts.Select(field => text[field.LabelKey]), row.Facts.Select(value => value.Label));
            if (definition.Type == "Replication")
            {
                Assert.Contains(culture == "de-DE" ? " zu " : " to ", row.Title);
                Assert.Contains("Source <system> & {0}", row.Title);
                Assert.Contains("Destination <system> & {1}", row.Title);
            }
            if (definition.Type == "DCDiag")
            {
                var details = Assert.Single(row.Details);
                Assert.Equal(culture == "de-DE" ? "Alle Tests (2)" : "All 2 tests", details.Label);
                Assert.Equal(["Failed <test>", "Passed <test>"], details.Items.Select(item => item.Label));
                Assert.Equal(["Failed <raw>", "Passed"], details.Items.Select(item => item.Value));
                Assert.Equal(["failure", "success"], details.Items.Select(item => item.Tone));
            }
        }
        Assert.Equal(ExpectedBadges(definition.Type, culture, count), display.Summary.Select(badge => badge.Text));

        var rawHtml = await Render(services, display);
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains($"aria-label=\"{display.Label}\"", html);
        Assert.Equal(definition.CollapseRows, rawHtml.Contains("class=\"query-result-details\"", StringComparison.Ordinal));
        foreach (var row in display.Rows)
        {
            Assert.Contains(row.Title, html);
            foreach (var value in row.Metadata.Concat(row.Facts))
                Assert.Contains(value.Value, html);
            foreach (var item in row.Alerts.Concat(row.Details.SelectMany(group => group.Items)))
                Assert.Contains(item.Value, html);
        }
        Assert.Equal(display.Rows.Any(row => row.Alerts.Count > 0),
            html.Contains(culture == "de-DE" ? "aria-label=\"Alarmdetails\"" : "aria-label=\"Alert details\"", StringComparison.Ordinal));
        Assert.DoesNotContain("<system>", rawHtml);
        Assert.DoesNotContain("<test>", rawHtml);
        Assert.DoesNotContain("QueryResults.", html);

        using var legacy = new DefinitionDirectory();
        var legacyDefinition = JsonNode.Parse(json)!;
        RemoveDisplayProperties(legacyDefinition);
        File.WriteAllText(Path.Combine(legacy.Path, filename), legacyDefinition.ToJsonString());
        var legacyPresenter = new QueryResultPresenter(legacy.Path);
        Assert.Equal(original, JsonSerializer.Serialize(legacyPresenter.Parse(raw)));
        Assert.Equal(original, JsonSerializer.Serialize(legacyPresenter.ParseForDisplay(raw, text)));
        Assert.Equal(original, JsonSerializer.Serialize(Presenter.Parse(raw)));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("en-US", 1)]
    [InlineData("en-US", 2)]
    [InlineData("de-DE", 0)]
    [InlineData("de-DE", 1)]
    [InlineData("de-DE", 2)]
    public async Task CountsFilteringTonesAndUnknownObjectValuesStayNeutral(string culture, int count)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        var raw = JsonNode.Parse(Payload("DCDiag", 1))!;
        var status = Enumerable.Range(0, count).ToDictionary(index => $"Original <test {index}>", _ => "Passed");
        raw["queryResult"]!["rows"]![0]![3] = JsonSerializer.Serialize(status);
        var presentation = Presenter.ParseForDisplay(raw.ToJsonString(), text)!;
        var row = Assert.Single(presentation.Rows);
        Assert.Empty(row.Alerts);
        Assert.Equal([culture == "de-DE" ? $"{count} bestanden" : $"{count} passed"],
            presentation.Summary.Select(badge => badge.Text));
        if (count == 0) Assert.Empty(row.Details);
        else Assert.Equal(culture == "de-DE" ? $"Alle Tests ({count})" : $"All {count} " + (count == 1 ? "test" : "tests"),
            Assert.Single(row.Details).Label);
        var neutral = Presenter.Parse(raw.ToJsonString())!;
        if (count == 1) Assert.Equal("All 1 tests", Assert.Single(neutral.Rows[0].Details).Label);

        var replication = JsonNode.Parse(Payload("Replication", 1))!;
        replication["queryResult"]!["rows"]![0]![11] = count == 0 ? "0" : "1234";
        replication["queryResult"]!["rows"]![0]![12] = "0";
        replication["queryResult"]!["rows"]![0]![13] = "0";
        var result = Presenter.ParseForDisplay(replication.ToJsonString(), text)!;
        Assert.Equal(count == 0 ? "success" : "failure", result.Summary[0].Tone);
        Assert.Equal(culture == "de-DE" ? $"{(count == 0 ? 0 : 1234)} Fehler" : $"{(count == 0 ? 0 : 1234)} failures",
            result.Summary[0].Text);
        Assert.DoesNotContain(result.Rows[0].Facts, value => value.Value == "0");
        Assert.Equal(count == 0 ? 0 : 1, result.Rows[0].Alerts.Count);

        var heartbeat = JsonNode.Parse(Payload("Heartbeat", 1))!;
        heartbeat["queryResult"]!["rows"]![0]![3] = "Current";
        Assert.Empty(Presenter.ParseForDisplay(heartbeat.ToJsonString(), text)!.Rows[0].Alerts);
        heartbeat["queryResult"]!["rows"]![0]![3] = "Heartbeat status <original>";
        Assert.Equal("Heartbeat status <original>",
            Assert.Single(Presenter.ParseForDisplay(heartbeat.ToJsonString(), text)!.Rows[0].Alerts).Value);
    }

    [Fact]
    public void AllEightDefinitionsHaveCompleteExplicitDisplayKeysAndArguments()
    {
        var files = Directory.GetFiles(DefinitionsPath, "*.json");
        Assert.Equal(8, files.Length);
        foreach (var file in files)
        {
            var definition = JsonSerializer.Deserialize<QueryResultDefinition>(File.ReadAllText(file), DefinitionOptions)!;
            Assert.False(string.IsNullOrWhiteSpace(UiResources.GetEnglish(definition.LabelKey)));
            foreach (var rule in definition.Summary)
            {
                Assert.Equal("{0} " + (rule.LabelKey == "QueryResults.Failures" ? "failure" :
                    string.IsNullOrWhiteSpace(rule.SingularLabel) ? rule.Label : rule.SingularLabel),
                    UiResources.GetEnglish(rule.LabelKey + ".One"));
                Assert.Equal("{0} " + rule.Label, UiResources.GetEnglish(rule.LabelKey + ".Other"));
            }
            foreach (var field in definition.Row.Metadata.Concat(definition.Row.Facts))
                Assert.Equal(field.Label, UiResources.GetEnglish(field.LabelKey));
            foreach (var item in definition.Row.Alerts.Concat(definition.Row.Details).Where(item => !string.IsNullOrWhiteSpace(item.Label)))
            {
                if (item.Source.Equals("object-entries", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(1, System.Text.CompositeFormat.Parse(UiResources.GetEnglish(item.LabelKey + ".One")).MinimumArgumentCount);
                    Assert.Equal(1, System.Text.CompositeFormat.Parse(UiResources.GetEnglish(item.LabelKey + ".Other")).MinimumArgumentCount);
                }
                else Assert.Equal(item.LabelArguments.Count,
                    System.Text.CompositeFormat.Parse(UiResources.GetEnglish(item.LabelKey)).MinimumArgumentCount);
            }
            if (!string.IsNullOrWhiteSpace(definition.Row.TitleKey))
                Assert.Equal(definition.Row.TitleArguments.Count,
                    System.Text.CompositeFormat.Parse(UiResources.GetEnglish(definition.Row.TitleKey)).MinimumArgumentCount);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task UnknownDisplayKeyFailsExplicitlyButNeutralParsingAndInvalidPayloadContractRemainUnchanged(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        using var definition = new DefinitionDirectory();
        File.WriteAllText(Path.Combine(definition.Path, "custom.json"),
            """{"type":"Custom","label":"Original label","labelKey":"QueryResults.MissingResource"}""");
        var presenter = new QueryResultPresenter(definition.Path);
        var raw = Payload("Custom", 1);
        Assert.Equal("Original label", presenter.Parse(raw)!.Label);
        Assert.Throws<InvalidOperationException>(() => presenter.ParseForDisplay(raw, text));
        foreach (var invalid in new[] { "", "not json", "{}", """{"queryResult":{"type":"Unknown","columns":[],"rows":[]}}""" })
            Assert.Null(Presenter.ParseForDisplay(invalid, text));
        const string duplicates = """{"queryResult":{"type":"DCPort","columns":[{"name":"RemoteSystem"},{"name":"remotesystem"}],"rows":[]}}""";
        Assert.Throws<ArgumentException>(() => Presenter.ParseForDisplay(duplicates, text));
    }

    internal static string Payload(string type, int count) => JsonSerializer.Serialize(new
    {
        queryResult = new
        {
            type,
            columns = new[] { "Computer", "Site", "TimeGenerated", "Status", "RemoteSystem", "RemoteSite",
                "LocalSystem", "Port", "Protocol", "SourceDSA", "DestDSA", "NumberOfFailures", "LastErrorStatus",
                "LastFailureTime", "LastSuccessTime", "SourceDSASite", "DestDSASite", "NC", "DelayMinutes",
                "SourceAlertName", "SourceAlertId", "Value", "Comparison", "Threshold", "WindowMinutes",
                "Workspace", "EventID", "Users", "AccountWarnings" }.Select(name => new { name, type = "string" }),
            rows = Enumerable.Range(0, count).Select(index => new[]
            {
                $"Observed <computer {index}> & {{0}}", "Site <original>", "2026-11-06T14:05:09Z",
                type == "DCDiag" ? """{"Passed <test>":"Passed","Failed <test>":"Failed <raw>"}""" : "Failed <raw>",
                "Remote <system>", "Remote <site>", "Source <original>", "389", "Current",
                "Source <system> & {0}", "Destination <system> & {1}", "1", "5",
                "2026-11-06T13:05:09Z", "2026-11-06T12:05:09Z", "Source <site>", "Destination <site>", "DC=original",
                "15", "Unknown inventory target <original>", "Source alert ID <original>", "1,234.50", ">",
                "1234,50", "15", "Workspace <original>", "4625", "Accounts / changes <original>", "Account warning <original>"
            }).ToArray()
        }
    });

    private static string[] ExpectedBadges(string type, string culture, int count)
    {
        var german = culture == "de-DE";
        return type switch
        {
            "DCDiag" => count == 0 ? [german ? "0 bestanden" : "0 passed"] :
                [german ? $"{count} bestanden" : $"{count} passed", german ? $"{count} fehlgeschlagen" : $"{count} failed"],
            "DCPort" => [german ? $"{count} fehlgeschlagene " + (count == 1 ? "Prüfung" : "Prüfungen")
                : $"{count} failed " + (count == 1 ? "check" : "checks")],
            "Heartbeat" => [german ? $"{count} " + (count == 1 ? "Heartbeat-Datensatz" : "Heartbeat-Datensätze")
                : $"{count} heartbeat " + (count == 1 ? "record" : "records")],
            "Replication" => [german ? $"{count} Fehler" : $"{count} " + (count == 1 ? "failure" : "failures"),
                german ? $"{count} " + (count == 1 ? "Verbindung" : "Verbindungen")
                    : $"{count} " + (count == 1 ? "link" : "links")],
            "ScheduledKql" => [german ? $"{count} " + (count == 1 ? "System" : "Systeme")
                : $"{count} " + (count == 1 ? "system" : "systems")],
            "ScheduledKqlWorkspace" => [german ? $"{count} " + (count == 1 ? "Workspace" : "Workspaces")
                : $"{count} " + (count == 1 ? "workspace" : "workspaces")],
            _ => []
        };
    }

    private static QueryResultPresentation WithoutDisplayLabels(QueryResultPresentation presentation) =>
        presentation with
        {
            Label = "",
            Summary = presentation.Summary.Select(value => value with { Text = "" }).ToArray(),
            Rows = presentation.Rows.Select(row => row with
            {
                Title = "",
                Metadata = row.Metadata.Select(value => value with { Label = "" }).ToArray(),
                Facts = row.Facts.Select(value => value with { Label = "" }).ToArray(),
                Alerts = row.Alerts.Select(value => value with { Label = "" }).ToArray(),
                Details = row.Details.Select(group => group with
                {
                    Label = "",
                    Items = group.Items.Select(value => value with { Label = "" }).ToArray()
                }).ToArray()
            }).ToArray()
        };

    private static async Task<string> Render(ServiceProvider services, QueryResultPresentation presentation)
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<QueryResultPresentationView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Presentation"] = presentation }))).ToHtmlString());
    }

    private static void RemoveDisplayProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "labelKey", "labelArguments", "titleKey", "titleArguments" })
                obj.Remove(key);
        }
        foreach (var child in node is JsonObject fields ? fields.Select(pair => pair.Value) :
            node is JsonArray array ? array : Enumerable.Empty<JsonNode?>())
            if (child is not null) RemoveDisplayProperties(child);
    }

    private sealed class DefinitionDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "query-result-localization-" + Guid.NewGuid().ToString("N"));
        public DefinitionDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
