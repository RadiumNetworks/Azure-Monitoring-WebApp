using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

/// <summary>
/// Represents one persisted Azure Monitor alert event together with its original JSON payload. Computed properties derive display names, site information, and the search query from that payload.
/// </summary>
public sealed record AlertRecord(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string AlertId,
    string Name,
    string Severity,
    string Status,
    string SignalType,
    string MonitorCondition,
    string Target,
    string ResourceGroup,
    string SubscriptionId,
    DateTimeOffset? FiredAt,
    string Description,
    string SearchResultsUrl,
    string Comments,
    string RawJson)
{
    public AlertDisplayIdentity? DisplayIdentity { get; init; }
    public string TargetName => DisplayIdentity?.TargetName ?? GetTargetName(GetConfigurationItem() ?? Target);
    public string SiteName => DisplayIdentity?.SiteName ?? string.Empty;
    public string TargetDisplayName => string.IsNullOrWhiteSpace(SiteName)
        ? TargetName
        : $"{TargetName} ({SiteName})";
    public string SearchQuery => GetSearchQuery(RawJson);

    /// <summary>
    /// Finds the first meaningful value for any requested dimension name in the alert criteria. Missing or invalid payload data returns null.
    /// </summary>
    internal string? GetDimensionValue(params string[] names)
    {
        if (string.IsNullOrWhiteSpace(RawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(RawJson);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("alertContext", out var alertContext) ||
                !alertContext.TryGetProperty("condition", out var condition) ||
                !condition.TryGetProperty("allOf", out var allOf) ||
                allOf.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var criterion in allOf.EnumerateArray())
            {
                if (!criterion.TryGetProperty("dimensions", out var dimensions) ||
                    dimensions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var dimension in dimensions.EnumerateArray())
                {
                    if (dimension.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String &&
                        names.Contains(nameElement.GetString(), StringComparer.OrdinalIgnoreCase) &&
                        dimension.TryGetProperty("value", out var valueElement) &&
                        valueElement.ValueKind == JsonValueKind.String &&
                        valueElement.GetString() is { } value &&
                        IsMeaningfulTarget(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the first meaningful configuration item from the Common Alert Schema essentials. Missing or invalid payload data returns null.
    /// </summary>
    private string? GetConfigurationItem()
    {
        if (string.IsNullOrWhiteSpace(RawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(RawJson);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("essentials", out var essentials) ||
                !essentials.TryGetProperty("configurationItems", out var configurationItems) ||
                configurationItems.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return configurationItems
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .FirstOrDefault(item => item is not null && IsMeaningfulTarget(item));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether a target value contains usable content rather than the Azure Monitor empty placeholder. Whitespace-only values are rejected.
    /// </summary>
    internal static bool IsMeaningfulTarget(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("<EMPTY_VALUE>", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the final segment from a slash- or backslash-delimited target path. A plain name is returned unchanged.
    /// </summary>
    internal static string GetTargetName(string value)
    {
        var normalizedValue = value.TrimEnd('/', '\\');
        var separatorIndex = normalizedValue.LastIndexOfAny(['/', '\\']);
        return separatorIndex >= 0 ? normalizedValue[(separatorIndex + 1)..] : normalizedValue;
    }

    /// <summary>
    /// Parses an alert payload and searches it for the first searchQuery property. Empty or invalid JSON returns an empty string.
    /// </summary>
    private static string GetSearchQuery(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return FindSearchQuery(document.RootElement) ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Recursively searches JSON objects and arrays for a string-valued searchQuery property. It returns null when no query is present.
    /// </summary>
    private static string? FindSearchQuery(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("searchQuery", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nestedValue = FindSearchQuery(property.Value);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nestedValue = FindSearchQuery(item);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Reports the alert returned by ingestion and whether a new database row was created. Duplicate events return the existing record with Created set to false.
/// </summary>
public sealed record AddAlertResult(AlertRecord Alert, bool Created);

/// <summary>
/// Summarizes an inventory prefill operation based on recently received alerts.
/// </summary>
public sealed record ComputerInventoryPrefillResult(
    int AlertsScanned,
    int SystemsFound,
    int SystemsCreated,
    int SitesFilled,
    int ResourceGroupsFilled,
    int RolesAssigned,
    int SystemsUnchanged);

/// <summary>
/// Provides database-backed alert ingestion, retrieval, and comment updates. It centralizes persistence errors, duplicate detection, and change notifications.
/// </summary>
public sealed class AlertStore
{
    private readonly IDbContextFactory<AlertDbContext> contextFactory;
    private readonly DatabaseConfigurationStatus databaseConfiguration;
    private readonly QueryResultPresenter queryResultPresenter;
    private readonly ILogger<AlertStore> logger;

    /// <summary>
    /// Creates an alert store with its database factory, validated configuration state, and logger. Database contexts are opened per operation.
    /// </summary>
    public AlertStore(
        IDbContextFactory<AlertDbContext> contextFactory,
        DatabaseConfigurationStatus databaseConfiguration,
        QueryResultPresenter queryResultPresenter,
        ILogger<AlertStore> logger)
    {
        this.contextFactory = contextFactory;
        this.databaseConfiguration = databaseConfiguration;
        this.queryResultPresenter = queryResultPresenter;
        this.logger = logger;
    }

    public event Action? Changed;

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
        EnsureDatabaseConfigured();

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
        EnsureDatabaseConfigured();

        var normalized = NormalizeAndValidateRule(rule);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var duplicateName = await context.AlertRules.AnyAsync(
            candidate => candidate.Id != normalized.Id && candidate.Name == normalized.Name,
            cancellationToken);
        if (duplicateName)
        {
            throw new InvalidOperationException($"An alert rule named '{normalized.Name}' already exists.");
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
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Failed to save alert rule {AlertRuleId}.", normalized.Id);
            throw new InvalidOperationException(
                "The alert rule could not be saved. Verify that its name is unique and try again.",
                exception);
        }

        Changed?.Invoke();
        return stored;
    }

    /// <summary>
    /// Deletes an alert rule by ID. It returns false when the rule no longer exists.
    /// </summary>
    public async Task<bool> DeleteAlertRuleAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseConfigured();
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException("A valid alert rule ID is required.");
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
            throw new InvalidOperationException("The alert rule could not be deleted.", exception);
        }

        if (deletedRows > 0)
        {
            Changed?.Invoke();
        }

        return deletedRows > 0;
    }

    /// <summary>
    /// Loads the computer inventory ordered by subscription and computer name.
    /// </summary>
    public async Task<IReadOnlyList<ComputerInventoryEntry>> GetComputerInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseConfigured();

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
        EnsureDatabaseConfigured();

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
    /// Scans alerts received during the last seven days and prefills the computer inventory.
    /// Existing domain and site values are preserved; only a missing site may be filled from an alert.
    /// </summary>
    public async Task<ComputerInventoryPrefillResult> PrefillComputerInventoryFromLastSevenDaysAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseConfigured();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alerts = await context.Alerts
            .AsNoTracking()
            .Where(alert => alert.ReceivedAt >= cutoff)
            .OrderByDescending(alert => alert.ReceivedAt)
            .ToArrayAsync(cancellationToken);
        var roleRules = await context.AlertRules
            .AsNoTracking()
            .Where(rule => rule.Enabled && rule.RuleType == AlertRuleTypes.InventoryRoleAssignment)
            .ToArrayAsync(cancellationToken);

        var candidates = new Dictionary<string, ComputerInventoryEntry>(StringComparer.Ordinal);
        foreach (var alert in alerts)
        {
            var resolvedAlert = ResolveDisplayIdentity(alert);
            var subscriptionId = resolvedAlert.SubscriptionId.Trim();
            var computer = resolvedAlert.TargetName.Trim();
            var site = resolvedAlert.SiteName.Trim();
            var resourceGroup = resolvedAlert.ResourceGroup.Trim();
            var role = InventoryRoleRuleMatcher.FindRole(resolvedAlert, roleRules);
            if (subscriptionId.Length is 0 or > 64 || computer.Length is 0 or > 256)
            {
                continue;
            }

            var key = InventoryKey(subscriptionId, computer);
            var validSite = site.Length is > 0 and <= 256 ? site : null;
            if (!candidates.TryGetValue(key, out var candidate))
            {
                candidates.Add(key, new ComputerInventoryEntry
                {
                    SubscriptionId = subscriptionId,
                    Computer = computer,
                    Site = validSite,
                    ResourceGroup = resourceGroup.Length is > 0 and <= 256 ? resourceGroup : null,
                    Role = role
                });
            }
            else if (string.IsNullOrWhiteSpace(candidate.Site) && validSite is not null)
            {
                candidate.Site = validSite;
            }

            if (candidates.TryGetValue(key, out candidate))
            {
                if (string.IsNullOrWhiteSpace(candidate.ResourceGroup) && resourceGroup.Length is > 0 and <= 256)
                {
                    candidate.ResourceGroup = resourceGroup;
                }

                if (string.IsNullOrWhiteSpace(candidate.Role) && !string.IsNullOrWhiteSpace(role))
                {
                    candidate.Role = role;
                }
            }
        }

        var storedEntries = await context.ComputerInventory.ToArrayAsync(cancellationToken);
        var storedByKey = storedEntries.ToDictionary(
            entry => InventoryKey(entry.SubscriptionId, entry.Computer),
            StringComparer.Ordinal);
        var systemsCreated = 0;
        var sitesFilled = 0;
        var resourceGroupsFilled = 0;
        var rolesAssigned = 0;
        var changedSystems = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, candidate) in candidates)
        {
            if (!storedByKey.TryGetValue(key, out var stored))
            {
                context.ComputerInventory.Add(candidate);
                systemsCreated++;
                changedSystems.Add(key);
            }
            else if (string.IsNullOrWhiteSpace(stored.Site) && !string.IsNullOrWhiteSpace(candidate.Site))
            {
                stored.Site = candidate.Site;
                sitesFilled++;
                changedSystems.Add(key);
            }

            if (storedByKey.TryGetValue(key, out stored))
            {
                if (string.IsNullOrWhiteSpace(stored.ResourceGroup) && !string.IsNullOrWhiteSpace(candidate.ResourceGroup))
                {
                    stored.ResourceGroup = candidate.ResourceGroup;
                    resourceGroupsFilled++;
                    changedSystems.Add(key);
                }

                if (string.IsNullOrWhiteSpace(stored.Role) && !string.IsNullOrWhiteSpace(candidate.Role))
                {
                    stored.Role = candidate.Role;
                    rolesAssigned++;
                    changedSystems.Add(key);
                }
            }
        }

        if (systemsCreated > 0 || sitesFilled > 0 || resourceGroupsFilled > 0 || rolesAssigned > 0)
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                logger.LogError(exception, "Failed to prefill computer inventory from recent alerts.");
                throw new InvalidOperationException(
                    "The computer inventory could not be prefilled. Refresh the page and try again.",
                    exception);
            }

            Changed?.Invoke();
        }

        return new ComputerInventoryPrefillResult(
            alerts.Length,
            candidates.Count,
            systemsCreated,
            sitesFilled,
            resourceGroupsFilled,
            rolesAssigned,
            candidates.Count - changedSystems.Count);
    }

    /// <summary>
    /// Creates a manually maintained computer inventory entry. Its subscription must have been observed in an alert.
    /// </summary>
    public async Task<ComputerInventoryEntry> CreateComputerInventoryEntryAsync(
        ComputerInventoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureDatabaseConfigured();

        var normalized = NormalizeAndValidateInventoryEntry(entry);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var knownSubscription = await context.Alerts.AnyAsync(
            alert => alert.SubscriptionId == normalized.SubscriptionId,
            cancellationToken);
        if (!knownSubscription)
        {
            throw new InvalidOperationException("Select a subscription ID that has been observed in an alert.");
        }

        var existing = await context.ComputerInventory.FindAsync(
            [normalized.SubscriptionId, normalized.Computer],
            cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Computer '{normalized.Computer}' already exists in subscription '{normalized.SubscriptionId}'.");
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
            throw new InvalidOperationException(
                "The inventory record could not be created. Verify that it does not already exist.",
                exception);
        }

        Changed?.Invoke();
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
        EnsureDatabaseConfigured();

        var normalized = NormalizeAndValidateInventoryEntry(entry);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.ComputerInventory.FindAsync(
            [normalized.SubscriptionId, normalized.Computer],
            cancellationToken);
        if (stored is null)
        {
            throw new InvalidOperationException("The inventory record no longer exists. Refresh the inventory and try again.");
        }

        stored.Domain = normalized.Domain;
        stored.Site = normalized.Site;
        stored.ResourceGroup = normalized.ResourceGroup;
        stored.Role = normalized.Role;

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
            throw new InvalidOperationException("The inventory record could not be updated.", exception);
        }

        Changed?.Invoke();
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
        EnsureDatabaseConfigured();

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
            throw new InvalidOperationException("The inventory record could not be deleted.", exception);
        }

        if (deletedRows > 0)
        {
            Changed?.Invoke();
        }

        return deletedRows > 0;
    }

    private void EnsureDatabaseConfigured()
    {
        if (!databaseConfiguration.IsValid)
        {
            throw new InvalidOperationException(
                $"Alert rules cannot be accessed: {databaseConfiguration.Error}");
        }
    }

    private static string InventoryKey(string subscriptionId, string computer) =>
        $"{subscriptionId.Trim().ToUpperInvariant()}\0{computer.Trim().ToUpperInvariant()}";

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
            InventoryRole = source.InventoryRole.Trim()
        };

        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 256)
        {
            throw new InvalidOperationException("Rule name is required and may contain at most 256 characters.");
        }

        if (rule.RuleType is not (AlertRuleTypes.Categorization or AlertRuleTypes.InventoryRoleAssignment))
        {
            throw new InvalidOperationException("Select a supported rule type.");
        }

        if (rule.RuleType == AlertRuleTypes.InventoryRoleAssignment)
        {
            if (string.IsNullOrWhiteSpace(rule.InventoryRole) || rule.InventoryRole.Length > 256)
            {
                throw new InvalidOperationException("Inventory role is required and may contain at most 256 characters.");
            }

            if (string.IsNullOrWhiteSpace(rule.QueryResultType) && string.IsNullOrWhiteSpace(rule.AlertNameContains))
            {
                throw new InvalidOperationException("An inventory-role rule requires an alert name or query-result type.");
            }

            rule.ConditionType = string.Empty;
            rule.CategoryName = string.Empty;
            rule.FailedItemName = string.Empty;
            rule.Threshold = 0;
            rule.ApplyToTarget = false;
            rule.Collapsed = false;
            rule.Tone = "info";
            return rule;
        }

        rule.InventoryRole = string.Empty;
        if (string.IsNullOrWhiteSpace(rule.CategoryName) || rule.CategoryName.Length > 256)
        {
            throw new InvalidOperationException("Category name is required and may contain at most 256 characters.");
        }

        if (rule.Priority < 0)
        {
            throw new InvalidOperationException("Priority cannot be negative.");
        }

        if (rule.AlertNameContains.Length > 256 || rule.QueryResultType.Length > 128)
        {
            throw new InvalidOperationException("One or more matching fields exceed their maximum length.");
        }

        if (rule.ConditionType == AlertRuleConditionTypes.RowCountGreaterThan)
        {
            if (rule.Threshold < 0)
            {
                throw new InvalidOperationException("The row-count threshold cannot be negative.");
            }

            rule.FailedItemName = string.Empty;
        }
        else if (rule.ConditionType == AlertRuleConditionTypes.OnlyFailedItem)
        {
            if (string.IsNullOrWhiteSpace(rule.FailedItemName) || rule.FailedItemName.Length > 256)
            {
                throw new InvalidOperationException(
                    "Failed item name is required for an OnlyFailedItem condition.");
            }

            rule.Threshold = 0;
        }
        else
        {
            throw new InvalidOperationException("Select a supported condition type.");
        }

        if (rule.Tone is not ("info" or "failure"))
        {
            throw new InvalidOperationException("Select either the info or failure category tone.");
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
    }

    private static string? NormalizeOptionalInventoryValue(string? value, int maximumLength, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            throw new InvalidOperationException($"{fieldName} may contain at most {maximumLength} characters.");
        }

        return normalized;
    }

    private static ComputerInventoryEntry NormalizeAndValidateInventoryEntry(ComputerInventoryEntry entry)
    {
        var normalized = new ComputerInventoryEntry
        {
            SubscriptionId = entry.SubscriptionId.Trim(),
            Computer = entry.Computer.Trim(),
            Domain = NormalizeOptionalInventoryValue(entry.Domain, 256, "Domain"),
            Site = NormalizeOptionalInventoryValue(entry.Site, 256, "Site"),
            ResourceGroup = NormalizeOptionalInventoryValue(entry.ResourceGroup, 256, "Resource group"),
            Role = NormalizeOptionalInventoryValue(entry.Role, 256, "Role")
        };

        if (string.IsNullOrWhiteSpace(normalized.SubscriptionId) || normalized.SubscriptionId.Length > 64)
        {
            throw new InvalidOperationException("Subscription ID is required and may contain at most 64 characters.");
        }

        if (string.IsNullOrWhiteSpace(normalized.Computer) || normalized.Computer.Length > 256)
        {
            throw new InvalidOperationException("Computer name is required and may contain at most 256 characters.");
        }

        return normalized;
    }

    /// <summary>
    /// Loads all stored alerts with the newest records first. Configuration or database failures are logged and return an empty list for the UI.
    /// </summary>
    public async Task<IReadOnlyList<AlertRecord>> GetAllAsync(CancellationToken cancellationToken = default)
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
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
            return alerts.Select(ResolveDisplayIdentity).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load alerts from the database.");
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
            return alerts.Select(ResolveDisplayIdentity).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load recent alerts from the database.");
            return [];
        }
    }

    /// <summary>
    /// Loads alerts received on or after the supplied timestamp, ordered newest first. Unlike the UI loader, configuration and database failures are propagated to the caller.
    /// </summary>
    public async Task<IReadOnlyList<AlertRecord>> GetSinceRequiredAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseIsConfigured();

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var alerts = await context.Alerts
                .AsNoTracking()
                .Where(alert => alert.ReceivedAt >= since)
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
            return alerts.Select(ResolveDisplayIdentity).ToArray();
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
        EnsureDatabaseIsConfigured();

        var data = GetObject(payload, "data");
        var essentials = GetObject(data, "essentials");
        var alertContext = GetObject(data, "alertContext");
        var context = essentials.ValueKind == JsonValueKind.Object ? essentials : payload;
        var targetResource = GetFirstArrayValue(context, "alertTargetIDs")
            ?? GetFirstArrayValue(data, "alertTargetIDs");
        var resourceGroup = GetString(context, "targetResourceGroup", "resourceGroupName");
        var subscriptionId = GetString(context, "targetSubscriptionId", "subscriptionId");

        if (!string.IsNullOrWhiteSpace(targetResource))
        {
            resourceGroup = FirstNonEmpty(resourceGroup, GetResourceIdSegment(targetResource, "resourceGroups"));
            subscriptionId = FirstNonEmpty(
                GetResourceIdSegment(targetResource, "subscriptions"),
                subscriptionId);
        }

        var alert = new AlertRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            GetString(context, "alertId"),
            GetString(context, "alertRule", "name", "alertName"),
            GetString(context, "severity"),
            GetString(context, "alertState", "status"),
            GetString(context, "signalType"),
            GetString(context, "monitorCondition"),
            targetResource ?? GetString(context, "targetResourceName", "resourceId"),
            resourceGroup,
            subscriptionId,
            GetDateTime(context, "firedDateTime", "startDateTime", "timestamp"),
            GetString(context, "description"),
            GetHttpUrl(alertContext, "linkToSearchResultsUI"),
            string.Empty,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        var resolvedAlert = ResolveDisplayIdentity(alert);

        AddAlertResult result;
        try
        {
            await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            result = await strategy.ExecuteAsync(async () =>
            {
                await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(alert.AlertId))
                {
                    var existingAlert = await dbContext.Alerts
                        .FirstOrDefaultAsync(
                            existing => existing.AlertId == alert.AlertId &&
                                existing.MonitorCondition == alert.MonitorCondition,
                            cancellationToken);
                    if (existingAlert is not null)
                    {
                        await UpsertComputerInventoryAsync(dbContext, resolvedAlert, cancellationToken);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        logger.LogInformation(
                            "Ignored duplicate alert {AlertId} with condition {MonitorCondition}.",
                            alert.AlertId,
                            alert.MonitorCondition);
                        await transaction.CommitAsync(cancellationToken);
                        return new AddAlertResult(existingAlert, false);
                    }
                }

                dbContext.Alerts.Add(alert);
                await UpsertComputerInventoryAsync(dbContext, resolvedAlert, cancellationToken);
                dbContext.ParsedAlerts.Add(ParsedAlertFactory.Create(resolvedAlert));
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new AddAlertResult(alert, true);
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist incoming alert {AlertId}.", alert.AlertId);
            throw;
        }

        if (result.Created)
        {
            Changed?.Invoke();
        }

        return result with { Alert = ResolveDisplayIdentity(result.Alert) };
    }

    /// <summary>
    /// Creates an inventory entry for a resolved alert or fills its missing site when a later alert provides it.
    /// Domain is intentionally left unchanged because alert payloads do not provide it yet.
    /// </summary>
    private async Task UpsertComputerInventoryAsync(
        AlertDbContext context,
        AlertRecord alert,
        CancellationToken cancellationToken)
    {
        var subscriptionId = alert.SubscriptionId.Trim();
        var computer = alert.TargetName.Trim();
        var site = alert.SiteName.Trim();
        var resourceGroup = alert.ResourceGroup.Trim();
        var roleRules = await context.AlertRules
            .Where(rule => rule.Enabled && rule.RuleType == AlertRuleTypes.InventoryRoleAssignment)
            .OrderBy(rule => rule.Priority)
            .ToArrayAsync(cancellationToken);
        var role = InventoryRoleRuleMatcher.FindRole(alert, roleRules);

        if (string.IsNullOrWhiteSpace(subscriptionId) ||
            subscriptionId.Length > 64 ||
            string.IsNullOrWhiteSpace(computer) ||
            computer.Length > 256)
        {
            logger.LogWarning(
                "Skipped computer inventory update for alert {AlertId} because its subscription ID or target name is missing or too long.",
                alert.AlertId);
            return;
        }

        var stored = await context.ComputerInventory.FindAsync(
            [subscriptionId, computer],
            cancellationToken);
        if (stored is null)
        {
            context.ComputerInventory.Add(new ComputerInventoryEntry
            {
                SubscriptionId = subscriptionId,
                Computer = computer,
                Site = string.IsNullOrWhiteSpace(site) || site.Length > 256 ? null : site,
                ResourceGroup = string.IsNullOrWhiteSpace(resourceGroup) || resourceGroup.Length > 256 ? null : resourceGroup,
                Role = role
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(stored.Site) &&
            !string.IsNullOrWhiteSpace(site) &&
            site.Length <= 256)
        {
            stored.Site = site;
        }

        if (string.IsNullOrWhiteSpace(stored.ResourceGroup) &&
            !string.IsNullOrWhiteSpace(resourceGroup) &&
            resourceGroup.Length <= 256)
        {
            stored.ResourceGroup = resourceGroup;
        }

        if (string.IsNullOrWhiteSpace(stored.Role) && !string.IsNullOrWhiteSpace(role))
        {
            stored.Role = role;
        }
    }

    private AlertRecord ResolveDisplayIdentity(AlertRecord alert) =>
        alert with { DisplayIdentity = queryResultPresenter.ResolveIdentity(alert) };

    /// <summary>
    /// Trims and updates operator comments for one alert record. It returns false when the record no longer exists and rejects comments longer than 4,000 characters.
    /// </summary>
    public async Task<bool> UpdateCommentsAsync(
        Guid id,
        string comments,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseIsConfigured();

        var normalizedComments = comments.Trim();
        if (normalizedComments.Length > 4000)
        {
            throw new ArgumentException("Comments cannot exceed 4000 characters.", nameof(comments));
        }

        int updatedRows;
        try
        {
            await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            updatedRows = await dbContext.Alerts
                .Where(alert => alert.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(alert => alert.Comments, normalizedComments),
                    cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update comments for alert {AlertRecordId}.", id);
            throw;
        }

        if (updatedRows > 0)
        {
            Changed?.Invoke();
        }

        return updatedRows > 0;
    }

    /// <summary>
    /// Ensures the parsed database configuration is valid before a required operation starts. Invalid configuration raises an InvalidOperationException with the recorded reason.
    /// </summary>
    private void EnsureDatabaseIsConfigured()
    {
        if (!databaseConfiguration.IsValid)
        {
            throw new InvalidOperationException(databaseConfiguration.Error);
        }
    }

    /// <summary>
    /// Walks a property path through nested JSON objects. A missing segment or non-object value returns the default JsonElement.
    /// </summary>
    private static JsonElement GetObject(JsonElement source, params string[] path)
    {
        var current = source;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return default;
            }
        }

        return current;
    }

    /// <summary>
    /// Reads the first available property from a list of alternative names. String values are returned directly and other JSON values are converted to text.
    /// </summary>
    private static string GetString(JsonElement source, params string[] names)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (source.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Recursively finds the first valid HTTP or HTTPS URL stored under the requested property name. Missing and invalid URLs produce an empty string.
    /// </summary>
    private static string GetHttpUrl(JsonElement source, string propertyName)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    {
                        return uri.AbsoluteUri;
                    }
                }

                var nestedValue = GetHttpUrl(property.Value, propertyName);
                if (nestedValue.Length > 0)
                {
                    return nestedValue;
                }
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
            {
                var nestedValue = GetHttpUrl(item, propertyName);
                if (nestedValue.Length > 0)
                {
                    return nestedValue;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the first value from a named JSON array. Missing, non-array, or empty properties return null.
    /// </summary>
    private static string? GetFirstArrayValue(JsonElement source, string name)
    {
        if (source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty(name, out var values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                return value.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts and URL-decodes the value following a named segment in an Azure resource ID. The method returns an empty string when the segment is absent.
    /// </summary>
    private static string GetResourceIdSegment(string resourceId, string segmentName)
    {
        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals(segmentName, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[index + 1]);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the preferred value when it contains text, otherwise the fallback value. This is used to combine explicit payload fields with resource-ID-derived values.
    /// </summary>
    private static string FirstNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    /// <summary>
    /// Reads the first matching JSON property and parses it as a timestamp. Missing or invalid values return null.
    /// </summary>
    private static DateTimeOffset? GetDateTime(JsonElement source, params string[] names)
    {
        var value = GetString(source, names);
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }
}