using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MonitoringApp.Tests;

public sealed class SqlAuthenticationPersistenceTests
{
    private static string Password => AuthenticationTestData.Cases.Password;

    [Fact]
    public async Task CredentialsAndUserSummariesPreserveRolesAndNeverExposeHashes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(("operator", SqlAuthenticationRoles.Reader), ("administrator", SqlAuthenticationRoles.Admin),
            ("legacy", "invalid"));
        Assert.Equal(new SqlAuthenticationResult("operator", SqlAuthenticationRoles.Reader),
            await fixture.Service.ValidateCredentialsAsync(" operator ", Password));
        Assert.Null(await fixture.Service.ValidateCredentialsAsync("operator", AuthenticationTestData.Cases.WrongPassword));
        Assert.Null(await fixture.Service.ValidateCredentialsAsync("unknown", Password));
        Assert.Null(await fixture.Service.ValidateCredentialsAsync("legacy", AuthenticationTestData.Cases.WrongPassword));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ValidateCredentialsAsync("legacy", Password));

        var users = await fixture.Service.GetUsersAsync();
        Assert.Equal(new[] { "administrator", "legacy", "operator" }, users.Select(user => user.Username));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(users));
        foreach (var user in document.RootElement.EnumerateArray())
            Assert.Equal(new[] { "Role", "Username" }, user.EnumerateObject().Select(property => property.Name).Order());
    }

    [Theory]
    [InlineData("username-empty")]
    [InlineData("username-long")]
    [InlineData("password-empty")]
    [InlineData("password-long")]
    public async Task InvalidCredentialLengthsAreRejectedBeforeOpeningAContext(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var username = kind == "username-empty" ? " " : kind == "username-long" ? new string('u', 129) : "operator";
        var password = kind == "password-empty" ? "" : kind == "password-long" ? new string('p', 1025) : Password;
        var created = fixture.Factory.Created;
        Assert.Null(await fixture.Service.ValidateCredentialsAsync(username, password));
        Assert.Equal(created, fixture.Factory.Created);
    }

    [Fact]
    public async Task CreationNormalizesUserAndRoleAndUsesIndependentPasswordSalts()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal("first", await fixture.Service.CreateUserAsync(" first ", Password, " operator "));
        await fixture.Service.CreateUserAsync("second", Password, SqlAuthenticationRoles.Reader);
        var first = await fixture.ReadAsync("first");
        var second = await fixture.ReadAsync("second");
        Assert.Equal(SqlAuthenticationRoles.Operator, first.Role);
        Assert.NotEqual(first.PasswordHash, second.PasswordHash);
        Assert.True(fixture.Hasher.Verify(Password, first.PasswordHash));
        Assert.True(fixture.Hasher.Verify(Password, second.PasswordHash));
        await Assert.ThrowsAsync<SqlAuthenticationException>(() =>
            fixture.Service.CreateUserAsync(" first ", Password, SqlAuthenticationRoles.Admin));
        Assert.Equal(SqlAuthenticationRoles.Operator, (await fixture.ReadAsync("first")).Role);
        Assert.Equal(2, (await fixture.Service.GetUsersAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationSurfacesInsertFailuresAndConcurrentDuplicateNames(bool race)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (race)
            fixture.Saves.BeforeSave = () => fixture.SeedAsync(("duplicate", SqlAuthenticationRoles.Reader));
        else
            await fixture.ExecuteAsync(
                "CREATE TRIGGER FailUserCreate BEFORE INSERT ON AuthenticationUsers BEGIN SELECT RAISE(ABORT, 'user create failed'); END;");
        var exception = await Assert.ThrowsAsync<SqlAuthenticationException>(() =>
            fixture.Service.CreateUserAsync("duplicate", Password, SqlAuthenticationRoles.Admin));
        Assert.StartsWith("The authentication user could not be created.", exception.Message);
        Assert.IsType<DbUpdateException>(exception.InnerException);
        Assert.Equal(race ? 1 : 0, (await fixture.Service.GetUsersAsync()).Count);
        if (race)
            Assert.Equal(SqlAuthenticationRoles.Reader, (await fixture.ReadAsync("duplicate")).Role);
        else
        {
            await fixture.ExecuteAsync("DROP TRIGGER FailUserCreate;");
            await fixture.Service.CreateUserAsync("duplicate", Password, SqlAuthenticationRoles.Admin);
            Assert.Single(await fixture.Service.GetUsersAsync());
        }
    }

    [Fact]
    public async Task PasswordResetFailurePreservesOldCredentialsAndSuccessfulRetryReplacesThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(("operator", SqlAuthenticationRoles.Operator));
        var original = await fixture.ReadAsync("operator");
        var replacement = Password + "-changed";
        await fixture.ExecuteAsync(
            "CREATE TRIGGER FailPasswordReset BEFORE UPDATE ON AuthenticationUsers BEGIN SELECT RAISE(ABORT, 'password reset failed'); END;");
        var exception = await Assert.ThrowsAsync<SqlAuthenticationException>(() =>
            fixture.Service.SetPasswordAsync(" operator ", replacement));
        Assert.Equal("The password could not be updated.", exception.Message);
        Assert.IsType<DbUpdateException>(exception.InnerException);
        Assert.Equal(original.PasswordHash, (await fixture.ReadAsync("operator")).PasswordHash);
        Assert.NotNull(await fixture.Service.ValidateCredentialsAsync("operator", Password));
        Assert.Null(await fixture.Service.ValidateCredentialsAsync("operator", replacement));

        await fixture.ExecuteAsync("DROP TRIGGER FailPasswordReset;");
        await fixture.Service.SetPasswordAsync(" operator ", replacement);
        Assert.Null(await fixture.Service.ValidateCredentialsAsync("operator", Password));
        Assert.NotNull(await fixture.Service.ValidateCredentialsAsync("operator", replacement));
        var updated = await fixture.ReadAsync("operator");
        Assert.NotEqual(original.PasswordHash, updated.PasswordHash);
        Assert.Equal(original.Role, updated.Role);
    }

    [Fact]
    public async Task LastAdministratorAndLastUserRemainProtectedUntilAReplacementExists()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(("admin", SqlAuthenticationRoles.Admin), ("reader", SqlAuthenticationRoles.Reader));
        await Assert.ThrowsAsync<SqlAuthenticationException>(() =>
            fixture.Service.SetRoleAsync("admin", SqlAuthenticationRoles.Operator));
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => fixture.Service.DeleteUserAsync("admin"));
        Assert.True(await fixture.Service.DeleteUserAsync(" reader "));
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => fixture.Service.DeleteUserAsync("admin"));
        await fixture.Service.SetRoleAsync("admin", SqlAuthenticationRoles.Admin);

        await fixture.SeedAsync(("replacement", SqlAuthenticationRoles.Reader));
        await fixture.Service.SetRoleAsync("replacement", " admin ");
        await fixture.Service.SetRoleAsync("admin", " operator ");
        Assert.Equal(SqlAuthenticationRoles.Operator, (await fixture.ReadAsync("admin")).Role);
        Assert.True(await fixture.Service.DeleteUserAsync("admin"));
        Assert.Equal("replacement", Assert.Single(await fixture.Service.GetUsersAsync()).Username);
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => fixture.Service.DeleteUserAsync("replacement"));
        await Assert.ThrowsAsync<SqlAuthenticationException>(() =>
            fixture.Service.SetRoleAsync("replacement", SqlAuthenticationRoles.Reader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastAdminGuardIsEvaluatedInTheWriteAfterAnotherAdminIsDemoted(bool changeRole)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(("admin-a", SqlAuthenticationRoles.Admin), ("admin-b", SqlAuthenticationRoles.Admin),
            ("reader", SqlAuthenticationRoles.Reader));
        var interleaved = false;
        fixture.Commands.BeforeWrite = async () =>
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.AuthenticationUsers.Where(user => user.Username == "admin-b").ExecuteUpdateAsync(
                update => update.SetProperty(user => user.Role, SqlAuthenticationRoles.Reader));
            interleaved = true;
        };
        Task Write() => changeRole
            ? fixture.Service.SetRoleAsync("admin-a", SqlAuthenticationRoles.Reader)
            : fixture.Service.DeleteUserAsync("admin-a");
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => Write());
        Assert.True(interleaved);
        Assert.Equal(SqlAuthenticationRoles.Admin, (await fixture.ReadAsync("admin-a")).Role);
        Assert.Equal(SqlAuthenticationRoles.Reader, (await fixture.ReadAsync("admin-b")).Role);
        Assert.Equal(3, (await fixture.Service.GetUsersAsync()).Count);
    }

    [Fact]
    public async Task LastUserGuardIsEvaluatedInTheDeleteAfterAnotherUserDisappears()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(("first", SqlAuthenticationRoles.Reader), ("second", SqlAuthenticationRoles.Reader));
        var interleaved = false;
        fixture.Commands.BeforeWrite = async () =>
        {
            await using var context = fixture.Factory.CreateDbContext();
            await context.AuthenticationUsers.Where(user => user.Username == "second").ExecuteDeleteAsync();
            interleaved = true;
        };
        var exception = await Record.ExceptionAsync(() => fixture.Service.DeleteUserAsync("first"));
        Assert.True(interleaved);
        Assert.True(exception is InvalidOperationException, fixture.Commands.InterleavedSql);
        Assert.Contains("EXISTS", fixture.Commands.InterleavedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("first", Assert.Single(await fixture.Service.GetUsersAsync()).Username);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("role")]
    [InlineData("delete")]
    public async Task ManagementRejectsEmptyNamesWithoutOpeningAContext(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = fixture.Factory.Created;
        Task Write() => operation switch
        {
            "create" => fixture.Service.CreateUserAsync(" ", Password, SqlAuthenticationRoles.Reader),
            "password" => fixture.Service.SetPasswordAsync(" ", Password),
            "role" => fixture.Service.SetRoleAsync(" ", SqlAuthenticationRoles.Reader),
            "delete" => fixture.Service.DeleteUserAsync(" "),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => Write());
        Assert.Equal(created, fixture.Factory.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagementRejectsEmptyPasswordsWithoutOpeningAContext(bool create)
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = fixture.Factory.Created;
        Task Write() => create
            ? fixture.Service.CreateUserAsync("operator", "", SqlAuthenticationRoles.Reader)
            : fixture.Service.SetPasswordAsync("operator", "");
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => Write());
        Assert.Equal(created, fixture.Factory.Created);
    }

    [Fact]
    public async Task MissingUsersKeepExistingReadDeleteAndUpdateSemantics()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Empty(await fixture.Service.GetUsersAsync());
        Assert.False(await fixture.Service.DeleteUserAsync("unknown"));
        await Assert.ThrowsAsync<SqlAuthenticationException>(() => fixture.Service.SetPasswordAsync("unknown", Password));
        await Assert.ThrowsAsync<SqlAuthenticationException>(() =>
            fixture.Service.SetRoleAsync("unknown", SqlAuthenticationRoles.Reader));
    }

    private sealed class Fixture(
        SqliteConnection connection, Factory factory, SaveInterceptor saves, WriteInterceptor commands) : IAsyncDisposable
    {
        private static readonly Lazy<string> SeedHash = new(() => new SqlPasswordHasher().Hash(Password));
        public Factory Factory { get; } = factory;
        public SaveInterceptor Saves { get; } = saves;
        public WriteInterceptor Commands { get; } = commands;
        public SqlPasswordHasher Hasher { get; } = new();
        public SqlAuthenticationService Service => new(Factory, Hasher);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var saves = new SaveInterceptor();
            var commands = new WriteInterceptor();
            var factory = new Factory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection)
                .AddInterceptors(saves, commands).Options);
            await using var context = factory.CreateDbContext();
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (statement.Contains("\"AuthenticationUsers\"", StringComparison.Ordinal))
                    await context.Database.ExecuteSqlRawAsync(statement.Replace("N'", "'", StringComparison.Ordinal));
            return new(connection, factory, saves, commands);
        }

        public async Task SeedAsync(params (string Username, string Role)[] users)
        {
            await using var context = Factory.CreateDbContext();
            context.AuthenticationUsers.AddRange(users.Select(user => new SqlAuthenticationUser
            {
                Username = user.Username, Role = user.Role, PasswordHash = SeedHash.Value
            }));
            await context.SaveChangesAsync();
        }

        public async Task<SqlAuthenticationUser> ReadAsync(string username)
        {
            await using var context = Factory.CreateDbContext();
            return await context.AuthenticationUsers.AsNoTracking().SingleAsync(user => user.Username == username);
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

    private sealed class SaveInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? BeforeSave { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var callback = BeforeSave;
            BeforeSave = null;
            if (callback is not null)
                await callback();
            return result;
        }
    }

    private sealed class WriteInterceptor : DbCommandInterceptor
    {
        public Func<Task>? BeforeWrite { get; set; }
        public string? InterleavedSql { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"AuthenticationUsers\"", StringComparison.Ordinal) &&
                (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal) ||
                 command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)))
            {
                var callback = BeforeWrite;
                BeforeWrite = null;
                if (callback is not null)
                {
                    await callback();
                    InterleavedSql = command.CommandText;
                }
            }
            return result;
        }
    }
}
