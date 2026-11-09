using Microsoft.Data.SqlClient;

namespace MonitoringApp;

public sealed record DatabaseConfigurationStatus(
    bool IsValid,
    string? Error,
    SqlConnectionStringBuilder Connection)
{
    public string Server => Connection.DataSource;
    public string Database => Connection.InitialCatalog;
    public string Authentication => Connection.Authentication.ToString();

    public static DatabaseConfigurationStatus Parse(string? connectionString, bool requireManagedIdentity)
    {
        string? error = null;
        SqlConnectionStringBuilder connection;
        try
        {
            connection = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            error = $"Connection string AlertsDatabase is malformed: {exception.Message}";
            connection = new SqlConnectionStringBuilder();
        }

        if (string.IsNullOrWhiteSpace(connectionString))
            error = "Connection string AlertsDatabase is missing.";
        else if (requireManagedIdentity &&
            (connection.Authentication != SqlAuthenticationMethod.ActiveDirectoryManagedIdentity ||
             string.IsNullOrWhiteSpace(connection.UserID)))
            error = "Production requires 'Authentication=Active Directory Managed Identity' and " +
                "'User Id=<managed-identity-client-id>'. User Id must be the client ID, not the object ID.";

        return new(error is null, error, connection);
    }

    public string RequireConnectionString()
    {
        if (!IsValid)
            throw new InvalidOperationException(
                $"A valid AlertsDatabase connection is required to load application settings: {Error}");
        return Connection.ConnectionString;
    }
}
