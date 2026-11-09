using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class QueryJsonImportTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void EmptyImportShowsAnActionableError(string json)
    {
        var exception = Assert.Throws<QueryTemplateValidationException>(() => QueryTemplateJson.ValidateImport(json));
        Assert.Equal(QueryTemplateValidationCode.InputRequired, exception.Code);
        Assert.Equal("Paste a query JSON document first.", exception.Message);
    }

    [Fact]
    public void ImportLimitMeasuresUtf8BytesIncludingWhitespace()
    {
        Assert.Equal(65536, QueryTemplateJson.MaxImportBytes);
        QueryTemplateJson.ValidateImport("{}" + new string(' ', 65534));
        QueryTemplateJson.ValidateImport(new string('\u20ac', 21845) + " ");
        var exception = Assert.Throws<QueryTemplateValidationException>(() =>
            QueryTemplateJson.ValidateImport(new string('\u20ac', 21845) + "  "));
        Assert.Equal(QueryTemplateValidationCode.ImportTooLarge, exception.Code);
        Assert.Equal("Query JSON must not exceed 64 KiB (UTF-8).", exception.Message);
        Assert.Equal(QueryTemplateValidationCode.ImportTooLarge, Assert.Throws<QueryTemplateValidationException>(
            () => QueryTemplateJson.ValidateImport("{}" + new string(' ', 65535))).Code);
    }

    [Fact]
    public void AllTemplateDocumentsFitTheImportLimitAndOpenIndependentDrafts()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var directory in new[] { "ActiveDirectory", "Scheduled" })
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", directory), "*.json"))
        {
            var json = File.ReadAllText(file);
            QueryTemplateJson.ValidateImport(json);
            if (directory == "ActiveDirectory")
            {
                var draft = AnalysisQueryTemplates.Parse(json);
                var snapshot = JsonSerializer.Deserialize<AnalysisQuerySnapshot>(json, jsonOptions)!;
                snapshot = snapshot with { Visualization = snapshot.Visualization ?? new() };
                Assert.True(JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(snapshot, jsonOptions),
                    JsonSerializer.SerializeToNode(AnalysisQueryRegistry.CreateSnapshot(draft), jsonOptions)), file);
                Assert.Equal(Guid.Empty, draft.Id);
                Assert.Equal(AnalysisQueryStatuses.Draft, draft.Status);
                Assert.Empty(draft.Versions);
                Assert.Empty(draft.Workspaces);
                Assert.Null(draft.PublishedVersionId);
                Assert.NotSame(draft, AnalysisQueryTemplates.Parse(json));
            }
            else
            {
                var draft = ScheduledQueryTemplates.Parse(json);
                Assert.NotEqual(Guid.Empty, draft.Id);
                Assert.NotEqual(draft.Id, ScheduledQueryTemplates.Parse(json).Id);
                Assert.False(draft.Enabled);
                Assert.Empty(draft.WorkspaceKeys);
                Assert.Equal(0, draft.Revision);
                Assert.Null(draft.LastRunUtc);
                Assert.Null(draft.LastWindowEndUtc);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("true")]
    [InlineData("\"query\"")]
    public void BothParsersRejectMissingOrMalformedDocuments(string json)
    {
        var code = json == "null" ? QueryTemplateValidationCode.NullTemplate : QueryTemplateValidationCode.InvalidJson;
        foreach (var exception in new[]
        {
            Assert.Throws<QueryTemplateValidationException>(() => AnalysisQueryTemplates.Parse(json)),
            Assert.Throws<QueryTemplateValidationException>(() => ScheduledQueryTemplates.Parse(json))
        })
        {
            Assert.Equal(code, exception.Code);
            Assert.IsAssignableFrom<InvalidOperationException>(exception);
            if (code == QueryTemplateValidationCode.NullTemplate)
            {
                Assert.Null(exception.InnerException);
                Assert.Equal("The template must not be null.", exception.Message);
            }
            else
            {
                var parser = Assert.IsAssignableFrom<JsonException>(exception.InnerException);
                Assert.Contains("query JSON: " + parser.Message, exception.Message);
            }
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("enabled")]
    [InlineData("workspaces")]
    [InlineData("workspaceKeys")]
    [InlineData("publishedVersionId")]
    [InlineData("versions")]
    [InlineData("lastRunUtc")]
    [InlineData("revision")]
    [InlineData("leaseOwner")]
    public void NeitherSchemaAcceptsIdentityPublicationOrRuntimeState(string property)
    {
        foreach (var directory in new[] { "ActiveDirectory", "Scheduled" })
        {
            var file = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", directory), "*.json").First();
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json[property] = null;
            if (directory == "ActiveDirectory")
                Assert.Equal(QueryTemplateValidationCode.InvalidJson, Assert.Throws<QueryTemplateValidationException>(
                    () => AnalysisQueryTemplates.Parse(json.ToJsonString())).Code);
            else
                Assert.Equal(QueryTemplateValidationCode.InvalidJson, Assert.Throws<QueryTemplateValidationException>(
                    () => ScheduledQueryTemplates.Parse(json.ToJsonString())).Code);
        }
    }

    [Fact]
    public void SchemasAreNotInterchangeable()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "AnalysisQueries");
        var analysis = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "ActiveDirectory"), "*.json").First());
        var scheduled = File.ReadAllText(Path.Combine(root, "Scheduled", "cpu-high.json"));
        Assert.Equal(QueryTemplateValidationCode.InvalidJson, Assert.Throws<QueryTemplateValidationException>(
            () => ScheduledQueryTemplates.Parse(analysis)).Code);
        Assert.Equal(QueryTemplateValidationCode.InvalidJson, Assert.Throws<QueryTemplateValidationException>(
            () => AnalysisQueryTemplates.Parse(scheduled)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothSchemasRejectNullAndWronglyTypedRequiredNames(bool wrongType)
    {
        foreach (var directory in new[] { "ActiveDirectory", "Scheduled" })
        {
            var file = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", directory), "*.json").First();
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json["name"] = wrongType ? JsonValue.Create(123) : null;
            var exception = directory == "ActiveDirectory"
                ? Assert.Throws<QueryTemplateValidationException>(() => AnalysisQueryTemplates.Parse(json.ToJsonString()))
                : Assert.Throws<QueryTemplateValidationException>(() => ScheduledQueryTemplates.Parse(json.ToJsonString()));
            Assert.Equal(QueryTemplateValidationCode.InvalidJson, exception.Code);
            Assert.IsAssignableFrom<JsonException>(exception.InnerException);
        }
    }

    public static IEnumerable<object[]> InvalidImportCases()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        foreach (var (input, code) in new[]
        {
            ("empty", QueryTemplateValidationCode.InputRequired), ("whitespace", QueryTemplateValidationCode.InputRequired),
            ("oversize-ascii", QueryTemplateValidationCode.ImportTooLarge), ("oversize-utf8", QueryTemplateValidationCode.ImportTooLarge),
            ("null", QueryTemplateValidationCode.NullTemplate), ("malformed", QueryTemplateValidationCode.InvalidJson),
            ("missing", QueryTemplateValidationCode.InvalidJson), ("unknown", QueryTemplateValidationCode.InvalidJson),
            ("array", QueryTemplateValidationCode.InvalidJson)
        })
            yield return [culture, input, code];
    }

    public static string InvalidImportJson(string input) => input switch
    {
        "empty" => "", "whitespace" => " \r\n ",
        "oversize-ascii" => "{}" + new string(' ', 65535),
        "oversize-utf8" => new string('\u20ac', 21845) + "  ",
        "null" => "null", "malformed" => "{", "missing" => "{}",
        "unknown" => """{"unknown":"private JSON diagnostic"}""", "array" => "[]",
        _ => throw new ArgumentOutOfRangeException(nameof(input))
    };

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    [InlineData("de-DE", false)]
    [InlineData("de-DE", true)]
    public async Task SharedPasteOnlyInputHasAnExplicitActionAndRespectsBusyState(string culture, bool busy)
    {
        using var selection = new UiSelection(culture);
        var formatCulture = CultureInfo.CurrentCulture.Name;
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<QueryJsonImport>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Busy"] = busy, ["Description"] = "Original <description> &" }));
            return root.ToHtmlString();
        });
        var decoded = WebUtility.HtmlDecode(html);
        Assert.Contains(culture == "de-DE" ? "Abfrage-JSON importieren" : "Import query JSON", decoded);
        Assert.Contains("Original &lt;description&gt; &amp;", html);
        Assert.Contains("maxlength=\"65536\"", html);
        Assert.Contains("rows=\"8\"", html);
        Assert.Contains("spellcheck=\"false\"", html);
        Assert.Contains(culture == "de-DE" ? "Abfrage-JSON (maximal 64 KiB UTF-8)" : "Query JSON (maximum 64 KiB UTF-8)", decoded);
        Assert.Contains(culture == "de-DE" ? "aria-label=\"Abfrage-JSON\"" : "aria-label=\"Query JSON\"", decoded);
        Assert.Contains(culture == "de-DE" ? "JSON in neuen Entwurf einlesen" : "Read JSON into new draft", decoded);
        Assert.Contains(culture == "de-DE"
            ? "Öffnet einen neuen, nicht gespeicherten Entwurf und ersetzt den aktuellen Editorinhalt. Nichts wird gespeichert, veröffentlicht, aktiviert oder ausgeführt."
            : "Opens a new, unsaved draft and replaces the current editor contents. Nothing is saved, published, enabled or executed.", decoded);
        Assert.DoesNotContain("type=\"file\"", html);
        var controls = Regex.Matches(html, "<(?:textarea|button)\\b[^>]*>");
        Assert.Equal(2, controls.Count);
        Assert.All(controls.Cast<Match>(), control => Assert.Equal(busy, control.Value.Contains("disabled", StringComparison.Ordinal)));
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task ImportCallbackPreservesExactInputIncludingWhitespaceUnicodeAndLimit(string culture)
    {
        using var selection = new UiSelection(culture);
        var formatCulture = CultureInfo.CurrentCulture.Name;
        var received = new List<string>();
        var input = new QueryJsonImport();
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(QueryJsonImport.OnImport)] = EventCallback.Factory.Create<string>(received, (string json) => received.Add(json))
        }).SetParameterProperties(input);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string[] payloads = ["", " \r\n ", "{\r\n \"name\": \"Abfrage \\u00e4 <script> &\", \"kql\": \"print Euro='€'\" \r\n}", "{}" + new string(' ', 65534)];
        foreach (var json in payloads)
        {
            typeof(QueryJsonImport).GetField("json", flags)!.SetValue(input, json);
            await (Task)typeof(QueryJsonImport).GetMethod("ImportAsync", flags)!.Invoke(input, [])!;
        }
        Assert.Equal(payloads, received);
        Assert.Equal(formatCulture, CultureInfo.CurrentCulture.Name);
    }

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiSelection(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
}
