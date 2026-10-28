using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
            throw new InvalidOperationException("Configure a Log Analytics workspace before installing the AD library.");
        var existing = (await ListAsync(cancellationToken)).Select(query => query.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var query in ActiveDirectoryAnalysisQueries.Create().Where(query => !existing.Contains(query.Key)))
        {
            query.Workspaces = options.Workspaces.Select(workspace => new AnalysisQueryWorkspace
            {
                WorkspaceKey = workspace.Key,
                Enabled = true
            }).ToList();
            await SaveDraftAsync(query, actor, cancellationToken);
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

    public async Task<AnalysisQueryDefinition> SaveDraftAsync(
        AnalysisQueryDefinition draft,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var errors = AnalysisQueryValidator.ValidateDefinition(draft);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
        if (draft.Workspaces.Any(assignment => !options.Workspaces.Any(workspace =>
            workspace.Key.Equals(assignment.WorkspaceKey, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Select only configured Log Analytics workspaces.");
        if (!draft.Workspaces.Any(assignment => assignment.Enabled))
            throw new InvalidOperationException("Select at least one Log Analytics workspace.");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedKey = draft.Key.Trim().ToLowerInvariant();
        if (await context.AnalysisQueryDefinitions.AnyAsync(
                query => query.Id != draft.Id && query.Key == normalizedKey,
                cancellationToken))
        {
            throw new InvalidOperationException($"Query key '{normalizedKey}' already exists.");
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
        }

        CopyDraft(draft, stored);
        stored.Key = normalizedKey;
        stored.Status = AnalysisQueryStatuses.Draft;
        stored.UpdatedAt = DateTimeOffset.UtcNow;
        stored.UpdatedBy = actor.Trim();

        foreach (var workspace in options.Workspaces)
        {
            var assignment = stored.Workspaces.FirstOrDefault(item =>
                item.WorkspaceKey.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase));
            if (assignment is null)
            {
                stored.Workspaces.Add(new AnalysisQueryWorkspace
                {
                    QueryDefinitionId = stored.Id,
                    WorkspaceKey = workspace.Key,
                    Enabled = false
                });
            }
        }
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
            throw new InvalidOperationException("Select at least one configured Log Analytics workspace.");
        var selectedWorkspaces = workspaceKeys.Select(key =>
            options.Workspaces.SingleOrDefault(workspace => workspace.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Log Analytics workspace '{key}' is not configured."))
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
            query.UpdatedAt = now;
            query.UpdatedBy = actor.Trim();
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
            throw new InvalidOperationException("Select between 1 and 100 systems.");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var subscriptions = systems.Select(system => system.SubscriptionId).Distinct().ToArray();
        var computers = systems.Select(system => system.Computer).Distinct().ToArray();
        var inventory = await context.ComputerInventory.AsNoTracking()
            .Where(entry => subscriptions.Contains(entry.SubscriptionId) && computers.Contains(entry.Computer))
            .ToArrayAsync(cancellationToken);
        var selected = systems.Distinct().Select(system => inventory.SingleOrDefault(entry =>
            entry.SubscriptionId.Equals(system.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
            entry.Computer.Equals(system.Computer, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Any(entry => entry is null))
            throw new InvalidOperationException("At least one selected system is not present in inventory.");
        return await runner.ExecuteDraftAsync(
            draft, workspaceKey, selected.Select(entry => entry!).ToArray(), timeRangeMinutes, cancellationToken, binMinutes, groupBy);
    }

    public async Task<AnalysisQueryDefinition> ValidateAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions
            .Include(item => item.Workspaces)
            .SingleAsync(item => item.Id == id, cancellationToken);
        AddMissingWorkspaceAssignments(query);
        var definitionErrors = AnalysisQueryValidator.ValidateDefinition(query);
        var kqlErrors = AnalysisQueryValidator.ValidateKql(query.Kql, query.VisualizationType, query.Scope);
        if (definitionErrors.Count + kqlErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", definitionErrors.Concat(kqlErrors)));
        }

        var inventory = await context.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken);
        foreach (var assignment in query.Workspaces.Where(item => item.Enabled))
        {
            var workspace = options.Workspaces.Single(item =>
                item.Key.Equals(assignment.WorkspaceKey, StringComparison.OrdinalIgnoreCase));
            var sample = inventory.FirstOrDefault(item => ResolvesTo(item, workspace.Key));
            if (sample is null)
            {
                SetValidation(
                    assignment,
                    AnalysisQueryValidationStatuses.Failed,
                    "No inventory system routes to this workspace.");
                continue;
            }

            var result = await runner.TryQueryAsync(
                workspace,
                query.Kql,
                [sample.Computer],
                Math.Min(query.DefaultTimeRangeMinutes, 60),
                query.DefaultBinMinutes,
                cancellationToken,
                query.VisualizationType);
            SetValidation(
                assignment,
                result.Error is null
                    ? AnalysisQueryValidationStatuses.Succeeded
                    : AnalysisQueryValidationStatuses.Failed,
                result.Error ?? $"Validated with {result.Tables?.Sum(table => table.Rows.Count) ?? result.Rows.Count} row(s).");
        }

        query.Status = query.Workspaces.Any(item => item.Enabled) &&
            query.Workspaces.Where(item => item.Enabled).All(item =>
            item.LastValidationStatus == AnalysisQueryValidationStatuses.Succeeded)
                ? AnalysisQueryStatuses.Validated
                : AnalysisQueryStatuses.Draft;
        query.UpdatedAt = DateTimeOffset.UtcNow;
        query.UpdatedBy = actor.Trim();
        await context.SaveChangesAsync(cancellationToken);
        return query;

        bool ResolvesTo(ComputerInventoryEntry entry, string workspaceKey)
        {
            try
            {
                return runner.WorkspaceResolver.Resolve(entry.SubscriptionId, entry.Domain).Workspace.Key
                    .Equals(workspaceKey, StringComparison.OrdinalIgnoreCase);
            }
            catch (TargetWorkspaceResolutionException)
            {
                return false;
            }
        }
    }

    public async Task<AnalysisQueryDefinition> PublishAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions
            .Include(item => item.Workspaces)
            .SingleAsync(item => item.Id == id, cancellationToken);
        if (query.Status != AnalysisQueryStatuses.Validated ||
            query.Workspaces.Where(item => item.Enabled).Any(item =>
                item.LastValidationStatus != AnalysisQueryValidationStatuses.Succeeded))
        {
            throw new InvalidOperationException("The query must validate successfully on every enabled workspace before publishing.");
        }

        query.Version++;
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(),
            QueryDefinitionId = query.Id,
            Version = query.Version,
            SnapshotJson = JsonSerializer.Serialize(CreateSnapshot(query), JsonOptions),
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBy = actor.Trim()
        };
        context.AnalysisQueryVersions.Add(version);
        query.PublishedVersionId = version.Id;
        query.Status = AnalysisQueryStatuses.Published;
        query.Enabled = true;
        query.UpdatedAt = version.PublishedAt;
        query.UpdatedBy = actor.Trim();
        await context.SaveChangesAsync(cancellationToken);
        return query;
    }

    public async Task UnpublishAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions.SingleAsync(item => item.Id == id, cancellationToken);
        if (query.PublishedVersionId is null)
            throw new InvalidOperationException("The query has no published version to withdraw.");

        query.PublishedVersionId = null;
        query.Status = query.Enabled ? AnalysisQueryStatuses.Draft : AnalysisQueryStatuses.Disabled;
        query.UpdatedAt = DateTimeOffset.UtcNow;
        query.UpdatedBy = actor.Trim();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DisableAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions.SingleAsync(item => item.Id == id, cancellationToken);
        query.Enabled = false;
        query.Status = AnalysisQueryStatuses.Disabled;
        query.UpdatedAt = DateTimeOffset.UtcNow;
        query.UpdatedBy = actor.Trim();
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
        var version = query.Versions.Single(item => item.Id == query.PublishedVersionId);
        var snapshot = JsonSerializer.Deserialize<AnalysisQuerySnapshot>(version.SnapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("The published query snapshot is invalid.");
        return new AnalysisQueryDefinition
        {
            Id = query.Id,
            Key = snapshot.Key,
            Name = snapshot.Name,
            Description = snapshot.Description,
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
        target.Category = AnalysisQueryCategories.Normalize(source.Category);
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

    private static void SetValidation(AnalysisQueryWorkspace workspace, string status, string message)
    {
        workspace.LastValidatedAt = DateTimeOffset.UtcNow;
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

public static partial class AnalysisQueryValidator
{
    [GeneratedRegex(@"(?im)^\s*\.|workspace\s*\(|cluster\s*\(")]
    private static partial Regex UnsafeKqlPattern();

    public static IReadOnlyList<string> ValidateDefinition(AnalysisQueryDefinition query)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(query.Key) || query.Key.Trim().Length > 128)
            errors.Add("Key is required and may contain at most 128 characters.");
        if (string.IsNullOrWhiteSpace(query.Name) || query.Name.Trim().Length > 256)
            errors.Add("Name is required and may contain at most 256 characters.");
        try { AnalysisQueryCategories.Normalize(query.Category); }
        catch (InvalidOperationException exception) { errors.Add(exception.Message); }
        if (query.DefaultTimeRangeMinutes is < 5 or > 43200)
            errors.Add("Default time range must be between 5 and 43200 minutes.");
        if (query.MaxTimeRangeMinutes < query.DefaultTimeRangeMinutes || query.MaxTimeRangeMinutes > 43200)
            errors.Add("Maximum time range must include the default and may not exceed 43200 minutes.");
        if (query.DefaultBinMinutes is < 1 or > 1440)
            errors.Add("Bin size must be between 1 and 1440 minutes.");
        if (!AnalysisQueryVisualizationTypes.All.Contains(query.VisualizationType))
            errors.Add("Select a supported visualization type.");
        if (!AnalysisQueryScopes.All.Contains(query.Scope))
            errors.Add("Select a supported query scope (SelectedSystems or Workspace).");
        if (query.MinimumRole is not (SqlAuthenticationRoles.Operator or SqlAuthenticationRoles.Admin))
            errors.Add("Minimum role must be Operator or Admin.");
        try
        {
            AnalysisVisualization.Parse(query.VisualizationJson);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add(exception.Message);
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateKql(
        string kql,
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart,
        string scope = AnalysisQueryScopes.SelectedSystems)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(kql))
            errors.Add("KQL is required.");
        if (!AnalysisQueryScopes.All.Contains(scope))
            errors.Add("Select a supported query scope (SelectedSystems or Workspace).");
        if (scope == AnalysisQueryScopes.SelectedSystems && !kql.Contains("_SelectedSystems", StringComparison.Ordinal))
            errors.Add("KQL must use _SelectedSystems.");
        if (scope == AnalysisQueryScopes.Workspace && !kql.Contains("_TimePeriod", StringComparison.Ordinal))
            errors.Add("Workspace-wide KQL must use _TimePeriod.");
        if (visualizationType is AnalysisQueryVisualizationTypes.LineChart or AnalysisQueryVisualizationTypes.StatusHistory && !kql.Contains("_BinSize", StringComparison.Ordinal))
            errors.Add("KQL must use _BinSize.");
        if (UnsafeKqlPattern().IsMatch(kql))
            errors.Add("Management commands and explicit workspace or cluster references are not allowed.");
        return errors;
    }
}

public interface IAnalysisLogAnalyticsQuery
{
    Task<LogsQueryResult> QueryAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
        string kql, int timeRangeMinutes, CancellationToken cancellationToken);
}

public sealed class AzureAnalysisLogAnalyticsQuery : IAnalysisLogAnalyticsQuery
{
    public async Task<LogsQueryResult> QueryAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string secret,
        string kql, int timeRangeMinutes, CancellationToken cancellationToken)
    {
        var credential = new ClientSecretCredential(workspace.TenantId, applicationClientId, secret);
        var response = await new LogsQueryClient(credential).QueryWorkspaceAsync(
            workspace.WorkspaceId, kql,
            new LogsQueryTimeRange(TimeSpan.FromMinutes(timeRangeMinutes)),
            cancellationToken: cancellationToken);
        return response.Value;
    }
}

public sealed class AnalysisQueryRunner(
    CrossTenantLogAnalyticsOptions options,
    ILogAnalyticsSecretProvider secretProvider,
    TargetWorkspaceResolver workspaceResolver,
    IAnalysisLogAnalyticsQuery? queryClient = null,
    ILogger<AnalysisQueryRunner>? logger = null)
{
    public const int MaxTableRows = 1000;
    public const int MaxTableResponseBytes = 4 * 1024 * 1024;
    private const int MaxDiagnosticMessageLength = 2048;
    private readonly IAnalysisLogAnalyticsQuery queryClient = queryClient ?? new AzureAnalysisLogAnalyticsQuery();
    private readonly ILogger<AnalysisQueryRunner> logger = logger ?? NullLogger<AnalysisQueryRunner>.Instance;
    public TargetWorkspaceResolver WorkspaceResolver { get; } = workspaceResolver;

    public AnalysisQueryExecutionPreview CreateDraftExecutionPreview(
        AnalysisQueryDefinition definition,
        string workspaceKey,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        int? binMinutes = null,
        string groupBy = "SourceDSA")
    {
        if (!options.Enabled)
            throw new InvalidOperationException("Live Log Analytics is disabled.");
        var errors = AnalysisQueryValidator.ValidateDefinition(definition)
            .Concat(AnalysisQueryValidator.ValidateKql(definition.Kql, definition.VisualizationType, definition.Scope)).ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
        ValidateExecutionLimits(definition, systems.Count, requestedMinutes);
        AnalysisQuerySelections.Validate(binMinutes == definition.DefaultBinMinutes ? null : binMinutes, groupBy);
        var workspace = options.Workspaces.SingleOrDefault(item =>
            item.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Select a configured Log Analytics workspace.");
        if (!definition.Workspaces.Any(item => item.Enabled &&
            item.WorkspaceKey.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Enable the selected workspace for this query.");
        if (systems.Any(system => !WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain)
            .Workspace.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Every selected system must route to the selected workspace.");
        var names = systems.Select(system => system.Computer)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(workspace.Key, workspace.DisplayName, names,
            BuildEffectiveKql(definition.Kql, names, binMinutes ?? definition.DefaultBinMinutes, requestedMinutes, groupBy, definition.VisualizationType));
    }

    public async Task<AnalysisQueryResponse> ExecuteDraftAsync(
        AnalysisQueryDefinition definition,
        string workspaceKey,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        CancellationToken cancellationToken = default,
        int? binMinutes = null,
        string groupBy = "SourceDSA")
    {
        var preview = CreateDraftExecutionPreview(definition, workspaceKey, systems, requestedMinutes, binMinutes, groupBy);
        var workspace = options.Workspaces.Single(item =>
            item.Key.Equals(preview.WorkspaceKey, StringComparison.OrdinalIgnoreCase));
        var watch = Stopwatch.StartNew();
        var from = DateTimeOffset.UtcNow.AddMinutes(-requestedMinutes);
        var result = await TryQueryAsync(workspace, definition.Kql, preview.Systems,
            requestedMinutes, binMinutes ?? definition.DefaultBinMinutes, cancellationToken, definition.VisualizationType, groupBy);
        if (result.Error is not null)
            throw new InvalidOperationException($"{workspace.DisplayName}: {result.Error}");
        var now = DateTimeOffset.UtcNow;
        return new(definition.Id, definition.Name, definition.Version, definition.Unit,
            from, now, now, watch.ElapsedMilliseconds, CreateSeries(result.Rows, workspace.DisplayName),
            result.Warning is null ? [] : [new(workspace.DisplayName, result.Warning)],
            definition.VisualizationType, result.Tables, AnalysisVisualization.Parse(definition.VisualizationJson),
            binMinutes ?? definition.DefaultBinMinutes, definition.Scope);
    }

    public IReadOnlyList<AnalysisQueryExecutionPreview> CreateExecutionPreviews(
        AnalysisQueryDefinition definition,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        int? binMinutes = null,
        string groupBy = "SourceDSA",
        string? workspaceKey = null,
        string? domain = null,
        string? site = null,
        string? role = null)
    {
        systems = FilterWorkspaceSystems(definition, systems, workspaceKey, domain, site, role);
        ValidateExecutionLimits(definition, systems.Count, requestedMinutes);
        AnalysisQuerySelections.Validate(binMinutes == definition.DefaultBinMinutes ? null : binMinutes, groupBy);
        return systems
            .Select(system => (
                System: system,
                Resolution: WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain)))
            .GroupBy(item => item.Resolution.Workspace.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var workspace = group.First().Resolution.Workspace;
                var names = group.Select(item => item.System.Computer)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new AnalysisQueryExecutionPreview(
                    workspace.Key,
                    workspace.DisplayName,
                    names,
                    BuildEffectiveKql(definition.Kql, names, binMinutes ?? definition.DefaultBinMinutes, requestedMinutes, groupBy, definition.VisualizationType));
            })
            .OrderBy(preview => preview.WorkspaceDisplayName)
            .ToArray();
    }

    private IReadOnlyList<ComputerInventoryEntry> FilterWorkspaceSystems(
        AnalysisQueryDefinition definition,
        IReadOnlyList<ComputerInventoryEntry> systems, string? workspaceKey, string? domain, string? site, string? role)
    {
        if (definition.Scope == AnalysisQueryScopes.Workspace ||
            (string.IsNullOrEmpty(workspaceKey) && string.IsNullOrEmpty(domain) &&
             string.IsNullOrEmpty(site) && string.IsNullOrEmpty(role)))
            return systems;
        var workspace = string.IsNullOrEmpty(workspaceKey) ? null :
            options.Workspaces.SingleOrDefault(item => item.Key.Equals(workspaceKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected Log Analytics workspace is not configured.");
        var normalizedDomain = CrossTenantLogAnalyticsOptions.NormalizeDomain(domain);
        CrossTenantLogAnalyticsWorkspaceOptions? domainWorkspace = null;
        if (!string.IsNullOrEmpty(domain))
        {
            if (normalizedDomain.Length == 0)
                throw new InvalidOperationException("The selected domain is not configured for the selected workspace.");
            domainWorkspace = options.Workspaces.SingleOrDefault(item =>
                (workspace is null || item.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)) &&
                item.Domains.Any(value => CrossTenantLogAnalyticsOptions.NormalizeDomain(value) == normalizedDomain))
                ?? systems.Where(system => CrossTenantLogAnalyticsOptions.NormalizeDomain(system.Domain) == normalizedDomain)
                    .Select(system => WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain).Workspace)
                    .FirstOrDefault(item => workspace is null || item.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected domain is not configured for the selected workspace.");
        }
        var filtered = systems.Where(system =>
            (workspace is null || WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain)
                .Workspace.Key.Equals(workspace.Key, StringComparison.OrdinalIgnoreCase)) &&
            (domainWorkspace is null || CrossTenantLogAnalyticsOptions.NormalizeDomain(system.Domain) == normalizedDomain)).ToArray();
        if (filtered.Length == 0)
        {
            if (domainWorkspace is not null)
                throw new InvalidOperationException($"No selected systems belong to domain '{domainWorkspace.GetDomainDisplayName(normalizedDomain)}'. Select systems from this domain or choose All domains.");
            if (workspace is not null)
                throw new InvalidOperationException($"No selected systems belong to workspace '{workspace.DisplayName}'. Select systems from this workspace or choose All workspaces.");
        }
        var matching = filtered.Where(system =>
            (string.IsNullOrEmpty(site) || string.Equals(system.Site, site, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(role) || string.Equals(system.Role, role, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matching.Length == 0 && (!string.IsNullOrEmpty(site) || !string.IsNullOrEmpty(role)))
            throw new InvalidOperationException("No selected systems match the selected site and role filters. Select matching systems or choose All sites / All roles.");
        return matching;
    }

    public async Task<AnalysisQueryResponse> ExecuteAsync(
        AnalysisQueryDefinition definition,
        IReadOnlyList<ComputerInventoryEntry> systems,
        int requestedMinutes,
        CancellationToken cancellationToken = default,
        int? binMinutes = null,
        string groupBy = "SourceDSA",
        string? workspaceKey = null,
        string? domain = null,
        string? site = null,
        string? role = null)
    {
        systems = FilterWorkspaceSystems(definition, systems, workspaceKey, domain, site, role);
        ValidateExecutionLimits(definition, systems.Count, requestedMinutes);
        AnalysisQuerySelections.Validate(binMinutes == definition.DefaultBinMinutes ? null : binMinutes, groupBy);
        var minutes = requestedMinutes;
        var grouped = systems
            .Select(system => (System: system, Resolution: WorkspaceResolver.Resolve(system.SubscriptionId, system.Domain)))
            .GroupBy(item => item.Resolution.Workspace.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var enabledWorkspaces = definition.Workspaces
            .Where(item => item.Enabled && item.LastValidationStatus == AnalysisQueryValidationStatuses.Succeeded)
            .Select(item => item.WorkspaceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grouped.Any(group => !enabledWorkspaces.Contains(group.Key)))
            throw new InvalidOperationException("The published query is not validated for every selected system workspace.");

        var watch = Stopwatch.StartNew();
        var series = new List<AnalysisQuerySeries>();
        var tables = new List<AnalysisQueryTable>();
        var succeeded = 0;
        var warnings = new List<AnalysisQueryWarning>();
        foreach (var group in grouped)
        {
            var workspace = group.First().Resolution.Workspace;
            var result = await TryQueryAsync(
                workspace,
                definition.Kql,
                group.Select(item => item.System.Computer).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                minutes,
                binMinutes ?? definition.DefaultBinMinutes,
                cancellationToken,
                definition.VisualizationType,
                groupBy);
            if (result.Error is not null)
            {
                warnings.Add(new AnalysisQueryWarning(workspace.DisplayName, result.Error));
                continue;
            }
            series.AddRange(CreateSeries(result.Rows, workspace.DisplayName));
            tables.AddRange(result.Tables ?? []);
            if (result.Warning is not null)
                warnings.Add(new(workspace.DisplayName, result.Warning));
            succeeded++;
        }

        if (succeeded == 0 && warnings.Count > 0)
            throw new InvalidOperationException(string.Join(" ", warnings.Select(warning => $"{warning.Workspace}: {warning.Message}")));

        var now = DateTimeOffset.UtcNow;
        return new AnalysisQueryResponse(
            definition.Id,
            definition.Name,
            definition.Version,
            definition.Unit,
            now.AddMinutes(-minutes),
            now,
            now,
            watch.ElapsedMilliseconds,
            series,
            warnings,
            definition.VisualizationType,
            tables,
            AnalysisVisualization.Parse(definition.VisualizationJson),
            binMinutes ?? definition.DefaultBinMinutes,
            definition.Scope);
    }

    public async Task<AnalysisQueryRunResult> TryQueryAsync(
        CrossTenantLogAnalyticsWorkspaceOptions workspace,
        string kql,
        IReadOnlyList<string> systems,
        int timeRangeMinutes,
        int binMinutes,
        CancellationToken cancellationToken,
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart,
        string groupBy = "SourceDSA")
    {
        try
        {
            var secret = await secretProvider.GetClientSecretAsync(cancellationToken);
            var response = await queryClient.QueryAsync(
                workspace, options.ApplicationClientId, secret,
                BuildEffectiveKql(kql, systems, binMinutes, timeRangeMinutes, groupBy, visualizationType),
                timeRangeMinutes, cancellationToken);
            if (response.Error is not null)
            {
                var diagnosticId = Guid.NewGuid().ToString("N");
                var details = LimitDiagnostic(response.Error.Message);
                var loggingFailure = LogFailure(diagnosticId, $"{response.Error.Code}: {details}");
                return new([], $"Query failed: {response.Error.Code}. {details} Partial results were not used. Diagnostic ID: {diagnosticId}.{loggingFailure}");
            }
            return NormalizeResult(response.Table, workspace.DisplayName, visualizationType);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(exception, "The Log Analytics request timed out. Try a shorter time range or fewer systems.");
        }
        catch (Azure.RequestFailedException exception)
        {
            return Failure(exception,
                $"Azure HTTP {exception.Status}" +
                (string.IsNullOrWhiteSpace(exception.ErrorCode) ? "." : $" ({exception.ErrorCode})."));
        }
        catch (Exception exception) when (exception is AuthenticationFailedException or InvalidOperationException)
        {
            return Failure(exception, exception is AuthenticationFailedException
                ? "Log Analytics authentication failed."
                : exception.Message);
        }

        AnalysisQueryRunResult Failure(Exception exception, string message)
        {
            var diagnosticId = Guid.NewGuid().ToString("N");
            var loggingFailure = LogFailure(diagnosticId, $"{exception.GetType().Name}: {LimitDiagnostic(exception.Message)}");
            return new([], $"Query failed: {LimitDiagnostic(message)} Diagnostic ID: {diagnosticId}. See application logs for details.{loggingFailure}");
        }

        string? LogFailure(string diagnosticId, string details)
        {
            try
            {
                logger.LogWarning(
                    "Log Analytics query failure {DiagnosticId}. Workspace={WorkspaceKey}, Systems={SystemCount}, Minutes={Minutes}, Details={Details}",
                    diagnosticId, workspace.Key, systems.Count, timeRangeMinutes, LimitDiagnostic(details));
                return null;
            }
            catch (AggregateException exception) when (exception.Flatten().InnerExceptions.Any(inner => inner is OutOfMemoryException))
            {
                return " Diagnostic logging also failed due to insufficient memory. Restart the application after deploying the memory limits.";
            }
        }
    }

    private static string LimitDiagnostic(string? message) =>
        message is null ? "No additional error details were returned." :
        message.Length <= MaxDiagnosticMessageLength ? message : message[..MaxDiagnosticMessageLength] + "...";

    public static string BuildEffectiveKql(
        string kql,
        IReadOnlyList<string> systems,
        int binMinutes,
        int timeRangeMinutes = 1440,
        string groupBy = "SourceDSA",
        string visualizationType = AnalysisQueryVisualizationTypes.LineChart)
    {
        if (binMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(binMinutes));
        if (timeRangeMinutes is < 5 or > 43200)
            throw new ArgumentOutOfRangeException(nameof(timeRangeMinutes));
        AnalysisQuerySelections.Validate(null, groupBy);
        var encodedSystems = JsonSerializer.Serialize(systems);
        var parameters = $"let _SelectedSystems = dynamic({encodedSystems});\nlet _BinSize = {binMinutes}m;\nlet _TimePeriod = {timeRangeMinutes}m;\nlet _GroupBy = \"{groupBy}\";\n";
        return visualizationType == AnalysisQueryVisualizationTypes.Table
            ? $"set truncationmaxsize={MaxTableResponseBytes};\n{parameters}let _MonitoringTableResult = () {{\n{kql}\n}};\n_MonitoringTableResult()\n| take {MaxTableRows + 1}"
            : parameters + kql;
    }

    private static string? FormatCell(object? value) => value switch
    {
        null => null,
        DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    public static AnalysisQueryRunResult NormalizeResult(LogsTable table, string workspace, string visualizationType)
    {
        var limit = visualizationType == AnalysisQueryVisualizationTypes.Table ? MaxTableRows : int.MaxValue;
        var normalized = NormalizeResult(new AnalysisQueryTable(
            workspace,
            table.Columns.Select(column => new AnalysisQueryColumn(column.Name, column.Type.ToString())).ToArray(),
            table.Rows.Take(limit).Select(row => (IReadOnlyList<string?>)Enumerable.Range(0, row.Count)
                .Select(index => FormatCell(row[index])).ToArray()).ToArray()), visualizationType);
        return visualizationType == AnalysisQueryVisualizationTypes.Table && table.Rows.Count > MaxTableRows
            ? normalized with { Warning = $"Table result limited to {MaxTableRows} rows for this workspace. More rows exist; narrow the filters or time range to inspect them." }
            : normalized;
    }

    public static AnalysisQueryRunResult NormalizeResult(AnalysisQueryTable table, string visualizationType)
    {
        if (!AnalysisQueryVisualizationTypes.All.Contains(visualizationType))
            return new([], "Unsupported visualization type.");
        if (table.Rows.Any(row => row.Count != table.Columns.Count))
            return new([], "The query returned inconsistent table rows.");
        if (visualizationType == AnalysisQueryVisualizationTypes.Table)
            return new([], null, [table]);
        var names = table.Columns.Select(column => column.Name).ToList();
        var timestampIndex = names.IndexOf("Timestamp");
        var seriesIndex = names.IndexOf("Series");
        var valueIndex = names.IndexOf("Value");
        if (visualizationType == AnalysisQueryVisualizationTypes.Stat)
        {
            if (seriesIndex < 0 || valueIndex < 0 ||
                table.Rows.Any(row => string.IsNullOrWhiteSpace(row[seriesIndex]) ||
                    row[valueIndex] is not null && (!double.TryParse(row[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var statValue) || !double.IsFinite(statValue))))
                return new([], "Stat queries must return Series and numeric or null Value columns.");
            return new([], null, [table]);
        }
        if (seriesIndex < 0 || valueIndex < 0 ||
            (visualizationType is AnalysisQueryVisualizationTypes.LineChart or AnalysisQueryVisualizationTypes.StatusHistory && timestampIndex < 0))
            return new([], "The query must return Timestamp, Series and Value for lines/status history, or Series and Value for honeycombs/bars/gauges.");
        var rows = new List<AnalysisQueryNormalizedRow>();
        foreach (var row in table.Rows)
        {
            if (string.IsNullOrWhiteSpace(row[seriesIndex]) ||
                !double.TryParse(row[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
                return new([], "The query returned invalid numeric values or series names.");
            var timestamp = DateTimeOffset.UnixEpoch;
            if (timestampIndex >= 0 &&
                !DateTimeOffset.TryParse(row[timestampIndex], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out timestamp))
                return new([], "The query returned an invalid Timestamp.");
            rows.Add(new(timestamp.ToUniversalTime(), row[seriesIndex]!, value,
                Cell("Label"), Cell("Dimension")));
            string? Cell(string name) => names.IndexOf(name) is var index && index >= 0 ? row[index] : null;
        }
        return new(rows, null, [table]);
    }

    private static AnalysisQuerySeries[] CreateSeries(
        IReadOnlyList<AnalysisQueryNormalizedRow> rows,
        string workspace) => rows.GroupBy(row => (row.Series, row.Dimension))
        .Select(group => new AnalysisQuerySeries(
            group.Key.Series, group.Key.Dimension, workspace,
            group.OrderBy(row => row.Timestamp)
                .Select(row => new AnalysisQueryPoint(row.Timestamp, row.Value, row.Label)).ToArray()))
        .ToArray();

    private static void ValidateExecutionLimits(
        AnalysisQueryDefinition definition,
        int systemCount,
        int requestedMinutes)
    {
        var errors = AnalysisQueryValidator.ValidateKql(definition.Kql, definition.VisualizationType, definition.Scope);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
        if (systemCount is < 1 or > 100)
            throw new InvalidOperationException("Select between 1 and 100 systems.");
        if (requestedMinutes < 5 || requestedMinutes > definition.MaxTimeRangeMinutes)
            throw new InvalidOperationException(
                $"Time range must be between 5 and {definition.MaxTimeRangeMinutes} minutes.");
    }

    public sealed class AnalysisQueryRawRow
    {
        public DateTimeOffset Timestamp { get; set; }
        public string Series { get; set; } = string.Empty;
        public double Value { get; set; }
        public string? Label { get; set; }
        public string? Dimension { get; set; }
    }
}

public sealed record AnalysisQueryNormalizedRow(
    DateTimeOffset Timestamp,
    string Series,
    double Value,
    string? Label,
    string? Dimension);

public sealed record AnalysisQueryRunResult(
    IReadOnlyList<AnalysisQueryNormalizedRow> Rows,
    string? Error,
    IReadOnlyList<AnalysisQueryTable>? Tables = null,
    string? Warning = null);

public sealed class AnalysisQueryExecutionService(
    IDbContextFactory<AlertDbContext> contextFactory,
    AnalysisQueryRunner runner)
{
    public async Task<AnalysisQueryResponse> ExecuteAsync(
        AnalysisQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = await context.AnalysisQueryDefinitions
            .AsNoTracking()
            .Include(item => item.Workspaces)
            .Include(item => item.Versions)
            .SingleOrDefaultAsync(item =>
                item.Id == request.QueryDefinitionId &&
                item.Enabled &&
                item.PublishedVersionId != null,
                cancellationToken)
            ?? throw new KeyNotFoundException("The published analysis query was not found.");
        query = AnalysisQueryRegistry.CreatePublishedDefinition(query);

        var requestedSystems = request.Systems
            .Select(system => (
                SubscriptionId: system.SubscriptionId.Trim(),
                Computer: system.Computer.Trim()))
            .Distinct()
            .ToArray();
        if (requestedSystems.Length is < 1 or > 100)
            throw new InvalidOperationException("Select between 1 and 100 distinct systems.");

        var subscriptions = requestedSystems.Select(item => item.SubscriptionId).Distinct().ToArray();
        var computers = requestedSystems.Select(item => item.Computer).Distinct().ToArray();
        var inventory = await context.ComputerInventory
            .AsNoTracking()
            .Where(entry => subscriptions.Contains(entry.SubscriptionId) && computers.Contains(entry.Computer))
            .ToArrayAsync(cancellationToken);
        var matched = requestedSystems
            .Select(request => inventory.SingleOrDefault(entry =>
                entry.SubscriptionId.Equals(request.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
                entry.Computer.Equals(request.Computer, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (matched.Any(entry => entry is null))
            throw new InvalidOperationException("At least one selected system is not present in inventory.");

        return await runner.ExecuteAsync(
            query,
            matched.Select(entry => entry!).ToArray(),
            request.TimeRangeMinutes,
            cancellationToken,
            request.BinMinutes,
            request.GroupBy,
            request.WorkspaceKey,
            request.Domain,
            request.Site,
            request.Role);
    }

    public static bool IsRoleAllowed(string minimumRole, System.Security.Claims.ClaimsPrincipal user, bool openAuthentication)
    {
        if (openAuthentication)
            return true;
        var required = RoleRank(minimumRole);
        return SqlAuthenticationRoles.All
            .Where(role => RoleRank(role) >= required)
            .Any(user.IsInRole);
    }

    private static int RoleRank(string role) => role switch
    {
        SqlAuthenticationRoles.Admin => 3,
        SqlAuthenticationRoles.Operator => 2,
        _ => 1
    };
}
