using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.RegularExpressions;

namespace MonitoringApp.Tests;

public sealed class EFCoreInfrastructureTests
{
    private const string ConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=InfrastructureModelOnly;Integrated Security=True;TrustServerCertificate=True";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingConnectionFailsBeforeServiceRegistration(string? connectionString)
    {
        var configuration = DatabaseConfigurationStatus.Parse(connectionString, requireManagedIdentity: false);
        Assert.False(configuration.IsValid);
        Assert.Equal("Connection string AlertsDatabase is missing.", configuration.Error);
        Assert.Throws<InvalidOperationException>(() => configuration.RequireConnectionString());
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddAlertDatabase(configuration));
        Assert.Empty(services);
    }

    [Fact]
    public void MalformedConnectionIsReportedExplicitly()
    {
        var configuration = DatabaseConfigurationStatus.Parse("not a connection string", requireManagedIdentity: false);
        Assert.False(configuration.IsValid);
        Assert.StartsWith("Connection string AlertsDatabase is malformed:", configuration.Error);
        Assert.Contains("A valid AlertsDatabase connection is required",
            Assert.Throws<InvalidOperationException>(() => configuration.RequireConnectionString()).Message);
    }

    [Fact]
    public void DevelopmentAcceptsIntegratedSecurityButProductionRequiresUserAssignedManagedIdentity()
    {
        var development = DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false);
        Assert.True(development.IsValid);
        Assert.Null(development.Error);
        Assert.Equal("InfrastructureModelOnly", development.Connection.InitialCatalog);
        Assert.Equal(development.Connection.DataSource, development.Server);
        Assert.Equal(development.Connection.InitialCatalog, development.Database);
        Assert.Equal(development.Connection.Authentication.ToString(), development.Authentication);
        Assert.NotEmpty(development.RequireConnectionString());

        var production = DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: true);
        Assert.False(production.IsValid);
        Assert.Contains("Production requires", production.Error);
        Assert.Throws<InvalidOperationException>(() => production.RequireConnectionString());
    }

    [Theory]
    [InlineData("Server=example.database.windows.net;Database=example;Authentication=Active Directory Managed Identity", false)]
    [InlineData("Server=example.database.windows.net;Database=example;Authentication=Active Directory Managed Identity;User Id=11111111-1111-1111-1111-111111111111", true)]
    public void ProductionChecksManagedIdentityClientId(string connectionString, bool valid)
    {
        var configuration = DatabaseConfigurationStatus.Parse(connectionString, requireManagedIdentity: true);
        Assert.Equal(valid, configuration.IsValid);
    }

    [Fact]
    public async Task RuntimeRegistrationPreservesRetriesInterceptorsAndIndependentContexts()
    {
        var configuration = DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddAlertDatabase(configuration)
            .BuildServiceProvider(validateScopes: true);
        var factory = services.GetRequiredService<IDbContextFactory<AlertDbContext>>();
        await using var first = await factory.CreateDbContextAsync();
        await using var second = await factory.CreateDbContextAsync();
        Assert.NotSame(first, second);
        Assert.Same(configuration, services.GetRequiredService<DatabaseConfigurationStatus>());
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", first.Database.ProviderName);
        Assert.True(first.Database.CreateExecutionStrategy().RetriesOnFailure);
        var options = first.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!;
        Assert.Contains(services.GetRequiredService<SqlCommandFailureLoggingInterceptor>(), options.Interceptors!);
        Assert.Contains(services.GetRequiredService<SqlConnectionFailureLoggingInterceptor>(), options.Interceptors!);
        Assert.Single(services.GetServices<IHostedService>().OfType<DatabaseStartupCheck>());
    }

    [Fact]
    public void QueryPersistenceServicesAreRegisteredOnceByTheDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        foreach (var type in new[] { typeof(AnalysisQueryRegistry), typeof(AnalysisQueryCategoryService), typeof(AnalysisQueryExecutionService) })
        {
            var registration = Assert.Single(registrations, descriptor => descriptor.ServiceType == type);
            Assert.Equal(type, registration.ImplementationType);
            Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        }
    }

    [Fact]
    public void DashboardPersistenceServicesRetainScopedLifetimeInDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        foreach (var type in new[] { typeof(InboxLayoutRepository), typeof(AnalysisDashboardRepository), typeof(AnalysisDashboardPublishing) })
        {
            var registration = Assert.Single(registrations, descriptor => descriptor.ServiceType == type);
            Assert.Equal(type, registration.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        }
    }

    [Fact]
    public void AlertFacadeRetainsSingletonRegistrationInDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        var registration = Assert.Single(registrations, descriptor => descriptor.ServiceType == typeof(AlertStore));
        Assert.Equal(typeof(AlertStore), registration.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
    }

    [Fact]
    public void RcaPersistenceRetainsScopedEditingAndSingletonReconciliation()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        var editing = Assert.Single(registrations, descriptor => descriptor.ServiceType == typeof(RcaService));
        Assert.Equal(typeof(RcaService), editing.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, editing.Lifetime);
        var reconciliation = Assert.Single(registrations,
            descriptor => descriptor.ServiceType == typeof(RcaReconciliationService));
        Assert.Equal(typeof(RcaReconciliationService), reconciliation.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, reconciliation.Lifetime);
    }

    [Fact]
    public void ScheduledQueriesRetainSingletonRegistrationInDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        var registration = Assert.Single(registrations,
            descriptor => descriptor.ServiceType == typeof(ScheduledQueryService));
        Assert.Equal(typeof(ScheduledQueryService), registration.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
    }

    [Fact]
    public void AgentAnalysisStoresRetainSingletonRegistrationInDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        foreach (var type in new[] { typeof(AgentAnalysisJobStore), typeof(AgentAnalysisReviewStore) })
        {
            var registration = Assert.Single(registrations, descriptor => descriptor.ServiceType == type);
            Assert.Equal(type, registration.ImplementationType);
            Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        }
    }

    [Fact]
    public void SqlAuthenticationRetainsSingletonRegistrationInDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        var registration = Assert.Single(registrations,
            descriptor => descriptor.ServiceType == typeof(SqlAuthenticationService));
        Assert.Equal(typeof(SqlAuthenticationService), registration.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
    }

    [Fact]
    public void LogbookRetainsSingletonRegistrationInDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        var registration = Assert.Single(registrations, descriptor => descriptor.ServiceType == typeof(LogbookStore));
        Assert.Equal(typeof(LogbookStore), registration.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
    }

    [Fact]
    public void MaintenanceServicesRetainScopedLifetimeInDatabaseCore()
    {
        var registrations = new ServiceCollection()
            .AddAlertDatabase(DatabaseConfigurationStatus.Parse(ConnectionString, requireManagedIdentity: false));
        foreach (var type in new[] { typeof(DatabaseResetService), typeof(LegacyAlertResolutionService) })
        {
            var registration = Assert.Single(registrations, descriptor => descriptor.ServiceType == type);
            Assert.Equal(type, registration.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        }
    }

    [Fact]
    public void SharedSqlServerOptionsPreserveModelAndMigrationDiscoveryWithoutConnecting()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>();
        DatabaseOptions.ConfigureSqlServer(options, ConnectionString);
        using var context = new AlertDbContext(options.Options);
        Assert.True(context.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains("20261102131756_AddScheduledQueryWorkspaceScope", context.Database.GetMigrations());
        Assert.NotEmpty(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql")));
    }

    [Fact]
    public void AllMigrationsGenerateIdempotentUpgradeAndDowngradeScriptsWithoutConnecting()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>();
        DatabaseOptions.ConfigureSqlServer(options, ConnectionString);
        using var context = new AlertDbContext(options.Options);
        var migrations = context.Database.GetMigrations().ToArray();
        var migrator = context.GetService<IMigrator>();
        var upgrade = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        var downgrade = migrator.GenerateScript(
            fromMigration: migrations[^1],
            toMigration: Migration.InitialDatabase,
            options: MigrationsSqlGenerationOptions.Idempotent);

        foreach (var migration in migrations)
        {
            Assert.Contains(migration, upgrade);
            Assert.Contains(migration, downgrade);
        }
        Assert.Contains("CREATE TABLE [Settings]", upgrade);
        Assert.Contains("DROP TABLE [Settings]", downgrade);
    }

    [Fact]
    public void ProvisioningTemplateRecordsExactlyTheDiscoverableMigrations()
    {
        var options = new DbContextOptionsBuilder<AlertDbContext>();
        DatabaseOptions.ConfigureSqlServer(options, ConnectionString);
        using var context = new AlertDbContext(options.Options);
        var script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        var recorded = Regex.Matches(script, @"N'(?<id>\d{14}_[^']+)'")
            .Select(match => match.Groups["id"].Value)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal);

        Assert.Equal(context.Database.GetMigrations().OrderBy(id => id, StringComparer.Ordinal), recorded);
    }

    [Theory]
    [InlineData("CreateMonitoringDatabase.template.sql")]
    [InlineData("DeleteAlertsAndRcas.sql")]
    [InlineData("ResetMonitoringData.sql")]
    [InlineData("Set-SqlAuthenticationUser.ps1")]
    public void DeploymentScriptsRetainFlatDatabasePackagePaths(string name)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Database", name)),
            $"Deployment script missing from Database/{name}.");
        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Services", "EFCore", "Scripts")));
    }
}
