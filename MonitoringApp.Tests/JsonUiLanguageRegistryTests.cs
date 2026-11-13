using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed partial class JsonUiLocalizationTests
{
    [Fact]
    public void RegistryStartsWithLegacySelectionAndIsCopiedToOutput()
    {
        var source = Path.Combine(ProjectDirectory(), "Localization", UiLanguageConfiguration.FileName);
        var output = Path.Combine(AppContext.BaseDirectory, "Localization", UiLanguageConfiguration.FileName);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(output));
        using var files = new LanguageFiles();
        using var services = files.Services();
        using var scope = services.CreateScope();
        var languages = scope.ServiceProvider.GetRequiredService<UiLanguageContext>();
        Assert.Equal("en-US", languages.DefaultCulture);
        Assert.Equal(new[] { "English", "Deutsch" }, languages.EnabledLanguages.Select(language => language.DisplayName));
        Assert.Equal("de-DE", languages.CanonicalCulture("DE-de"));
        Assert.Null(languages.CanonicalCulture("fr-FR"));
    }

    [Fact]
    public async Task RegistryAddsLanguageAndSwitchesDefaultOnTheSameRunningHost()
    {
        using var files = new LanguageFiles();
        await using var host = await RegistryHost.Start(files);
        var before = await host.Probe();
        Assert.Equal("en-US", before.UiCulture);
        AddLanguage(files, "fr-FR", "Francais", """{"Language.Title":"Langue"}""");
        EditRegistry(files, registry => registry["defaultCulture"] = "fr-FR");
        var after = await host.Probe();
        Assert.Equal("fr-FR", after.UiCulture);
        Assert.Equal("fr-FR", after.DefaultCulture);
        Assert.Equal("Langue", after.Title);
        Assert.Equal("Change language", after.Apply);
        Assert.Equal(new[] { "en-US", "de-DE", "fr-FR" }, after.Enabled);
        Assert.NotEqual(before.Revision, after.Revision);
        Assert.Equal(before.FormatCulture, after.FormatCulture);
        Assert.Equal(before.ParsedDate, after.ParsedDate);
        Assert.Equal(before.RawJson, after.RawJson);
        Assert.False(after.Warning);
        Assert.Contains(LogLevel.Warning, files.Logger.Levels);

        EditRegistry(files, registry => registry["defaultCulture"] = "de-DE");
        Assert.Equal("de-DE", (await host.Probe()).UiCulture);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RegistryUiCultureNeverChangesServerParsingCulture(string serverCulture)
    {
        using var files = new LanguageFiles();
        AddLanguage(files, "fr-FR", "Francais", """{"Language.Title":"Langue"}""");
        await using var host = await RegistryHost.Start(files, serverCulture);
        var original = await host.Probe();
        EditRegistry(files, registry => registry["defaultCulture"] = "fr-FR");
        var updated = await host.Probe();
        Assert.Equal("fr-FR", updated.UiCulture);
        Assert.Equal(serverCulture, updated.FormatCulture);
        Assert.Equal(original.ParsedDate, updated.ParsedDate);
        Assert.Equal(original.RawJson, updated.RawJson);
        Assert.Equal(serverCulture == "en-US" ? 2 : 1, updated.ParsedDate!.Value.Day);
    }

    [Theory]
    [InlineData("broken", "de-DE")]
    [InlineData("c=en-US|uic=zz-ZZ", "de-DE")]
    [InlineData("c=en-US|uic=fr-FR", "de-DE")]
    [InlineData("c=en-US|uic=en-US", "en-US")]
    public async Task RegistryDefaultReplacesOnlyInvalidOrUnsupportedCookies(string cookie, string expected)
    {
        using var files = new LanguageFiles();
        EditRegistry(files, registry => registry["defaultCulture"] = "de-DE");
        await using var host = await RegistryHost.Start(files);
        host.SetCookie(cookie);
        Assert.Equal(expected, (await host.Probe()).UiCulture);
    }

    [Fact]
    public async Task RegistryDisablingOrRemovingLanguageRejectsItsCookieAndPostButKeepsValidCookies()
    {
        using var files = new LanguageFiles();
        AddLanguage(files, "fr-FR", "Francais", """{"Language.Title":"Langue"}""");
        EditRegistry(files, registry => registry["defaultCulture"] = "fr-FR");
        await using var host = await RegistryHost.Start(files);
        host.SetCookie("c=en-US|uic=de-DE");
        Assert.Equal("de-DE", (await host.Probe()).UiCulture);
        EditRegistry(files, registry => registry["languages"]![1]!["enabled"] = false);
        Assert.Equal("fr-FR", (await host.Probe()).UiCulture);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Change("de-DE")).StatusCode);
        EditRegistry(files, registry => registry["languages"]!.AsArray().RemoveAt(1));
        Assert.Equal("fr-FR", (await host.Probe()).UiCulture);
        host.SetCookie("c=en-US|uic=en-US");
        Assert.Equal("en-US", (await host.Probe()).UiCulture);
    }

    [Fact]
    public async Task RegistryEnglishCanBeDisabledForSelectionButRemainsTheMandatoryTextFallback()
    {
        using var files = new LanguageFiles();
        files.Edit("en-US", values => values["Language.Apply"] = "Current English fallback");
        files.Edit("de-DE", values => values.Remove("Language.Apply"));
        EditRegistry(files, registry =>
        {
            registry["defaultCulture"] = "de-DE";
            registry["languages"]![0]!["enabled"] = false;
        });
        await using var host = await RegistryHost.Start(files);
        host.SetCookie("c=en-US|uic=en-US");
        var probe = await host.Probe();
        Assert.Equal("de-DE", probe.UiCulture);
        Assert.Equal("Current English fallback", probe.Apply);
        Assert.Equal(new[] { "de-DE" }, probe.Enabled);
        using var response = await host.Change("en-US");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task RegistryLanguagePostSupportsNewCultureAndStillRequiresAntiforgeryAndLocalRedirect()
    {
        using var files = new LanguageFiles();
        await using var host = await RegistryHost.Start(files);
        AddLanguage(files, "fr-FR", "Francais", """{"Language.Title":"Langue"}""");
        using var response = await host.Change("FR-fr");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/probe", response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("uic%3Dfr-FR", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fr-FR", (await host.Probe()).UiCulture);
        using var csrf = await host.Change("fr-FR", token: false);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        Assert.False(csrf.Headers.Contains("Set-Cookie"));
        using var redirect = await host.Change("fr-FR", returnUrl: "//example.test");
        Assert.Equal(HttpStatusCode.BadRequest, redirect.StatusCode);
        Assert.False(redirect.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task RegistrySelectorUsesConfiguredOrderNamesAndOnlyEnabledEntries()
    {
        using var files = new LanguageFiles();
        AddLanguage(files, "fr-FR", "<b>Francais</b>", """{"Language.Title":"Langue"}""");
        EditRegistry(files, registry => registry["languages"]![1]!["enabled"] = false);
        await using var host = await RegistryHost.Start(files);
        var html = await host.Client.GetStringAsync("/selector");
        Assert.Contains("value=\"en-US\"", html);
        Assert.Contains("value=\"fr-FR\"", html);
        Assert.DoesNotContain("value=\"de-DE\"", html);
        Assert.Contains("&lt;b&gt;Francais&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Francais</b>", html);
        Assert.True(html.IndexOf("value=\"en-US\"", StringComparison.Ordinal) <
            html.IndexOf("value=\"fr-FR\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RegistryCanMapToRenamedFilesWithoutHardcodedCultureFilenames()
    {
        using var culture = new CultureScope("de-DE");
        using var files = new LanguageFiles();
        File.Move(files.PathFor("en-US"), Path.Combine(Path.GetDirectoryName(files.PathFor("en-US"))!, "core-english.json"));
        File.Move(files.PathFor("de-DE"), Path.Combine(Path.GetDirectoryName(files.PathFor("de-DE"))!, "custom-german.json"));
        EditRegistry(files, registry =>
        {
            registry["languages"]![0]!["file"] = "core-english.json";
            registry["languages"]![1]!["file"] = "custom-german.json";
        });
        using var services = files.Services();
        using var scope = services.CreateScope();
        Assert.Equal("Sprache", scope.ServiceProvider.GetRequiredService<UiText>()["Language.Title"]);
    }

    [Fact]
    public async Task RegistryProviderAndTextUseOneSnapshotEvenIfFilesChangeDuringRequest()
    {
        using var files = new LanguageFiles();
        var changed = false;
        await using var host = await RegistryHost.Start(files, duringRequest: () =>
        {
            if (changed) return;
            EditRegistry(files, registry => registry["defaultCulture"] = "de-DE");
            files.Edit("de-DE", values => values["Language.Title"] = "Neue Sprache");
            changed = true;
        });
        var first = await host.Probe();
        Assert.Equal("en-US", first.UiCulture);
        Assert.Equal("en-US", first.DefaultCulture);
        Assert.Equal("Language", first.Title);
        var second = await host.Probe();
        Assert.Equal("de-DE", second.UiCulture);
        Assert.Equal("de-DE", second.DefaultCulture);
        Assert.Equal("Neue Sprache", second.Title);
        Assert.NotEqual(first.Revision, second.Revision);
    }

    [Fact]
    public async Task RegistryInvalidCandidateKeepsConfigDefaultAndAllPacksTogetherThenRecovers()
    {
        using var files = new LanguageFiles();
        await using var host = await RegistryHost.Start(files);
        var original = await host.Probe();
        EditRegistry(files, registry => registry["defaultCulture"] = "de-DE");
        files.Edit("en-US", values => values["Language.Title"] = "Not yet active");
        files.Write("de-DE", "{");
        var failed = await host.Probe();
        Assert.Equal("en-US", failed.UiCulture);
        Assert.Equal(original.DefaultCulture, failed.DefaultCulture);
        Assert.Equal(original.Title, failed.Title);
        Assert.Equal(original.Revision, failed.Revision);
        Assert.True(failed.Warning);
        files.Write("de-DE", """{"Language.Title":"Neue Sprache"}""");
        var recovered = await host.Probe();
        Assert.Equal("de-DE", recovered.UiCulture);
        Assert.Equal("Neue Sprache", recovered.Title);
        Assert.NotEqual(original.Revision, recovered.Revision);
        Assert.False(recovered.Warning);
    }

    [Fact]
    public void RegistryDoesNotReportFallbackActivationForAnInvalidCandidate()
    {
        using var files = new LanguageFiles();
        files.Edit("de-DE", values => values.Remove("Language.Apply"));
        AddLanguage(files, "fr-FR", "Francais", "{");
        Assert.Throws<InvalidOperationException>(files.Store.Validate);
        Assert.DoesNotContain(LogLevel.Warning, files.Logger.Levels);
        Assert.Contains(LogLevel.Critical, files.Logger.Levels);
    }

    [Theory]
    [InlineData("unsupported-version")]
    [InlineData("missing-version")]
    [InlineData("missing-default")]
    [InlineData("disabled-default")]
    [InlineData("no-active-languages")]
    [InlineData("unknown-default")]
    [InlineData("unknown-culture")]
    [InlineData("duplicate-culture")]
    [InlineData("duplicate-file")]
    [InlineData("missing-english")]
    [InlineData("empty-languages")]
    [InlineData("null-languages")]
    [InlineData("null-language")]
    [InlineData("missing-display-name")]
    [InlineData("blank-display-name")]
    [InlineData("long-display-name")]
    [InlineData("control-display-name")]
    [InlineData("missing-enabled")]
    [InlineData("string-enabled")]
    [InlineData("missing-pack")]
    [InlineData("disabled-missing-pack")]
    [InlineData("unknown-property")]
    [InlineData("fallback-cycle")]
    public void RegistryRejectsInvalidConfiguration(string scenario)
    {
        using var files = new LanguageFiles();
        EditRegistry(files, registry =>
        {
            var languages = registry["languages"]!.AsArray();
            switch (scenario)
            {
                case "unsupported-version": registry["version"] = 2; break;
                case "missing-version": registry.Remove("version"); break;
                case "missing-default": registry.Remove("defaultCulture"); break;
                case "disabled-default": languages[0]!["enabled"] = false; break;
                case "no-active-languages":
                    languages[0]!["enabled"] = false;
                    languages[1]!["enabled"] = false;
                    break;
                case "unknown-default": registry["defaultCulture"] = "fr-FR"; break;
                case "unknown-culture": languages[1]!["culture"] = "zz-ZZ"; break;
                case "duplicate-culture": languages[1]!["culture"] = "EN-us"; break;
                case "duplicate-file": languages[1]!["file"] = "UI.EN-us.JSON"; break;
                case "missing-english":
                    languages.RemoveAt(0);
                    registry["defaultCulture"] = "de-DE";
                    break;
                case "empty-languages": languages.Clear(); break;
                case "null-languages": registry["languages"] = null; break;
                case "null-language": languages[1] = null; break;
                case "missing-display-name": languages[1]!.AsObject().Remove("displayName"); break;
                case "blank-display-name": languages[1]!["displayName"] = " "; break;
                case "long-display-name": languages[1]!["displayName"] = new string('a', 129); break;
                case "control-display-name": languages[1]!["displayName"] = "bad\nname"; break;
                case "missing-enabled": languages[1]!.AsObject().Remove("enabled"); break;
                case "string-enabled": languages[1]!["enabled"] = "true"; break;
                case "missing-pack": languages[1]!["file"] = "missing.json"; break;
                case "disabled-missing-pack":
                    languages[1]!["file"] = "missing.json";
                    languages[1]!["enabled"] = false;
                    break;
                case "unknown-property": registry["unexpected"] = true; break;
                case "fallback-cycle": languages[1]!["fallbackCulture"] = "de-DE"; break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        });
        Assert.Throws<InvalidOperationException>(files.Store.Validate);
        Assert.Contains(LogLevel.Critical, files.Logger.Levels);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("..\\outside.json")]
    [InlineData("C:\\outside.json")]
    [InlineData("\\\\server\\share\\outside.json")]
    [InlineData("https://example.test/pack.json")]
    [InlineData("subdir/pack.json")]
    [InlineData("pack.json:secret")]
    [InlineData("languages.json")]
    [InlineData("LANGUAGES.JSON")]
    [InlineData("pack.txt")]
    [InlineData("NUL.json")]
    [InlineData("con.more.json")]
    [InlineData("")]
    [InlineData(null)]
    public void RegistryRejectsUnsafeLanguageFilenames(string? file)
    {
        using var files = new LanguageFiles();
        EditRegistry(files, registry => registry["languages"]![1]!["file"] = file);
        Assert.Throws<InvalidOperationException>(files.Store.Validate);
        Assert.Contains(LogLevel.Critical, files.Logger.Levels);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("malformed-json")]
    [InlineData("duplicate-property")]
    [InlineData("oversized-file")]
    [InlineData("invalid-utf8")]
    public void RegistryFailuresRetainAndReportTheLastGoodSnapshot(string failure)
    {
        using var files = new LanguageFiles();
        using var services = files.Services();
        using var first = services.CreateScope();
        var original = first.ServiceProvider.GetRequiredService<UiLanguageContext>();
        var path = RegistryPath(files);
        var originalBytes = File.ReadAllBytes(path);
        switch (failure)
        {
            case "missing-file": File.Delete(path); break;
            case "malformed-json": File.WriteAllText(path, "{"); break;
            case "duplicate-property":
                File.WriteAllText(path, Encoding.UTF8.GetString(originalBytes).Replace("\"version\": 1", "\"version\": 1, \"version\": 1"));
                break;
            case "oversized-file":
                File.WriteAllBytes(path, new byte[UiLanguageConfiguration.MaximumFileBytes + 1]);
                break;
            case "invalid-utf8": File.WriteAllBytes(path, [0xff]); break;
            default: throw new ArgumentOutOfRangeException(nameof(failure));
        }
        using var second = services.CreateScope();
        Assert.Equal(original.Revision, second.ServiceProvider.GetRequiredService<UiLanguageContext>().Revision);
        Assert.True(second.ServiceProvider.GetRequiredService<UiText>().HasResourceReloadFailure);
        File.WriteAllBytes(path, originalBytes);
        using var restored = services.CreateScope();
        Assert.Equal(original.Revision, restored.ServiceProvider.GetRequiredService<UiLanguageContext>().Revision);
        Assert.False(restored.ServiceProvider.GetRequiredService<UiText>().HasResourceReloadFailure);
        Assert.Contains(LogLevel.Error, files.Logger.Levels);
    }

    [Fact]
    public void RegistryExactSizeLimitAndUtf8BomAreAccepted()
    {
        using var files = new LanguageFiles();
        var path = RegistryPath(files);
        var bytes = Encoding.UTF8.GetPreamble().Concat(File.ReadAllBytes(path)).ToArray();
        File.WriteAllBytes(path, bytes.Concat(Enumerable.Repeat((byte)' ',
            UiLanguageConfiguration.MaximumFileBytes - bytes.Length)).ToArray());
        Assert.Equal(UiLanguageConfiguration.MaximumFileBytes, new FileInfo(path).Length);
        files.Store.Validate();
    }

    [Theory]
    [InlineData(UiLanguageConfiguration.MaximumLanguages, true)]
    [InlineData(UiLanguageConfiguration.MaximumLanguages + 1, false)]
    public void RegistryLanguageCountLimitIsEnforcedExactly(int count, bool valid)
    {
        var cultures = new[] { "en-US" }.Concat(CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Select(culture => culture.Name).Where(name => name.Length > 0 && name != "en-US")).Take(count).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            version = 1, defaultCulture = "en-US",
            languages = cultures.Select((culture, index) => new
            {
                culture, displayName = culture, file = $"pack-{index}.json", enabled = true
            })
        });
        if (valid)
            Assert.Equal(count, UiLanguageConfiguration.Parse(json).Languages.Count);
        else
            Assert.Throws<InvalidDataException>(() => UiLanguageConfiguration.Parse(json));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"version":1,"version":1,"defaultCulture":"en-US","languages":[]}""")]
    public void RegistryInvalidInitialDocumentFailsBeforeActivation(string json)
    {
        using var files = new LanguageFiles();
        File.WriteAllText(RegistryPath(files), json);
        Assert.Throws<InvalidOperationException>(files.Store.Validate);
        Assert.Contains(LogLevel.Critical, files.Logger.Levels);
    }

    [Fact]
    public void RegistryIsMandatoryAtStartup()
    {
        using var files = new LanguageFiles();
        File.Delete(RegistryPath(files));
        Assert.Throws<InvalidOperationException>(files.Store.Validate);
        Assert.Contains(LogLevel.Critical, files.Logger.Levels);
    }

    private static string RegistryPath(LanguageFiles files) =>
        Path.Combine(Path.GetDirectoryName(files.PathFor("en-US"))!, UiLanguageConfiguration.FileName);

    private static void EditRegistry(LanguageFiles files, Action<JsonObject> edit)
    {
        var registry = JsonNode.Parse(File.ReadAllText(RegistryPath(files)))!.AsObject();
        edit(registry);
        File.WriteAllText(RegistryPath(files), registry.ToJsonString());
    }

    private static void AddLanguage(LanguageFiles files, string culture, string name, string json)
    {
        files.Write(culture, json);
        EditRegistry(files, registry => registry["languages"]!.AsArray().Add(new JsonObject
        {
            ["culture"] = culture, ["displayName"] = name, ["file"] = $"ui.{culture}.json", ["enabled"] = true
        }));
    }

    private sealed record RegistryProbe(string UiCulture, string FormatCulture, string DefaultCulture,
        string Title, string Apply, string[] Enabled, string Revision, bool Warning, DateTimeOffset? ParsedDate, string RawJson);

    private sealed class RegistryHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async Task<RegistryProbe> Probe() => (await Client.GetFromJsonAsync<RegistryProbe>("/probe"))!;
        public void SetCookie(string cookie)
        {
            Client.DefaultRequestHeaders.Remove("Cookie");
            Client.DefaultRequestHeaders.Add("Cookie", $"{UiLocalization.CookieName}={Uri.EscapeDataString(cookie)}");
        }
        public async Task<HttpResponseMessage> Change(string culture, bool token = true, string returnUrl = "/probe")
        {
            var antiforgery = token ? await Client.GetFromJsonAsync<string>("/token") : "forged-token";
            return await Client.PostAsync(UiLocalization.ChangePath, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Culture"] = culture, ["ReturnUrl"] = returnUrl, ["__RequestVerificationToken"] = antiforgery!
            }));
        }

        public static async Task<RegistryHost> Start(LanguageFiles files, string serverCulture = "en-US", Action? duringRequest = null)
        {
            var previous = CultureInfo.CurrentCulture;
            WebApplication app;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(serverCulture);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppContext.BaseDirectory, Args = []
                });
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddRazorComponents().AddInteractiveServerComponents();
                builder.Services.AddUiLocalization();
                builder.Services.AddSingleton(files.Store);
                builder.Services.AddScoped<NavigationManager, TestNavigation>();
                app = builder.Build();
            }
            finally { CultureInfo.CurrentCulture = previous; }
            app.UseRequestLocalization();
            app.UseAntiforgery();
            if (duringRequest is not null)
                app.Use((context, next) => { duringRequest(); return next(context); });
            app.MapUiLanguage();
            app.MapGet("/token", (HttpContext context, IAntiforgery antiforgery) =>
                TypedResults.Ok(antiforgery.GetAndStoreTokens(context).RequestToken));
            app.MapGet("/probe", (UiText text, UiLanguageContext languages) =>
            {
                using var payload = JsonDocument.Parse("""{"name":"Original alert","firedDateTime":"01/02/2026 12:00:00 +00:00"}""");
                var (alert, _) = AlertPayload.Parse(payload.RootElement);
                return new RegistryProbe(CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name,
                    languages.DefaultCulture, text["Language.Title"], text["Language.Apply"],
                    languages.EnabledLanguages.Select(language => language.Culture).ToArray(),
                    languages.Revision, text.HasResourceReloadFailure, alert.FiredAt, alert.RawJson);
            });
            app.MapGet("/selector", async (HttpContext context) =>
            {
                await using var renderer = new HtmlRenderer(context.RequestServices,
                    context.RequestServices.GetRequiredService<ILoggerFactory>());
                var html = await renderer.Dispatcher.InvokeAsync(async () =>
                    (await renderer.RenderComponentAsync<LanguageSelector>()).ToHtmlString());
                return TypedResults.Content(html, "text/html");
            });
            try { await app.StartAsync(); }
            catch { await app.DisposeAsync(); throw; }
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new RegistryHost(app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(address)
            });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
