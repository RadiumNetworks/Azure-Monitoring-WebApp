using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

internal sealed record StandardQueryTextProvenance(
    int SchemaVersion, string TemplateId, string TemplateRevision, string[] UnmodifiedFieldIds)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string Create(QueryTemplateCatalogEntry entry, bool includeName = true) =>
        JsonSerializer.Serialize(new StandardQueryTextProvenance(1, entry.TemplateId, entry.Revision,
            (includeName ? new[] { "name" } : []).Concat(entry.Description is null ? [] : new[] { "description" }).ToArray()), JsonOptions);

    internal static StandardQueryTextProvenance Parse(string json, bool scheduled)
    {
        if (json.Length > 4096)
            throw new InvalidOperationException("The standard query text provenance exceeds its supported size.");
        StandardQueryTextProvenance provenance;
        try
        {
            provenance = JsonSerializer.Deserialize<StandardQueryTextProvenance>(json, JsonOptions)
                ?? throw new InvalidOperationException("The standard query text provenance must not be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The standard query text provenance JSON is invalid.", exception);
        }
        var entry = QueryTemplateCatalog.Get(provenance.TemplateId, provenance.TemplateRevision);
        if (provenance.SchemaVersion != 1 || (entry.Library == "Scheduled") != scheduled)
            throw new InvalidOperationException("The standard query text provenance schema or template kind is invalid.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in provenance.UnmodifiedFieldIds)
        {
            if (field is not ("name" or "description") || field == "description" && entry.Description is null || !fields.Add(field))
                throw new InvalidOperationException("The standard query text provenance field selection is invalid.");
        }
        return provenance;
    }

    internal static string? Retain(
        string? json, bool scheduled, string previousName, string? previousDescription,
        string name, string? description)
    {
        if (json is null) return null;
        var provenance = Parse(json, scheduled);
        var entry = QueryTemplateCatalog.Get(provenance.TemplateId, provenance.TemplateRevision);
        var fields = provenance.UnmodifiedFieldIds.Where(field => field == "name"
            ? previousName.Trim() == entry.Name && name.Trim() == entry.Name
            : previousDescription?.Trim() == entry.Description && description?.Trim() == entry.Description).ToArray();
        return JsonSerializer.Serialize(provenance with { UnmodifiedFieldIds = fields }, JsonOptions);
    }
}

internal sealed class ScheduledQueryTemplateDraft
{
    private readonly QueryTemplateCatalogEntry entry;
    private readonly Guid ruleId;
    private bool nameUnmodified = true;
    private bool active = true;

    private ScheduledQueryTemplateDraft(QueryTemplateCatalogEntry entry, ScheduledQueryRule rule)
    {
        this.entry = entry;
        Rule = rule;
        ruleId = rule.Id;
    }

    internal ScheduledQueryRule Rule { get; }

    internal static ScheduledQueryTemplateDraft Create(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName) ||
            !fileName.EndsWith(".json", StringComparison.Ordinal))
            throw new InvalidOperationException("Select a packaged scheduled query template filename.");
        var entry = QueryTemplateCatalog.GetActive("kql/scheduled/" + Path.GetFileNameWithoutExtension(fileName));
        var json = QueryTemplateCatalog.ReadPackagedTemplate(entry);
        return new(entry, ScheduledQueryTemplates.Parse(json));
    }

    internal void ObserveName()
    {
        if (Rule.Name?.Trim() != entry.Name) nameUnmodified = false;
    }

    internal string CaptureProvenance()
    {
        if (!active || Rule.Id != ruleId || Rule.Revision != 0)
            throw new InvalidOperationException("The scheduled template draft is no longer a new active template draft.");
        ObserveName();
        return StandardQueryTextProvenance.Create(entry, nameUnmodified);
    }

    internal void Complete() => active = false;
}
