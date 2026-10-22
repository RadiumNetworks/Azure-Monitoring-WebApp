using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed record PublishedAnalysisDashboard(
    Guid Id, string Name, int Version, string Owner, DateTimeOffset PublishedAt);

public sealed record PublishedAnalysisDashboardSnapshot(
    PublishedAnalysisDashboard Publication, AnalysisDashboardDocument Document);

public sealed class AnalysisDashboardPublishing(
    IDbContextFactory<AlertDbContext> contextFactory,
    IAuthorizationService authorization,
    CurrentUserIdentity identity,
    ApplicationAuthenticationOptions authentication)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> CanPublishAsync(ClaimsPrincipal user) =>
        (await authorization.AuthorizeAsync(user, "AdminAccess")).Succeeded;

    public async Task<IReadOnlyList<PublishedAnalysisDashboard>> ListAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await RequireAsync(user, "OperatorAccess");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AnalysisDashboardTemplates.AsNoTracking()
            .Where(item => item.Published)
            .OrderBy(item => item.Name).ThenBy(item => item.UpdatedBy)
            .Select(item => new PublishedAnalysisDashboard(item.Id, item.Name, item.Version, item.UpdatedBy, item.UpdatedAt))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<PublishedAnalysisDashboardSnapshot> LoadAsync(
        Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await RequireAsync(user, "OperatorAccess");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var publication = await context.AnalysisDashboardTemplates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.Published, cancellationToken)
            ?? throw new KeyNotFoundException("This dashboard is no longer published. It is now private to its creator.");
        try
        {
            var document = JsonSerializer.Deserialize<AnalysisDashboardDocument>(publication.DocumentJson, JsonOptions)
                ?? throw new InvalidOperationException("The published dashboard is invalid.");
            var state = new AnalysisDashboardState();
            state.Load(document);
            return new(ToInfo(publication), state.Document);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The published dashboard is invalid.", exception);
        }
    }

    public async Task<PublishedAnalysisDashboard> PublishAsync(
        Guid dashboardId, int expectedRevision, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await RequireAsync(user, "AdminAccess");
        var owner = identity.GetActor(user, authentication.IsOpen);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.AnalysisDashboards.SingleOrDefaultAsync(item => item.OwnerKey == owner, cancellationToken)
            ?? throw new InvalidOperationException("Save your own dashboard before publishing.");
        if (stored.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("Your dashboards changed in another session. Reload before publishing.");
        var collection = AnalysisDashboardCollection.Deserialize(stored.DocumentJson);
        var entry = collection.Dashboards.SingleOrDefault(item => item.Id == dashboardId)
            ?? throw new UnauthorizedAccessException("Only the creator can publish this dashboard.");
        var publication = await context.AnalysisDashboardTemplates.SingleOrDefaultAsync(item => item.Id == dashboardId, cancellationToken);
        if (publication is not null && !IsOwner(publication.UpdatedBy, owner))
            throw new UnauthorizedAccessException("Only the creator can update this publication.");
        if (publication is null)
        {
            publication = new() { Id = dashboardId, UpdatedBy = owner };
            context.AnalysisDashboardTemplates.Add(publication);
        }
        var state = new AnalysisDashboardState();
        state.Load(entry.Document);
        state.Document.HiddenQueryIds.Clear();
        publication.Name = state.Document.Name;
        publication.Version++;
        publication.Published = true;
        publication.MinimumRole = SqlAuthenticationRoles.Operator;
        publication.DocumentJson = JsonSerializer.Serialize(state.Document, JsonOptions);
        publication.UpdatedAt = DateTimeOffset.UtcNow;
        // Protect publication against a concurrent deletion or edit of the source collection.
        stored.Revision++;
        stored.UpdatedAt = publication.UpdatedAt;
        await context.SaveChangesAsync(cancellationToken);
        return ToInfo(publication);
    }

    public async Task WithdrawAsync(Guid dashboardId, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        await RequireAsync(user, "AdminAccess");
        var owner = identity.GetActor(user, authentication.IsOpen);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var publication = await context.AnalysisDashboardTemplates.SingleOrDefaultAsync(item => item.Id == dashboardId, cancellationToken)
            ?? throw new KeyNotFoundException("The dashboard publication was not found.");
        if (!IsOwner(publication.UpdatedBy, owner))
            throw new UnauthorizedAccessException("Only the creator can withdraw this dashboard.");
        publication.Published = false;
        publication.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task RequireAsync(ClaimsPrincipal user, string policy)
    {
        if (!(await authorization.AuthorizeAsync(user, policy)).Succeeded)
            throw new UnauthorizedAccessException("You do not have permission for this dashboard action.");
    }

    private static bool IsOwner(string owner, string actor) => owner.Equals(actor, StringComparison.OrdinalIgnoreCase);
    private static PublishedAnalysisDashboard ToInfo(AnalysisDashboardTemplate item) =>
        new(item.Id, item.Name, item.Version, item.UpdatedBy, item.UpdatedAt);
}
