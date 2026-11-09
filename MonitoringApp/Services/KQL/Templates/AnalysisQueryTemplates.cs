using System.Text.Json;

namespace MonitoringApp;

internal static class AnalysisQueryTemplates
{
    internal const string NewDraftKql = """
        Perf
        | where Computer in~ (_SelectedSystems)
        | summarize Value = avg(CounterValue) by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer
        | project Timestamp, Series, Value
        """;

    internal static AnalysisQueryDefinition Parse(string json)
    {
        try
        {
            var template = JsonSerializer.Deserialize<AnalysisQuerySnapshot>(json, QueryTemplateJson.Options)
                ?? throw new QueryTemplateValidationException(QueryTemplateValidationCode.NullTemplate, "The template must not be null.");
            var query = new AnalysisQueryDefinition
            {
                Key = template.Key,
                Name = template.Name,
                Description = template.Description,
                Category = template.Category,
                Kql = template.Kql,
                Scope = template.Scope,
                VisualizationType = template.VisualizationType,
                VisualizationJson = AnalysisVisualization.Serialize(template.Visualization),
                Unit = template.Unit,
                DefaultTimeRangeMinutes = template.DefaultTimeRangeMinutes,
                MaxTimeRangeMinutes = template.MaxTimeRangeMinutes,
                DefaultBinMinutes = template.DefaultBinMinutes,
                MinimumRole = template.MinimumRole
            };
            AnalysisQueryValidator.RequireValidQuery(query);

            query.Key = query.Key.Trim().ToLowerInvariant();
            return query;
        }
        catch (JsonException exception)
        {
            throw new QueryTemplateValidationException(QueryTemplateValidationCode.InvalidJson, $"Invalid analysis query JSON: {exception.Message}", exception);
        }
    }
}
