using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed class UiLocalizationTests
{
    [Theory]
    [InlineData("en-US", "Language", "Change language")]
    [InlineData("de-DE", "Sprache", "Sprache wechseln")]
    public void StandardResourcesAreCultureSpecific(string culture, string title, string apply)
    {
        using var selection = new UiCultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        using var scope = services.CreateScope();
        var text = scope.ServiceProvider.GetRequiredService<UiText>();
        UiResources.ValidateEnglish();
        Assert.Equal(title, text["Language.Title"]);
        Assert.Equal(apply, text["Language.Apply"]);
        Assert.Throws<InvalidOperationException>(() => text["Missing.English.Key"]);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Language.Apply", true)]
    [InlineData("   ", false)]
    public void MissingOrEmptyGermanTextFallsBackIndividuallyWithoutChangingLanguage(string value, bool missing)
    {
        using var selection = new UiCultureScope("de-DE");
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var real = services.GetRequiredService<IStringLocalizer<UiResources>>();
        var text = new UiText(new OverriddenLocalizer(real, value, missing), NullLogger<UiText>.Instance);
        Assert.Equal("Change language", text["Language.Apply"]);
        Assert.Equal("Sprache", text["Language.Title"]);
        Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void MissingGermanSatelliteUsesRealEnglishResourcesWhileGermanRemainsSelected()
    {
        using var selection = new UiCultureScope("de-DE");
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        var assembly = typeof(UiLocalizationTests).Assembly;
        Assert.Throws<FileNotFoundException>(() => assembly.GetSatelliteAssembly(CultureInfo.GetCultureInfo("de-DE")));
        var real = services.GetRequiredService<IStringLocalizerFactory>()
            .Create("EnglishOnlyResources", assembly.GetName().Name!);
        Assert.False(real["Language.Title"].ResourceNotFound);
        var text = new UiText(new OverriddenLocalizer(real, "", false, overrideApply: false), NullLogger<UiText>.Instance);
        Assert.Equal("Language", text["Language.Title"]);
        Assert.Equal("Change language", text["Language.Apply"]);
        Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void AbsentSatelliteFileUsesEnglishWithoutChangingSelectedCulture()
    {
        using var selection = new UiCultureScope("fr-FR");
        using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        using var scope = services.CreateScope();
        Assert.Equal("Language", scope.ServiceProvider.GetRequiredService<UiText>()["Language.Title"]);
        Assert.Equal("fr-FR", CultureInfo.CurrentUICulture.Name);
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/admin/settings?tab=language#editor", true)]
    [InlineData("//example.test", false)]
    [InlineData("/\\example.test", false)]
    [InlineData("https://example.test", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData("/\r\nLocation: https://example.test", false)]
    [InlineData("/folder\\other", false)]
    public void ReturnUrlsMustBeLocal(string url, bool expected) =>
        Assert.Equal(expected, UiLanguageEndpoints.IsLocalReturnUrl(url));

    [Fact]
    public async Task BrowserHeaderAndQueryCannotChooseLanguageWithoutCookie()
    {
        await using var fixture = await Fixture.Start();
        fixture.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE");
        var result = await fixture.Client.GetFromJsonAsync<Probe>("/probe?culture=de-DE&ui-culture=de-DE");
        Assert.Equal("en-US", result!.UiCulture);
        Assert.Equal("Language", result.Title);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("c=fr-FR|uic=fr-FR")]
    [InlineData("c=de-DE|uic=unsupported")]
    public async Task UnsupportedCookieFallsBackToEnglish(string cookie)
    {
        await using var fixture = await Fixture.Start();
        fixture.Client.DefaultRequestHeaders.Add("Cookie", $"{UiLocalization.CookieName}={Uri.EscapeDataString(cookie)}");
        var result = await fixture.Client.GetFromJsonAsync<Probe>("/probe");
        Assert.Equal("en-US", result!.UiCulture);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task CookieChangesOnlyUiCultureAndPreservesRealIngestionParsing(string serverCulture)
    {
        await using var fixture = await Fixture.Start(serverCulture);
        var before = await fixture.Client.GetFromJsonAsync<Probe>("/probe");
        var token = await fixture.Client.GetFromJsonAsync<string>("/token");
        using var response = await fixture.Change("de-DE", "/probe", token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/probe", response.Headers.Location!.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains(UiLocalization.CookieName, cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=31536000", cookie, StringComparison.OrdinalIgnoreCase);
        var after = await fixture.Client.GetFromJsonAsync<Probe>("/probe");
        Assert.Equal("de-DE", after!.UiCulture);
        Assert.Equal("Sprache", after.Title);
        Assert.Equal(serverCulture, before!.FormatCulture);
        Assert.Equal(before.FormatCulture, after.FormatCulture);
        Assert.Equal(before.FiredAt, after.FiredAt);
        Assert.Equal(before.RawJson, after.RawJson);
        Assert.Equal(serverCulture == "en-US" ? 2 : 1, after.FiredAt!.Value.Day);
        var englishToken = await fixture.Client.GetFromJsonAsync<string>("/token");
        using var switchedBack = await fixture.Change("en-US", "/probe", englishToken);
        Assert.Equal(HttpStatusCode.Redirect, switchedBack.StatusCode);
        Assert.Equal("en-US", (await fixture.Client.GetFromJsonAsync<Probe>("/probe"))!.UiCulture);
    }

    [Theory]
    [InlineData("fr-FR", "/probe", true)]
    [InlineData("de-DE", "//example.test", true)]
    [InlineData("de-DE", "/\\example.test", true)]
    [InlineData("de-DE", "https://example.test", true)]
    [InlineData("de-DE", "/probe", false)]
    public async Task InvalidOrCsrfRequestsDoNotWriteCultureCookie(string culture, string returnUrl, bool validToken)
    {
        await using var fixture = await Fixture.Start();
        var token = validToken ? await fixture.Client.GetFromJsonAsync<string>("/token") : "forged-token";
        using var response = await fixture.Change(culture, returnUrl, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("en-US", (await fixture.Client.GetFromJsonAsync<Probe>("/probe"))!.UiCulture);
    }

    [Fact]
    public async Task IndependentBrowsersAndNewRequestsKeepTheirOwnSelection()
    {
        await using var fixture = await Fixture.Start();
        using var other = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = fixture.Client.BaseAddress };
        var token = await fixture.Client.GetFromJsonAsync<string>("/token");
        using var response = await fixture.Change("de-DE", "/probe", token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var independent = await Task.WhenAll(
            fixture.Client.GetFromJsonAsync<Probe>("/probe"),
            other.GetFromJsonAsync<Probe>("/probe"));
        Assert.Equal("de-DE", independent[0]!.UiCulture);
        Assert.Equal("en-US", independent[1]!.UiCulture);
        Assert.Equal("de-DE", (await fixture.Client.GetFromJsonAsync<Probe>("/probe"))!.UiCulture);
    }

    [Fact]
    public async Task HttpsWritesSecureCookieAndGetCannotChangeLanguage()
    {
        await using var fixture = await Fixture.Start();
        using var get = await fixture.Client.GetAsync(UiLocalization.ChangePath);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        var token = await fixture.Client.GetFromJsonAsync<string>("/token");
        fixture.Client.DefaultRequestHeaders.Add("X-Test-Https", "true");
        using var response = await fixture.Change("de-DE", "/probe", token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("secure", Assert.Single(response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en-US", "Cancel", "Unsaved changes")]
    [InlineData("de-DE", "Abbrechen", "Ungespeicherte")]
    public async Task SelectorRendersNativePostAndSafeCancel(string culture, string cancel, string warning)
    {
        using var selection = new UiCultureScope(culture);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddUiLocalization();
        builder.Services.AddScoped<NavigationManager, TestNavigation>();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<LanguageSelector>()).ToHtmlString());
        html = WebUtility.HtmlDecode(html);
        Assert.Contains("action=\"/ui/language\"", html);
        Assert.Contains("method=\"post\"", html);
        Assert.Contains($"data-current-culture=\"{culture}\"", html);
        Assert.Contains("data-language-cancel", html);
        Assert.Contains("autofocus", html);
        Assert.Contains(cancel, html);
        Assert.Contains(warning, html);
        Assert.Contains("disabled", html);
        var text = scope.ServiceProvider.GetRequiredService<UiText>();
        Assert.Contains(text["Language.BrowserUnavailable"], html);
        Assert.Contains(text["Language.BrowserFailed"], html);
        Assert.Contains("data-language-unavailable hidden role=\"alert\"", html);
        Assert.Contains("data-language-error hidden role=\"alert\"", html);
    }

    private sealed class OverriddenLocalizer(IStringLocalizer original, string value, bool missing, bool overrideApply = true)
        : IStringLocalizer<UiResources>
    {
        public LocalizedString this[string name] => overrideApply && name == "Language.Apply"
            ? new LocalizedString(name, value, missing) : original[name];
        public LocalizedString this[string name, params object[] arguments] => this[name];
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => original.GetAllStrings(includeParentCultures);
    }

    private sealed class UiCultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiCultureScope(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/admin/settings");
    }

    private sealed record Probe(string UiCulture, string FormatCulture, string Title, DateTimeOffset? FiredAt, string RawJson);

    private sealed class Fixture(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public Task<HttpResponseMessage> Change(string culture, string returnUrl, string? token) =>
            Client.PostAsync(UiLocalization.ChangePath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Culture"] = culture, ["ReturnUrl"] = returnUrl,
                ["__RequestVerificationToken"] = token ?? string.Empty
            }));

        public static async Task<Fixture> Start(string serverCulture = "en-US")
        {
            var previous = CultureInfo.CurrentCulture;
            WebApplication app;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(serverCulture);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [] });
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddUiLocalization();
                builder.Services.AddAntiforgery();
                app = builder.Build();
            }
            finally { CultureInfo.CurrentCulture = previous; }
            app.Use((context, next) =>
            {
                if (context.Request.Headers["X-Test-Https"] == "true") context.Request.Scheme = "https";
                return next(context);
            });
            app.UseRequestLocalization();
            app.UseAntiforgery();
            app.MapUiLanguage();
            app.MapGet("/token", (HttpContext context, IAntiforgery antiforgery) =>
                TypedResults.Ok(antiforgery.GetAndStoreTokens(context).RequestToken));
            app.MapGet("/probe", (UiText text) =>
            {
                using var payload = JsonDocument.Parse("""{"name":"Original alert","firedDateTime":"01/02/2026 12:00:00 +00:00"}""");
                var (alert, _) = AlertPayload.Parse(payload.RootElement);
                return TypedResults.Ok(new Probe(CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name,
                    text["Language.Title"], alert.FiredAt, alert.RawJson));
            });
            try { await app.StartAsync(); }
            catch { await app.DisposeAsync(); throw; }
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Fixture(app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
