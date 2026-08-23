using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed record DatabaseConfigurationStatus(
    bool IsValid,
    string? Error,
    SqlConnectionStringBuilder Connection);

public sealed class DatabaseStartupCheck(
    IDbContextFactory<AlertDbContext> contextFactory,
    DatabaseConfigurationStatus configuration,
    ILogger<DatabaseStartupCheck> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.IsValid)
        {
            logger.LogCritical(
                "Azure SQL configuration is invalid: {ConfigurationError} Configure either the App Service setting ConnectionStrings__AlertsDatabase or the App Service connection string AlertsDatabase.",
                configuration.Error);
            return;
        }

        var connection = configuration.Connection;
        logger.LogInformation(
            "Checking Azure SQL connectivity. Server={Server}, Database={Database}, Authentication={Authentication}, ManagedIdentityClientId={ManagedIdentityClientId}",
            connection.DataSource,
            connection.InitialCatalog,
            connection.Authentication,
            MaskClientId(connection.UserID));

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(stoppingToken);
            if (await context.Database.CanConnectAsync(stoppingToken))
            {
                logger.LogInformation(
                    "Azure SQL connectivity check succeeded. Server={Server}, Database={Database}",
                    connection.DataSource,
                    connection.InitialCatalog);
            }
            else
            {
                logger.LogError(
                    "Azure SQL connectivity check failed without an exception. Verify networking, the managed identity assignment, and database permissions.");
            }
        }
        catch (SqlException exception)
        {
            logger.LogError(
                exception,
                "Azure SQL connectivity check failed with SQL error {SqlErrorNumber}, State={SqlState}, Class={SqlErrorClass}, ClientConnectionId={ClientConnectionId}. Server={Server}, Database={Database}. Verify firewall/private endpoint and that the database user maps to the configured user-assigned managed identity.",
                exception.Number,
                exception.State,
                exception.Class,
                exception.ClientConnectionId,
                connection.DataSource,
                connection.InitialCatalog);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Azure SQL connectivity check failed. Server={Server}, Database={Database}. Verify that the user-assigned managed identity is attached to the App Service, User Id contains its client ID, and the database contains a user for that identity with db_datareader and db_datawriter roles.",
                connection.DataSource,
                connection.InitialCatalog);
        }
    }

    private static string MaskClientId(string clientId) => clientId.Length < 8
        ? "(missing)"
        : $"{clientId[..4]}...{clientId[^4..]}";
}