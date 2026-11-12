using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class IdentityMigrationService(
    IDbContextFactory<AlertDbContext> factory,
    IdentityDirectoryService directory)
{
    public async Task<IReadOnlyList<string>> CandidatesAsync(ClaimsPrincipal administrator, CancellationToken cancellationToken = default)
    {
        await directory.RequireAdministratorAsync(administrator);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await HistoricalUserQueries.AliasesAsync(context, cancellationToken);
    }

    public async Task<IdentityMigrationPreview> PreviewAsync(
        ClaimsPrincipal administrator, Guid userId, IReadOnlyList<string> aliases, CancellationToken cancellationToken = default)
    {
        await directory.RequireAdministratorAsync(administrator);
        return await DatabaseTransactions.ExecuteAsync(factory, async context =>
            (await ReadAsync(context, userId, Normalize(aliases), cancellationToken)).Preview,
            cancellationToken, IsolationLevel.Serializable);
    }

    public async Task CommitAsync(
        ClaimsPrincipal administrator, Guid userId, IReadOnlyList<string> aliases,
        string expectedFingerprint, string verificationNote, CancellationToken cancellationToken = default)
    {
        await directory.RequireAdministratorAsync(administrator);
        if (string.IsNullOrWhiteSpace(verificationNote) || verificationNote.Trim().Length > 2000)
            throw new IdentityMigrationException("IdentityMigration.Errors.Evidence");
        var resolved = await directory.ResolveAsync(administrator, cancellationToken);
        var approver = DirectoryIdentity.UserId(resolved)
            ?? throw new UnauthorizedAccessException("The approving administrator has no directory identity.");
        await DatabaseTransactions.ExecuteAsync(factory, async context =>
        {
            if (await context.IdentityMigrationRecords.AnyAsync(record =>
                record.UserId == userId && record.Fingerprint == expectedFingerprint, cancellationToken))
                return;
            var state = await ReadAsync(context, userId, Normalize(aliases), cancellationToken);
            if (state.Preview.Fingerprint != expectedFingerprint)
                throw new IdentityMigrationException("IdentityMigration.Errors.Stale");
            if (state.Preview.Conflicts.Count > 0)
                throw new IdentityMigrationException("IdentityMigration.Errors.Conflicts");
            var owner = DirectoryIdentity.OwnerKey(userId);
            var now = DateTimeOffset.UtcNow;
            context.IdentityMigrationRecords.Add(new()
            {
                UserId = userId, ApprovedByUserId = approver, CreatedAt = now,
                Fingerprint = expectedFingerprint, VerificationNote = verificationNote.Trim(),
                OriginalStateJson = state.OriginalStateJson
            });
            if (state.Dashboards.Count > 0)
            {
                var collections = state.Dashboards.Select(row => AnalysisDashboardCollection.Deserialize(row.DocumentJson)).ToArray();
                var merged = collections[0];
                merged.Dashboards = collections.SelectMany(collection => collection.Dashboards).ToList();
                merged.HiddenQueryIds = collections.SelectMany(collection => collection.HiddenQueryIds).Distinct().ToList();
                var retained = state.Dashboards.FirstOrDefault(row => row.OwnerKey == owner) ?? state.Dashboards[0];
                retained.OwnerKey = owner;
                retained.Name = merged.Active.Document.Name;
                retained.DocumentJson = merged.Serialize();
                retained.DocumentVersion = merged.Version;
                retained.Revision = checked(state.Dashboards.Max(row => row.Revision) + 1);
                retained.UpdatedAt = now;
                context.AnalysisDashboards.RemoveRange(state.Dashboards.Where(row => row != retained));
            }
            if (state.Inboxes.SingleOrDefault() is { } inbox)
            {
                inbox.OwnerKey = owner;
                inbox.Revision++;
                inbox.UpdatedAt = now;
            }
            foreach (var template in state.Templates)
            {
                template.UpdatedBy = owner;
                template.UpdatedAt = now;
            }
            var tenantId = await context.EntraUserIdentities.Where(identity => identity.UserId == userId)
                .Select(identity => (Guid?)identity.TenantId).SingleOrDefaultAsync(cancellationToken);
            foreach (var alias in state.Aliases)
            {
                var link = state.Links.SingleOrDefault(link => link.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
                if (link is null)
                {
                    link = new() { Alias = alias, TenantId = tenantId };
                    context.LegacyUserLinks.Add(link);
                }
                link.TargetUserId = userId;
                link.Status = LegacyUserLinkStatuses.Confirmed;
                link.ApprovedByUserId = approver;
                link.ApprovedAt = now;
                link.VerificationNote = verificationNote.Trim();
                link.Revision++;
            }
            state.User.UsesStableIdentity = true;
            state.User.Revision++;
            state.User.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken, IsolationLevel.Serializable);
    }

    private static async Task<State> ReadAsync(
        AlertDbContext context, Guid userId, string[] aliases, CancellationToken cancellationToken)
    {
        var user = await context.DirectoryUsers.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken)
            ?? throw new IdentityMigrationException("IdentityMigration.Errors.Target");
        var owner = DirectoryIdentity.OwnerKey(userId);
        var keys = aliases.Append(owner).ToArray();
        var dashboards = await context.AnalysisDashboards.Where(row => keys.Contains(row.OwnerKey)).OrderBy(row => row.OwnerKey).ToListAsync(cancellationToken);
        var inboxes = await context.InboxLayouts.Where(row => keys.Contains(row.OwnerKey)).OrderBy(row => row.OwnerKey).ToListAsync(cancellationToken);
        var templates = await context.AnalysisDashboardTemplates.Where(row => keys.Contains(row.UpdatedBy)).OrderBy(row => row.Id).ToListAsync(cancellationToken);
        var links = await context.LegacyUserLinks.Where(link => aliases.Contains(link.Alias)).OrderBy(link => link.Id).ToListAsync(cancellationToken);
        var jobs = await context.AgentAnalysisJobs.Where(row => keys.Contains(row.CreatedBy))
            .OrderBy(row => row.Id).Select(row => new { row.Id, row.CreatedBy, row.IdempotencyKey }).ToArrayAsync(cancellationToken);
        var conflicts = new List<string>();
        if (!user.IsEnabled || await context.ScimUserStates.AnyAsync(state => state.UserId == userId && !state.IsActive, cancellationToken))
            conflicts.Add("IdentityMigration.Conflicts.Disabled");
        if (links.GroupBy(link => link.Alias, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
            links.Any(link => link.TargetUserId != null && link.TargetUserId != userId))
            conflicts.Add("IdentityMigration.Conflicts.Alias");
        var historicalProfiles = HistoricalUserQueries.Profiles(context);
        var groupedProfiles = context.DirectoryUsers.Where(profile => aliases.Contains(profile.UserName));
        if (await context.HistoricalUserAliases.AnyAsync(link =>
            groupedProfiles.Any(profile => profile.Id == link.MainUserId || profile.Id == link.AliasUserId), cancellationToken))
            conflicts.Add("IdentityMigration.Conflicts.HistoricalGroup");
        if (await context.SqlUserIdentities.AnyAsync(identity => identity.UserId != userId && aliases.Contains(identity.Username), cancellationToken) ||
            await context.DirectoryUsers.AnyAsync(other => other.Id != userId && aliases.Contains(other.UserName) &&
                !historicalProfiles.Any(profile => profile.Id == other.Id), cancellationToken))
            conflicts.Add("IdentityMigration.Conflicts.Login");
        var otherObjects = await context.EntraUserIdentities.Where(identity => identity.UserId != userId)
            .Select(identity => identity.ObjectId).ToArrayAsync(cancellationToken);
        if (otherObjects.Any(objectId => aliases.Contains(objectId.ToString("D"), StringComparer.OrdinalIgnoreCase)))
            conflicts.Add("IdentityMigration.Conflicts.Login");
        var sqlName = await context.SqlUserIdentities.Where(identity => identity.UserId == userId)
            .Select(identity => identity.Username).SingleOrDefaultAsync(cancellationToken);
        var externalId = await context.EntraUserIdentities.Where(identity => identity.UserId == userId)
            .Select(identity => (Guid?)identity.ObjectId).SingleOrDefaultAsync(cancellationToken);
        if (sqlName is null && externalId is null)
            conflicts.Add("IdentityMigration.Conflicts.Identity");
        // Every current access key must be reviewed, even if it has no stored dashboard yet.
        var required = new[] { user.UserName, sqlName, externalId?.ToString("D") }.Where(value => !string.IsNullOrWhiteSpace(value));
        if (required.Any(value => !aliases.Contains(value!, StringComparer.OrdinalIgnoreCase)))
            conflicts.Add("IdentityMigration.Conflicts.MissingAlias");
        if (inboxes.Count > 1)
            conflicts.Add("IdentityMigration.Conflicts.Inbox");
        var count = 0;
        var ids = new HashSet<Guid>();
        foreach (var dashboard in dashboards)
        {
            try
            {
                using var json = JsonDocument.Parse(dashboard.DocumentJson);
                if (!json.RootElement.TryGetProperty("dashboards", out _))
                {
                    conflicts.Add("IdentityMigration.Conflicts.LegacyJson");
                    continue;
                }
                var entries = json.RootElement.GetProperty("dashboards");
                if (entries.ValueKind != JsonValueKind.Array || entries.EnumerateArray().Any(entry =>
                    entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !id.TryGetGuid(out var entryId) || entryId == Guid.Empty ||
                    !entry.TryGetProperty("document", out var document) || document.ValueKind != JsonValueKind.Object))
                {
                    conflicts.Add("IdentityMigration.Conflicts.InvalidJson");
                    continue;
                }
                var collection = AnalysisDashboardCollection.Deserialize(dashboard.DocumentJson);
                count += collection.Dashboards.Count;
                if (collection.Dashboards.Any(entry => !ids.Add(entry.Id)))
                    conflicts.Add("IdentityMigration.Conflicts.DashboardId");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                conflicts.Add("IdentityMigration.Conflicts.InvalidJson");
            }
        }
        if (await context.AnalysisDashboardTemplates.AnyAsync(row => ids.Contains(row.Id) && !keys.Contains(row.UpdatedBy), cancellationToken) ||
            templates.Any(template => !ids.Contains(template.Id)))
            conflicts.Add("IdentityMigration.Conflicts.Publication");
        var snapshot = JsonSerializer.Serialize(new { user, aliases, dashboards, inboxes, templates, links, jobs });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        return new(user, aliases, dashboards, inboxes, templates, links, snapshot,
            new(userId, fingerprint, aliases, dashboards.Count, count, inboxes.Count, templates.Count, jobs.Length,
                conflicts.Distinct().ToArray()));
    }

    private static string[] Normalize(IReadOnlyList<string> aliases)
    {
        var result = aliases.Select(alias => alias.Trim()).Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (result.Length > 100 || result.Any(alias => alias.Length > 256 || alias.StartsWith("user:", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals("open-access", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals("open-mode-operator", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals(CriticalAlertLogbook.SystemUser, StringComparison.OrdinalIgnoreCase)))
            throw new IdentityMigrationException("IdentityMigration.Errors.Aliases");
        return result;
    }

    private sealed record State(
        DirectoryUser User, string[] Aliases, List<AnalysisDashboard> Dashboards,
        List<InboxLayout> Inboxes, List<AnalysisDashboardTemplate> Templates,
        List<LegacyUserLink> Links, string OriginalStateJson, IdentityMigrationPreview Preview);
}
