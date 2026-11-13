using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace MonitoringApp;

/// <summary>Changes only the browser's UI language and returns to a local page.</summary>
public sealed record UiLanguageRequest
{
    public string Culture { get; init; } = string.Empty;
    public string ReturnUrl { get; init; } = "/";
}

public static class UiLanguageEndpoints
{
    public static void MapUiLanguage(this WebApplication app)
    {
        app.MapPost(UiLocalization.ChangePath,
            Results<RedirectHttpResult, ProblemHttpResult> (
                [FromForm] UiLanguageRequest request, HttpContext context,
                UiText text, UiLanguageContext languages, ILoggerFactory logging) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var culture = languages.CanonicalCulture(request.Culture);
                if (culture is null || !IsLocalReturnUrl(request.ReturnUrl))
                {
                    logging.CreateLogger("UiLanguage").LogWarning("An invalid UI language change request was rejected.");
                    return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                        title: text["Language.InvalidRequest"]);
                }

                context.Response.Cookies.Append(UiLocalization.CookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = context.Request.IsHttps,
                        SameSite = SameSiteMode.Lax,
                        IsEssential = true,
                        Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
                        MaxAge = TimeSpan.FromDays(365)
                    });
                return TypedResults.LocalRedirect(request.ReturnUrl);
            })
            .AllowAnonymous()
            .WithName("ChangeUiLanguage")
            .WithSummary("Changes the browser UI language using an antiforgery-protected form")
            .WithDescription("Allows only languages enabled in the validated runtime registry. Does not change roles, server parsing culture, SQL data or prompts. Redirects only to a local URL.")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    internal static bool IsLocalReturnUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url[0] == '/' &&
        (url.Length == 1 || url[1] is not ('/' or '\\')) &&
        !url.Any(character => char.IsControl(character) || character == '\\') &&
        Uri.TryCreate(url, UriKind.Relative, out _);
}
