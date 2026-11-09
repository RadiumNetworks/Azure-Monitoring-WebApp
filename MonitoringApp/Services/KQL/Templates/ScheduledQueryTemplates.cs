using System.Text.Json;

namespace MonitoringApp;

internal static class ScheduledQueryTemplates
{
    internal static IReadOnlyList<int> EventIds { get; } = [4624, 4768, 4769, 5136, 4724, 4736, 4794];

    internal static ScheduledQueryRule Example(bool kerberos) =>
        Load(TemplatePath(kerberos ? "kerberos-stopped.json" : "cpu-high.json"));

    internal static ScheduledQueryRule EventExample(int eventId)
    {
        if (!EventIds.Contains(eventId))
            throw new ArgumentException("Select a supported event ID.", nameof(eventId));
        return Load(TemplatePath($"event-{eventId}.json"));
    }

    private static string TemplatePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "Scheduled", name);

    internal static ScheduledQueryRule Load(string file)
    {
        try
        {
            return Parse(File.ReadAllText(file));
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"Invalid scheduled query template '{file}': {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read scheduled query template '{file}': {exception.Message}", exception);
        }
    }

    internal static ScheduledQueryRule Parse(string json)
    {
        try
        {
            var template = JsonSerializer.Deserialize<ScheduledQueryTemplate>(json, QueryTemplateJson.Options)
                ?? throw new QueryTemplateValidationException(QueryTemplateValidationCode.NullTemplate, "The template must not be null.");
            var rule = new ScheduledQueryRule
            {
                Name = template.Name,
                CategoryName = template.CategoryName,
                Scope = template.Scope,
                Kql = template.Kql,
                IntervalMinutes = template.IntervalMinutes,
                WindowMinutes = template.WindowMinutes,
                IngestionDelayMinutes = template.IngestionDelayMinutes,
                Comparison = template.Comparison,
                Threshold = template.Threshold,
                Severity = template.Severity
            };
            ScheduledQueryExecution.ValidateTemplate(rule);
            return rule;
        }
        catch (JsonException exception)
        {
            throw new QueryTemplateValidationException(QueryTemplateValidationCode.InvalidJson, $"Invalid scheduled query JSON: {exception.Message}", exception);
        }
    }

    private sealed record ScheduledQueryTemplate(
        string Name, string CategoryName, string Scope, string Kql,
        int IntervalMinutes, int WindowMinutes, int IngestionDelayMinutes,
        string Comparison, double Threshold, string Severity);
}
