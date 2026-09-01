using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SQLAuth.Data;

namespace SQLAuth.Authentication;

public static class UserRoles
{
    public const string User = "User";
    public const string Admin = "Admin";

    public static string Normalize(string role) => role.Trim().ToUpperInvariant() switch
    {
        "USER" => User,
        "ADMIN" => Admin,
        _ => throw new InvalidOperationException("Role must be User or Admin.")
    };
}

public sealed record AuthenticatedUser(string Username, string Role);
public sealed record UserSummary(string Username, string Role);

public sealed class PasswordHasher
{
    private const string Algorithm = "PBKDF2-SHA256";
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
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
            if (parts.Length != 4 || parts[0] != Algorithm ||
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
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expectedKey.Length);
            return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length is < 12 or > 1024)
        {
            throw new InvalidOperationException("Password must contain between 12 and 1024 characters.");
        }
    }
}

public sealed class SqlUserService(
    IDbContextFactory<AppDbContext> contextFactory,
    PasswordHasher passwordHasher)
{
    private const string DummyHash = "PBKDF2-SHA256$600000$MDEyMzQ1Njc4OUFCQ0RFRg==$xKPYhItL4ZykbLDQKl7QVpmF5O5oYJJq5P2OMfrI8JQ=";

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AnyAsync(cancellationToken);
    }

    public async Task<AuthenticatedUser?> ValidateCredentialsAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = username.Trim();
        if (normalizedUsername.Length is 0 or > 128 || password.Length is 0 or > 1024)
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Username == normalizedUsername, cancellationToken);
        var isValid = passwordHasher.Verify(password, user?.PasswordHash ?? DummyHash);
        return user is not null && isValid
            ? new AuthenticatedUser(user.Username, UserRoles.Normalize(user.Role))
            : null;
    }

    public async Task CreateInitialAdminAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        PasswordHasher.ValidatePassword(password);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Setup is complete. Sign in with an existing account.");
        }

        db.Users.Add(new ApplicationUser
        {
            Username = normalizedUsername,
            PasswordHash = passwordHasher.Hash(password),
            Role = UserRoles.Admin
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .OrderBy(user => user.Username)
            .Select(user => new UserSummary(user.Username, user.Role))
            .ToArrayAsync(cancellationToken);
    }

    public async Task CreateUserAsync(
        string username, string password, string role, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        PasswordHasher.ValidatePassword(password);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Users.AnyAsync(user => user.Username == normalizedUsername, cancellationToken))
        {
            throw new InvalidOperationException("That username already exists.");
        }

        db.Users.Add(new ApplicationUser
        {
            Username = normalizedUsername,
            PasswordHash = passwordHasher.Hash(password),
            Role = UserRoles.Normalize(role)
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 128)
        {
            throw new InvalidOperationException("Username is required and may contain at most 128 characters.");
        }

        return normalized;
    }
}

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class SetupRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}