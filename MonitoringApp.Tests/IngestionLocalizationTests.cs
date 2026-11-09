using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class IngestionLocalizationTests
{
    [Theory]
    [InlineData("en-US", "sql")]
    [InlineData("de-DE", "sql")]
    [InlineData("en-US", "entra")]
    [InlineData("de-DE", "entra")]
    [InlineData("en-US", "open")]
    [InlineData("de-DE", "open")]
    public async Task PageWarningsAndEndpointSlotsTranslateWithoutChangingTechnicalContracts(string culture, string mode)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = Fixture.Create(mode);
        var originalOptions = JsonSerializer.Serialize(fixture.Options);
        await fixture.Render();
        Assert.Contains(fixture.Text["IngestionAdmin.Title"], fixture.Decoded);
        Assert.Contains(fixture.Text["IngestionAdmin.DisabledTitle"], fixture.Decoded);
        Assert.Contains(fixture.Text["IngestionAdmin.NoRequests"], fixture.Decoded);
        Assert.Contains(fixture.Text.Format("IngestionAdmin.RetentionHint", 20), fixture.Decoded);
        foreach (var literal in new[]
        {
            "POST /api/alerts", "GET /api/alert-query-refresh-targets",
            "PUT /api/alerts/{id}/query-result", "azp", "idtyp=app"
        })
            Assert.Contains($"<code>{literal}</code>", fixture.Html);
        Assert.Contains("<code>Alerts.Write&lt;original&gt;</code>", fixture.Html);
        Assert.DoesNotContain("<original>", fixture.Html);
        if (mode == "open")
        {
            foreach (var literal in new[] { "open", "entra", "sql" })
                Assert.Contains($"<code>{literal}</code>", fixture.Html);
            Assert.Contains("AdminAccess", fixture.Decoded);
        }
        Assert.Equal(originalOptions, JsonSerializer.Serialize(fixture.Options));
        Assert.Equal(1, fixture.Diagnostics.Reads);
    }

    [Theory]
    [InlineData("en-US", true, true, "Present in validated token")]
    [InlineData("de-DE", true, true, "Im validierten Token vorhanden")]
    [InlineData("en-US", true, false, "Missing from validated token")]
    [InlineData("de-DE", true, false, "Fehlt im validierten Token")]
    [InlineData("en-US", false, true, "Present (unverified claim)")]
    [InlineData("de-DE", false, true, "Vorhanden (nicht verifizierter Claim)")]
    [InlineData("en-US", false, false, "Missing (unverified claims)")]
    [InlineData("de-DE", false, false, "Fehlt (nicht verifizierte Claims)")]
    [InlineData("en-US", false, null, "Unknown - no readable token")]
    [InlineData("de-DE", false, null, "Unbekannt - kein lesbares Token")]
    public async Task RoleDisplayDistinguishesVerifiedUnverifiedMissingAndUnknownWithoutTranslatingIdentity(
        string culture, bool validated, bool? role, string expected)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = Fixture.Create();
        fixture.Options.Enabled = true;
        var record = Record(validated, role);
        fixture.Diagnostics.Items = [record];
        var original = JsonSerializer.Serialize(record);
        await fixture.Render();
        Assert.Contains(expected, fixture.Decoded);
        Assert.Equal(role, record.RequiredRolePresent);
        Assert.Contains("Original authentication diagnostic", fixture.Decoded);
        Assert.Contains("Original inspection diagnostic", fixture.Decoded);
        Assert.Contains("original-v1-fallback", fixture.Decoded);
        foreach (var literal in new[] { "original-tenant", "original-object", "original-issuer", "Original.Role" })
            Assert.Contains($"<code>{literal}</code>", fixture.Html);
        Assert.Contains("<code>Original &lt;audience&gt;</code>", fixture.Html);
        Assert.Contains(">1.0</dd>", fixture.Html);
        Assert.Contains(">app</dd>", fixture.Html);
        Assert.Contains("2026-11-06 13:34:18", fixture.Decoded);
        Assert.Contains("trace-&lt;original&gt;", fixture.Html);
        Assert.Contains("2026-11-06 12:34:18 UTC", fixture.Decoded);
        Assert.Contains(fixture.Text.Format("IngestionAdmin.Blocked", 403), fixture.Decoded);
        Assert.Contains(fixture.Text["IngestionAdmin.Truncated"], fixture.Decoded);
        Assert.Equal(!validated, fixture.Decoded.Contains(fixture.Text["IngestionAdmin.UnverifiedWarning"], StringComparison.Ordinal));
        Assert.Contains(fixture.Text["IngestionAdmin.AssignmentHint"], fixture.Decoded);
        if (role.HasValue)
        {
            Assert.Contains(fixture.Text.Format("IngestionAdmin.CheckResult",
                fixture.Text[role.Value ? "IngestionAdmin.Pass" : "IngestionAdmin.Fail"], "Required application role"), fixture.Decoded);
            Assert.Contains("Original policy detail", fixture.Decoded);
        }
        else Assert.Contains(fixture.Text["IngestionAdmin.NoClaims"], fixture.Decoded);
        Assert.Equal(original, JsonSerializer.Serialize(record));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task MissingValuesAndDisabledAuthenticationNeverImplyVerifiedIdentityOrBlocking(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = Fixture.Create();
        fixture.Diagnostics.Items = [Record(false, null) with
        {
            AuthenticationRequired = false, TenantId = null, ApplicationId = null,
            Audiences = [], Roles = [], ExpiresAt = null, ListsTruncated = false
        }];
        await fixture.Render();
        Assert.Contains($"<code>{fixture.Text["Common.Missing"]}</code>", fixture.Html);
        Assert.Contains($"<code>{fixture.Text["Common.None"]}</code>", fixture.Html);
        Assert.Contains(fixture.Text["IngestionAdmin.ExpiryMissing"], fixture.Decoded);
        Assert.Contains(fixture.Text["IngestionAdmin.RoleUnknown"], fixture.Decoded);
        Assert.Contains(fixture.Text.Format("IngestionAdmin.HttpStatus", 403), fixture.Decoded);
        Assert.DoesNotContain("ingestion-diagnostic-blocked", fixture.Html);
        Assert.Contains(fixture.Text["IngestionAdmin.UnverifiedWarning"], fixture.Decoded);
        Assert.DoesNotContain(fixture.Text["IngestionAdmin.Truncated"], fixture.Decoded);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task RefreshOnlyReadsExistingHistoryAndRetainsNeutralConfigurationMessages(string culture)
    {
        using var selection = new UiSelection(culture);
        await using var fixture = Fixture.Create();
        fixture.Options.Audience = "";
        fixture.Options.Sources = [];
        fixture.Diagnostics.Items = [Record(false, true)];
        var originalErrors = fixture.Options.ValidateConfiguration().ToArray();
        await fixture.Render();
        Assert.Contains(fixture.Text["IngestionAdmin.ConfigurationInvalid"], fixture.Decoded);
        Assert.Contains(fixture.Text["IngestionAdmin.NoSources"], fixture.Decoded);
        foreach (var error in originalErrors) Assert.Contains(error, fixture.Decoded);
        Assert.Contains(fixture.Text.Format("IngestionAdmin.RecentRequests", 1), fixture.Decoded);
        fixture.Diagnostics.Items = [];
        await fixture.Refresh();
        Assert.Equal(2, fixture.Diagnostics.Reads);
        Assert.Contains(fixture.Text["IngestionAdmin.NoRequests"], fixture.Decoded);
        Assert.Contains(fixture.Text.Format("IngestionAdmin.RecentRequests", 0), fixture.Decoded);
        Assert.Equal(originalErrors, fixture.Options.ValidateConfiguration());
    }

    private static AlertIngestionRequestDiagnostic Record(bool validated, bool? role) => new(
        RecordedAt: new DateTimeOffset(2026, 11, 6, 12, 34, 18, TimeSpan.Zero),
        Method: "PUT", Endpoint: "/api/alerts/{id}/query-result", StatusCode: 403,
        TraceId: "trace-<original>", AuthenticationRequired: true, TokenValidated: validated,
        AuthenticationStatus: "Original authentication diagnostic",
        InspectionStatus: "Original inspection diagnostic", TenantId: "original-tenant",
        AuthorizedParty: null, ApplicationId: "original-v1-fallback", ObjectId: "original-object",
        TokenVersion: "1.0", IdentityType: "app", Issuer: "original-issuer",
        Audiences: ["Original <audience>"], Roles: ["Original.Role"], ListsTruncated: true,
        ExpiresAt: new DateTimeOffset(2026, 11, 6, 13, 34, 18, TimeSpan.Zero),
        PolicyChecks: role.HasValue
            ? [new AlertIngestionAuthorizationCheck("Required application role", role.Value, "Original policy detail")]
            : []);

    private sealed class UiSelection : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentUICulture;
        public UiSelection(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class ReadOnlyDiagnostics : IAlertIngestionDiagnostics
    {
        public IReadOnlyList<AlertIngestionRequestDiagnostic> Items { get; set; } = [];
        public int Reads { get; private set; }
        public IReadOnlyList<AlertIngestionRequestDiagnostic> GetRecent() { Reads++; return Items; }
        public void Record(HttpContext context, Microsoft.AspNetCore.Authentication.AuthenticateResult? authentication) =>
            throw new InvalidOperationException("The read-only page must not record or authenticate requests.");
    }

    private sealed class Activator(IServiceProvider services) : IComponentActivator
    {
        public AdminAlertAuthentication Page { get; private set; } = default!;
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, type);
            if (component is AdminAlertAuthentication page) Page = page;
            return component;
        }
    }

    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type type, int? parentId, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type)
                : throw new NotSupportedException($"Unsupported test render mode: {mode.GetType().Name}");
    }

    private sealed class Fixture(WebApplication app, AsyncServiceScope scope, Renderer renderer) : IAsyncDisposable
    {
        public AlertIngestionAuthenticationOptions Options => scope.ServiceProvider.GetRequiredService<AlertIngestionAuthenticationOptions>();
        public ReadOnlyDiagnostics Diagnostics => scope.ServiceProvider.GetRequiredService<ReadOnlyDiagnostics>();
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        private HtmlRootComponent root;
        public string Html => renderer.Dispatcher.InvokeAsync(root.ToHtmlString).GetAwaiter().GetResult();
        public string Decoded => WebUtility.HtmlDecode(Html);

        public async Task Render() => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = renderer.BeginRenderingComponent(typeof(AdminAlertAuthentication), ParameterView.Empty);
            await root.QuiescenceTask;
        });
        public Task Refresh() => renderer.Dispatcher.InvokeAsync(() =>
        {
            var page = scope.ServiceProvider.GetRequiredService<Activator>().Page;
            typeof(AdminAlertAuthentication).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
        });

        public static Fixture Create(string mode = "sql")
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents();
            builder.Services.AddUiLocalization();
            builder.Services.AddSingleton(new ApplicationAuthenticationOptions { Type = mode });
            builder.Services.AddSingleton(new AlertIngestionAuthenticationOptions
            {
                Audience = "33333333-3333-3333-3333-333333333333",
                RequiredRole = "Alerts.Write<original>",
                Sources = [new AlertIngestionSource
                {
                    TenantId = "11111111-1111-1111-1111-111111111111",
                    ClientId = "22222222-2222-2222-2222-222222222222"
                }]
            });
            builder.Services.AddSingleton<ReadOnlyDiagnostics>();
            builder.Services.AddSingleton<IAlertIngestionDiagnostics>(services => services.GetRequiredService<ReadOnlyDiagnostics>());
            builder.Services.AddScoped<Activator>();
            builder.Services.AddScoped<IComponentActivator>(services => services.GetRequiredService<Activator>());
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            return new Fixture(app, scope, new Renderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()));
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)renderer).DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
        }
    }
}
