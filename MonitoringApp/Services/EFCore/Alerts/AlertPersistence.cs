using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal sealed class AlertPersistence(
    IDbContextFactory<AlertDbContext> contextFactory,
    DatabaseConfigurationStatus databaseConfiguration,
    QueryResultPresenter queryResultPresenter,
    AlertRuleEvaluator alertRuleEvaluator,
    ILogger<AlertStore> logger,
    ComputerInventoryPersistence inventory,
    Action notifyChanged)
{
    /// <summary>
    /// Loads all stored alerts with the newest records first. Required reads propagate failures; other reads log failures and return an empty list.
    /// </summary>
    public async Task<IReadOnlyList<AlertRecord>> GetAllAsync(
        CancellationToken cancellationToken = default, bool required = false)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.GetAll");
        if (required)
            AlertStoreAccess.Require(databaseConfiguration);
        if (!databaseConfiguration.IsValid)
        {
            logger.LogError("Alerts cannot be loaded: {ConfigurationError}", databaseConfiguration.Error);
            return [];
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var alerts = await context.Alerts
                .AsNoTracking()
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
            var refreshTimes = await context.ParsedAlerts
                .AsNoTracking()
                .Where(record => record.QueryResultRefreshedAt != null)
                .ToDictionaryAsync(
                    record => record.Id,
                    record => record.QueryResultRefreshedAt,
                    cancellationToken);
            return alerts
                .Where(alert => !InventoryHeartbeatBatch.IsInventoryHeartbeat(alert))
                .Select(alert => queryResultPresenter.ResolveDisplayIdentity(
                    alert,
                    refreshTimes.GetValueOrDefault(alert.Id)))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load alerts from the database.");
            if (required)
                throw;
            return [];
        }
    }

    /// <summary>
    /// Loads the immutable snapshots captured when alerts were first received as fired. Required reads propagate failures instead of returning an empty list.
    /// </summary>
    public async Task<IReadOnlyList<AlertRecord>> GetFiredHistoryAsync(
        CancellationToken cancellationToken = default, bool required = false)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.GetFiredHistory");
        if (required)
            AlertStoreAccess.Require(databaseConfiguration);
        if (!databaseConfiguration.IsValid)
        {
            logger.LogError("Alert history cannot be loaded: {ConfigurationError}", databaseConfiguration.Error);
            return [];
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var history = await context.AlertHistory
                .AsNoTracking()
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
            return history
                .Select(alert => queryResultPresenter.ResolveDisplayIdentity(alert.ToAlertRecord()))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load fired alert history from the database.");
            if (required)
                throw;
            return [];
        }
    }

    /// <summary>
    /// Loads alerts received on or after the supplied timestamp, ordered newest first. Configuration or database failures are logged and return an empty list for the UI.
    /// </summary>
    public async Task<IReadOnlyList<AlertRecord>> GetSinceAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        if (!databaseConfiguration.IsValid)
        {
            logger.LogError("Alerts cannot be loaded: {ConfigurationError}", databaseConfiguration.Error);
            return [];
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var alerts = await context.Alerts
                .AsNoTracking()
                .Where(alert => alert.ReceivedAt >= since)
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
            var alertIds = alerts.Select(alert => alert.Id).ToArray();
            var refreshTimes = await context.ParsedAlerts
                .AsNoTracking()
                .Where(record => alertIds.Contains(record.Id) && record.QueryResultRefreshedAt != null)
                .ToDictionaryAsync(
                    record => record.Id,
                    record => record.QueryResultRefreshedAt,
                    cancellationToken);
            return alerts
                .Where(alert => !InventoryHeartbeatBatch.IsInventoryHeartbeat(alert))
                .Select(alert => queryResultPresenter.ResolveDisplayIdentity(
                    alert,
                    refreshTimes.GetValueOrDefault(alert.Id)))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load recent alerts from the database.");
            return [];
        }
    }

    /// <summary>
    /// Returns active fired alerts of one supported type and the validated dimension required by the scheduled query.
    /// </summary>
    public async Task<IReadOnlyList<AlertQueryRefreshTarget>> GetActiveAlertQueryRefreshTargetsAsync(
        string type,
        CancellationToken cancellationToken = default)
    {
        AlertStoreAccess.Require(databaseConfiguration);
        if (!AlertQueryRefresh.TryGetDefinition(type, out var requestedDefinition))
        {
            throw new ArgumentException("The query-result type is not supported.", nameof(type));
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var lifecycleAlerts = await context.Alerts
                .AsNoTracking()
                .Where(alert => !string.IsNullOrWhiteSpace(alert.AlertId))
                .ToArrayAsync(cancellationToken);
            var activeAlerts = AlertLifecycle.GetActiveAlerts(lifecycleAlerts);
            var activeIds = activeAlerts.Select(alert => alert.Id).ToArray();
            if (activeIds.Length == 0)
            {
                return [];
            }

            var parsedAlerts = await context.ParsedAlerts
                .AsNoTracking()
                .Where(record => activeIds.Contains(record.Id))
                .ToDictionaryAsync(record => record.Id, cancellationToken);

            return activeAlerts
                .Select(alert =>
                {
                    if (!parsedAlerts.TryGetValue(alert.Id, out var parsed) ||
                            !AlertQueryRefresh.TryReadContext(
                                parsed.QueryResults,
                                out var storedDefinition,
                                out var storedDimensionValue) ||
                            !storedDefinition.Type.Equals(
                                requestedDefinition.Type,
                                StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    var dimensionValue = string.IsNullOrWhiteSpace(storedDimensionValue)
                        ? alert.GetDimensionValue(requestedDefinition.DimensionName) ?? string.Empty
                        : storedDimensionValue;
                    return AlertQueryRefresh.IsSafeDimensionValue(dimensionValue)
                        ? new AlertQueryRefreshTarget(
                            alert.Id,
                            alert.AlertId,
                            requestedDefinition.Type,
                            requestedDefinition.DimensionName,
                            dimensionValue.Trim())
                        : null;
                })
                    .Where(target => target is not null)
                    .Cast<AlertQueryRefreshTarget>()
                    .OrderBy(target => target.DimensionValue, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(target => target.Id)
                    .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to load active {QueryResultType} query-refresh targets.",
                requestedDefinition.Type);
            throw;
        }
    }

    /// <summary>
    /// Atomically replaces a supported query result for an existing active alert without creating a new alert event.
    /// </summary>
    public async Task<AlertQueryRefreshUpdateResult> UpdateAlertQueryResultAsync(
        Guid id,
        UpdateAlertQueryResultRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.UpdateAlertQueryResult");
        AlertStoreAccess.Require(databaseConfiguration);
        if (!AlertQueryRefresh.TryGetDefinition(request.Type, out var requestedDefinition))
        {
            return new AlertQueryRefreshUpdateResult(
                AlertQueryRefreshUpdateStatus.TypeMismatch,
                null);
        }

        try
        {
            var result = await DatabaseTransactions.ExecuteAsync(contextFactory, async dbContext =>
            {
                var parsedAlert = await dbContext.ParsedAlerts
                    .Include(record => record.Alert)
                    .FirstOrDefaultAsync(record => record.Id == id, cancellationToken);
                if (parsedAlert is null)
                {
                    return new AlertQueryRefreshUpdateResult(AlertQueryRefreshUpdateStatus.NotFound, null);
                }

                if (!AlertQueryRefresh.TryReadContext(
                        parsedAlert.QueryResults,
                        out var storedDefinition,
                        out var storedDimensionValue) ||
                    !storedDefinition.Type.Equals(
                        requestedDefinition.Type,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new AlertQueryRefreshUpdateResult(
                        AlertQueryRefreshUpdateStatus.TypeMismatch,
                        parsedAlert.QueryResultRefreshedAt);
                }

                var expectedDimensionValue = string.IsNullOrWhiteSpace(storedDimensionValue)
                    ? parsedAlert.Alert.GetDimensionValue(requestedDefinition.DimensionName) ?? string.Empty
                    : storedDimensionValue;
                if (!requestedDefinition.DimensionName.Equals(
                        request.DimensionName,
                        StringComparison.OrdinalIgnoreCase) ||
                    !expectedDimensionValue.Equals(
                        request.DimensionValue.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new AlertQueryRefreshUpdateResult(
                        AlertQueryRefreshUpdateStatus.DimensionMismatch,
                        parsedAlert.QueryResultRefreshedAt);
                }

                var lifecycleAlerts = await dbContext.Alerts
                    .AsNoTracking()
                    .Where(alert => alert.AlertId == parsedAlert.AlertId)
                    .ToArrayAsync(cancellationToken);
                var isActive = AlertLifecycle.GetActiveAlerts(lifecycleAlerts)
                    .Any(alert => alert.Id == id);
                if (!isActive)
                {
                    return new AlertQueryRefreshUpdateResult(
                        AlertQueryRefreshUpdateStatus.NotActive,
                        parsedAlert.QueryResultRefreshedAt);
                }

                if (parsedAlert.QueryResultRefreshedAt > request.QueryExecutedAt)
                {
                    return new AlertQueryRefreshUpdateResult(
                        AlertQueryRefreshUpdateStatus.Stale,
                        parsedAlert.QueryResultRefreshedAt);
                }

                var queryResultJson = AlertQueryRefresh.CreateQueryResultJson(request);
                if (parsedAlert.QueryResultRefreshedAt == request.QueryExecutedAt &&
                    JsonNode.DeepEquals(
                        JsonNode.Parse(parsedAlert.QueryResults),
                        JsonNode.Parse(queryResultJson)))
                {
                    return new AlertQueryRefreshUpdateResult(
                        AlertQueryRefreshUpdateStatus.Unchanged,
                        parsedAlert.QueryResultRefreshedAt);
                }

                if (parsedAlert.QueryResultRefreshedAt == request.QueryExecutedAt)
                {
                    return new AlertQueryRefreshUpdateResult(
                        AlertQueryRefreshUpdateStatus.Stale,
                        parsedAlert.QueryResultRefreshedAt);
                }

                string updatedRawJson;
                try
                {
                    updatedRawJson = AlertQueryRefresh.ReplaceQueryResult(
                        parsedAlert.Alert.RawJson,
                        queryResultJson);
                }
                catch (JsonException)
                {
                    return new AlertQueryRefreshUpdateResult(
                        AlertQueryRefreshUpdateStatus.TypeMismatch,
                        parsedAlert.QueryResultRefreshedAt);
                }

                parsedAlert.QueryResults = queryResultJson;
                parsedAlert.QueryResultRefreshedAt = request.QueryExecutedAt;
                await dbContext.Alerts
                    .Where(alert => alert.Id == id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(alert => alert.RawJson, updatedRawJson),
                        cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new AlertQueryRefreshUpdateResult(
                    AlertQueryRefreshUpdateStatus.Updated,
                    request.QueryExecutedAt);
            }, cancellationToken, IsolationLevel.Serializable);

            if (result.Status == AlertQueryRefreshUpdateStatus.Updated)
            {
                logger.LogInformation(
                    "Updated {QueryResultType} query result for alert record {AlertRecordId} with {RowCount} rows from query time {QueryExecutedAt}.",
                    requestedDefinition.Type, id, request.RowCount, request.QueryExecutedAt);
                notifyChanged();
            }

            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to update {QueryResultType} query result for alert record {AlertRecordId}.",
                requestedDefinition.Type,
                id);
            throw;
        }
    }

    /// <summary>
    /// Loads inventory systems and their parsed alerts received on or after the supplied timestamp for the graph.
    /// </summary>
    public async Task<IReadOnlyList<AlertGraphRecord>> GetAlertGraphRecordsAsync(
        DateTimeOffset receivedSince,
        CancellationToken cancellationToken = default, bool required = false)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.GetAlertGraphRecords");
        if (required) AlertStoreAccess.Require(databaseConfiguration);
        if (!databaseConfiguration.IsValid)
        {
            logger.LogError("Graph records cannot be loaded: {ConfigurationError}", databaseConfiguration.Error);
            return [];
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await BackfillMissingParsedAlertsAsync(context, cancellationToken);
            var parsedAlerts = await context.ParsedAlerts
                .AsNoTracking()
                .Include(record => record.Alert)
                .Include(record => record.InventoryComputerEntry)
                .Where(record => record.Alert.ReceivedAt >= receivedSince && record.InventoryComputerEntry != null)
                .OrderByDescending(record => record.Alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);

            var inventoryEntries = await context.ComputerInventory
                .AsNoTracking()
                .ToArrayAsync(cancellationToken);

            var alertRecords = parsedAlerts
                .Where(record => !InventoryHeartbeatBatch.IsInventoryHeartbeat(record.Alert))
                .Select(record =>
            {
                var inventory = record.InventoryComputerEntry!;
                return new AlertGraphRecord(
                    record.Id,
                    record.Alert.ReceivedAt,
                    record.AlertId,
                    record.MonitorCondition,
                    record.Alert.Comments,
                    record.AlertName,
                    inventory.SubscriptionId,
                    AlertPayload.FirstNonEmpty(inventory.ResourceGroup ?? string.Empty, record.ResourceGroup),
                    inventory.Computer,
                    inventory.Site ?? string.Empty,
                    inventory.Domain ?? string.Empty,
                        inventory.Role ?? string.Empty,
                        inventory.LastHeartbeatAt,
                        Maintenance: inventory.Maintenance,
                        HasServiceHealthAlert: inventory.HasServiceHealthAlert);
            });
            var inventoryRecords = inventoryEntries.Select(entry => new AlertGraphRecord(
            Guid.NewGuid(),
            entry.LastHeartbeatAt ?? DateTimeOffset.MinValue,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            entry.SubscriptionId,
            entry.ResourceGroup ?? string.Empty,
            entry.Computer,
            entry.Site ?? string.Empty,
            entry.Domain ?? string.Empty,
            entry.Role ?? string.Empty,
            entry.LastHeartbeatAt,
            true,
            entry.Maintenance,
            entry.HasServiceHealthAlert));

            return alertRecords.Concat(inventoryRecords).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load parsed alert graph records from the database.");
            if (required) throw;
            return [];
        }
    }

    /// <summary>
    /// Loads critical fired-alert lifecycles that overlap the supplied time range.
    /// </summary>
    public async Task<IReadOnlyList<CriticalAlertLifecycle>> GetCriticalAlertLifecyclesAsync(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken cancellationToken = default,
        bool required = false)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.GetCriticalAlertLifecycles");
        if (required)
            AlertStoreAccess.Require(databaseConfiguration);
        if (!databaseConfiguration.IsValid)
        {
            logger.LogError("Critical alert lifecycles cannot be loaded: {ConfigurationError}", databaseConfiguration.Error);
            return [];
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await BackfillMissingParsedAlertsAsync(context, cancellationToken);
            await SynchronizeParsedAlertLifecyclesAsync(context, null, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            var records = await context.ParsedAlerts
                .AsNoTracking()
                .Include(record => record.Alert)
                .Where(record => record.IsCritical &&
                    record.MonitorCondition == "Fired" &&
                    (record.FiredDateTime ?? record.Alert.ReceivedAt) <= rangeEnd &&
                    (record.ResolvedAt == null || record.ResolvedAt >= rangeStart))
                .OrderBy(record => record.FiredDateTime ?? record.Alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);

            return records
                .GroupBy(
                    record => string.IsNullOrWhiteSpace(record.AlertId)
                        ? record.Id.ToString("D")
                        : record.AlertId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new CriticalAlertLifecycle(
                    group.Key,
                    group.Min(record => record.FiredDateTime ?? record.Alert.ReceivedAt),
                    group.Max(record => record.ResolvedAt)))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load critical alert lifecycles from the database.");
            if (required)
                throw;
            return [];
        }
    }

    /// <summary>
    /// Creates parsed and inventory records for alerts stored before ParsedAlerts was introduced.
    /// </summary>
    private async Task BackfillMissingParsedAlertsAsync(
        AlertDbContext context,
        CancellationToken cancellationToken)
    {
        var missingAlerts = await context.Alerts
            .AsNoTracking()
            .Where(alert => !context.ParsedAlerts.Any(parsed => parsed.Id == alert.Id))
            .OrderBy(alert => alert.ReceivedAt)
            .ToArrayAsync(cancellationToken);
        if (missingAlerts.Length == 0)
        {
            return;
        }

        var inventoryEntries = await context.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken);

        foreach (var alert in missingAlerts)
        {
            var resolvedAlert = queryResultPresenter.ResolveDisplayIdentity(alert);
            var parsed = ParsedAlertFactory.Create(resolvedAlert);
            ComputerInventoryPersistence.LinkInventoryTarget(parsed, resolvedAlert, inventoryEntries);
            context.ParsedAlerts.Add(parsed);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await SynchronizeParsedAlertLifecyclesAsync(context, null, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Backfilled {ParsedAlertCount} parsed alert records from existing alert history.",
                missingAlerts.Length);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            var remainingIds = missingAlerts.Select(alert => alert.Id).ToArray();
            var remainingCount = await context.Alerts
                .AsNoTracking()
                .CountAsync(
                    alert => remainingIds.Contains(alert.Id) &&
                        !context.ParsedAlerts.Any(parsed => parsed.Id == alert.Id),
                    cancellationToken);
            if (remainingCount > 0)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Loads alerts received on or after the supplied timestamp, ordered newest first. Unlike the UI loader, configuration and database failures are propagated to the caller.
    /// </summary>
    public async Task<IReadOnlyList<AlertRecord>> GetSinceRequiredAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        AlertStoreAccess.Require(databaseConfiguration);

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var alerts = await context.Alerts
                .AsNoTracking()
                .Where(alert => alert.ReceivedAt >= since)
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
            return alerts.Select(alert => queryResultPresenter.ResolveDisplayIdentity(alert)).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query alerts from the database.");
            throw;
        }
    }

    /// <summary>
    /// Converts a Common Alert Schema payload into an AlertRecord and stores it transactionally. An existing alert with the same alert ID and monitor condition is returned instead of creating a duplicate.
    /// </summary>
    public async Task<AddAlertResult> AddAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.AddAlert");
        AlertStoreAccess.Require(databaseConfiguration);

        var (alert, resolvedAt) = AlertPayload.Parse(payload);

        if (InventoryHeartbeatBatch.IsInventoryHeartbeat(payload))
        {
            var inventoryResult = await inventory.TryReconcileInventoryHeartbeatAsync(alert, payload, cancellationToken);
            if (inventoryResult is not null)
            {
                return inventoryResult;
            }
        }

        var resolvedAlert = queryResultPresenter.ResolveDisplayIdentity(alert);

        AddAlertResult result;
        var warningsCreated = false;
        try
        {
            result = await DatabaseTransactions.ExecuteAsync(contextFactory, async dbContext =>
            {
                warningsCreated = await CreateReplicationInventoryWarningsAsync(dbContext, resolvedAlert, cancellationToken);
                var suppressionRules = await dbContext.AlertRules
                    .AsNoTracking()
                    .Where(rule => rule.Enabled && rule.RuleType == AlertRuleTypes.Suppression)
                    .OrderBy(rule => rule.Priority)
                    .ToArrayAsync(cancellationToken);
                var suppressionRule = suppressionRules.FirstOrDefault(rule =>
                    alertRuleEvaluator.MatchesSuppressionRule(rule, resolvedAlert));
                if (suppressionRule is not null)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "Suppressed alert {AlertId} during ingestion using rule {RuleName}.",
                        alert.AlertId,
                        suppressionRule.Name);
                    return new AddAlertResult(alert, false, true);
                }

                if (!string.IsNullOrWhiteSpace(alert.AlertId))
                {
                    var existingAlert = await dbContext.Alerts
                        .FirstOrDefaultAsync(
                            existing => existing.AlertId == alert.AlertId &&
                                existing.MonitorCondition == alert.MonitorCondition,
                            cancellationToken);
                    if (existingAlert is not null)
                    {
                        await dbContext.SaveChangesAsync(cancellationToken);
                        logger.LogInformation(
                            "Ignored duplicate alert {AlertId} with condition {MonitorCondition}.",
                            alert.AlertId,
                            alert.MonitorCondition);
                        return new AddAlertResult(existingAlert, false);
                    }
                }

                dbContext.Alerts.Add(alert);
                if (resolvedAlert.MonitorCondition.Equals("Fired", StringComparison.OrdinalIgnoreCase))
                {
                    dbContext.AlertHistory.Add(AlertHistoryRecord.Create(resolvedAlert));
                }

                var parsedAlert = ParsedAlertFactory.Create(resolvedAlert);
                ComputerInventoryPersistence.LinkInventoryTarget(parsedAlert, resolvedAlert,
                    await dbContext.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken));
                if (resolvedAlert.MonitorCondition.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
                {
                    parsedAlert.ResolvedAt = resolvedAt ?? resolvedAlert.ReceivedAt;
                }

                dbContext.ParsedAlerts.Add(parsedAlert);
                await dbContext.SaveChangesAsync(cancellationToken);
                await SynchronizeParsedAlertLifecyclesAsync(
                    dbContext,
                    resolvedAlert.AlertId,
                    cancellationToken);
                var logbookEntry = CriticalAlertLogbook.CreateEntry(
                    resolvedAlert,
                    parsedAlert.IsCritical,
                    DateTimeOffset.UtcNow);
                if (logbookEntry is not null)
                {
                    dbContext.LogbookEntries.Add(logbookEntry);
                }

                await RcaAutomation.ReconcileAsync(dbContext, alertRuleEvaluator,
                    queryResultPresenter, DateTimeOffset.UtcNow, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new AddAlertResult(alert, true);
            }, cancellationToken, IsolationLevel.Serializable);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist incoming alert {AlertId}.", alert.AlertId);
            throw;
        }

        if (result.Created || result.Suppressed || warningsCreated)
        {
            notifyChanged();
        }

        return result with { Alert = queryResultPresenter.ResolveDisplayIdentity(result.Alert) };
    }

    private async Task<bool> CreateReplicationInventoryWarningsAsync(
        AlertDbContext context, AlertRecord source, CancellationToken cancellationToken)
    {
        if (!ReplicationInventoryWarning.IsReplication(source) ||
            AlertInventoryLearning.HasZeroRowCount(source.RawJson))
            return false;
        var rule = await context.AlertRules.AsNoTracking()
            .Where(candidate => candidate.Enabled && candidate.RuleType == AlertRuleTypes.ReplicationInventoryWarning)
            .OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (rule is null)
            return false;
        if (source.SubscriptionId.Trim().Length is 0 or > 64)
        {
            logger.LogWarning("Cannot check replication inventory targets for alert {AlertId}: its subscription ID is missing or too long.", source.AlertId);
            return false;
        }

        IReadOnlyList<string> systems;
        try
        {
            systems = ReplicationInventoryWarning.ReadSystems(source);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Cannot check replication inventory targets for alert {AlertId}: invalid query-result table.", source.AlertId);
            return false;
        }
        if (systems.Count == 0)
        {
            logger.LogWarning("No valid SourceDSA or DestDSA systems were found in replication alert {AlertId}.", source.AlertId);
            return false;
        }

        var inventory = await context.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken);
        var created = new List<AlertRecord>();
        foreach (var computer in systems.Where(computer => !inventory.Any(entry =>
            entry.SubscriptionId.Equals(source.SubscriptionId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            entry.Computer.Equals(computer, StringComparison.OrdinalIgnoreCase))))
        {
            var warning = ReplicationInventoryWarning.Create(source, rule, computer);
            if (await context.Alerts.AnyAsync(candidate => candidate.AlertId == warning.AlertId, cancellationToken))
                continue;
            context.Alerts.Add(warning);
            context.AlertHistory.Add(AlertHistoryRecord.Create(warning));
            var parsed = ParsedAlertFactory.Create(warning);
            parsed.InventorySubscriptionId = null;
            parsed.InventoryComputer = null;
            context.ParsedAlerts.Add(parsed);
            created.Add(warning);
            logger.LogWarning("Replication alert {AlertId} reports unknown inventory target {Computer} in subscription {SubscriptionId}.",
                source.AlertId, computer, source.SubscriptionId);
        }
        if (created.Count == 0)
            return false;
        await context.SaveChangesAsync(cancellationToken);
        foreach (var warning in created)
        {
            await SynchronizeParsedAlertLifecyclesAsync(context, warning.AlertId, cancellationToken);
            var parsed = context.ParsedAlerts.Local.Single(record => record.Id == warning.Id);
            var logbookEntry = CriticalAlertLogbook.CreateEntry(warning, parsed.IsCritical, DateTimeOffset.UtcNow);
            if (logbookEntry is not null)
                context.LogbookEntries.Add(logbookEntry);
        }
        await RcaAutomation.ReconcileAsync(context, alertRuleEvaluator,
            queryResultPresenter, DateTimeOffset.UtcNow, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Re-evaluates critical categorization rules and propagates criticality and resolution time across each alert lifecycle.
    /// </summary>
    private Task SynchronizeParsedAlertLifecyclesAsync(
        AlertDbContext context,
        string? alertId,
        CancellationToken cancellationToken) =>
        ParsedAlertLifecyclePersistence.SynchronizeAsync(context, alertRuleEvaluator, alertId, cancellationToken);

    /// <summary>
    /// Trims and updates operator comments for one alert record. It returns false when the record no longer exists and rejects comments longer than 4,000 characters.
    /// </summary>
    public async Task<bool> UpdateCommentsAsync(
        Guid id,
        string comments,
        string user,
        CancellationToken cancellationToken = default)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.UpdateComments");
        AlertStoreAccess.Require(databaseConfiguration);

        var normalizedComments = comments.Trim();
        if (normalizedComments.Length > 4000)
        {
            throw new ArgumentException("Comments cannot exceed 4000 characters.", nameof(comments));
        }

        try
        {
            var updated = await DatabaseTransactions.ExecuteAsync(contextFactory, async dbContext =>
            {
                var storedAlert = await dbContext.Alerts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(alert => alert.Id == id, cancellationToken);
                if (storedAlert is null)
                {
                    return false;
                }

                if (string.Equals(storedAlert.Comments, normalizedComments, StringComparison.Ordinal))
                {
                    return true;
                }

                var logbookEntry = AlertCommentLogbook.CreateEntry(
                    queryResultPresenter.ResolveDisplayIdentity(storedAlert),
                    user,
                    normalizedComments,
                    DateTimeOffset.UtcNow);
                if (logbookEntry is not null)
                {
                    dbContext.LogbookEntries.Add(logbookEntry);
                }

                var updatedRows = await dbContext.Alerts
                    .Where(alert => alert.Id == id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(alert => alert.Comments, normalizedComments),
                        cancellationToken);
                if (updatedRows == 0)
                {
                    return false;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken, commitWhen: result => result);
            if (!updated)
            {
                return false;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update comments for alert {AlertRecordId}.", id);
            throw;
        }

        notifyChanged();
        return true;
    }
}
