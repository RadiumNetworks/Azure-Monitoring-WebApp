namespace MonitoringApp;

internal sealed record QueryTextDisplayContext(
    Guid QueryId, int Version, string Name, string Description, string? ProvenanceJson)
{
    internal static QueryTextDisplayContext Capture(AnalysisQueryDefinition query) =>
        new(query.Id, query.Version, query.Name, query.Description, query.StandardTextProvenanceJson);
}

public sealed class QueryTextPresentation(UiText text, ILogger<QueryTextPresentation> logger)
{
    public string Name(AnalysisQueryDefinition query) =>
        Project(query.Name, query.Description, query.StandardTextProvenanceJson, false).Name;

    public string Description(AnalysisQueryDefinition query) =>
        Project(query.Name, query.Description, query.StandardTextProvenanceJson, false).Description;

    public string Name(ScheduledQueryRule rule) =>
        Project(rule.Name, "", rule.StandardTextProvenanceJson, true).Name;

    public string TemplateName(string templateId)
    {
        try
        {
            return text[QueryTemplateCatalog.GetActive(templateId).NameResourceKey];
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception).Name;
        }
    }

    public string Name(AnalysisQueryResponse response)
    {
        if (response.StandardTextDisplayContext is not { } context) return response.Name;
        if (response.QueryDefinitionId != context.QueryId || response.Version != context.Version ||
            response.Name != context.Name)
            return Invalid(new InvalidOperationException("The query result does not match its executed text context.")).Name;
        return Project(context.Name, context.Description, context.ProvenanceJson, false).Name;
    }

    private (string Name, string Description) Project(
        string name, string description, string? json, bool scheduled)
    {
        if (json is null) return (name, description);
        try
        {
            var provenance = StandardQueryTextProvenance.Parse(json, scheduled);
            var entry = QueryTemplateCatalog.Get(provenance.TemplateId, provenance.TemplateRevision);
            return (
                provenance.UnmodifiedFieldIds.Contains("name") && name.Trim() == entry.Name
                    ? text[entry.NameResourceKey] : name,
                provenance.UnmodifiedFieldIds.Contains("description") && description.Trim() == entry.Description
                    ? text[entry.DescriptionResourceKey!] : description);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(exception);
        }
    }

    private (string Name, string Description) Invalid(InvalidOperationException exception)
    {
        logger.LogError(exception, "Could not project standard query text from its provenance.");
        var diagnostic = text["QueryTemplates.Display.Invalid"];
        return (diagnostic, diagnostic);
    }
}
