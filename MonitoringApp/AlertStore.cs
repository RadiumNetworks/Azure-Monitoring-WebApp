using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

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
    public string TargetName => GetTargetName(
        GetDimensionValue("SourceDSA", "Computer") ?? GetConfigurationItem() ?? Target);
    public string SiteName => GetDimensionValue("Site", "SourceDSASite") ?? string.Empty;
    public string TargetDisplayName => string.IsNullOrWhiteSpace(SiteName)
        ? TargetName
        : $"{TargetName} ({SiteName})";
    public string SearchQuery => GetSearchQuery(RawJson);

    private string? GetDimensionValue(params string[] names)
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

    private static bool IsMeaningfulTarget(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("<EMPTY_VALUE>", StringComparison.OrdinalIgnoreCase);

    private static string GetTargetName(string value)
    {
        var normalizedValue = value.TrimEnd('/', '\\');
        var separatorIndex = normalizedValue.LastIndexOfAny(['/', '\\']);
        return separatorIndex >= 0 ? normalizedValue[(separatorIndex + 1)..] : normalizedValue;
    }

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

public sealed record AddAlertResult(AlertRecord Alert, bool Created);

public sealed class AlertStore
{
    private readonly IDbContextFactory<AlertDbContext> contextFactory;
    private readonly DatabaseConfigurationStatus databaseConfiguration;
    private readonly ILogger<AlertStore> logger;

    public AlertStore(
        IDbContextFactory<AlertDbContext> contextFactory,
        DatabaseConfigurationStatus databaseConfiguration,
        ILogger<AlertStore> logger)
    {
        this.contextFactory = contextFactory;
        this.databaseConfiguration = databaseConfiguration;
        this.logger = logger;
    }

    public event Action? Changed;

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
            return await context.Alerts
                .AsNoTracking()
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load alerts from the database.");
            return [];
        }
    }

    public async Task<IReadOnlyList<AlertRecord>> GetSinceRequiredAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseIsConfigured();

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await context.Alerts
                .AsNoTracking()
                .Where(alert => alert.ReceivedAt >= since)
                .OrderByDescending(alert => alert.ReceivedAt)
                .ToArrayAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to query alerts from the database.");
            throw;
        }
    }

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
            subscriptionId = FirstNonEmpty(subscriptionId, GetResourceIdSegment(targetResource, "subscriptions"));
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
                        logger.LogInformation(
                            "Ignored duplicate alert {AlertId} with condition {MonitorCondition}.",
                            alert.AlertId,
                            alert.MonitorCondition);
                        await transaction.CommitAsync(cancellationToken);
                        return new AddAlertResult(existingAlert, false);
                    }
                }

                dbContext.Alerts.Add(alert);
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

        return result;
    }

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

    private void EnsureDatabaseIsConfigured()
    {
        if (!databaseConfiguration.IsValid)
        {
            throw new InvalidOperationException(databaseConfiguration.Error);
        }
    }

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

    private static string FirstNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static DateTimeOffset? GetDateTime(JsonElement source, params string[] names)
    {
        var value = GetString(source, names);
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }
}