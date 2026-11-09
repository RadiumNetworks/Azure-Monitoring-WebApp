namespace MonitoringApp;

public sealed class AnalysisQueryCategory
{
    public string Path { get; set; } = string.Empty;
}

public sealed record AnalysisQueryCategoryNode(
    string Path, string Name, IReadOnlyList<AnalysisQueryDefinition> Queries,
    IReadOnlyList<AnalysisQueryCategoryNode> Children)
{
    public int QueryCount => Queries.Count + Children.Sum(child => child.QueryCount);
}

public static class AnalysisQueryCategories
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AnalysisValidationException(AnalysisValidationCode.CategoryRequired, "A category is required.");
        var segments = path.Split('/').Select(segment => segment.Trim()).ToArray();
        var result = string.Join('/', segments);
        if (result.Length > 128 || path.Any(char.IsControl) || segments.Any(segment => segment.Length == 0))
            throw new AnalysisValidationException(AnalysisValidationCode.CategoryPathInvalid, "Category paths require nonempty names without control characters and at most 128 characters in total. Use / between levels.");
        return result;
    }

    public static bool IsWithin(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Expand(IEnumerable<string> paths) => paths
        .Select(Normalize)
        .SelectMany(path => path.Split('/').Select((_, index) => string.Join('/', path.Split('/').Take(index + 1))))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<AnalysisQueryCategoryNode> Build(
        IEnumerable<AnalysisQueryDefinition> queries, IEnumerable<string>? categories = null)
    {
        var definitions = queries.ToArray();
        var paths = Expand((categories ?? []).Concat(definitions.Select(query => query.Category)));
        var groups = definitions.ToLookup(query => Normalize(query.Category), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<AnalysisQueryCategoryNode> Children(string? parent) => paths
            .Where(path => string.Equals(path.Contains('/') ? path[..path.LastIndexOf('/')] : null, parent, StringComparison.OrdinalIgnoreCase))
            .Select(path => new AnalysisQueryCategoryNode(path, path[(path.LastIndexOf('/') + 1)..],
                groups[path].OrderBy(query => query.Name, StringComparer.OrdinalIgnoreCase).ToArray(), Children(path))).ToArray();
        return Children(null);
    }
}
