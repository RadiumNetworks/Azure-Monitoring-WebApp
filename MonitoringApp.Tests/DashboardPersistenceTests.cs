using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MonitoringApp.Tests;

public sealed class DashboardPersistenceTests
{
    [Theory]
    [InlineData("", "open-access")]
    [InlineData("  ", "open-access")]
    [InlineData(" owner ", "owner")]
    public async Task RepositoriesShareOwnerNormalizationWithoutSharingDocuments(string owner, string normalized)
    {
        await using var database = await Database.CreateAsync();
        var inbox = new InboxLayoutRepository(database);
        var analysis = new AnalysisDashboardRepository(database);
        await inbox.SaveAsync(owner, new() { Name = "Inbox" });
        await analysis.SaveAsync(owner, AnalysisDashboardCollection.Create(new() { Name = "Analysis" }), 0);
        Assert.Equal("Inbox", (await inbox.LoadAsync(normalized))!.Name);
        Assert.Equal("Analysis", (await analysis.LoadAsync(normalized)).Collection!.Active.Document.Name);
        Assert.Null(await inbox.LoadAsync("other-owner"));
        Assert.Equal((null, 0), await analysis.LoadAsync("other-owner"));
        await using var context = database.CreateDbContext();
        Assert.Equal(normalized, (await context.InboxLayouts.SingleAsync()).OwnerKey);
        Assert.Equal(normalized, (await context.AnalysisDashboards.SingleAsync()).OwnerKey);
    }

    [Fact]
    public async Task RepositoriesRejectOversizedOwnerKeysBeforeWriting()
    {
        await using var database = await Database.CreateAsync();
        var owner = new string('x', 257);
        var inbox = new InboxLayoutRepository(database);
        var analysis = new AnalysisDashboardRepository(database);
        var loadInbox = await Assert.ThrowsAsync<ArgumentException>(() => inbox.LoadAsync(owner));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.LoadRequiredAsync(owner));
        var loadAnalysis = await Assert.ThrowsAsync<ArgumentException>(() => analysis.LoadAsync(owner));
        Assert.Equal("ownerKey", loadInbox.ParamName);
        Assert.Equal("ownerKey", loadAnalysis.ParamName);
        Assert.Contains("layout owner key", loadInbox.Message);
        Assert.Contains("dashboard owner key", loadAnalysis.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.SaveAsync(owner, new()));
        await Assert.ThrowsAsync<ArgumentException>(() => analysis.SaveAsync(owner, AnalysisDashboardCollection.Create(), 0));
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.InboxLayouts.ToArrayAsync());
        Assert.Empty(await verify.AnalysisDashboards.ToArrayAsync());
    }

    [Fact]
    public async Task MissingInboxLayoutUsesDefaultWithoutLoggingACorruptionWarning()
    {
        await using var database = await Database.CreateAsync();
        var logger = new LayoutLogger();
        Assert.Null(await new InboxLayoutRepository(database, logger).LoadAsync("owner"));
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task InvalidInboxJsonRetainsDefaultFallbackLogsWarningAndDoesNotOverwriteStoredLayout(string json)
    {
        await using var database = await Database.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            context.InboxLayouts.Add(new() { OwnerKey = "owner", DocumentJson = json, Revision = 7 });
            await context.SaveChangesAsync();
        }
        var logger = new LayoutLogger();
        Assert.Null(await new InboxLayoutRepository(database, logger).LoadAsync("owner"));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("default workspace", warning.Message);
        if (json != "null")
            Assert.IsAssignableFrom<JsonException>(warning.Exception);
        await using var verify = database.CreateDbContext();
        var stored = await verify.InboxLayouts.SingleAsync();
        Assert.Equal(json, stored.DocumentJson);
        Assert.Equal(7, stored.Revision);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("""{"dashboards":[]}""")]
    public async Task InvalidAnalysisJsonRemainsAnExplicitErrorAndDoesNotOverwriteStoredCollection(string json)
    {
        await using var database = await Database.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            context.AnalysisDashboards.Add(new() { OwnerKey = "owner", DocumentJson = json, Revision = 7 });
            await context.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AnalysisDashboardRepository(database).LoadAsync("owner"));
        if (json == "{")
        {
            Assert.Equal("The saved analysis dashboard document is invalid.", error.Message);
            Assert.IsAssignableFrom<JsonException>(error.InnerException);
        }
        await using var verify = database.CreateDbContext();
        var stored = await verify.AnalysisDashboards.SingleAsync();
        Assert.Equal(json, stored.DocumentJson);
        Assert.Equal(7, stored.Revision);
    }

    [Fact]
    public async Task InboxRoundTripPreservesEnumFormatVersionAndMetadataWithoutUpgradingDocument()
    {
        await using var database = await Database.CreateAsync();
        var repository = new InboxLayoutRepository(database);
        var document = InboxDashboardCatalog.CreateDefault();
        document.Name = " Inbox ";
        document.Layout = InboxLayoutMode.Balanced;
        document.Version = 1;
        var first = await repository.SaveAsync("owner", document);
        var loaded = (await repository.LoadAsync("owner"))!;
        Assert.Equal(InboxLayoutMode.Balanced, loaded.Layout);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(document.Widgets.Count, loaded.Widgets.Count);
        var second = await repository.SaveAsync("owner", loaded);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        await using var context = database.CreateDbContext();
        var stored = await context.InboxLayouts.SingleAsync();
        Assert.Equal("Inbox", stored.Name);
        Assert.Equal(1, stored.DocumentVersion);
        Assert.Equal(first.UpdatedAt, stored.CreatedAt);
        Assert.Equal(second.UpdatedAt, stored.UpdatedAt);
        Assert.Contains("\"layout\":\"Balanced\"", stored.DocumentJson);
    }

    [Fact]
    public async Task InboxRetainsLatestWriteBehaviorWhileAnalysisRejectsStaleRevisions()
    {
        await using var database = await Database.CreateAsync();
        var inbox = new InboxLayoutRepository(database);
        await inbox.SaveAsync("owner", new() { Name = "First" });
        await inbox.SaveAsync("owner", new() { Name = "Second" });
        var overwritten = await inbox.SaveAsync("owner", new() { Name = "First" });
        Assert.Equal(3, overwritten.Revision);
        Assert.Equal("First", (await inbox.LoadAsync("owner"))!.Name);

        var analysis = new AnalysisDashboardRepository(database);
        var first = AnalysisDashboardCollection.Create(new() { Name = "First" });
        var second = AnalysisDashboardCollection.Create(new() { Name = "Second" });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => analysis.SaveAsync("owner", first, 1));
        await analysis.SaveAsync("owner", first, 0);
        await analysis.SaveAsync("owner", second, 1);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => analysis.SaveAsync("owner", first, 1));
        var current = await analysis.LoadAsync("owner");
        Assert.Equal(2, current.Revision);
        Assert.Equal("Second", current.Collection!.Active.Document.Name);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"layout":"Sensitive <invalid> &"}""")]
    public async Task RequiredInboxReadsPropagateCorruptionWithoutChangingLegacyFallbackOrStoredData(string json)
    {
        await using var database = await Database.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            context.InboxLayouts.Add(new() { OwnerKey = "owner", DocumentJson = json, Revision = 7, DocumentVersion = 1 });
            await context.SaveChangesAsync();
        }
        var logger = new LayoutLogger();
        var repository = new InboxLayoutRepository(database, logger);
        Assert.Null(await repository.LoadAsync(" owner "));
        Assert.Contains("default workspace", Assert.Single(logger.Entries).Message);
        Exception failure = json == "null"
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => repository.LoadRequiredAsync(" owner "))
            : await Assert.ThrowsAsync<JsonException>(() => repository.LoadRequiredAsync(" owner "));
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(LogLevel.Warning, logger.Entries[1].Level);
        Assert.DoesNotContain("default workspace", logger.Entries[1].Message);
        if (json != "null") Assert.Same(failure, logger.Entries[1].Exception);
        await using var verification = database.CreateDbContext();
        var stored = await verification.InboxLayouts.SingleAsync();
        Assert.Equal(json, stored.DocumentJson);
        Assert.Equal(7, stored.Revision);
        Assert.Equal(1, stored.DocumentVersion);
    }

    [Fact]
    public async Task RequiredInboxReadsPreserveMissingValidAndDatabaseFailureContracts()
    {
        await using var database = await Database.CreateAsync();
        var logger = new LayoutLogger();
        var repository = new InboxLayoutRepository(database, logger);
        Assert.Null(await repository.LoadAsync("owner"));
        Assert.Null(await repository.LoadRequiredAsync("owner"));
        var document = InboxDashboardCatalog.CreateDefault();
        document.Name = "Original <workspace> &";
        document.Version = 1;
        document.Widgets[0].Title = "Original <widget> &";
        await repository.SaveAsync(" owner ", document);
        Assert.Equal(JsonSerializer.Serialize(await repository.LoadAsync("owner")),
            JsonSerializer.Serialize(await repository.LoadRequiredAsync(" owner ")));
        Assert.Equal(1, (await repository.LoadRequiredAsync("owner"))!.Version);
        Assert.Empty(logger.Entries);
        string original;
        await using (var context = database.CreateDbContext())
        {
            original = (await context.InboxLayouts.SingleAsync()).DocumentJson;
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"InboxLayouts\" RENAME TO \"UnavailableLayout\";");
        }
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => repository.LoadAsync("owner"));
            await Assert.ThrowsAsync<SqliteException>(() => repository.LoadRequiredAsync("owner"));
        }
        finally
        {
            await using var context = database.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableLayout\" RENAME TO \"InboxLayouts\";");
        }
        Assert.Equal(document.Name, (await repository.LoadRequiredAsync("owner"))!.Name);
        await using var verification = database.CreateDbContext();
        Assert.Equal(original, (await verification.InboxLayouts.SingleAsync()).DocumentJson);
    }

    private sealed class Database(SqliteConnection connection, DbContextOptions<AlertDbContext> options)
        : IDbContextFactory<AlertDbContext>, IAsyncDisposable
    {
        public AlertDbContext CreateDbContext() => new(options);

        public static async Task<Database> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var database = new Database(connection, new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
            await using var context = database.CreateDbContext();
            var tables = new[] { "InboxLayouts", "AnalysisDashboards", "AnalysisDashboardTemplates" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => tables.Any(name =>
                    statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal) ||
                    statement.Contains($" ON \"{name}\" ", StringComparison.Ordinal))))
                await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            return database;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class LayoutLogger : ILogger<InboxLayoutRepository>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
