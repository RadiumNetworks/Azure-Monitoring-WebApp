using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MonitoringApp;

internal sealed record QueryTemplateCatalogEntry(
    string TemplateId, string Revision, string Name, string? Description, int ResourceVersion)
{
    internal string NameResourceKey => FormattableString.Invariant($"QueryTemplates.V{ResourceVersion}.{TemplateId.Replace('/', '.')}.Name");
    internal string? DescriptionResourceKey => Description is null
        ? null
        : FormattableString.Invariant($"QueryTemplates.V{ResourceVersion}.{TemplateId.Replace('/', '.')}.Description");

    internal string? Library => TemplateId.StartsWith("kql/ad/", StringComparison.Ordinal)
        ? "ActiveDirectory"
        : TemplateId.StartsWith("kql/scheduled/", StringComparison.Ordinal) ? "Scheduled" : null;

    internal string? FileName => Library is null ? null : TemplateId[(TemplateId.LastIndexOf('/') + 1)..] + ".json";
}

internal static partial class QueryTemplateCatalog
{
    private const string ResourceName = "MonitoringApp.QueryTemplateCatalog.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<IReadOnlyList<QueryTemplateCatalogEntry>> Catalog = new(Load);

    internal static IReadOnlyList<QueryTemplateCatalogEntry> Entries => Catalog.Value;

    internal static QueryTemplateCatalogEntry Get(string templateId, string revision) =>
        Entries.SingleOrDefault(entry => entry.TemplateId == templateId && entry.Revision == revision)
        ?? throw new InvalidOperationException($"Unknown query template catalog revision '{templateId}/{revision}'.");

    internal static QueryTemplateCatalogEntry GetActive(string templateId) =>
        Entries.LastOrDefault(entry => entry.TemplateId == templateId)
        ?? throw new InvalidOperationException($"Unknown active query template catalog entry '{templateId}'.");

    internal static string ReadPackagedTemplate(QueryTemplateCatalogEntry entry)
    {
        entry = Get(entry.TemplateId, entry.Revision);
        if (entry.Library is null)
            throw new InvalidOperationException($"Catalog entry '{entry.TemplateId}' is not a packaged query JSON template.");
        var file = Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", entry.Library, entry.FileName!);
        var bytes = File.ReadAllBytes(file);
        RequireHash(entry, Convert.ToHexString(SHA256.HashData(bytes)), file);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Matching bytes do not establish origin; only a trusted server-side creation path can do that.
    internal static void VerifyFile(QueryTemplateCatalogEntry entry, string file)
    {
        if (entry.Library is null)
            throw new InvalidOperationException($"Catalog entry '{entry.TemplateId}' is not a query JSON template.");
        using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        RequireHash(entry, hash, file);
    }

    private static void RequireHash(QueryTemplateCatalogEntry entry, string hash, string file)
    {
        if (!string.Equals(hash, entry.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException($"Query template '{file}' does not match catalog revision '{entry.TemplateId}/{entry.Revision}'.");
    }

    internal static IReadOnlyList<QueryTemplateCatalogEntry> Parse(string json)
    {
        CatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
                ?? throw new InvalidOperationException("The query template catalog must not be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The query template catalog JSON is invalid.", exception);
        }
        if (document.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported query template catalog schema '{document.SchemaVersion}'.");

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Templates)
        {
            if (entry is null)
                throw new InvalidOperationException("The query template catalog contains a null entry.");
            if (!TemplateIdPattern().IsMatch(entry.TemplateId) ||
                entry.ResourceVersion <= 0 ||
                entry.Revision.Length != 64 || entry.Revision.Any(character => !char.IsAsciiHexDigit(character)) ||
                entry.Revision != entry.Revision.ToUpperInvariant() ||
                string.IsNullOrWhiteSpace(entry.Name) || entry.Name != entry.Name.Trim() ||
                (entry.Library == "Scheduled"
                    ? entry.Description is not null
                    : string.IsNullOrWhiteSpace(entry.Description) || entry.Description != entry.Description.Trim()))
                throw new InvalidOperationException($"Invalid query template catalog entry '{entry.TemplateId}'.");
            if (!identifiers.Add(entry.TemplateId + "/" + entry.Revision))
                throw new InvalidOperationException($"Duplicate query template catalog revision '{entry.TemplateId}/{entry.Revision}'.");
        }
        if (document.Templates.Length == 0)
            throw new InvalidOperationException("The query template catalog is empty.");
        return Array.AsReadOnly(document.Templates);
    }

    private static IReadOnlyList<QueryTemplateCatalogEntry> Load()
    {
        var assembly = typeof(QueryTemplateCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("MonitoringApp.QueryTemplateCatalog.v", StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(CatalogFileVersion).ToArray();
        if (!resources.Contains(ResourceName, StringComparer.Ordinal))
            throw new InvalidOperationException($"The packaged query template catalog '{ResourceName}' is missing.");
        var entries = new List<QueryTemplateCatalogEntry>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The packaged query template catalog '{resource}' is missing.");
            using var reader = new StreamReader(stream);
            foreach (var entry in Parse(reader.ReadToEnd()))
            {
                if (!identifiers.Add(entry.TemplateId + "/" + entry.Revision))
                    throw new InvalidOperationException($"Duplicate packaged query template catalog revision '{entry.TemplateId}/{entry.Revision}'.");
                entries.Add(entry);
            }
        }
        foreach (var entry in entries)
        {
            if (UiResources.GetEnglish(entry.NameResourceKey) != entry.Name ||
                entry.DescriptionResourceKey is { } descriptionKey &&
                UiResources.GetEnglish(descriptionKey) != entry.Description)
                throw new InvalidOperationException($"The English resources do not match query template catalog revision '{entry.TemplateId}/{entry.Revision}'.");
        }
        return entries.AsReadOnly();
    }

    [GeneratedRegex(@"^(kql/(ad|scheduled)|sql/analysis)/[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateIdPattern();

    private static int CatalogFileVersion(string resource)
    {
        var version = resource["MonitoringApp.QueryTemplateCatalog.v".Length..^".json".Length];
        return int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new InvalidOperationException($"Invalid packaged query template catalog filename '{resource}'.");
    }

    private sealed record CatalogDocument(int SchemaVersion, QueryTemplateCatalogEntry[] Templates);
}
