using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class DatabaseSettingsServiceTests
{
    [Fact]
    public async Task LoadsAndSavesAllRequiredSettingsWithoutTouchingOtherSettingsOrUsers()
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        var original = await SeedAsync(database);
        var service = Service(database);
        Assert.Equal(original.OrderBy(pair => pair.Key), (await service.LoadAsync()).OrderBy(pair => pair.Key));
        var changed = new Dictionary<string, string>(original)
        {
            [DatabaseSettingsLoader.AlertHistory] = """{"Days":14}""",
            [DatabaseSettingsLoader.DashboardFilters] = """{"Workspace":"production","Site":"New site"}"""
        };
        var saved = await service.SaveAsync(changed);
        Assert.Equal(changed.OrderBy(pair => pair.Key), saved.OrderBy(pair => pair.Key));
        Assert.Equal(changed.OrderBy(pair => pair.Key), (await service.LoadAsync()).OrderBy(pair => pair.Key));
        await using var context = database.CreateDbContext();
        Assert.Equal("keep", (await context.Settings.SingleAsync(setting => setting.Name == "Unrelated")).JsonValue);
        Assert.Empty(await context.AuthenticationUsers.ToArrayAsync());
    }

    [Theory]
    [InlineData("Authentication", """{"Type":"unsafe"}""")]
    [InlineData("AlertHistory", """{"Days":0}""")]
    [InlineData("AlertGraph", "{}")]
    [InlineData("AlertSeverityDisplay", "{}")]
    [InlineData("InboxTargetOutputDisplay", """{"SuppressedColor":"unsafe"}""")]
    [InlineData("RcaDisplay", """{"ClosedColor":"unsafe"}""")]
    [InlineData("DashboardFilters", """{"Workspace":"unknown"}""")]
    [InlineData("AgentAnalysisContext", """{"InventoryCorrelationLayers":[]}""")]
    [InlineData("AgentAnalysisExecution", """{"Mode":"unsafe"}""")]
    [InlineData("AlertHistory", "{")]
    [InlineData("AlertHistory", "null")]
    public async Task InvalidInputNeverWritesAnySettings(string name, string json)
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        var original = await SeedAsync(database);
        var submitted = new Dictionary<string, string>(original)
        {
            [DatabaseSettingsLoader.AlertHistory] = """{"Days":14}""",
            [name] = json
        };
        var error = await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service(database).SaveAsync(submitted));
        Assert.Equal(json == "{" ? DatabaseSettingsValidationCode.JsonInvalid :
            json == "null" ? DatabaseSettingsValidationCode.JsonNull : DatabaseSettingsValidationCode.InvalidValues, error.Code);
        Assert.Equal(name, error.SettingName);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(original.OrderBy(pair => pair.Key), (await Service(database).LoadAsync()).OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task MissingStoredSettingsAreReportedByLoadAndSaveWithoutPartialUpdates()
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        var original = await SeedAsync(database);
        await using (var context = database.CreateDbContext())
            await context.Settings.Where(setting => setting.Name == DatabaseSettingsLoader.RcaDisplay).ExecuteDeleteAsync();
        var loadError = await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service(database).LoadAsync());
        Assert.Equal(DatabaseSettingsValidationCode.MissingSettings, loadError.Code);
        Assert.Equal("Required database settings are missing from dbo.Settings: RcaDisplay.", loadError.Message);
        var changed = new Dictionary<string, string>(original) { [DatabaseSettingsLoader.AlertHistory] = """{"Days":14}""" };
        Assert.Equal(DatabaseSettingsValidationCode.MissingSettings,
            (await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service(database).SaveAsync(changed))).Code);
        await using var verify = database.CreateDbContext();
        Assert.Equal(original[DatabaseSettingsLoader.AlertHistory],
            (await verify.Settings.SingleAsync(setting => setting.Name == DatabaseSettingsLoader.AlertHistory)).JsonValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlAuthenticationRequiresAnExistingUser(bool hasUser)
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        var submitted = await SeedAsync(database);
        submitted[DatabaseSettingsLoader.Authentication] = """{"Type":"sql"}""";
        if (hasUser)
        {
            await using var context = database.CreateDbContext();
            context.AuthenticationUsers.Add(new() { Username = "admin", PasswordHash = "unchanged", Role = SqlAuthenticationRoles.Admin });
            await context.SaveChangesAsync();
            await Service(database).SaveAsync(submitted);
            Assert.True(DatabaseSettingsLoader.ParseRequired(await Service(database).LoadAsync()).Authentication.IsSql);
            Assert.Equal("unchanged", (await context.AuthenticationUsers.SingleAsync()).PasswordHash);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service(database).SaveAsync(submitted));
            Assert.Equal(DatabaseSettingsValidationCode.SqlUserRequired, error.Code);
            Assert.Equal("Create at least one authentication user before enabling SQL authentication.", error.Message);
            Assert.True(DatabaseSettingsLoader.ParseRequired(await Service(database).LoadAsync()).Authentication.IsOpen);
        }
    }

    [Fact]
    public async Task EntraAuthenticationCanBeSavedWithoutSqlAccounts()
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        var submitted = await SeedAsync(database);
        submitted[DatabaseSettingsLoader.Authentication] = """{"Type":"entra"}""";
        await Service(database).SaveAsync(submitted);
        Assert.True(DatabaseSettingsLoader.ParseRequired(await Service(database).LoadAsync()).Authentication.IsEntra);
        await using var context = database.CreateDbContext();
        Assert.Empty(await context.AuthenticationUsers.ToArrayAsync());
    }

    [Fact]
    public async Task MissingOrUnknownSubmittedSettingsAreRejected()
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        var submitted = await SeedAsync(database);
        submitted.Remove(DatabaseSettingsLoader.RcaDisplay);
        Assert.Equal(DatabaseSettingsValidationCode.MissingSettings,
            (await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service(database).SaveAsync(submitted))).Code);
        submitted["Unknown"] = "{}";
        var error = await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service(database).SaveAsync(submitted));
        Assert.Equal(DatabaseSettingsValidationCode.UnknownSettings, error.Code);
        Assert.Equal("Unknown database settings: Unknown.", error.Message);
    }

    [Fact]
    public async Task FailureAfterSaveChangesRollsBackAllSettings()
    {
        var interceptor = new FailAfterSaveInterceptor();
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync(interceptor: interceptor);
        var original = await SeedAsync(database);
        var changed = new Dictionary<string, string>(original) { [DatabaseSettingsLoader.AlertHistory] = """{"Days":14}""" };
        interceptor.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(database).SaveAsync(changed));
        Assert.Equal(original.OrderBy(pair => pair.Key), (await Service(database).LoadAsync()).OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task CancellationDoesNotLoadOrSaveSettings()
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        var original = await SeedAsync(database);
        var token = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(database).LoadAsync(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(database).SaveAsync(original, token));
        Assert.Equal(original.OrderBy(pair => pair.Key), (await Service(database).LoadAsync()).OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task SettingsPageUsesServiceAndShowsValidationErrorsInsteadOfSuccess()
    {
        await using var database = await DatabaseTransactionsTests.TestDatabase.CreateAsync();
        await SeedAsync(database);
        var page = new AdminSettings();
        var services = new ServiceCollection().AddLogging();
        services.AddUiLocalization();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var text = scope.ServiceProvider.GetRequiredService<UiText>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(AdminSettings);
        type.GetProperty("SettingsService", flags)!.SetValue(page, Service(database));
        type.GetProperty("Text", flags)!.SetValue(page, text);
        type.GetProperty("Logger", flags)!.SetValue(page,
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AdminSettings>>());
        await (Task)type.GetMethod("LoadAsync", flags)!.Invoke(page, [])!;
        Assert.Null(type.GetField("errorMessage", flags)!.GetValue(page));
        type.GetField("alertHistoryJson", flags)!.SetValue(page, """{"Days":14}""");
        await (Task)type.GetMethod("SaveAsync", flags)!.Invoke(page, [])!;
        Assert.Null(type.GetField("errorMessage", flags)!.GetValue(page));
        Assert.Equal(text["AdminSettings.Saved"], (string)type.GetField("statusMessage", flags)!.GetValue(page)!);
        Assert.Equal(14, DatabaseSettingsLoader.ParseRequired(await Service(database).LoadAsync()).AlertHistory.Days);

        type.GetField("alertHistoryJson", flags)!.SetValue(page, """{"Days":0}""");
        await (Task)type.GetMethod("SaveAsync", flags)!.Invoke(page, [])!;
        Assert.Equal(text.Format("AdminSettings.Validation.InvalidValues", text["AdminSettings.AlertHistory"]),
            (string)type.GetField("errorMessage", flags)!.GetValue(page)!);
        Assert.Null(type.GetField("statusMessage", flags)!.GetValue(page));
        Assert.Equal(14, DatabaseSettingsLoader.ParseRequired(await Service(database).LoadAsync()).AlertHistory.Days);
    }

    private static DatabaseSettingsService Service(DatabaseTransactionsTests.TestDatabase database) =>
        new(database, new() { Workspaces = [new() { Key = "production" }] });

    private static async Task<Dictionary<string, string>> SeedAsync(DatabaseTransactionsTests.TestDatabase database)
    {
        var cases = TestCaseLoader.Load<DatabaseSettingsTestCases>("database-settings.json");
        var values = new Dictionary<string, string>
        {
            [DatabaseSettingsLoader.Authentication] = """{"Type":"open"}""",
            [DatabaseSettingsLoader.AlertHistory] = """{"Days":7}""",
            [DatabaseSettingsLoader.AlertGraph] = cases.AlertGraph.ToJsonString(),
            [DatabaseSettingsLoader.AlertSeverityDisplay] = cases.SeverityDisplay.ToJsonString(),
            [DatabaseSettingsLoader.InboxTargetOutputDisplay] = JsonSerializer.Serialize(new InboxTargetOutputDisplayOptions()),
            [DatabaseSettingsLoader.RcaDisplay] = JsonSerializer.Serialize(new RcaDisplayOptions()),
            [DatabaseSettingsLoader.DashboardFilters] = "{}",
            [DatabaseSettingsLoader.AgentAnalysisContext] = JsonSerializer.Serialize(new AgentAnalysisContextOptions()),
            [DatabaseSettingsLoader.AgentAnalysisExecution] = JsonSerializer.Serialize(new AgentAnalysisExecutionOptions())
        };
        await using var context = database.CreateDbContext();
        context.Settings.AddRange(values.Select(pair => new DatabaseSetting { Name = pair.Key, JsonValue = pair.Value }));
        context.Settings.Add(new() { Name = "Unrelated", JsonValue = "keep" });
        await context.SaveChangesAsync();
        return values;
    }

    private sealed class FailAfterSaveInterceptor : SaveChangesInterceptor
    {
        public bool Fail { get; set; }
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            Fail ? throw new InvalidOperationException("Failure before transaction commit.") : ValueTask.FromResult(result);
    }
}
