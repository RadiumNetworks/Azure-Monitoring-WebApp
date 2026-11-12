using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal sealed class AlertRulePersistence(
    IDbContextFactory<AlertDbContext> contextFactory,
    DatabaseConfigurationStatus databaseConfiguration,
    ILogger<AlertStore> logger,
    AlertRuleEvaluator evaluator,
    Action notifyChanged)
{
    public async Task<IReadOnlyList<AlertRule>> GetAlertRulesAsync(CancellationToken cancellationToken = default)
    {
        if (!databaseConfiguration.IsValid)
        {
            return [];
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await context.AlertRules
                .AsNoTracking()
                .Where(rule => rule.Enabled)
                .OrderBy(rule => rule.Priority)
                .ToArrayAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load alert categorization rules.");
            return [];
        }
    }

    public async Task<IReadOnlyList<AlertRule>> GetAllAlertRulesAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = ApplicationTelemetry.StartOperation("AlertStore.GetAllAlertRules");
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AlertRules
            .AsNoTracking()
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<AlertRule> SaveAlertRuleAsync(
        AlertRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");

        var normalized = NormalizeAndValidateRule(rule);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (normalized.RuleType == AlertRuleTypes.ReplicationInventoryWarning &&
            await context.AlertRules.AnyAsync(
                candidate => candidate.Id != normalized.Id &&
                    candidate.RuleType == AlertRuleTypes.ReplicationInventoryWarning,
                cancellationToken))
        {
            throw new AlertRuleException(AlertRuleErrorCode.DuplicateReplicationWarning, "Only one global replication inventory warning rule is allowed. Edit the existing rule.");
        }

        if (normalized.RuleType == AlertRuleTypes.InventoryHeartbeatFreshness &&
            await context.AlertRules.AnyAsync(
                candidate => candidate.Id != normalized.Id &&
                    candidate.RuleType == AlertRuleTypes.InventoryHeartbeatFreshness,
                cancellationToken))
        {
            throw new AlertRuleException(AlertRuleErrorCode.DuplicateHeartbeatFreshness, "Only one global inventory heartbeat freshness rule is allowed. Edit the existing rule.");
        }

        var duplicateName = await context.AlertRules.AnyAsync(
            candidate => candidate.Id != normalized.Id && candidate.Name == normalized.Name,
            cancellationToken);
        if (duplicateName)
        {
            throw new AlertRuleException(AlertRuleErrorCode.DuplicateName, $"An alert rule named '{normalized.Name}' already exists.", arguments: [normalized.Name]);
        }

        var stored = await context.AlertRules.FindAsync([normalized.Id], cancellationToken);
        if (stored is null)
        {
            stored = normalized;
            context.AlertRules.Add(stored);
        }
        else
        {
            CopyRuleValues(normalized, stored);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await ParsedAlertLifecyclePersistence.SynchronizeAsync(context, evaluator, null, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Failed to save alert rule {AlertRuleId}.", normalized.Id);
            throw new AlertRuleException(AlertRuleErrorCode.ChangeUnconfirmed,
                "The alert rule could not be saved. Verify that its name is unique, that there is only one inventory heartbeat freshness rule, and try again.",
                exception);
        }

        NotifyAfterChange(AlertRuleErrorCode.SavedNotificationFailed, stored.Id);
        return stored;
    }

    /// <summary>
    /// Deletes an alert rule by ID. It returns false when the rule no longer exists.
    /// </summary>
    public async Task<bool> DeleteAlertRuleAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        AlertStoreAccess.Require(databaseConfiguration, "Alert rules cannot be accessed: ");
        if (id == Guid.Empty)
        {
            throw new AlertRuleException(AlertRuleErrorCode.InvalidId, "A valid alert rule ID is required.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        int deletedRows;
        try
        {
            deletedRows = await context.AlertRules
                .Where(rule => rule.Id == id)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete alert rule {AlertRuleId}.", id);
            throw new AlertRuleException(AlertRuleErrorCode.ChangeUnconfirmed, "The alert rule could not be deleted.", exception);
        }

        if (deletedRows > 0)
        {
            NotifyAfterChange(AlertRuleErrorCode.DeletedNotificationFailed, id);
        }

        return deletedRows > 0;
    }

    private void NotifyAfterChange(AlertRuleErrorCode code, Guid id)
    {
        try
        {
            notifyChanged();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to notify subscribers after committing alert rule {AlertRuleId}.", id);
            throw new AlertRuleException(code, "The alert rule change was committed, but notifications failed.", exception);
        }
    }

    private static AlertRule NormalizeAndValidateRule(AlertRule source)
    {
        var rule = new AlertRule
        {
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
            Name = source.Name.Trim(),
            Enabled = source.Enabled,
            Priority = source.Priority,
            RuleType = source.RuleType.Trim(),
            AlertNameContains = source.AlertNameContains.Trim(),
            QueryResultType = source.QueryResultType.Trim(),
            ConditionType = source.ConditionType.Trim(),
            Threshold = source.Threshold,
            FailedItemName = source.FailedItemName.Trim(),
            CategoryName = source.CategoryName.Trim(),
            ApplyToTarget = source.ApplyToTarget,
            Collapsed = source.Collapsed,
            Tone = source.Tone.Trim().ToLowerInvariant(),
            InventoryRole = InventoryRoleRuleMatcher.NormalizeRole(source.InventoryRole.Trim()) ?? string.Empty,
            IsCritical = source.IsCritical,
            AnalysisClassification = source.AnalysisClassification.Trim()
        };

        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 256)
        {
            throw new AlertRuleException(AlertRuleErrorCode.Name, "Rule name is required and may contain at most 256 characters.");
        }

        if (rule.RuleType is not (AlertRuleTypes.Categorization or AlertRuleTypes.Suppression or AlertRuleTypes.InventoryRoleAssignment or AlertRuleTypes.InventoryStatus or AlertRuleTypes.InventoryHeartbeatFreshness or AlertRuleTypes.ReplicationInventoryWarning))
        {
            throw new AlertRuleException(AlertRuleErrorCode.Type, "Select a supported rule type.");
        }

        if (rule.RuleType == AlertRuleTypes.ReplicationInventoryWarning)
        {
            rule.Enabled = false;
            rule.Priority = 0;
            rule.AlertNameContains = string.Empty;
            rule.QueryResultType = "Replication";
            rule.ConditionType = string.Empty;
            rule.Threshold = 0;
            rule.FailedItemName = string.Empty;
            rule.CategoryName = string.Empty;
            rule.InventoryRole = string.Empty;
            rule.ApplyToTarget = false;
            rule.Collapsed = false;
            rule.Tone = "info";
            rule.IsCritical = false;
            rule.AnalysisClassification = AlertRuleAnalysisClassifications.None;
            return rule;
        }

        if (rule.RuleType == AlertRuleTypes.InventoryHeartbeatFreshness)
        {
            if (rule.Threshold < 1)
            {
                throw new AlertRuleException(AlertRuleErrorCode.HeartbeatFreshness, "The inventory heartbeat freshness limit must be at least 1 minute.");
            }

            rule.Priority = 0;
            rule.AlertNameContains = string.Empty;
            rule.QueryResultType = string.Empty;
            rule.ConditionType = string.Empty;
            rule.FailedItemName = string.Empty;
            rule.CategoryName = string.Empty;
            rule.InventoryRole = string.Empty;
            rule.ApplyToTarget = false;
            rule.Collapsed = false;
            rule.Tone = "info";
            rule.IsCritical = false;
            rule.AnalysisClassification = AlertRuleAnalysisClassifications.None;
            return rule;
        }

        if (rule.RuleType == AlertRuleTypes.InventoryRoleAssignment)
        {
            if (rule.Enabled)
                throw new AlertRuleException(AlertRuleErrorCode.RetiredRoleAssignment, "Inventory-role assignment from alerts is retired. Roles are reported by InventoryHeartbeat services.");
            if (string.IsNullOrWhiteSpace(rule.InventoryRole) || rule.InventoryRole.Length > 256)
            {
                throw new AlertRuleException(AlertRuleErrorCode.InventoryRole, "Inventory role is required and may contain at most 256 characters.");
            }

            if (string.IsNullOrWhiteSpace(rule.QueryResultType) && string.IsNullOrWhiteSpace(rule.AlertNameContains))
            {
                throw new AlertRuleException(AlertRuleErrorCode.InventoryRoleMatch, "An inventory-role rule requires an alert name or query-result type.");
            }

            rule.ConditionType = string.Empty;
            rule.CategoryName = string.Empty;
            rule.FailedItemName = string.Empty;
            rule.Threshold = 0;
            rule.ApplyToTarget = false;
            rule.Collapsed = false;
            rule.Tone = "info";
            rule.IsCritical = false;
            rule.AnalysisClassification = AlertRuleAnalysisClassifications.None;
            return rule;
        }

        rule.InventoryRole = string.Empty;
        if (rule.RuleType == AlertRuleTypes.Categorization &&
            rule.AnalysisClassification is not (
                AlertRuleAnalysisClassifications.None or
                AlertRuleAnalysisClassifications.SystemOutage or
                AlertRuleAnalysisClassifications.Suppressed))
        {
            throw new AlertRuleException(AlertRuleErrorCode.AnalysisClassification, "Select a supported analysis classification.");
        }

        if (rule.RuleType == AlertRuleTypes.Categorization &&
            (string.IsNullOrWhiteSpace(rule.CategoryName) || rule.CategoryName.Length > 256))
        {
            throw new AlertRuleException(AlertRuleErrorCode.CategoryName, "Category name is required and may contain at most 256 characters.");
        }

        if (rule.RuleType is AlertRuleTypes.Suppression or AlertRuleTypes.InventoryStatus)
        {
            if (string.IsNullOrWhiteSpace(rule.AlertNameContains) &&
                string.IsNullOrWhiteSpace(rule.QueryResultType))
            {
                throw new AlertRuleException(AlertRuleErrorCode.RequiredMatch,
                    "A suppression or inventory-status rule requires an alert name or query-result type.");
            }

            rule.CategoryName = string.Empty;
            rule.ApplyToTarget = false;
            rule.Collapsed = false;
            rule.Tone = "info";
            rule.IsCritical = false;
            rule.AnalysisClassification = AlertRuleAnalysisClassifications.None;
        }

        if (rule.Priority < 0)
        {
            throw new AlertRuleException(AlertRuleErrorCode.Priority, "Priority cannot be negative.");
        }

        if (rule.AlertNameContains.Length > 256 || rule.QueryResultType.Length > 128)
        {
            throw new AlertRuleException(AlertRuleErrorCode.MatchLength, "One or more matching fields exceed their maximum length.");
        }

        if (rule.ConditionType == AlertRuleConditionTypes.AnyMatchingAlert)
        {
            if (rule.RuleType is not (AlertRuleTypes.Suppression or AlertRuleTypes.InventoryStatus))
            {
                throw new AlertRuleException(AlertRuleErrorCode.AnyMatchingAlert, "AnyMatchingAlert is supported only for suppression and inventory-status rules.");
            }

            rule.Threshold = 0;
            rule.FailedItemName = string.Empty;
        }
        else if (rule.ConditionType is AlertRuleConditionTypes.RowCountGreaterThan or AlertRuleConditionTypes.RowCountEquals)
        {
            if (rule.Threshold < 0)
            {
                throw new AlertRuleException(AlertRuleErrorCode.RowCountThreshold, "The row-count threshold cannot be negative.");
            }

            rule.FailedItemName = string.Empty;
        }
        else if (rule.ConditionType == AlertRuleConditionTypes.OnlyFailedItems)
        {
            var failedItemNames = rule.FailedItemName
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (failedItemNames.Length == 0)
            {
                throw new AlertRuleException(AlertRuleErrorCode.FailedItemsRequired,
                    "At least one failed item name is required for an OnlyFailedItems condition.");
            }

            rule.FailedItemName = string.Join(", ", failedItemNames);
            if (rule.FailedItemName.Length > 256)
            {
                throw new AlertRuleException(AlertRuleErrorCode.FailedItemsLength, "Failed item names may contain at most 256 characters.");
            }

            rule.Threshold = 0;
        }
        else if (rule.ConditionType == AlertRuleConditionTypes.NoFailedItems)
        {
            rule.Threshold = 0;
            rule.FailedItemName = string.Empty;
        }
        else if (rule.ConditionType == AlertRuleConditionTypes.DomainControllerPercentageInDomain)
        {
            if (rule.RuleType != AlertRuleTypes.Categorization)
            {
                throw new AlertRuleException(AlertRuleErrorCode.DomainConditionType, "The domain outage condition is supported only for categorization rules.");
            }

            if (rule.Threshold is < 0 or > 100)
            {
                throw new AlertRuleException(AlertRuleErrorCode.DomainPercentage, "The domain-controller percentage must be between 0 and 100.");
            }

            rule.AlertNameContains = string.Empty;
            rule.QueryResultType = string.Empty;
            rule.FailedItemName = string.Empty;
            rule.ApplyToTarget = false;
        }
        else if (rule.ConditionType == AlertRuleConditionTypes.AllDomainControllersInSite)
        {
            if (rule.RuleType != AlertRuleTypes.Categorization)
            {
                throw new AlertRuleException(AlertRuleErrorCode.SiteConditionType, "The site outage condition is supported only for categorization rules.");
            }

            rule.AlertNameContains = string.Empty;
            rule.QueryResultType = string.Empty;
            rule.Threshold = 0;
            rule.FailedItemName = string.Empty;
            rule.ApplyToTarget = false;
        }
        else
        {
            throw new AlertRuleException(AlertRuleErrorCode.ConditionType, "Select a supported condition type.");
        }

        if (rule.RuleType == AlertRuleTypes.Categorization && rule.Tone is not ("info" or "failure"))
        {
            throw new AlertRuleException(AlertRuleErrorCode.Tone, "Select either the info or failure category tone.");
        }

        return rule;
    }

    private static void CopyRuleValues(AlertRule source, AlertRule destination)
    {
        destination.Name = source.Name;
        destination.Enabled = source.Enabled;
        destination.Priority = source.Priority;
        destination.RuleType = source.RuleType;
        destination.AlertNameContains = source.AlertNameContains;
        destination.QueryResultType = source.QueryResultType;
        destination.ConditionType = source.ConditionType;
        destination.Threshold = source.Threshold;
        destination.FailedItemName = source.FailedItemName;
        destination.CategoryName = source.CategoryName;
        destination.ApplyToTarget = source.ApplyToTarget;
        destination.Collapsed = source.Collapsed;
        destination.Tone = source.Tone;
        destination.InventoryRole = source.InventoryRole;
        destination.IsCritical = source.IsCritical;
        destination.AnalysisClassification = source.AnalysisClassification;
    }
}
