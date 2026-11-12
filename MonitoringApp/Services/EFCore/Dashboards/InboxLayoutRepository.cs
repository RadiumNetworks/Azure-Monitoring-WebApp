using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MonitoringApp;

public sealed class InboxLayoutRepository(
    IDbContextFactory<AlertDbContext> contextFactory,
    ILogger<InboxLayoutRepository>? logger = null,
    IdentityDirectoryOptions? directoryOptions = null)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ILogger<InboxLayoutRepository> logger = logger ?? NullLogger<InboxLayoutRepository>.Instance;

    public Task<InboxDashboardDocument?> LoadAsync(
        string ownerKey,
        CancellationToken cancellationToken = default) =>
        LoadCoreAsync(ownerKey, required: false, cancellationToken);

    /// <summary>
    /// Returns null only when no saved layout exists; invalid stored documents and database failures propagate.
    /// </summary>
    public Task<InboxDashboardDocument?> LoadRequiredAsync(
        string ownerKey,
        CancellationToken cancellationToken = default) =>
        LoadCoreAsync(ownerKey, required: true, cancellationToken);

    private async Task<InboxDashboardDocument?> LoadCoreAsync(
        string ownerKey,
        bool required,
        CancellationToken cancellationToken)
    {
        ownerKey = DashboardOwnerKeys.Normalize(ownerKey, "layout");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await IdentityOwnerGuard.ValidateAsync(db, ownerKey, directoryOptions, cancellationToken);
        var json = await db.InboxLayouts.AsNoTracking()
            .Where(item => item.OwnerKey == ownerKey)
            .Select(item => item.DocumentJson)
            .SingleOrDefaultAsync(cancellationToken);

        if (json is null)
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<InboxDashboardDocument>(json, JsonOptions);
            if (document is null)
            {
                logger.LogWarning(required
                    ? "Stored inbox layout contains JSON null."
                    : "Stored inbox layout contains JSON null. Using the default workspace.");
                if (required)
                    throw new InvalidOperationException("The saved inbox layout document is null.");
            }
            return document;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, required
                ? "Stored inbox layout contains invalid JSON."
                : "Stored inbox layout contains invalid JSON. Using the default workspace.");
            if (required)
                throw;
            return null;
        }
    }

    public async Task<InboxLayoutSaveResult> SaveAsync(
        string ownerKey,
        InboxDashboardDocument document,
        CancellationToken cancellationToken = default)
    {
        ownerKey = DashboardOwnerKeys.Normalize(ownerKey, "layout");
        if (directoryOptions?.Enabled == true)
            return await DatabaseTransactions.ExecuteAsync(contextFactory,
                context => SaveCoreAsync(context, ownerKey, document, cancellationToken),
                cancellationToken, System.Data.IsolationLevel.Serializable);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await SaveCoreAsync(db, ownerKey, document, cancellationToken);
    }

    private async Task<InboxLayoutSaveResult> SaveCoreAsync(AlertDbContext db, string ownerKey,
        InboxDashboardDocument document, CancellationToken cancellationToken)
    {
        await IdentityOwnerGuard.ValidateAsync(db, ownerKey, directoryOptions, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var entity = await db.InboxLayouts.SingleOrDefaultAsync(
            item => item.OwnerKey == ownerKey,
            cancellationToken);

        if (entity is null)
        {
            entity = new InboxLayout
            {
                OwnerKey = ownerKey,
                CreatedAt = now,
                Revision = 1
            };
            db.InboxLayouts.Add(entity);
        }
        else
        {
            entity.Revision++;
        }

        entity.Name = string.IsNullOrWhiteSpace(document.Name) ? "Alert operations workspace" : document.Name.Trim();
        entity.DocumentJson = JsonSerializer.Serialize(document, JsonOptions);
        entity.DocumentVersion = document.Version;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new InboxLayoutSaveResult(entity.Revision, entity.UpdatedAt);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
