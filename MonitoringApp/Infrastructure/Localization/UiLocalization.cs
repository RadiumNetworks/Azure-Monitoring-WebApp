using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;

namespace MonitoringApp;

public static class UiLocalization
{
    public const string CookieName = ".MonitoringApp.UiCulture";
    public const string ChangePath = "/ui/language";
    public const string English = "en-US";
    public const string German = "de-DE";

    public static IServiceCollection AddUiLocalization(this IServiceCollection services)
    {
        UiResources.ValidateEnglish();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddSingleton(provider => new JsonUiLanguageStore(
            Path.Combine(AppContext.BaseDirectory, "Localization"),
            provider.GetRequiredService<ILogger<JsonUiLanguageStore>>()));
        services.AddScoped<UiLanguageContext>();
        services.AddScoped<UiLanguageAdministration>();
        services.AddScoped<IStringLocalizer<UiResources>, JsonUiStringLocalizer>();
        services.AddScoped<UiText>();
        services.AddScoped<QueryTextPresentation>();
        // Existing machine parsing/formatting must not inherit the selected UI language.
        var serverCulture = CultureInfo.GetCultureInfo(CultureInfo.CurrentCulture.Name);
        services.AddOptions<RequestLocalizationOptions>().Configure<ILoggerFactory>((options, logging) =>
        {
            options.DefaultRequestCulture = new RequestCulture(serverCulture, CultureInfo.GetCultureInfo(English));
            options.SupportedCultures = [serverCulture];
            // The sole provider validates UI cultures against the request's registry snapshot.
            options.SupportedUICultures = UiLanguageConfiguration.AvailableCultures();
            options.FallBackToParentUICultures = false;
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
            var languages = httpContext.RequestServices.GetRequiredService<UiLanguageContext>();
            var selected = languages.DefaultCulture;
            if (httpContext.Request.Cookies.TryGetValue(CookieName, out var cookie))
            {
                var parsed = CookieRequestCultureProvider.ParseCookieValue(cookie);
                var requested = parsed?.UICultures.Count == 1
                    ? languages.CanonicalCulture(parsed.UICultures[0].Value) : null;
                if (requested is null)
                    logger.LogWarning("An invalid, disabled or unsupported UI culture cookie was ignored; using configured default {Culture}.",
                        languages.DefaultCulture);
                else
                    selected = requested;
            }

            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(serverCulture.Name, selected));
        }
    }
}
