using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MonitoringApp.Components.Pages;
using MonitoringApp.Migrations;

namespace MonitoringApp.Tests;

public sealed class DashboardFilterOptionsTests
{
    private static CrossTenantLogAnalyticsOptions Workspaces => new()
    {
        Workspaces =
        [
            new() { Key = "workspace-b", DisplayName = "Zulu", Domains = ["beta.example"] },
            new() { Key = "workspace-a", DisplayName = "Alpha", Domains = ["alpha.example"] }
        ]
    };

    [Theory]
    [InlineData(null, "workspace-a")]
    [InlineData("", "")]
    [InlineData(" WORKSPACE-B ", "workspace-b")]
    public void ResolvesFirstWorkspaceExplicitWorkspaceAndAllWorkspaces(string? workspace, string expected)
    {
        var defaults = new DashboardFilterOptions { Workspace = workspace };
        Assert.Empty(defaults.Validate(Workspaces));
        Assert.Equal(expected, defaults.ResolveWorkspace(Workspaces));
        var json = JsonSerializer.Serialize(defaults);
        Assert.Equal(expected, DatabaseSettingsLoader.Deserialize<DashboardFilterOptions>(
            DatabaseSettingsLoader.DashboardFilters, json).ResolveWorkspace(Workspaces));
    }

    [Fact]
    public void NoConfiguredWorkspacesKeepsAllWorkspacesWithoutInventingAWorkspace()
    {
        Assert.Equal("", new DashboardFilterOptions().ResolveWorkspace(new()));
        Assert.Equal("", new DashboardFilterOptions { Workspace = "" }.ResolveWorkspace(new()));
    }

    [Theory]
    [InlineData("""{"Workspace":"missing"}""")]
    [InlineData("""{"Workspace":" "}""")]
    [InlineData("""{"Domain":" "}""")]
    [InlineData("""{"Domain":"."}""")]
    [InlineData("""{"Site":"North\nSouth"}""")]
    [InlineData("""{"Role":"\t"}""")]
    [InlineData("""{"Workspace":"workspace-a","Domain":"beta.example"}""")]
    public void RejectsUnknownWorkspacesInvalidValuesAndCrossWorkspaceDomains(string json)
    {
        var defaults = DatabaseSettingsLoader.Deserialize<DashboardFilterOptions>(DatabaseSettingsLoader.DashboardFilters, json);
        Assert.NotEmpty(defaults.Validate(Workspaces));
        Assert.Throws<InvalidOperationException>(() => defaults.ResolveWorkspace(Workspaces));
    }

    [Fact]
    public void AcceptsInventorySiteRoleAndSubscriptionRoutedDomainWithoutRequiringStaticDomainConfiguration()
    {
        var defaults = new DashboardFilterOptions
        {
            Workspace = "workspace-b", Domain = "inventory.example", Site = "Berlin", Role = "Domain Controller"
        };
        Assert.Empty(defaults.Validate(Workspaces));
        Assert.NotEmpty(new DashboardFilterOptions { Role = new string('x', 257) }.Validate(Workspaces));
    }

    [Theory]
    [InlineData("""{"Workspce":"workspace-a"}""")]
    [InlineData("""{"Site":42}""")]
    [InlineData("null")]
    [InlineData("{")]
    public void RejectsMalformedOrMisspelledSettingsRatherThanSilentlyUsingDefaults(string json)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseSettingsLoader.Deserialize<DashboardFilterOptions>(DatabaseSettingsLoader.DashboardFilters, json));
    }

    [Fact]
    public void MigrationAndProvisioningSeedTheSameBackwardCompatibleDefaultsWithoutChangingTheModel()
    {
        var migration = new AddDashboardFilterSetting();
        var operation = Assert.IsType<SqlOperation>(Assert.Single(migration.UpOperations));
        Assert.Contains("IF NOT EXISTS", operation.Sql);
        Assert.DoesNotContain("UPDATE ", operation.Sql);
        var json = operation.Sql[operation.Sql.IndexOf('{')..(operation.Sql.LastIndexOf('}') + 1)];
        var defaults = DatabaseSettingsLoader.Deserialize<DashboardFilterOptions>(DatabaseSettingsLoader.DashboardFilters, json);
        Assert.Null(defaults.Workspace);
        Assert.Null(defaults.Domain);
        Assert.Null(defaults.Site);
        Assert.Null(defaults.Role);
        Assert.Equal("workspace-a", defaults.ResolveWorkspace(Workspaces));
        Assert.Contains(DatabaseSettingsLoader.DashboardFilters, DatabaseSettingsLoader.RequiredNames);
        var down = Assert.IsType<DeleteDataOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal("DashboardFilters", down.KeyValues[0, 0]);

        using var context = new AlertDbContext(new DbContextOptionsBuilder<AlertDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;").Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261022140500_AddRcaDisplaySetting", "20261027082000_AddDashboardFilterSetting");
        Assert.Contains(json, script);
        Assert.False(context.Database.HasPendingModelChanges());
        var provisioning = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "CreateMonitoringDatabase.template.sql"));
        Assert.Contains(json, provisioning);
        Assert.Contains("20261027082000_AddDashboardFilterSetting", provisioning);
    }

    [Fact]
    public async Task AdminSettingsLoadSaveAndResetPersistAllFourFiltersAndRejectInvalidUpdates()
    {
        await using var fixture = await SettingsFixture.CreateAsync();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new AdminSettings();
        typeof(AdminSettings).GetProperty("DbContextFactory", flags)!.SetValue(page, fixture.Factory);
        typeof(AdminSettings).GetProperty("LogAnalyticsOptions", flags)!.SetValue(page, Workspaces);
        var field = typeof(AdminSettings).GetField("dashboardFiltersJson", flags)!;
        var error = typeof(AdminSettings).GetField("errorMessage", flags)!;

        await InvokeAsync("LoadAsync");
        Assert.Null(error.GetValue(page));
        Assert.Contains("Workspace", Assert.IsType<string>(field.GetValue(page)));
        field.SetValue(page, """{"Workspace":"workspace-b","Domain":"beta.example","Site":"South","Role":"Domain Controller"}""");
        await InvokeAsync("SaveAsync");
        Assert.Null(error.GetValue(page));
        Assert.Contains("Restart", Assert.IsType<string>(typeof(AdminSettings).GetField("statusMessage", flags)!.GetValue(page)));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var stored = await context.Settings.SingleAsync(setting => setting.Name == DatabaseSettingsLoader.DashboardFilters);
            var defaults = DatabaseSettingsLoader.Deserialize<DashboardFilterOptions>(stored.Name, stored.JsonValue);
            Assert.Equal("workspace-b", defaults.Workspace);
            Assert.Equal("beta.example", defaults.Domain);
            Assert.Equal("South", defaults.Site);
            Assert.Equal("Domain Controller", defaults.Role);
        }

        Dictionary<string, string> before;
        await using (var context = fixture.Factory.CreateDbContext())
            before = await context.Settings.ToDictionaryAsync(setting => setting.Name, setting => setting.JsonValue);
        field.SetValue(page, """{"Workspace":"unknown"}""");
        await InvokeAsync("SaveAsync");
        Assert.Contains("Workspace must match", Assert.IsType<string>(error.GetValue(page)));
        await using (var context = fixture.Factory.CreateDbContext())
        {
            var after = await context.Settings.ToDictionaryAsync(setting => setting.Name, setting => setting.JsonValue);
            Assert.Equal(before.OrderBy(item => item.Key), after.OrderBy(item => item.Key));
        }
        await InvokeAsync("LoadAsync");
        Assert.Null(error.GetValue(page));
        Assert.Contains("workspace-b", Assert.IsType<string>(field.GetValue(page)));

        async Task InvokeAsync(string name) =>
            await (Task)typeof(AdminSettings).GetMethod(name, flags)!.Invoke(page, [])!;
    }

    private sealed class SettingsFixture(SqliteConnection connection, ContextFactory factory) : IAsyncDisposable
    {
        public ContextFactory Factory { get; } = factory;

        public static async Task<SettingsFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new ContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = factory.CreateDbContext();
            var statement = context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Single(statement => statement.TrimStart().StartsWith("CREATE TABLE \"Settings\" ", StringComparison.Ordinal));
            await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            var testCases = TestCaseLoader.Load<DatabaseSettingsTestCases>("database-settings.json");
            foreach (var name in DatabaseSettingsLoader.RequiredNames)
            {
                var json = name switch
                {
                    DatabaseSettingsLoader.Authentication => JsonSerializer.Serialize(new ApplicationAuthenticationOptions
                    {
                        Type = ApplicationAuthenticationOptions.Open
                    }),
                    DatabaseSettingsLoader.AlertHistory => JsonSerializer.Serialize(new AlertHistoryOptions()),
                    DatabaseSettingsLoader.AlertGraph => testCases.AlertGraph.ToJsonString(),
                    DatabaseSettingsLoader.AlertSeverityDisplay => testCases.SeverityDisplay.ToJsonString(),
                    DatabaseSettingsLoader.InboxTargetOutputDisplay => JsonSerializer.Serialize(new InboxTargetOutputDisplayOptions()),
                    DatabaseSettingsLoader.RcaDisplay => JsonSerializer.Serialize(new RcaDisplayOptions()),
                    DatabaseSettingsLoader.DashboardFilters => JsonSerializer.Serialize(new DashboardFilterOptions()),
                    DatabaseSettingsLoader.AgentAnalysisContext => JsonSerializer.Serialize(new AgentAnalysisContextOptions()),
                    DatabaseSettingsLoader.AgentAnalysisExecution => JsonSerializer.Serialize(new AgentAnalysisExecutionOptions()),
                    _ => throw new InvalidOperationException($"No default test value for '{name}'.")
                };
                context.Settings.Add(new() { Name = name, JsonValue = json });
            }
            await context.SaveChangesAsync();
            return new(connection, factory);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class ContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }
}
