using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MonitoringApp;

/// <summary>
/// Allows EF tooling to apply migrations before startup-critical database settings exist.
/// </summary>
public sealed class AlertDbContextFactory : IDesignTimeDbContextFactory<AlertDbContext>
{
    public AlertDbContext CreateDbContext(string[] args)
    {
        var contentRoot = FindContentRoot();
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("AlertsDatabase")
            ?? throw new InvalidOperationException("Connection string AlertsDatabase is missing.");

        var options = new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer(connectionString, sqlServerOptions => sqlServerOptions.EnableRetryOnFailure())
            .Options;
        return new AlertDbContext(options);
    }

    private static string FindContentRoot()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(currentDirectory, "appsettings.json")))
        {
            return currentDirectory;
        }

        var projectDirectory = Path.Combine(currentDirectory, "MonitoringApp");
        if (File.Exists(Path.Combine(projectDirectory, "appsettings.json")))
        {
            return projectDirectory;
        }

        throw new InvalidOperationException("Could not locate the MonitoringApp content root.");
    }
}
