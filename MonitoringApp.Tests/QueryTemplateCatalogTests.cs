using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

public sealed class QueryTemplateCatalogTests
{
    public static IEnumerable<object[]> TemplateEntries() =>
        QueryTemplateCatalog.Entries.Where(entry => entry.Library is not null)
            .Where(entry => entry == QueryTemplateCatalog.GetActive(entry.TemplateId))
            .Select(entry => new object[] { entry.TemplateId, entry.Revision });

    [Fact]
    public void CatalogCoversExactlyThe48PackagedTemplatesAndThreeSeparateSeeds()
    {
        var entries = QueryTemplateCatalog.Entries;
        Assert.Equal(52, entries.Count);
        Assert.Equal(40, entries.Count(entry => entry.Library == "ActiveDirectory"));
        Assert.Equal(9, entries.Count(entry => entry.Library == "Scheduled"));
        Assert.Equal(3, entries.Count(entry => entry.Library is null));
        Assert.Equal(51, entries.Select(entry => entry.TemplateId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(93, entries.SelectMany(entry => entry.DescriptionResourceKey is { } key
            ? new[] { entry.NameResourceKey, key } : new[] { entry.NameResourceKey }).Distinct().Count());
        Assert.All(entries.Where(entry => entry.Library == "Scheduled"), entry => Assert.Null(entry.Description));
        foreach (var library in new[] { "ActiveDirectory", "Scheduled" })
        {
            var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", library), "*.json")
                .Select(Path.GetFileName).Order(StringComparer.Ordinal);
            Assert.Equal(files, entries.Where(entry => entry.Library == library)
                .Select(entry => entry.FileName).Distinct().Order(StringComparer.Ordinal));
        }
        Assert.Contains("MonitoringApp.QueryTemplateCatalog.v1.json",
            typeof(QueryTemplateCatalog).Assembly.GetManifestResourceNames());
        Assert.Contains("MonitoringApp.QueryTemplateCatalog.v2.json",
            typeof(QueryTemplateCatalog).Assembly.GetManifestResourceNames());
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Localization", "QueryTemplateCatalog.v1.json")));
    }

    [Fact]
    public void DcdiagKeepsItsHistoricalRevisionAndResourcesWithoutTrustingOldBytesAsTheNewTemplate()
    {
        var entries = QueryTemplateCatalog.Entries.Where(entry => entry.TemplateId == "kql/ad/ad-dcdiag").ToArray();
        Assert.Equal(2, entries.Length);
        var original = QueryTemplateCatalog.Get("kql/ad/ad-dcdiag",
            "B1FE78634336D1BE76BFB1F5E60425379E23B6BEEA9E58F04907D63439F72D61");
        var active = QueryTemplateCatalog.GetActive(original.TemplateId);
        Assert.NotEqual(original.Revision, active.Revision);
        Assert.Equal(original.NameResourceKey, active.NameResourceKey);
        Assert.Equal(original.DescriptionResourceKey, active.DescriptionResourceKey);
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.ReadPackagedTemplate(original));
        Assert.Contains("bag_unpack", QueryTemplateCatalog.ReadPackagedTemplate(active));
    }

    [Theory]
    [MemberData(nameof(TemplateEntries))]
    public void EveryTemplatePreservesExactFileBytesOriginalTextsAndNeutralParsedData(string templateId, string revision)
    {
        var entry = QueryTemplateCatalog.Get(templateId, revision);
        var file = Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", entry.Library!, entry.FileName!);
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(revision, Convert.ToHexString(SHA256.HashData(bytes)));
        QueryTemplateCatalog.VerifyFile(entry, file);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(document.RootElement.GetProperty("name").GetString(), entry.Name);
        if (entry.Library == "ActiveDirectory")
        {
            Assert.Equal(document.RootElement.GetProperty("description").GetString(), entry.Description);
            var parsed = AnalysisQueryTemplates.Parse(Encoding.UTF8.GetString(bytes));
            Assert.Equal(entry.Name, parsed.Name);
            Assert.Equal(entry.Description, parsed.Description);
            Assert.Equal(document.RootElement.GetProperty("kql").GetString(), parsed.Kql);
            Assert.Equal("Operator", parsed.MinimumRole);
            Assert.Equal(Guid.Empty, parsed.Id);
        }
        else
        {
            Assert.False(document.RootElement.TryGetProperty("description", out _));
            var parsed = ScheduledQueryTemplates.Parse(Encoding.UTF8.GetString(bytes));
            Assert.Equal(entry.Name, parsed.Name);
            Assert.Equal(document.RootElement.GetProperty("kql").GetString(), parsed.Kql);
            Assert.False(parsed.Enabled);
            Assert.Equal(0, parsed.Revision);
        }
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void All93FieldsHaveRealResourcesWithoutChangingOriginalDataOrCurrentCulture(string culture)
    {
        using var selection = new CultureScope(culture);
        var originalCulture = CultureInfo.CurrentCulture;
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<UiText>();
        UiResources.ValidateEnglish();
        foreach (var entry in QueryTemplateCatalog.Entries)
        {
            var originalName = entry.Name;
            var originalDescription = entry.Description;
            var name = text[entry.NameResourceKey];
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.NotEqual(entry.NameResourceKey, name);
            if (culture == "en-US")
                Assert.Equal(originalName, name);
            if (entry.DescriptionResourceKey is { } key)
            {
                var description = text[key];
                Assert.False(string.IsNullOrWhiteSpace(description));
                Assert.NotEqual(key, description);
                if (culture == "en-US")
                    Assert.Equal(originalDescription, description);
            }
            Assert.Equal(originalName, entry.Name);
            Assert.Equal(originalDescription, entry.Description);
        }
        Assert.Equal(culture, CultureInfo.CurrentUICulture.Name);
        Assert.Equal(originalCulture, CultureInfo.CurrentCulture);
        var processor = QueryTemplateCatalog.Entries.Single(entry => entry.TemplateId == "kql/ad/ad-processor");
        Assert.Equal(culture == "de-DE" ? "Prozessorauslastung" : "Processor utilization", text[processor.NameResourceKey]);
        var kerberos = QueryTemplateCatalog.Entries.Single(entry => entry.TemplateId == "kql/scheduled/kerberos-stopped");
        Assert.Equal(culture == "de-DE" ? "Kerberos-Authentifizierungen ausgeblieben" : "Kerberos authentications stopped", text[kerberos.NameResourceKey]);
    }

    [Fact]
    public void FileMismatchFailsExplicitlyWithoutChangingOrGrantingTrustToTheFile()
    {
        var entry = QueryTemplateCatalog.Entries.First(entry => entry.Library is not null);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a packaged template");
            var before = File.ReadAllBytes(path);
            var error = Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.VerifyFile(entry, path));
            Assert.Contains(entry.TemplateId, error.Message);
            Assert.Contains("does not match", error.Message);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":1,"templates":[null]}""")]
    [InlineData("""{"schemaVersion":1,"templates":null}""")]
    [InlineData("""{"schemaVersion":1,"templates":[]}""")]
    [InlineData("""{"schemaVersion":2,"templates":[]}""")]
    [InlineData("""{"schemaVersion":1,"templates":[],"untrusted":true}""")]
    public void InvalidDocumentsFailExplicitly(string json) =>
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Parse(json));

    [Theory]
    [InlineData("templateId", "\"kql/ad/../escape\"")]
    [InlineData("templateId", "\"kql/AD/ad-processor\"")]
    [InlineData("templateId", "null")]
    [InlineData("revision", "\"abc\"")]
    [InlineData("revision", "null")]
    [InlineData("name", "\" \"")]
    [InlineData("name", "\" leading space\"")]
    [InlineData("description", "null")]
    [InlineData("resourceVersion", "0")]
    [InlineData("resourceVersion", "-1")]
    [InlineData("untrusted", "true")]
    public void InvalidEntriesCannotBecomeCatalogOrigins(string field, string json)
    {
        var entry = JsonSerializer.SerializeToNode(QueryTemplateCatalog.Entries.First(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        entry[field] = JsonNode.Parse(json);
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Parse(Document(entry)));
    }

    [Fact]
    public void DuplicateRevisionsAndInventedScheduledDescriptionsAreRejected()
    {
        var first = JsonSerializer.SerializeToNode(QueryTemplateCatalog.Entries.First(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Parse(Document(first, first.DeepClone())));
        var scheduled = JsonSerializer.SerializeToNode(QueryTemplateCatalog.Entries.First(entry => entry.Library == "Scheduled"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        scheduled["description"] = "Not part of a scheduled template";
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Parse(Document(scheduled)));
    }

    [Fact]
    public void RevisionAndOriginMustBeExactAndOldResourceNamespacesRemainIndependent()
    {
        var entry = QueryTemplateCatalog.Entries.First();
        Assert.Same(entry, QueryTemplateCatalog.Get(entry.TemplateId, entry.Revision));
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Get(entry.TemplateId, new string('0', 64)));
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Get(entry.TemplateId.ToUpperInvariant(), entry.Revision));
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Get(entry.Name, entry.Revision));
        var next = entry with { ResourceVersion = 2, Name = "A future template name" };
        Assert.NotEqual(entry.NameResourceKey, next.NameResourceKey);
        Assert.NotEqual(entry.DescriptionResourceKey, next.DescriptionResourceKey);
        Assert.Contains(".V1.", entry.NameResourceKey);
        Assert.Contains(".V2.", next.NameResourceKey);
    }

    [Fact]
    public void SeedRevisionsArePinnedToTheirOwnOriginalIdentityAndText()
    {
        var provisioning = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        foreach (var entry in QueryTemplateCatalog.Entries.Where(entry => entry.Library is null))
        {
            Assert.Null(entry.FileName);
            var original = Encoding.UTF8.GetBytes($"{entry.TemplateId}\n{entry.Name}\n{entry.Description}");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), entry.Revision);
            Assert.Contains($"N'{entry.TemplateId["sql/analysis/".Length..]}'", provisioning);
            Assert.Contains($"N'{entry.Name.Replace("'", "''")}'", provisioning);
            Assert.Contains($"N'{entry.Description!.Replace("'", "''")}'", provisioning);
            Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.VerifyFile(entry, "not-a-file"));
        }
    }

    [Theory]
    [InlineData("templateId")]
    [InlineData("revision")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("resourceVersion")]
    public void MissingEntryFieldsCannotSilentlySelectAnotherRevision(string field)
    {
        var entry = JsonSerializer.SerializeToNode(QueryTemplateCatalog.Entries.First(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        entry.Remove(field);
        Assert.Throws<InvalidOperationException>(() => QueryTemplateCatalog.Parse(Document(entry)));
    }

    private static string Document(params JsonNode[] entries) =>
        new JsonObject { ["schemaVersion"] = 1, ["templates"] = new JsonArray(entries) }.ToJsonString();

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;

        internal CultureScope(string culture) =>
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
}
