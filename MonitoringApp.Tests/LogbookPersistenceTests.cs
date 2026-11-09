using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MonitoringApp.Tests;

public sealed class LogbookPersistenceTests
{
    [Fact]
    public async Task AppendsTrimmedCommentWithServerAuditDataAndReturnsDetachedEntries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = DateTimeOffset.UtcNow;
        var entry = await fixture.Store.AddAsync(" operator ", " First comment ");
        var after = DateTimeOffset.UtcNow;
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.InRange(entry.CreatedAt, before, after);
        Assert.Equal("operator", entry.User);
        Assert.Equal("First comment", entry.Comment);
        Assert.Null(entry.AlertRecordId);
        entry.Comment = "Changed returned object";
        var stored = Assert.Single(await fixture.Store.GetAllAsync());
        Assert.Equal(entry.Id, stored.Id);
        Assert.Equal("First comment", stored.Comment);
        stored.User = "Changed loaded object";
        var reloaded = Assert.Single(await fixture.Store.GetAllAsync());
        Assert.Equal("operator", reloaded.User);
        Assert.Equal("First comment", reloaded.Comment);
    }

    [Theory]
    [InlineData("user-empty", LogbookValidationCode.UserRequired, "A logged-on user is required to write a logbook entry.")]
    [InlineData("user-long", LogbookValidationCode.UserLength, "The username may contain at most 256 characters.")]
    [InlineData("comment-empty", LogbookValidationCode.CommentRequired, "Enter a comment before saving.")]
    [InlineData("comment-long", LogbookValidationCode.CommentLength, "The comment may contain at most 4000 characters.")]
    public async Task RejectsInvalidInputBeforeCreatingContextAndPreservesEarlierEntries(
        string kind, LogbookValidationCode code, string message)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = await fixture.Store.AddAsync("operator", "Keep this entry");
        var user = kind == "user-empty" ? " " : kind == "user-long" ? new string('u', 257) : "operator";
        var comment = kind == "comment-empty" ? " " : kind == "comment-long" ? new string('c', 4001) : "Comment";
        var created = fixture.Factory.Created;
        var error = await Assert.ThrowsAsync<LogbookValidationException>(() => fixture.Store.AddAsync(user, comment));
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
        Assert.Equal(created, fixture.Factory.Created);
        Assert.Equal(original.Id, Assert.Single(await fixture.Store.GetAllAsync()).Id);
    }

    [Fact]
    public async Task AcceptsExactTrimmedLengthLimitsWithoutTruncatingContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = new string('u', 256);
        var comment = new string('c', 4000);
        await fixture.Store.AddAsync(" " + user + " ", " " + comment + " ");
        var stored = Assert.Single(await fixture.Store.GetAllAsync());
        Assert.Equal(user, stored.User);
        Assert.Equal(comment, stored.Comment);
    }

    [Fact]
    public async Task ListsNewestFirstWithDescendingIdTieBreaker()
    {
        await using var fixture = await Fixture.CreateAsync();
        var time = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var latest = Guid.Parse("00000000-0000-0000-0000-000000000003");
        await using (var context = fixture.Factory.CreateDbContext())
        {
            context.LogbookEntries.AddRange(
                new() { Id = first, CreatedAt = time, User = "operator", Comment = "First" },
                new() { Id = second, CreatedAt = time, User = "operator", Comment = "Second" },
                new() { Id = latest, CreatedAt = time.AddTicks(1), User = "operator", Comment = "Latest" });
            await context.SaveChangesAsync();
        }
        Assert.Equal(new[] { latest, second, first }, (await fixture.Store.GetAllAsync()).Select(entry => entry.Id));
    }

    [Fact]
    public async Task WriteFailureRemainsVisibleAndSuccessfulRetryCreatesOnlyOneEntry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync(
            "CREATE TRIGGER FailLogbook BEFORE INSERT ON LogbookEntries BEGIN SELECT RAISE(ABORT, 'logbook failed'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Store.AddAsync("operator", "Comment"));
        Assert.Empty(await fixture.Store.GetAllAsync());
        await fixture.ExecuteAsync("DROP TRIGGER FailLogbook;");
        await fixture.Store.AddAsync("operator", "Comment");
        Assert.Equal("Comment", Assert.Single(await fixture.Store.GetAllAsync()).Comment);
    }

    private sealed class Fixture(SqliteConnection connection, Factory factory) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public LogbookStore Store => new(Factory);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, TimestampModelCustomizer>().Options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (statement.Contains("\"LogbookEntries\"", StringComparison.Ordinal))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal));
            return new(connection, factory);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var context = Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public int Created { get; private set; }
        public AlertDbContext CreateDbContext()
        {
            Created++;
            return new(options);
        }
    }

    public sealed class TimestampModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<LogbookEntry>().Property(entry => entry.CreatedAt)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        }
    }
}
