using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class ProvisioningPersistence
{
    public static async Task RequireSourceAsync(AlertDbContext context, ProvisioningSource source, CancellationToken token)
    {
        var connection = await context.ScimConnections.SingleOrDefaultAsync(row => row.Id == source.ConnectionId, token)
            ?? throw new ProvisioningException(ProvisioningError.SourceMismatch, "The provisioning source is unavailable.");
        ValidateSource(connection, source);
    }
    public static void ValidateSource(ScimConnection connection, ProvisioningSource source)
    {
        if (connection.TenantId != source.TenantId)
            throw new ProvisioningException(ProvisioningError.SourceMismatch, "The provisioning source does not match the configured tenant.");
        if (!connection.IsEnabled)
            throw new ProvisioningException(ProvisioningError.Disabled, "The provisioning source is disabled.");
    }
    public static async Task<T> ExecuteAsync<T>(IDbContextFactory<AlertDbContext> factory, ILogger logger,
        Func<AlertDbContext, Task<T>> operation, CancellationToken token, Func<ProvisioningException>? conflict = null)
    {
        try { return await DatabaseTransactions.ExecuteAsync(factory, operation, token, IsolationLevel.Serializable); }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(exception, "SCIM persistence revision conflict.");
            throw new ProvisioningException(ProvisioningError.Version, "The resource changed. Read it again before retrying.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            logger.LogWarning(exception, "SCIM persistence uniqueness conflict.");
            throw conflict?.Invoke() ?? new ProvisioningException(ProvisioningError.Conflict, "A resource with this stable identity already exists.");
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or DbUpdateException)
        {
            logger.LogError(exception, "SCIM persistence failed; the operation was not confirmed.");
            throw new ProvisioningException(ProvisioningError.Unavailable, "Provisioning storage is unavailable. Retry later.", exception);
        }
    }
}
