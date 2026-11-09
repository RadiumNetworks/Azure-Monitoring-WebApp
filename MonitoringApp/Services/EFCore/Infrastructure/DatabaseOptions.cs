using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public static class DatabaseOptions
{
    public static DbContextOptionsBuilder ConfigureSqlServer(
        DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlServer(connectionString, sqlServerOptions => sqlServerOptions.EnableRetryOnFailure());
}
