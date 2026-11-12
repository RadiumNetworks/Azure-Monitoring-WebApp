using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

public sealed class ScimRequestFilter(
    ScimOptions options, IProvisioningUserStore store, ILogger<ScimRequestFilter> logger) : IEndpointFilter
{
    private static readonly object SourceKey = new();
    public const string ContentType = "application/scim+json";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            if (!options.Enabled) throw new ScimProtocolException(404, "SCIM provisioning is disabled.");
            if (!http.Request.IsHttps) throw new ScimProtocolException(403, "SCIM requires HTTPS.");
            if (!ValidToken(http.Request))
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                throw new ScimProtocolException(401, "A valid SCIM provisioning token is required.");
            }
            await store.EnsureSourceAsync(options.Source, http.RequestAborted);
            http.Items[SourceKey] = options.Source;
            return await next(context);
        }
        catch (ScimProtocolException exception)
        {
            logger.LogWarning("SCIM request rejected. Status={Status}, Type={ScimType}", exception.Status, exception.ScimType);
            return Error(exception.Status, exception.Message, exception.ScimType);
        }
        catch (ProvisioningException exception)
        {
            var status = exception.Code switch
            {
                ProvisioningError.NotFound => 404,
                ProvisioningError.Conflict => 409,
                ProvisioningError.Disabled or ProvisioningError.SourceMismatch => 403,
                ProvisioningError.Version => 412,
                ProvisioningError.InvalidMember => 400,
                _ => 503
            };
            logger.LogWarning("SCIM storage request rejected. Code={Code}, Status={Status}", exception.Code, status);
            return Error(status, exception.Message, status == 409 ? "uniqueness" : status == 400 ? "invalidValue" : null);
        }
        catch (JsonException)
        {
            logger.LogWarning("SCIM request contains invalid JSON.");
            return Error(400, "The request body is not valid SCIM JSON.", "invalidSyntax");
        }
        catch (BadHttpRequestException exception)
        {
            logger.LogWarning("SCIM HTTP request rejected. Status={Status}", exception.StatusCode);
            return Error(exception.StatusCode, "The HTTP request is invalid.", "invalidSyntax");
        }
    }

    public static ProvisioningSource Source(HttpContext http) =>
        http.Items[SourceKey] as ProvisioningSource ?? throw new InvalidOperationException("The SCIM request was not authenticated.");

    private bool ValidToken(HttpRequest request)
    {
        if (request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(request.Headers.Authorization[0], out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { Length: >= 32 and <= 512 } token || token.Any(char.IsWhiteSpace))
            return false;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var valid = false;
        foreach (var hash in options.TokenHashes)
            valid |= CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(hash));
        return valid;
    }

    private static IResult Error(int status, string detail, string? type) => TypedResults.Json(
        new ScimErrorResponse([ScimSchemas.Error], status.ToString(CultureInfo.InvariantCulture), detail, type),
        JsonOptions, ContentType, statusCode: status);
}
