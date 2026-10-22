using System.Data;
using Microsoft.EntityFrameworkCore;

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
            throw new InvalidOperationException("A category is required.");
        var segments = path.Split('/').Select(segment => segment.Trim()).ToArray();
        var result = string.Join('/', segments);
        if (result.Length > 128 || path.Any(char.IsControl) || segments.Any(segment => segment.Length == 0))
            throw new InvalidOperationException("Category paths require nonempty names without control characters and at most 128 characters in total. Use / between levels.");
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

public sealed class AnalysisQueryCategoryService(IDbContextFactory<AlertDbContext> factory)
{
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await PathsAsync(context, cancellationToken);
    }

    public Task CreateAsync(string name, string? parent, CancellationToken cancellationToken = default) =>
        ChangeAsync(async context =>
        {
            var paths = await PathsAsync(context, cancellationToken);
            var path = NewPath(name, parent, paths);
            if (paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("This category already exists.");
            await EnsurePathsAsync(context, paths.Append(path), cancellationToken);
        }, cancellationToken);

    public Task MoveQueryAsync(Guid queryId, string path, string actor, CancellationToken cancellationToken = default) =>
        ChangeAsync(async context =>
        {
            var paths = await PathsAsync(context, cancellationToken);
            var target = Resolve(paths, path);
            var query = await context.AnalysisQueryDefinitions.SingleOrDefaultAsync(query => query.Id == queryId, cancellationToken)
                ?? throw new InvalidOperationException("The query no longer exists. Refresh the registry.");
            await EnsurePathsAsync(context, paths, cancellationToken);
            query.Category = target;
            query.UpdatedAt = DateTimeOffset.UtcNow;
            query.UpdatedBy = actor.Trim();
        }, cancellationToken);

    public Task UpdateAsync(string path, string name, string? parent, string actor, CancellationToken cancellationToken = default) =>
        ChangeAsync(async context =>
        {
            var paths = await PathsAsync(context, cancellationToken);
            var source = Resolve(paths, path);
            var target = NewPath(name, parent, paths);
            if (!string.IsNullOrWhiteSpace(parent) && AnalysisQueryCategories.IsWithin(AnalysisQueryCategories.Normalize(parent), source))
                throw new InvalidOperationException("A category cannot be moved into itself or one of its subcategories.");
            var moved = paths.Where(item => AnalysisQueryCategories.IsWithin(item, source)).ToArray();
            var remaining = paths.Except(moved, StringComparer.OrdinalIgnoreCase).ToArray();
            var replacements = moved.Select(item => AnalysisQueryCategories.Normalize(target + item[source.Length..])).ToArray();
            if (replacements.Any(item => remaining.Contains(item, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The destination category already exists. Categories cannot be merged.");
            var queries = await context.AnalysisQueryDefinitions.ToArrayAsync(cancellationToken);
            foreach (var query in queries.Where(query => AnalysisQueryCategories.IsWithin(AnalysisQueryCategories.Normalize(query.Category), source)))
            {
                query.Category = target + AnalysisQueryCategories.Normalize(query.Category)[source.Length..];
                query.UpdatedAt = DateTimeOffset.UtcNow;
                query.UpdatedBy = actor.Trim();
            }
            var stored = await context.AnalysisQueryCategories.Select(category => category.Path).ToArrayAsync(cancellationToken);
            var removed = stored.Where(item => AnalysisQueryCategories.IsWithin(AnalysisQueryCategories.Normalize(item), source)).ToArray();
            await context.AnalysisQueryCategories.Where(category => removed.Contains(category.Path)).ExecuteDeleteAsync(cancellationToken);
            await EnsurePathsAsync(context, remaining.Concat(replacements), cancellationToken);
        }, cancellationToken);

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        ChangeAsync(async context =>
        {
            var paths = await PathsAsync(context, cancellationToken);
            var source = Resolve(paths, path);
            var assigned = await context.AnalysisQueryDefinitions.Select(query => query.Category).ToArrayAsync(cancellationToken);
            if (paths.Any(item => !item.Equals(source, StringComparison.OrdinalIgnoreCase) && AnalysisQueryCategories.IsWithin(item, source)) ||
                assigned.Any(item => AnalysisQueryCategories.IsWithin(AnalysisQueryCategories.Normalize(item), source)))
                throw new InvalidOperationException("Only empty categories without subcategories can be deleted. Move their queries and subcategories first.");
            var stored = await context.AnalysisQueryCategories.Select(category => category.Path).ToArrayAsync(cancellationToken);
            var removed = stored.Where(item => AnalysisQueryCategories.Normalize(item).Equals(source, StringComparison.OrdinalIgnoreCase)).ToArray();
            await context.AnalysisQueryCategories.Where(category => removed.Contains(category.Path)).ExecuteDeleteAsync(cancellationToken);
        }, cancellationToken);

    private static string Resolve(IReadOnlyList<string> paths, string path) =>
        paths.FirstOrDefault(item => item.Equals(AnalysisQueryCategories.Normalize(path), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("The category no longer exists. Refresh the registry.");

    private static string NewPath(string name, string? parent, IReadOnlyList<string> paths)
    {
        if (name.Contains('/'))
            throw new InvalidOperationException("Enter a category name without /. Select its parent separately.");
        var prefix = string.IsNullOrWhiteSpace(parent) ? "" : Resolve(paths, parent) + "/";
        return AnalysisQueryCategories.Normalize(prefix + name);
    }

    private static async Task<IReadOnlyList<string>> PathsAsync(AlertDbContext context, CancellationToken cancellationToken)
    {
        var stored = await context.AnalysisQueryCategories.Select(category => category.Path).ToArrayAsync(cancellationToken);
        var assigned = await context.AnalysisQueryDefinitions.Select(query => query.Category).Distinct().ToArrayAsync(cancellationToken);
        return AnalysisQueryCategories.Expand(stored.Concat(assigned));
    }

    private static async Task EnsurePathsAsync(AlertDbContext context, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var existing = (await context.AnalysisQueryCategories.Select(category => category.Path).ToArrayAsync(cancellationToken))
            .Select(AnalysisQueryCategories.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        context.AnalysisQueryCategories.AddRange(AnalysisQueryCategories.Expand(paths)
            .Where(existing.Add).Select(path => new AnalysisQueryCategory { Path = path }));
    }

    private async Task ChangeAsync(Func<AlertDbContext, Task> change, CancellationToken cancellationToken)
    {
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await change(context);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
