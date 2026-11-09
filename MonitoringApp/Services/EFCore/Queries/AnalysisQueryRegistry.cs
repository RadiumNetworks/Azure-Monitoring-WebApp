using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class AnalysisQueryRegistry(
    IDbContextFactory<AlertDbContext> contextFactory,
    CrossTenantLogAnalyticsOptions options,
    AnalysisQueryRunner runner)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AnalysisQueryDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AnalysisQueryDefinitions
            .AsNoTracking()
            .Include(query => query.Workspaces)
            .Include(query => query.Versions)
            .OrderBy(query => query.Category)
            .ThenBy(query => query.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<int> InstallActiveDirectoryLibraryAsync(string actor, CancellationToken cancellationToken = default)
    {
        if (options.Workspaces.Length == 0)
            throw new AnalysisValidationException(AnalysisValidationCode.RegistryWorkspaceRequired, "Configure a Log Analytics workspace before installing the AD library.");
        var existing = (await ListAsync(cancellationToken)).Select(query => query.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = QueryTemplateCatalog.Entries.Where(entry => entry.Library == "ActiveDirectory")
            .GroupBy(entry => entry.TemplateId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "ActiveDirectory"), "*.json")
            .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(entries.Select(entry => entry.FileName)))
            throw new InvalidOperationException("The packaged AD library files do not match the trusted catalog. Import custom queries separately.");
        var templates = entries.Select(entry => (Entry: entry,
            Query: AnalysisQueryTemplates.Parse(QueryTemplateCatalog.ReadPackagedTemplate(entry)))).ToArray();
        var added = 0;
        foreach (var (entry, query) in templates)
        {
            if (existing.Contains(query.Key)) continue;
            query.Workspaces = options.Workspaces.Select(workspace => new AnalysisQueryWorkspace
            {
                WorkspaceKey = workspace.Key,
                Enabled = true
            }).ToList();
            await SaveDraftCoreAsync(query, actor, StandardQueryTextProvenance.Create(entry), cancellationToken);
            existing.Add(query.Key);
            added++;
        }
        return added;
    }

    public async Task<IReadOnlyList<AnalysisQueryDefinition>> ListPublishedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definitions = await context.AnalysisQueryDefinitions
            .AsNoTracking()
            .Include(query => query.Workspaces)
            .Include(query => query.Versions)
            .Where(query =>
                query.Enabled &&
                query.PublishedVersionId != null)
            .OrderBy(query => query.Category)
            .ThenBy(query => query.Name)
            .ToArrayAsync(cancellationToken);
        return definitions.Select(CreatePublishedDefinition).ToArray();
    }

    public Task<AnalysisQueryDefinition> SaveDraftAsync(
        AnalysisQueryDefinition draft,
        string actor,
        CancellationToken cancellationToken = default) =>
        SaveDraftCoreAsync(draft, actor, null, cancellationToken);

    private async Task<AnalysisQueryDefinition> SaveDraftCoreAsync(
        AnalysisQueryDefinition draft, string actor, string? newTemplateProvenance,
        CancellationToken cancellationToken)
    {
        AnalysisQueryValidator.RequireValidDefinition(draft);
        if (draft.Workspaces.Any(assignment => !options.Workspaces.Any(workspace =>
            workspace.Key.Equals(assignment.WorkspaceKey, StringComparison.OrdinalIgnoreCase))))
            throw new AnalysisValidationException(AnalysisValidationCode.RegistryWorkspaceUnavailable, "Select only configured Log Analytics workspaces.");
        if (!draft.Workspaces.Any(assignment => assignment.Enabled))
            throw new AnalysisValidationException(AnalysisValidationCode.RegistryWorkspaceRequired, "Select at least one Log Analytics workspace.");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedKey = draft.Key.Trim().ToLowerInvariant();
        if (await context.AnalysisQueryDefinitions.AnyAsync(
                query => query.Id != draft.Id && query.Key == normalizedKey,
                cancellationToken))
        {
            throw new AnalysisValidationException(AnalysisValidationCode.RegistryKeyExists, $"Query key '{normalizedKey}' already exists.");
        }

        var stored = draft.Id == Guid.Empty
            ? null
            : await context.AnalysisQueryDefinitions
                .Include(query => query.Workspaces)
                .SingleOrDefaultAsync(query => query.Id == draft.Id, cancellationToken);
        if (stored is null)
        {
            stored = new AnalysisQueryDefinition
            {
                Id = draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id
            };
            context.AnalysisQueryDefinitions.Add(stored);
            stored.StandardTextProvenanceJson = newTemplateProvenance;
            stored.Name = draft.Name;
            stored.Description = draft.Description;
        }
        else if (newTemplateProvenance is not null)
            throw new InvalidOperationException("Template creation cannot mark an existing analysis query.");

        stored.StandardTextProvenanceJson = StandardQueryTextProvenance.Retain(
            stored.StandardTextProvenanceJson, false, stored.Name, stored.Description, draft.Name, draft.Description);
        CopyDraft(draft, stored);
        stored.Key = normalizedKey;
        stored.Status = AnalysisQueryStatuses.Draft;
        AnalysisQueryWriteRules.AssignCategory(stored, draft.Category, actor);

        AddMissingWorkspaceAssignments(stored);
        foreach (var assignment in stored.Workspaces)
        {
            var enabled = draft.Workspaces.Any(item =>
                item.Enabled && item.WorkspaceKey.Equals(assignment.WorkspaceKey, StringComparison.OrdinalIgnoreCase));
            SetWorkspaceEnabled(assignment, enabled);
        }
        await context.SaveChangesAsync(cancellationToken);
        return stored;
    }

    public async Task<AnalysisQueryWorkspaceAssignmentResult> EnableWorkspacesForAllAsync(
        IReadOnlyCollection<string> workspaceKeys,
        bool includeDisabledQueries,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (workspaceKeys.Count == 0 || workspaceKeys.Any(string.IsNullOrWhiteSpace))
            throw new AnalysisValidationException(AnalysisValidationCode.RegistryWorkspaceRequired, "Select at least one configured Log Analytics workspace.");
        var selectedWorkspaces = workspaceKeys.Select(key =>
            options.Workspaces.SingleOrDefault(workspace => workspace.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new AnalysisValidationException(AnalysisValidationCode.RegistryWorkspaceUnavailable, $"Log Analytics workspace '{key}' is not configured."))
            .DistinctBy(workspace => workspace.Key, StringComparer.OrdinalIgnoreCase).ToArray();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var queries = await context.AnalysisQueryDefinitions
            .Include(query => query.Workspaces)
            .Where(query => includeDisabledQueries || query.Enabled && query.Status != AnalysisQueryStatuses.Disabled)
            .ToArrayAsync(cancellationToken);
        var updatedQueries = 0;
        var enabledAssignments = 0;
        var reactivatedQueries = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var query in queries)
        {
            var changed = false;
            foreach (var workspace in selectedWorkspaces)
            {
                var assignment = query.Workspaces.SingleOrDefault(item =>
                    item.WorkspaceKey.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase));
                if (assignment is null)
                {
                    assignment = new AnalysisQueryWorkspace
                    {
                        QueryDefinitionId = query.Id,
                        WorkspaceKey = workspace.Key,
                        Enabled = false
                    };
                    query.Workspaces.Add(assignment);
                }
                if (assignment.Enabled) continue;
                SetWorkspaceEnabled(assignment, true);
                enabledAssignments++;
                changed = true;
            }
            if (!query.Enabled || query.Status == AnalysisQueryStatuses.Disabled)
            {
                query.Enabled = true;
                reactivatedQueries++;
                changed = true;
            }
            if (!changed) continue;
            query.Status = AnalysisQueryStatuses.Draft;
            AnalysisQueryWriteRules.UpdateAudit(query, actor, now);
            updatedQueries++;
        }
        await context.SaveChangesAsync(cancellationToken);
        return new(updatedQueries, enabledAssignments, reactivatedQueries);
    }

    public async Task<AnalysisQueryResponse> TestDraftAsync(
        AnalysisQueryDefinition draft,
        string workspaceKey,
        IReadOnlyList<AnalysisQuerySystem> systems,
        int timeRangeMinutes,
        CancellationToken cancellationToken = default,
        int? binMinutes = null,
        string groupBy = "SourceDSA")
    {
        if (systems.Count is < 1 or > 100)
            throw new AnalysisValidationException(AnalysisValidationCode.SystemsRequired, "Select between 1 and 100 systems.");
        ComputerInventoryEntry[] selected;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
            selected = await AnalysisQueryInventory.LoadSelectedAsync(context, systems, cancellationToken);
        return await runner.ExecuteDraftAsync(
            draft, workspaceKey, selected, timeRangeMinutes, cancellationToken, binMinutes, groupBy);
    }

    public async Task<AnalysisQueryDefinition> ValidateAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken = default)
    {
        AnalysisQueryDefinition snapshot;
        ComputerInventoryEntry[] inventory;
        IReadOnlyDictionary<string, object?> expectedValues;
        WorkspaceValidationState[] expectedWorkspaces;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            snapshot = await context.AnalysisQueryDefinitions.AsNoTracking()
                .Include(item => item.Workspaces)
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new AnalysisValidationException(AnalysisValidationCode.RegistryQueryMissing,
                    "The query no longer exists. Refresh the registry.");
            expectedValues = CaptureValidationValues(context, snapshot);
            expectedWorkspaces = CaptureWorkspaceState(snapshot);
            AddMissingWorkspaceAssignments(snapshot);
            inventory = await context.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken);
        }

        var validations = await AnalysisQueryValidation.ExecuteAsync(
            snapshot, inventory, options, runner, cancellationToken);
        return await DatabaseTransactions.ExecuteAsync(contextFactory, async context =>
        {
            var stored = await context.AnalysisQueryDefinitions
                .Include(item => item.Workspaces)
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (stored is null ||
                !CaptureValidationValues(context, stored).All(item =>
                    expectedValues.TryGetValue(item.Key, out var expected) && ValidationValueEquals(item.Value, expected)) ||
                !expectedWorkspaces.SequenceEqual(CaptureWorkspaceState(stored)))
            {
                throw new DbUpdateConcurrencyException(
                    "The query or its workspace assignments changed during validation. Reload the registry and validate again.");
            }
            AddMissingWorkspaceAssignments(stored);
            foreach (var validation in validations)
            {
                var assignment = stored.Workspaces.Single(item =>
                    item.WorkspaceKey.Equals(validation.WorkspaceKey, StringComparison.OrdinalIgnoreCase));
                SetValidation(assignment, validation.Status, validation.Message, validation.CheckedAt);
            }
            stored.Status = stored.Workspaces.Any(item => item.Enabled) &&
                stored.Workspaces.Where(item => item.Enabled).All(item =>
                    item.LastValidationStatus == AnalysisQueryValidationStatuses.Succeeded)
                        ? AnalysisQueryStatuses.Validated
                        : AnalysisQueryStatuses.Draft;
            AnalysisQueryWriteRules.UpdateAudit(stored, actor);
            await context.SaveChangesAsync(cancellationToken);
            return stored;
        }, cancellationToken, IsolationLevel.Serializable);
    }

    private static IReadOnlyDictionary<string, object?> CaptureValidationValues(
        AlertDbContext context, AnalysisQueryDefinition query) =>
        context.Entry(query).Properties.ToDictionary(property => property.Metadata.Name,
            property => property.CurrentValue is byte[] bytes ? bytes.ToArray() : property.CurrentValue);

    private static bool ValidationValueEquals(object? current, object? expected) =>
        current is byte[] currentBytes && expected is byte[] expectedBytes
            ? currentBytes.AsSpan().SequenceEqual(expectedBytes)
            : Equals(current, expected);

    private static WorkspaceValidationState[] CaptureWorkspaceState(AnalysisQueryDefinition query) =>
        query.Workspaces.OrderBy(item => item.WorkspaceKey, StringComparer.Ordinal)
            .Select(item => new WorkspaceValidationState(item.WorkspaceKey, item.Enabled,
                item.LastValidatedAt, item.LastValidationStatus, item.LastValidationMessage)).ToArray();

    private sealed record WorkspaceValidationState(
        string Key, bool Enabled, DateTimeOffset? ValidatedAt, string Status, string Message);

    public async Task<AnalysisQueryDefinition> PublishAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions
            .Include(item => item.Workspaces)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new AnalysisValidationException(AnalysisValidationCode.RegistryQueryMissing,
                "The query no longer exists. Refresh the registry.");
        if (query.Status != AnalysisQueryStatuses.Validated ||
            query.Workspaces.Where(item => item.Enabled).Any(item =>
                item.LastValidationStatus != AnalysisQueryValidationStatuses.Succeeded))
        {
            throw new AnalysisValidationException(AnalysisValidationCode.RegistryPublishRequired, "The query must validate successfully on every enabled workspace before publishing.");
        }

        query.Version++;
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(),
            QueryDefinitionId = query.Id,
            Version = query.Version,
            SnapshotJson = JsonSerializer.Serialize(CreateSnapshot(query), JsonOptions),
            StandardTextProvenanceJson = StandardQueryTextProvenance.Retain(
                query.StandardTextProvenanceJson, false, query.Name, query.Description, query.Name, query.Description),
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBy = actor.Trim()
        };
        context.AnalysisQueryVersions.Add(version);
        query.PublishedVersionId = version.Id;
        query.Status = AnalysisQueryStatuses.Published;
        query.Enabled = true;
        AnalysisQueryWriteRules.UpdateAudit(query, actor, version.PublishedAt);
        await context.SaveChangesAsync(cancellationToken);
        return query;
    }

    public async Task UnpublishAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new AnalysisValidationException(AnalysisValidationCode.RegistryQueryMissing,
                "The query no longer exists. Refresh the registry.");
        if (query.PublishedVersionId is null)
            throw new AnalysisValidationException(AnalysisValidationCode.RegistryPublicationMissing, "The query has no published version to withdraw.");

        query.PublishedVersionId = null;
        query.Status = query.Enabled ? AnalysisQueryStatuses.Draft : AnalysisQueryStatuses.Disabled;
        AnalysisQueryWriteRules.UpdateAudit(query, actor);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DisableAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new AnalysisValidationException(AnalysisValidationCode.RegistryQueryMissing,
                "The query no longer exists. Refresh the registry.");
        query.Enabled = false;
        query.Status = AnalysisQueryStatuses.Disabled;
        AnalysisQueryWriteRules.UpdateAudit(query, actor);
        await context.SaveChangesAsync(cancellationToken);
    }

    public static AnalysisQuerySnapshot CreateSnapshot(AnalysisQueryDefinition query) => new(
        query.Key,
        query.Name,
        query.Description,
        query.Category,
        query.Kql,
        query.VisualizationType,
        query.Unit,
        query.DefaultTimeRangeMinutes,
        query.MaxTimeRangeMinutes,
        query.DefaultBinMinutes,
        query.MinimumRole,
        AnalysisVisualization.Parse(query.VisualizationJson),
        query.Scope);

    public static AnalysisQueryDefinition CreatePublishedDefinition(AnalysisQueryDefinition query)
    {
        AnalysisQueryVersion version;
        try { version = query.Versions.Single(item => item.Id == query.PublishedVersionId); }
        catch (InvalidOperationException exception)
        {
            throw new AnalysisQueryPublicationException(AnalysisQueryPublicationCode.VersionUnavailable, exception.Message, exception);
        }
        AnalysisQuerySnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<AnalysisQuerySnapshot>(version.SnapshotJson, JsonOptions)
                ?? throw new AnalysisQueryPublicationException(AnalysisQueryPublicationCode.SnapshotInvalid,
                    "The published query snapshot is invalid.");
        }
        catch (JsonException exception)
        {
            throw new AnalysisQuerySnapshotJsonException(exception);
        }
        return new AnalysisQueryDefinition
        {
            Id = query.Id,
            Key = snapshot.Key,
            Name = snapshot.Name,
            Description = snapshot.Description,
            StandardTextProvenanceJson = StandardQueryTextProvenance.Retain(
                version.StandardTextProvenanceJson, false, snapshot.Name, snapshot.Description, snapshot.Name, snapshot.Description),
            Category = query.Category,
            Kql = snapshot.Kql,
            Scope = snapshot.Scope,
            VisualizationType = snapshot.VisualizationType,
            VisualizationJson = AnalysisVisualization.Serialize(snapshot.Visualization),
            Unit = snapshot.Unit,
            DefaultTimeRangeMinutes = snapshot.DefaultTimeRangeMinutes,
            MaxTimeRangeMinutes = snapshot.MaxTimeRangeMinutes,
            DefaultBinMinutes = snapshot.DefaultBinMinutes,
            MinimumRole = snapshot.MinimumRole,
            Status = AnalysisQueryStatuses.Published,
            Enabled = query.Enabled,
            Version = version.Version,
            PublishedVersionId = version.Id,
            Workspaces = query.Workspaces
        };
    }

    private static void CopyDraft(AnalysisQueryDefinition source, AnalysisQueryDefinition target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description.Trim();
        target.Kql = source.Kql.Trim();
        target.Scope = source.Scope;
        target.VisualizationType = source.VisualizationType;
        target.VisualizationJson = source.VisualizationJson;
        target.Unit = source.Unit.Trim();
        target.DefaultTimeRangeMinutes = source.DefaultTimeRangeMinutes;
        target.MaxTimeRangeMinutes = source.MaxTimeRangeMinutes;
        target.DefaultBinMinutes = source.DefaultBinMinutes;
        target.MinimumRole = source.MinimumRole;
        target.Enabled = source.Enabled;
    }

    private static void SetValidation(
        AnalysisQueryWorkspace workspace, string status, string message, DateTimeOffset checkedAt)
    {
        workspace.LastValidatedAt = checkedAt;
        workspace.LastValidationStatus = status;
        workspace.LastValidationMessage = message.Length <= 1000 ? message : message[..1000];
    }

    private static void SetWorkspaceEnabled(AnalysisQueryWorkspace assignment, bool enabled)
    {
        if (enabled && !assignment.Enabled)
        {
            assignment.LastValidatedAt = null;
            assignment.LastValidationStatus = AnalysisQueryValidationStatuses.NotValidated;
            assignment.LastValidationMessage = string.Empty;
        }
        assignment.Enabled = enabled;
    }

    private void AddMissingWorkspaceAssignments(AnalysisQueryDefinition query)
    {
        foreach (var workspace in options.Workspaces.Where(workspace =>
            query.Workspaces.All(item =>
                !item.WorkspaceKey.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase))))
        {
            query.Workspaces.Add(new AnalysisQueryWorkspace
            {
                QueryDefinitionId = query.Id,
                WorkspaceKey = workspace.Key,
                Enabled = false
            });
        }
    }
}
