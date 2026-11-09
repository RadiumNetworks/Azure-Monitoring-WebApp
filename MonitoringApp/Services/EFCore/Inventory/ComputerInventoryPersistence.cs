using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal sealed class ComputerInventoryPersistence(
    IDbContextFactory<AlertDbContext> contextFactory,
    DatabaseConfigurationStatus databaseConfiguration,
    QueryResultPresenter queryResultPresenter,
    AlertRuleEvaluator alertRuleEvaluator,
    ILogger<AlertStore> logger,
    Action notifyChanged)
{
    /// <summary>
    /// Loads the computer inventory ordered by subscription and computer name.
    /// </summary>
    public async Task<IReadOnlyList<ComputerInventoryEntry>> GetComputerInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.GetComputerInventory");
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ComputerInventory
            .AsNoTracking()
            .OrderBy(entry => entry.SubscriptionId)
            .ThenBy(entry => entry.Computer)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Loads distinct subscription IDs observed in stored alerts for manual inventory entry creation.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAlertSubscriptionIdsAsync(
        CancellationToken cancellationToken = default)
    {
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var subscriptionIds = await context.Alerts
            .AsNoTracking()
            .Where(alert => alert.SubscriptionId != "")
            .Select(alert => alert.SubscriptionId)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        return subscriptionIds
            .Select(subscriptionId => subscriptionId.Trim())
            .Where(subscriptionId => subscriptionId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(subscriptionId => subscriptionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Creates a manually maintained computer inventory entry. Its subscription must have been observed in an alert.
    /// </summary>
    public async Task<ComputerInventoryEntry> CreateComputerInventoryEntryAsync(
        ComputerInventoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");

        var normalized = NormalizeAndValidateInventoryEntry(entry);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var knownSubscription = await context.Alerts.AnyAsync(
            alert => alert.SubscriptionId == normalized.SubscriptionId,
            cancellationToken);
        if (!knownSubscription)
        {
            throw new ComputerInventoryException(ComputerInventoryErrorCode.UnobservedSubscription,
                "Select a subscription ID that has been observed in an alert.");
        }

        var existing = await context.ComputerInventory.FindAsync(
            [normalized.SubscriptionId, normalized.Computer],
            cancellationToken);
        if (existing is not null)
        {
            throw new ComputerInventoryException(ComputerInventoryErrorCode.Duplicate,
                $"Computer '{normalized.Computer}' already exists in subscription '{normalized.SubscriptionId}'.",
                null, normalized.Computer, normalized.SubscriptionId);
        }

        context.ComputerInventory.Add(normalized);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(
                exception,
                "Failed to create inventory entry for subscription {SubscriptionId} and computer {Computer}.",
                normalized.SubscriptionId,
                normalized.Computer);
            throw new ComputerInventoryException(ComputerInventoryErrorCode.CreateUnconfirmed,
                "The inventory record could not be created. Verify that it does not already exist.",
                exception);
        }

        NotifyAfterChange(ComputerInventoryErrorCode.SavedNotificationFailed, normalized);
        return normalized;
    }

    /// <summary>
    /// Updates the editable domain and site values of an existing computer inventory entry.
    /// The subscription and computer fields form the primary key and cannot be changed.
    /// </summary>
    public async Task<ComputerInventoryEntry> SaveComputerInventoryEntryAsync(
        ComputerInventoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");

        var normalized = NormalizeAndValidateInventoryEntry(entry);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.ComputerInventory.FindAsync(
            [normalized.SubscriptionId, normalized.Computer],
            cancellationToken);
        if (stored is null)
        {
            throw new ComputerInventoryException(ComputerInventoryErrorCode.Missing,
                "The inventory record no longer exists. Refresh the inventory and try again.");
        }

        stored.Domain = normalized.Domain;
        stored.Site = normalized.Site;
        stored.ResourceGroup = normalized.ResourceGroup;
        stored.Role = normalized.Role;
        stored.Maintenance = normalized.Maintenance;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(
                exception,
                "Failed to update inventory entry for subscription {SubscriptionId} and computer {Computer}.",
                normalized.SubscriptionId,
                normalized.Computer);
            throw new ComputerInventoryException(ComputerInventoryErrorCode.UpdateUnconfirmed,
                "The inventory record could not be updated.", exception);
        }

        NotifyAfterChange(ComputerInventoryErrorCode.SavedNotificationFailed, stored);
        return stored;
    }

    /// <summary>
    /// Deletes an inventory entry by its subscription and computer composite key.
    /// </summary>
    public async Task<bool> DeleteComputerInventoryEntryAsync(
        string subscriptionId,
        string computer,
        CancellationToken cancellationToken = default)
    {
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");

        var key = NormalizeAndValidateInventoryEntry(new ComputerInventoryEntry
        {
            SubscriptionId = subscriptionId,
            Computer = computer
        });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        int deletedRows;
        try
        {
            deletedRows = await context.ComputerInventory
                .Where(entry => entry.SubscriptionId == key.SubscriptionId && entry.Computer == key.Computer)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to delete inventory entry for subscription {SubscriptionId} and computer {Computer}.",
                key.SubscriptionId,
                key.Computer);
            throw new ComputerInventoryException(ComputerInventoryErrorCode.DeleteUnconfirmed,
                "The inventory record could not be deleted.", exception);
        }

        if (deletedRows > 0)
        {
            NotifyAfterChange(ComputerInventoryErrorCode.DeletedNotificationFailed, key);
        }

        return deletedRows > 0;
    }

    internal static string InventoryKey(string subscriptionId, string computer) =>
        $"{subscriptionId.Trim().ToUpperInvariant()}\0{computer.Trim().ToUpperInvariant()}";

    private void NotifyAfterChange(ComputerInventoryErrorCode code, ComputerInventoryEntry entry)
    {
        try
        {
            notifyChanged();
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed to notify subscribers after committing inventory changes for subscription {SubscriptionId} and computer {Computer}.",
                entry.SubscriptionId, entry.Computer);
            throw new ComputerInventoryException(code,
                "The inventory change was committed, but notifications failed.", exception);
        }
    }

    private static string? NormalizeOptionalInventoryValue(
        string? value, int maximumLength, string fieldName, ComputerInventoryErrorCode code)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            throw new ComputerInventoryException(code,
                $"{fieldName} may contain at most {maximumLength} characters.", null, maximumLength);
        }

        return normalized;
    }

    private static ComputerInventoryEntry NormalizeAndValidateInventoryEntry(ComputerInventoryEntry entry)
    {
        var normalized = new ComputerInventoryEntry
        {
            SubscriptionId = entry.SubscriptionId.Trim(),
            Computer = entry.Computer.Trim(),
            Domain = NormalizeOptionalInventoryValue(entry.Domain, 256, "Domain", ComputerInventoryErrorCode.DomainLength),
            Site = NormalizeOptionalInventoryValue(entry.Site, 256, "Site", ComputerInventoryErrorCode.SiteLength),
            ResourceGroup = NormalizeOptionalInventoryValue(entry.ResourceGroup, 256, "Resource group", ComputerInventoryErrorCode.ResourceGroupLength),
            Role = InventoryRoleRuleMatcher.NormalizeRole(NormalizeOptionalInventoryValue(entry.Role, 256, "Role", ComputerInventoryErrorCode.RoleLength)),
            Maintenance = entry.Maintenance
        };

        if (string.IsNullOrWhiteSpace(normalized.SubscriptionId) || normalized.SubscriptionId.Length > 64)
        {
            throw new ComputerInventoryException(ComputerInventoryErrorCode.Subscription,
                "Subscription ID is required and may contain at most 64 characters.");
        }

        if (string.IsNullOrWhiteSpace(normalized.Computer) || normalized.Computer.Length > 256)
        {
            throw new ComputerInventoryException(ComputerInventoryErrorCode.Computer,
                "Computer name is required and may contain at most 256 characters.");
        }

        return normalized;
    }

    internal async Task<AddAlertResult?> TryReconcileInventoryHeartbeatAsync(
        AlertRecord alert,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var observations = InventoryHeartbeatBatch.Parse(payload);
            if (observations.Count == 0 && !AlertInventoryLearning.HasZeroRowCount(payload))
                throw new InvalidOperationException(
                    "The InventoryHeartbeat payload does not contain any valid inventory rows.");

            var changed = await DatabaseTransactions.ExecuteAsync(contextFactory, async dbContext =>
            {
                var inventoryRules = await dbContext.AlertRules
                    .AsNoTracking()
                    .Where(rule => rule.Enabled && rule.RuleType == AlertRuleTypes.InventoryStatus)
                    .OrderBy(rule => rule.Priority)
                    .ToArrayAsync(cancellationToken);
                if (!inventoryRules.Any(rule => alertRuleEvaluator.MatchesInventoryStatusRule(rule, alert)))
                {
                    return (Matched: false, Changed: false);
                }

                var hasChanges = false;

                foreach (var observation in observations)
                {
                    var stored = await dbContext.ComputerInventory.FindAsync(
                        [observation.SubscriptionId, observation.Computer],
                        cancellationToken);
                    if (stored is null)
                    {
                        dbContext.ComputerInventory.Add(new ComputerInventoryEntry
                        {
                            SubscriptionId = observation.SubscriptionId,
                            Computer = observation.Computer,
                            Domain = observation.Domain,
                            Site = observation.Site,
                            PrivateIP = observation.PrivateIP,
                            LastHeartbeatAt = observation.LastHeartbeatAt,
                            LastBootTime = observation.LastBootTime,
                            NtdsFileSizeInGb = observation.NtdsFileSizeInGb,
                            CertificatesJson = observation.CertificatesJson ?? "[]",
                            ServicesJson = observation.ServicesJson ?? "[]",
                            Role = observation.DetectedRole
                        });
                        hasChanges = true;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(observation.Domain) &&
                        !string.Equals(stored.Domain, observation.Domain, StringComparison.OrdinalIgnoreCase))
                    {
                        stored.Domain = observation.Domain;
                        hasChanges = true;
                    }

                    if (!string.IsNullOrWhiteSpace(observation.PrivateIP) &&
                        !string.Equals(stored.PrivateIP, observation.PrivateIP, StringComparison.OrdinalIgnoreCase))
                    {
                        stored.PrivateIP = observation.PrivateIP;
                        hasChanges = true;
                    }

                    if (!string.IsNullOrWhiteSpace(observation.Site) &&
                        !string.Equals(stored.Site, observation.Site, StringComparison.OrdinalIgnoreCase))
                    {
                        stored.Site = observation.Site;
                        hasChanges = true;
                    }

                    if (observation.HasNtdsFileSizeData &&
                        stored.NtdsFileSizeInGb != observation.NtdsFileSizeInGb)
                    {
                        stored.NtdsFileSizeInGb = observation.NtdsFileSizeInGb;
                        hasChanges = true;
                    }

                    if (observation.CertificatesJson is not null &&
                        !string.Equals(
                        stored.CertificatesJson,
                        observation.CertificatesJson,
                        StringComparison.Ordinal))
                    {
                        stored.CertificatesJson = observation.CertificatesJson;
                        hasChanges = true;
                    }

                    if (observation.ServicesJson is not null &&
                        !string.Equals(stored.ServicesJson, observation.ServicesJson, StringComparison.Ordinal))
                    {
                        stored.ServicesJson = observation.ServicesJson;
                        hasChanges = true;
                    }

                    if (observation.DetectedRole is { } detectedRole &&
                        !string.Equals(stored.Role, detectedRole, StringComparison.OrdinalIgnoreCase))
                    {
                        stored.Role = detectedRole;
                        hasChanges = true;
                    }

                    if (observation.LastBootTime is { } lastBootTime &&
                        (stored.LastHeartbeatAt is null || observation.LastHeartbeatAt >= stored.LastHeartbeatAt) &&
                        stored.LastBootTime != lastBootTime)
                    {
                        stored.LastBootTime = lastBootTime;
                        hasChanges = true;
                    }

                    if (stored.LastHeartbeatAt is null || observation.LastHeartbeatAt > stored.LastHeartbeatAt)
                    {
                        stored.LastHeartbeatAt = observation.LastHeartbeatAt;
                        hasChanges = true;
                    }
                }

                if (hasChanges)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    var pendingAlerts = await dbContext.ParsedAlerts.Include(record => record.Alert)
                        .Where(record => record.InventorySubscriptionId == null)
                        .ToArrayAsync(cancellationToken);
                    var inventory = await dbContext.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken);
                    foreach (var parsed in pendingAlerts)
                        LinkInventoryTarget(parsed, queryResultPresenter.ResolveDisplayIdentity(parsed.Alert), inventory);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                return (Matched: true, Changed: hasChanges);
            }, cancellationToken, IsolationLevel.Serializable, commitWhen: result => result.Matched);

            if (!changed.Matched)
            {
                return null;
            }

            logger.LogInformation(
                "Reconciled {InventoryCount} inventory heartbeat observations without storing alert {AlertId}.",
                observations.Count,
                alert.AlertId);
            if (changed.Changed)
            {
                notifyChanged();
            }

            return new AddAlertResult(alert, false, true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to reconcile inventory heartbeat batch {AlertId}.", alert.AlertId);
            throw;
        }
    }

    internal static void LinkInventoryTarget(
        ParsedAlertRecord parsed, AlertRecord alert, IReadOnlyList<ComputerInventoryEntry> inventory)
    {
        var target = ScheduledQueryAlert.IsWorkspace(alert) ? null : AlertInventoryLearning.IsDcPort(alert.RawJson) &&
            !AlertInventoryLearning.HasZeroRowCount(alert.RawJson)
            ? AlertInventoryLearning.FindExistingDcPortTarget(alert, inventory)
            : inventory.FirstOrDefault(entry =>
                entry.SubscriptionId.Equals(alert.SubscriptionId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                entry.Computer.Equals(alert.TargetName.Trim(), StringComparison.OrdinalIgnoreCase));
        parsed.InventorySubscriptionId = target?.SubscriptionId;
        parsed.InventoryComputer = target?.Computer;
    }

}
