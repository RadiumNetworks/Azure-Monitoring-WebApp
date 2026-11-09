using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

public sealed class LocalizationRestTextAcceptanceTests
{
    private const string LiteralNodePattern =
        """<(?<tag>[A-Za-z][\w.-]*)\b(?:[^"'<>]|"[^"]*"|'[^']*')*>(?<text>[^<@]*)</\k<tag>\s*>""";

    private static readonly IReadOnlyDictionary<string, string[]> PreservedLiteralNodes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [@"Components\DatabaseResetPanel.razor"] = ["RESET"],
            [@"Components\GraphSiteMap.razor"] = ["Property of Fabricator-General of Mars, Adeptus Mechanicus"],
            [@"Components\Layout\LoginLayout.razor"] = ["Alert Console", "Azure Monitor"],
            [@"Components\Layout\MainLayout.razor"] = ["Alert Console", "Azure Monitor", "Log Analytics"],
            [@"Components\LegacyAlertResolutionPanel.razor"] = ["RESOLVE", "Resolved"],
            [@"Components\Pages\AdminAlertAuthentication.razor"] =
                ["AdminAccess", "azp", "entra", "GET /api/alert-query-refresh-targets", "idtyp=app",
                    "open", "POST /api/alerts", "PUT /api/alerts/{id}/query-result", "sql"],
            [@"Components\Pages\AdminAnalysisQueries.razor"] = ["KQL"],
            [@"Components\Pages\AdminLogAnalytics.razor"] = ["CrossTenantLogAnalytics:Enabled", "Key Vault", "true"],
            [@"Components\Pages\AdminScheduledQueries.razor"] = ["KQL"],
            [@"Components\Pages\AdminSettings.razor"] = ["API", "entra", "open", "sql"],
            [@"Components\Pages\AdminUsers.razor"] = ["sql"],
            [@"Components\Pages\Error.razor"] = ["Alert Console"],
            [@"Components\Pages\Graph.razor"] = ["World map configurable.svg"],
            [@"Components\Pages\Home.razor"] = ["Alert Console"],
            [@"Components\Pages\Queries.razor"] = ["HTTP 200 \u00b7 application/json"]
        };

    [Fact]
    public void DirectAlphabeticMarkupNodesAreExactlyTheReviewedPreservedTexts()
    {
        var actual = ComponentFiles().SelectMany(file => LiteralNodes(Markup(File.ReadAllText(file)))
            .Select(text => Path.GetRelativePath(ProjectDirectory(), file).Replace(Path.DirectorySeparatorChar, '\\')
                + ": " + text)).Distinct().Order(StringComparer.Ordinal);
        var expected = PreservedLiteralNodes.SelectMany(entry => entry.Value.Select(text => entry.Key + ": " + text))
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DirectAlphabeticAccessibilityAndInputHintsAreNotHardCoded()
    {
        foreach (var file in ComponentFiles())
        {
            var matches = Regex.Matches(Markup(File.ReadAllText(file)),
                @"\b(?:title|placeholder|aria-label|alt)=""(?<value>[^""]*)""");
            foreach (Match match in matches)
            {
                var value = match.Groups["value"].Value;
                Assert.False(!value.Contains('@') && Regex.IsMatch(value, @"\p{L}"),
                    $"{Path.GetRelativePath(ProjectDirectory(), file)}: {match.Value}");
            }
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void LiteralRichContentResourceKeysKeepTheirActualRendererContract(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        var references = 0;
        foreach (var file in ComponentFiles())
        foreach (Match match in Regex.Matches(Markup(File.ReadAllText(file)),
            @"<(?<component>LocalizedContent|LocalizedEmphasis)\b[^>]*\bResourceKey=""(?<key>[^""@]*)"""))
        {
            references++;
            var key = match.Groups["key"].Value;
            var value = text[key];
            Assert.NotEqual(key, value);
            if (match.Groups["component"].Value == "LocalizedEmphasis")
            {
                Assert.Equal(new[] { 0 }, LocalizationResourceAcceptanceTests.ParameterIndexes(value));
                Assert.Equal(2, value.Split("{0}", StringSplitOptions.None).Length);
            }
            else
            {
                Assert.InRange(CompositeFormat.Parse(value).MinimumArgumentCount, 0, 6);
                foreach (Match slot in Regex.Matches(value, @"\{[0-9][^}]*\}"))
                    Assert.Matches(@"^\{[0-5]\}$", slot.Value);
            }
        }
        Assert.True(references > 0, "No literal rich-content resources were checked.");
    }

    [Theory]
    [InlineData("<p>Hello</p>", "Hello")]
    [InlineData("<p>\nLong   text\n</p>", "Long text")]
    [InlineData("<p><strong>RESET</strong></p>", "RESET")]
    [InlineData("<p>\u00c4</p>", "\u00c4")]
    [InlineData("<button onclick=\"() => ZoomAsync(1.25)\">+</button>", null)]
    [InlineData("<p>@Text[\"Common.Empty\"]</p>", null)]
    public void LiteralNodeScanHandlesQuotedAttributesAndOnlyClaimsDirectText(string source, string? expected)
    {
        var nodes = LiteralNodes(source).ToArray();
        if (expected is null) Assert.Empty(nodes);
        else Assert.Equal(new[] { expected }, nodes);
    }

    private static IEnumerable<string> LiteralNodes(string source) =>
        Regex.Matches(source, LiteralNodePattern).Cast<Match>()
            .Select(match => Regex.Replace(match.Groups["text"].Value, @"\s+", " ").Trim())
            .Where(text => Regex.IsMatch(text, @"\p{L}"));

    private static string Markup(string source)
    {
        var code = source.IndexOf("@code", StringComparison.Ordinal);
        return code < 0 ? source : source[..code];
    }

    private static IEnumerable<string> ComponentFiles() =>
        Directory.EnumerateFiles(Path.Combine(ProjectDirectory(), "Components"), "*.razor", SearchOption.AllDirectories);

    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));
}
