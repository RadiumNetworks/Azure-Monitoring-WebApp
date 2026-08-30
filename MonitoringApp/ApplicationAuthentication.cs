using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

/// <summary>
/// Selects whether the application is anonymously accessible or protected by database-backed accounts.
/// Alert ingestion remains governed separately by AlertIngestionAuthentication.
/// </summary>
public sealed class ApplicationAuthenticationOptions
{
    public const string SectionName = "Authentication";
    public const string Open = "open";
    public const string Sql = "sql";

    public string Type { get; init; } = Open;
    public bool IsSql => string.Equals(Type, Sql, StringComparison.OrdinalIgnoreCase);
    public bool IsOpen => string.Equals(Type, Open, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate() => IsOpen || IsSql
        ? []
        : [$"{SectionName}:Type must be either '{Open}' or '{Sql}'."];
}

/// <summary>
/// Stores one local login. PasswordHash contains a versioned PBKDF2 string including its random salt.
/// </summary>
public sealed class SqlAuthenticationUser
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = SqlAuthenticationRoles.Reader;
}

public static class SqlAuthenticationRoles
{
    public const string Reader = "Reader";
    public const string Operator = "Operator";
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All = [Reader, Operator, Admin];

    public static string Normalize(string role)
    {
        var match = All.FirstOrDefault(candidate =>
            candidate.Equals(role?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            $"Role must be one of: {string.Join(", ", All)}.");
    }
}

public sealed record SqlAuthenticationResult(string Username, string Role);
public sealed record SqlAuthenticationUserSummary(string Username, string Role);

/// <summary>
/// Hashes and verifies passwords with PBKDF2-HMAC-SHA256. The encoded value includes algorithm,
/// work factor, salt, and derived key, so no separate salt column is required.
/// </summary>
public sealed class SqlPasswordHasher
{
    private const string Algorithm = "PBKDF2-SHA256";
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        try
        {
            var parts = encodedHash.Split('$');
            if (parts.Length != 4 ||
                !parts[0].Equals(Algorithm, StringComparison.Ordinal) ||
                !int.TryParse(parts[1], out var iterations) ||
                iterations is < 100_000 or > 2_000_000)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[2]);
            var expectedKey = Convert.FromBase64String(parts[3]);
            if (salt.Length < SaltSize || expectedKey.Length != KeySize)
            {
                return false;
            }

            var actualKey = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedKey.Length);
            return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

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
        var normalizedRole = SqlAuthenticationRoles.Normalize(role);
        ValidatePassword(password);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.AuthenticationUsers.AnyAsync(
            user => user.Username == normalizedUsername,
            cancellationToken))
        {
            throw new InvalidOperationException($"User '{normalizedUsername}' already exists.");
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
            throw new InvalidOperationException(
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
        var normalizedRole = SqlAuthenticationRoles.Normalize(role);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existingRole = await context.AuthenticationUsers
            .Where(user => user.Username == normalizedUsername)
            .Select(user => user.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingRole is null)
        {
            throw new InvalidOperationException("The authentication user no longer exists.");
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
                throw new InvalidOperationException(
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
            throw new InvalidOperationException("The authentication user no longer exists.");
        }

        user.PasswordHash = passwordHasher.Hash(password);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException("The password could not be updated.", exception);
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
                    context.AuthenticationUsers.Count() > 1 &&
                    (user.Role != SqlAuthenticationRoles.Admin ||
                        context.AuthenticationUsers.Count(candidate =>
                            candidate.Role == SqlAuthenticationRoles.Admin) > 1))
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException("The authentication user could not be deleted.", exception);
        }

        if (deletedRows == 0)
        {
            throw new InvalidOperationException(
                "The last authentication user or last Admin cannot be deleted. Create a replacement first.");
        }

        return true;
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 128)
        {
            throw new InvalidOperationException(
                "Username is required and may contain at most 128 characters.");
        }

        return normalized;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
        {
            throw new InvalidOperationException(
                "Password is required and may contain at most 1024 characters.");
        }
    }
}

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
