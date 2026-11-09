using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MonitoringApp;

public interface IAlertIngestionDiagnostics
{
    IReadOnlyList<AlertIngestionRequestDiagnostic> GetRecent();
    void Record(HttpContext context, AuthenticateResult? authentication);
}

/// <summary>Retains only selected non-secret ingestion metadata, bounded by age and count per application process.</summary>
public sealed class AlertIngestionDiagnostics(
    AlertIngestionAuthenticationOptions options,
    TimeProvider timeProvider) : IAlertIngestionDiagnostics
{
    public const int Capacity = 20;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private const int MaximumTokenLength = 16 * 1024;
    private const int MaximumValueLength = 256;
    private readonly object gate = new();
    private readonly Queue<AlertIngestionRequestDiagnostic> records = new();

    public IReadOnlyList<AlertIngestionRequestDiagnostic> GetRecent()
    {
        lock (gate)
        {
            RemoveExpired(timeProvider.GetUtcNow());
            return records.Reverse().ToArray();
        }
    }

    public void Record(HttpContext context, AuthenticateResult? authentication)
    {
        var (unverifiedPrincipal, inspection) = Inspect(context.Request.Headers.Authorization.ToString());
        var validated = options.Enabled && authentication?.Succeeded == true;
        var principal = validated ? authentication!.Principal : unverifiedPrincipal;
        var checks = principal is null ? [] : AlertIngestionAuthorization.Evaluate(principal, options);
        var roleClaims = principal?.FindAll("roles").ToArray() ?? [];
        var audienceClaims = principal?.FindAll("aud").ToArray() ?? [];
        var now = timeProvider.GetUtcNow();
        var record = new AlertIngestionRequestDiagnostic(
            now,
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            context.Response.StatusCode,
            Limit(context.TraceIdentifier),
            options.Enabled,
            validated,
            !options.Enabled
                ? "Not validated: ingestion authentication is disabled."
                : validated
                    ? "Bearer token validated."
                    : AuthenticationFailure(authentication?.Failure),
            validated ? "Selected claims from the validated Bearer identity." : inspection,
            ClaimValue(principal, "tid"),
            ClaimValue(principal, "azp"),
            ClaimValue(principal, "appid"),
            ClaimValue(principal, "oid"),
            ClaimValue(principal, "ver"),
            ClaimValue(principal, "idtyp"),
            ClaimValue(principal, "iss"),
            audienceClaims.Take(8).Select(claim => Limit(claim.Value)).ToArray(),
            roleClaims.Take(32).Select(claim => Limit(claim.Value)).ToArray(),
            roleClaims.Length > 32 || audienceClaims.Length > 8 ||
                roleClaims.Concat(audienceClaims).Any(claim => claim.Value.Length > MaximumValueLength),
            UnixTime(principal?.FindFirstValue("exp")),
            checks);

        lock (gate)
        {
            var recordedAt = timeProvider.GetUtcNow();
            RemoveExpired(recordedAt);
            records.Enqueue(record with { RecordedAt = recordedAt });
            while (records.Count > Capacity)
                records.Dequeue();
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        while (records.TryPeek(out var record) && now - record.RecordedAt >= Retention)
            records.Dequeue();
    }

    private static (ClaimsPrincipal? Principal, string Status) Inspect(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return (null, "No Authorization header.");
        if (header.Length > MaximumTokenLength)
            return (null, "Authorization header exceeds the 16 KiB diagnostic inspection limit.");
        if (!AuthenticationHeaderValue.TryParse(header, out var authorization) ||
            !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return (null, "No single Bearer token was supplied.");

        var handler = new JsonWebTokenHandler { MaximumTokenSizeInBytes = MaximumTokenLength };
        if (!handler.CanReadToken(authorization.Parameter))
            return (null, "Bearer token is not a readable JWT.");
        try
        {
            var token = handler.ReadJsonWebToken(authorization.Parameter);
            return token.IsEncrypted
                ? (null, "Encrypted token claims cannot be inspected.")
                : (new ClaimsPrincipal(new ClaimsIdentity(token.Claims)), "Selected JWT claims decoded without verification.");
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
        {
            // Parser exception messages can contain token material.
            return (null, "Bearer token could not be decoded.");
        }
    }

    private static string AuthenticationFailure(Exception? failure) => failure switch
    {
        SecurityTokenExpiredException => "Rejected: token expired.",
        SecurityTokenNotYetValidException => "Rejected: token is not yet valid.",
        SecurityTokenInvalidAudienceException => "Rejected: invalid audience.",
        SecurityTokenInvalidIssuerException => "Rejected: invalid issuer.",
        SecurityTokenSignatureKeyNotFoundException => "Rejected: signing key could not be resolved.",
        SecurityTokenInvalidSignatureException => "Rejected: invalid signature.",
        null => "No authenticated Bearer identity.",
        _ => "Bearer authentication failed. Check protected application logs for validation or metadata errors."
    };

    private static string? ClaimValue(ClaimsPrincipal? principal, string type) =>
        principal?.FindFirstValue(type) is { } value ? Limit(value) : null;

    private static string Limit(string value) =>
        value.Length <= MaximumValueLength ? value : value[..(MaximumValueLength - 3)] + "...";

    private static DateTimeOffset? UnixTime(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
        seconds is >= -62135596800 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}

/// <summary>Selected request and identity metadata; never contains a bearer token, request body, cookie, or arbitrary header collection.</summary>
public sealed record AlertIngestionRequestDiagnostic(
    DateTimeOffset RecordedAt,
    string Method,
    string Endpoint,
    int StatusCode,
    string TraceId,
    bool AuthenticationRequired,
    bool TokenValidated,
    string AuthenticationStatus,
    string InspectionStatus,
    string? TenantId,
    string? AuthorizedParty,
    string? ApplicationId,
    string? ObjectId,
    string? TokenVersion,
    string? IdentityType,
    string? Issuer,
    IReadOnlyList<string> Audiences,
    IReadOnlyList<string> Roles,
    bool ListsTruncated,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<AlertIngestionAuthorizationCheck> PolicyChecks)
{
    public bool AuthenticationBlocked => AuthenticationRequired &&
        StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;
    public string? UamiClientId => AuthorizedParty ?? ApplicationId;
    public bool? RequiredRolePresent => PolicyChecks
        .FirstOrDefault(check => check.Name == "Required application role")?.Passed;
}
