namespace MonitoringApp;

public static class ActiveDirectoryAnalysisQueries
{
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
        try
        {
            return AnalysisQueryTemplates.Parse(File.ReadAllText(file));
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"Invalid AD query template '{file}': {exception.Message}", exception);
        }
    }
}
