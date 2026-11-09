using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MonitoringApp.Tests;

public sealed class LocalizationResourceAcceptanceTests
{
    private const string LiteralReferencePattern =
        """\b(?:Text|text)\s*(?:\[\s*"(?<key>[^"]*)"\s*\]|\.(?<method>Format|Plural)\s*\(\s*"(?<key>[^"]*)"\s*[,)])""";

    [Theory]
    [InlineData("UiResources.resx", "")]
    [InlineData("UiResources.de-DE.resx", "de-DE")]
    public void SourceResourcesMatchThePackagedResourceSet(string file, string culture)
    {
        var path = Path.Combine(ProjectDirectory(), "Resources", file);
        var entries = XDocument.Load(path).Root!.Elements("data").ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries.GroupBy(entry => (string?)entry.Attribute("name")),
            group => Assert.Single(group));
        var source = entries.ToDictionary(
            entry => (string)entry.Attribute("name")!,
            entry => entry.Element("value")!.Value,
            StringComparer.Ordinal);
        var manager = new ResourceManager("MonitoringApp.Resources.UiResources", typeof(UiResources).Assembly);
        var resources = manager.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false);
        Assert.NotNull(resources);
        Assert.Equal(source.Keys.Order(StringComparer.Ordinal),
            resources.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).Order(StringComparer.Ordinal));
        foreach (var (key, value) in source)
        {
            Assert.False(string.IsNullOrWhiteSpace(key), path);
            Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: {key}");
            Assert.NotEqual(key, value);
            Assert.Equal(value, resources.GetString(key));
            _ = ParameterIndexes(value);
        }
    }

    [Fact]
    public void EveryDirectLiteralUiReferenceHasEnglishResources()
    {
        var scopes = new[] { "Components", "Presentation", "Infrastructure", "Services", "Domain", "Endpoints", "Middleware" };
        var files = scopes.SelectMany(scope => Directory.EnumerateFiles(
                Path.Combine(ProjectDirectory(), scope), "*", SearchOption.AllDirectories))
            .Where(file => Path.GetExtension(file) is ".cs" or ".razor")
            .Append(Path.Combine(ProjectDirectory(), "Program.cs"));
        var references = 0;
        foreach (var file in files)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), LiteralReferencePattern))
            {
                references++;
                var key = match.Groups["key"].Value;
                var keys = match.Groups["method"].Value == "Plural"
                    ? new[] { key + ".One", key + ".Other" }
                    : new[] { key };
                foreach (var required in keys)
                    Assert.False(string.IsNullOrWhiteSpace(UiResources.GetEnglish(required)),
                        $"{Path.GetRelativePath(ProjectDirectory(), file)}: {required}");
            }
        }
        Assert.True(references > 0, "No literal UI resource references were examined.");
        UiResources.ValidateEnglish();
    }

    [Theory]
    [InlineData("Text[\"Common.Cancel\"]", "Common.Cancel", false)]
    [InlineData("text.Format(\"Common.Empty\", value)", "Common.Empty", false)]
    [InlineData("Text.Plural(\"Rules.Count\", count)", "Rules.Count", true)]
    [InlineData("Text \n [ \"Common.Cancel\" ]", "Common.Cancel", false)]
    [InlineData("Text[\"\"]", "", false)]
    public void DirectReferenceScanIncludesEmptyKeysAndWhitespace(string source, string key, bool plural)
    {
        var match = Assert.Single(Regex.Matches(source, LiteralReferencePattern).Cast<Match>());
        Assert.Equal(key, match.Groups["key"].Value);
        Assert.Equal(plural, match.Groups["method"].Value == "Plural");
    }

    [Theory]
    [InlineData("Text[key]")]
    [InlineData("Text[$\"Prefix.{suffix}\"]")]
    [InlineData("text.Format(\"Prefix.\" + suffix, value)")]
    [InlineData("Text[\"Prefix.\" + suffix]")]
    public void DynamicReferencesAreNotMistakenForCompleteLiteralKeys(string source) =>
        Assert.Empty(Regex.Matches(source, LiteralReferencePattern).Cast<Match>());

    [Theory]
    [InlineData("{0} {2}", "{1} {2}", false)]
    [InlineData("{0} {2}", "{2} {0}", true)]
    [InlineData("{0} {1}", "{1} {1}", false)]
    [InlineData("{0} {1}", "{1} {0} {1}", true)]
    [InlineData("{{0}} {1}", "{0} {1}", false)]
    [InlineData("{{0}} {1}", "{1} {{0}}", true)]
    [InlineData("{{{0}}}", "{0}", true)]
    [InlineData("{0,12:N0} {2:u}", "{2:u} {0,-12:N0}", true)]
    [InlineData("{{literal}}", "Text", true)]
    [InlineData("{0}", "{{0}}", false)]
    public void ParameterIdentityAllowsReorderingAndRepetitionButNotSubstitution(
        string english, string german, bool compatible) =>
        Assert.Equal(compatible, ParameterIndexes(english).SequenceEqual(ParameterIndexes(german)));

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{x}")]
    [InlineData("{-1}")]
    [InlineData("{0")]
    [InlineData("{0:broken{format}}")]
    public void MalformedCompositeFormatsAreRejected(string value) =>
        Assert.Throws<FormatException>(() => ParameterIndexes(value));

    internal static int[] ParameterIndexes(string value)
    {
        var format = CompositeFormat.Parse(value);
        var recorder = new ParameterRecorder();
        object[] arguments = Enumerable.Range(0, format.MinimumArgumentCount).Cast<object>().ToArray();
        _ = string.Format(recorder, format, arguments);
        return recorder.Indexes.Order().ToArray();
    }

    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));

    private sealed class ParameterRecorder : IFormatProvider, ICustomFormatter
    {
        public HashSet<int> Indexes { get; } = [];

        public object? GetFormat(Type? formatType) =>
            formatType == typeof(ICustomFormatter) ? this : null;

        public string Format(string? format, object? argument, IFormatProvider? provider)
        {
            Indexes.Add(Assert.IsType<int>(argument));
            return "";
        }
    }
}
