using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

/// <summary>
/// Validates SQL-authentication credentials without exposing password hashes to callers.
/// </summary>
public sealed class SqlAuthenticationService(
    IDbContextFactory<AlertDbContext> contextFactory,
    SqlPasswordHasher passwordHasher)
{
    // A valid hash used when a username is unknown, reducing username-dependent timing differences.
    private const string DummyHash = "PBKDF2-SHA256$600000$MDEyMzQ1Njc4OUFCQ0RFRg==$xKPYhItL4ZykbLDQKl7QVpmF5O5oYJJq5P2OMfrI8JQ=";

    public async Task<SqlAuthenticationResult?> ValidateCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = username.Trim();
        if (normalizedUsername.Length is 0 or > 128 || password.Length is 0 or > 1024)
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.AuthenticationUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Username == normalizedUsername, cancellationToken);
        var passwordIsValid = passwordHasher.Verify(password, user?.PasswordHash ?? DummyHash);
        return user is not null && passwordIsValid
            ? new SqlAuthenticationResult(user.Username, SqlAuthenticationRoles.Normalize(user.Role))
            : null;
    }

    public async Task<IReadOnlyList<SqlAuthenticationUserSummary>> GetUsersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AuthenticationUsers
            .AsNoTracking()
            .OrderBy(user => user.Username)
            .Select(user => new SqlAuthenticationUserSummary(user.Username, user.Role))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<string> CreateUserAsync(
        string username,
        string password,
        string role,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var normalizedRole = NormalizeRole(role);
        ValidatePassword(password);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.AuthenticationUsers.AnyAsync(
            user => user.Username == normalizedUsername,
            cancellationToken))
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.DuplicateUser,
                $"User '{normalizedUsername}' already exists.", arguments: [normalizedUsername]);
        }

        context.AuthenticationUsers.Add(new SqlAuthenticationUser
        {
            Username = normalizedUsername,
            PasswordHash = passwordHasher.Hash(password),
            Role = normalizedRole
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.CreateUnconfirmed,
                "The authentication user could not be created. Verify that the username is unique.",
                exception);
        }

        return normalizedUsername;
    }

    public async Task SetRoleAsync(
        string username,
        string role,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var normalizedRole = NormalizeRole(role);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existingRole = await context.AuthenticationUsers
            .Where(user => user.Username == normalizedUsername)
            .Select(user => user.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingRole is null)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.Missing, "The authentication user no longer exists.");
        }

        if (existingRole == SqlAuthenticationRoles.Admin && normalizedRole != SqlAuthenticationRoles.Admin)
        {
            var updatedRows = await context.AuthenticationUsers
                .Where(user => user.Username == normalizedUsername &&
                    context.AuthenticationUsers.Count(candidate =>
                        candidate.Role == SqlAuthenticationRoles.Admin) > 1)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(user => user.Role, normalizedRole),
                    cancellationToken);
            if (updatedRows == 0)
            {
                throw new SqlAuthenticationException(SqlAuthenticationErrorCode.LastAdmin,
                    "The last Admin cannot be assigned another role. Create another Admin first.");
            }

            return;
        }

        await context.AuthenticationUsers
            .Where(user => user.Username == normalizedUsername)
            .ExecuteUpdateAsync(
                update => update.SetProperty(user => user.Role, normalizedRole),
                cancellationToken);
    }

    public async Task SetPasswordAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        ValidatePassword(password);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.AuthenticationUsers.FindAsync([normalizedUsername], cancellationToken);
        if (user is null)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.Missing, "The authentication user no longer exists.");
        }

        user.PasswordHash = passwordHasher.Hash(password);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.PasswordUnconfirmed, "The password could not be updated.", exception);
        }
    }

    public async Task<bool> DeleteUserAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var userExists = await context.AuthenticationUsers.AnyAsync(
            user => user.Username == normalizedUsername,
            cancellationToken);
        if (!userExists)
        {
            return false;
        }

        int deletedRows;
        try
        {
            deletedRows = await context.AuthenticationUsers
                .Where(user => user.Username == normalizedUsername &&
                    context.AuthenticationUsers.Any(candidate => candidate.Username != user.Username) &&
                    (user.Role != SqlAuthenticationRoles.Admin ||
                        context.AuthenticationUsers.Count(candidate =>
                            candidate.Role == SqlAuthenticationRoles.Admin) > 1))
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.DeleteUnconfirmed, "The authentication user could not be deleted.", exception);
        }

        if (deletedRows == 0)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.LastUserOrAdmin,
                "The last authentication user or last Admin cannot be deleted. Create a replacement first.");
        }

        return true;
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 128)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.Username,
                "Username is required and may contain at most 128 characters.");
        }

        return normalized;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.Password,
                "Password is required and may contain at most 1024 characters.");
        }

    }

    private static string NormalizeRole(string role)
    {
        try
        {
            return SqlAuthenticationRoles.Normalize(role);
        }
        catch (InvalidOperationException exception)
        {
            throw new SqlAuthenticationException(SqlAuthenticationErrorCode.Role, exception.Message, exception);
        }
    }
}
