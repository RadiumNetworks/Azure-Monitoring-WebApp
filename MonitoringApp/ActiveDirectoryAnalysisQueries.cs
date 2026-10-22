using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

public static class ActiveDirectoryAnalysisQueries
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<AnalysisQueryDefinition> Create(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "ActiveDirectory");
        try
        {
            var files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
            if (files.Length == 0)
                throw new InvalidOperationException($"No AD query templates found in '{directory}'.");

            var queries = new List<AnalysisQueryDefinition>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var query = Load(file);
                if (!keys.Add(query.Key))
                    throw new InvalidOperationException($"Duplicate AD query key '{query.Key}' in '{file}'.");
                queries.Add(query);
            }
            return queries;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not read AD query templates from '{directory}': {exception.Message}", exception);
        }
    }

    private static AnalysisQueryDefinition Load(string file)
    {
        AnalysisQuerySnapshot template;
        try
        {
            using var stream = File.OpenRead(file);
            template = JsonSerializer.Deserialize<AnalysisQuerySnapshot>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"AD query template '{file}' must not be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid AD query template '{file}': {exception.Message}", exception);
        }

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
        var errors = AnalysisQueryValidator.ValidateDefinition(query)
            .Concat(AnalysisQueryValidator.ValidateKql(query.Kql, query.VisualizationType, query.Scope)).ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException($"Invalid AD query template '{file}': {string.Join(" ", errors)}");

        query.Key = query.Key.Trim().ToLowerInvariant();
        return query;
    }
}
