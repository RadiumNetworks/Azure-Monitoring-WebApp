using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace MonitoringApp;

public static class UiLocalization
{
    public const string CookieName = ".MonitoringApp.UiCulture";
    public const string ChangePath = "/ui/language";
    public const string English = "en-US";
    public const string German = "de-DE";

    public static string? CanonicalCulture(string? culture) =>
        string.Equals(culture, English, StringComparison.OrdinalIgnoreCase) ? English :
        string.Equals(culture, German, StringComparison.OrdinalIgnoreCase) ? German : null;

    public static IServiceCollection AddUiLocalization(this IServiceCollection services)
    {
        UiResources.ValidateEnglish();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddScoped<UiText>();
        services.AddScoped<QueryTextPresentation>();
        // Existing machine parsing/formatting must not inherit the selected UI language.
        var serverCulture = CultureInfo.GetCultureInfo(CultureInfo.CurrentCulture.Name);
        services.AddOptions<RequestLocalizationOptions>().Configure<ILoggerFactory>((options, logging) =>
        {
            options.DefaultRequestCulture = new RequestCulture(serverCulture, CultureInfo.GetCultureInfo(English));
            options.SupportedCultures = [serverCulture];
            options.SupportedUICultures =
                [CultureInfo.GetCultureInfo(English), CultureInfo.GetCultureInfo(German)];
            options.RequestCultureProviders =
                [new UiCultureCookieProvider(serverCulture, logging.CreateLogger<UiCultureCookieProvider>())];
        });
        return services;
    }

    private sealed class UiCultureCookieProvider(
        CultureInfo serverCulture, ILogger<UiCultureCookieProvider> logger) : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var cookie))
                return Task.FromResult<ProviderCultureResult?>(null);

            var parsed = CookieRequestCultureProvider.ParseCookieValue(cookie);
            var selected = parsed?.UICultures.Count == 1
                ? CanonicalCulture(parsed.UICultures[0].Value)
                : null;
            if (selected is null)
            {
                logger.LogWarning("An invalid or unsupported UI culture cookie was ignored; using English.");
                return Task.FromResult<ProviderCultureResult?>(null);
            }

            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(serverCulture.Name, selected));
        }
    }
}
