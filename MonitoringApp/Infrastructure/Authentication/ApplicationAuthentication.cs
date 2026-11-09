using System.Security.Cryptography;
using System.Text;

namespace MonitoringApp;

/// <summary>
/// Selects open access, database-backed accounts, or App Service Entra authentication.
/// Alert ingestion remains governed separately by AlertIngestionAuthentication.
/// </summary>
public sealed class ApplicationAuthenticationOptions
{
    public const string SectionName = "Authentication";
    public const string Open = "open";
    public const string Sql = "sql";
    public const string Entra = "entra";

    public string Type { get; init; } = Sql;
    public bool IsSql => string.Equals(Type, Sql, StringComparison.OrdinalIgnoreCase);
    public bool IsOpen => string.Equals(Type, Open, StringComparison.OrdinalIgnoreCase);
    public bool IsEntra => string.Equals(Type, Entra, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate() => IsOpen || IsSql || IsEntra
        ? []
        : [$"{SectionName}:Type must be '{Open}', '{Sql}', or '{Entra}'."];
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

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
