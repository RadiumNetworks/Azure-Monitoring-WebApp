using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

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
                throw new AnalysisValidationException(AnalysisValidationCode.CategoryExists, "This category already exists.");
            await EnsurePathsAsync(context, paths.Append(path), cancellationToken);
        }, cancellationToken);

    public Task MoveQueryAsync(Guid queryId, string path, string actor, CancellationToken cancellationToken = default) =>
        ChangeAsync(async context =>
        {
            var paths = await PathsAsync(context, cancellationToken);
            var target = Resolve(paths, path);
            var query = await context.AnalysisQueryDefinitions.SingleOrDefaultAsync(query => query.Id == queryId, cancellationToken)
                ?? throw new AnalysisValidationException(AnalysisValidationCode.CategoryQueryMissing, "The query no longer exists. Refresh the registry.");
            await EnsurePathsAsync(context, paths, cancellationToken);
            AnalysisQueryWriteRules.AssignCategory(query, target, actor);
        }, cancellationToken);

    public Task UpdateAsync(string path, string name, string? parent, string actor, CancellationToken cancellationToken = default) =>
        ChangeAsync(async context =>
        {
            var paths = await PathsAsync(context, cancellationToken);
            var source = Resolve(paths, path);
            var target = NewPath(name, parent, paths);
            if (!string.IsNullOrWhiteSpace(parent) && AnalysisQueryCategories.IsWithin(AnalysisQueryCategories.Normalize(parent), source))
                throw new AnalysisValidationException(AnalysisValidationCode.CategoryCycle, "A category cannot be moved into itself or one of its subcategories.");
            var moved = paths.Where(item => AnalysisQueryCategories.IsWithin(item, source)).ToArray();
            var remaining = paths.Except(moved, StringComparer.OrdinalIgnoreCase).ToArray();
            var replacements = moved.Select(item => AnalysisQueryCategories.Normalize(target + item[source.Length..])).ToArray();
            if (replacements.Any(item => remaining.Contains(item, StringComparer.OrdinalIgnoreCase)))
                throw new AnalysisValidationException(AnalysisValidationCode.CategoryCollision, "The destination category already exists. Categories cannot be merged.");
            var queries = await context.AnalysisQueryDefinitions.ToArrayAsync(cancellationToken);
            foreach (var query in queries.Where(query => AnalysisQueryCategories.IsWithin(AnalysisQueryCategories.Normalize(query.Category), source)))
            {
                AnalysisQueryWriteRules.AssignCategory(
                    query, target + AnalysisQueryCategories.Normalize(query.Category)[source.Length..], actor);
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
                throw new AnalysisValidationException(AnalysisValidationCode.CategoryNotEmpty, "Only empty categories without subcategories can be deleted. Move their queries and subcategories first.");
            var stored = await context.AnalysisQueryCategories.Select(category => category.Path).ToArrayAsync(cancellationToken);
            var removed = stored.Where(item => AnalysisQueryCategories.Normalize(item).Equals(source, StringComparison.OrdinalIgnoreCase)).ToArray();
            await context.AnalysisQueryCategories.Where(category => removed.Contains(category.Path)).ExecuteDeleteAsync(cancellationToken);
        }, cancellationToken);

    private static string Resolve(IReadOnlyList<string> paths, string path) =>
        paths.FirstOrDefault(item => item.Equals(AnalysisQueryCategories.Normalize(path), StringComparison.OrdinalIgnoreCase))
        ?? throw new AnalysisValidationException(AnalysisValidationCode.CategoryMissing, "The category no longer exists. Refresh the registry.");

    private static string NewPath(string name, string? parent, IReadOnlyList<string> paths)
    {
        if (name.Contains('/'))
            throw new AnalysisValidationException(AnalysisValidationCode.CategoryNameInvalid, "Enter a category name without /. Select its parent separately.");
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
        await DatabaseTransactions.ExecuteAsync(factory, async context =>
        {
            await change(context);
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken, IsolationLevel.Serializable);
    }
}
