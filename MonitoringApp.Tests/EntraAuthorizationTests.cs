using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;

namespace MonitoringApp.Tests;

public sealed class EntraAuthorizationTests
{
    [Theory]
    [InlineData("Reader", true, false, false)]
    [InlineData("Operator", true, true, false)]
    [InlineData("Admin", true, true, true)]
    [InlineData("Reader,Operator", true, true, false)]
    [InlineData("Operator,Admin", true, true, true)]
    [InlineData("", false, false, false)]
    [InlineData("Unknown", false, false, false)]
    [InlineData("reader", false, false, false)]
    public async Task EnforcesGroupRolesForApplicationPagesAndActions(
        string roles, bool appAllowed, bool operatorAllowed, bool adminAllowed)
    {
        await using var fixture = await EndpointFixture.CreateAsync();
        fixture.Client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", EncodePrincipal(roles));
        Assert.Equal(appAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await fixture.Client.GetAsync("/api/app")).StatusCode);
        Assert.Equal(operatorAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await fixture.Client.PostAsync("/api/operator-action", null)).StatusCode);
        Assert.Equal(adminAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await fixture.Client.PostAsync("/api/admin-action", null)).StatusCode);

        foreach (var page in ProtectedPages())
        {
            var policy = Assert.Single(page.GetCustomAttributes<AuthorizeAttribute>()).Policy;
            var allowed = policy == ApplicationAuthorization.OperatorPolicy ? operatorAllowed : adminAllowed;
            foreach (var route in page.GetCustomAttributes<RouteAttribute>())
            {
                using var response = await fixture.Client.GetAsync(route.Template);
                Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, response.StatusCode);
                if (!allowed)
                    Assert.Equal("/access-denied", response.Headers.Location?.OriginalString);
            }
        }
    }

    [Fact]
    public async Task MissingIdentityChallengesBrowserButReturns401ForApi()
    {
        await using var fixture = await EndpointFixture.CreateAsync();
        fixture.Client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-NAME", "admin@contoso.test");
        fixture.Client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/app")).StatusCode);
        using var browser = await fixture.Client.GetAsync("/admin/settings?test=1");
        Assert.Equal(HttpStatusCode.Redirect, browser.StatusCode);
        Assert.Equal("/.auth/login/aad?post_login_redirect_uri=%2Fadmin%2Fsettings%3Ftest%3D1",
            browser.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/access-denied")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/login")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64")]
    [InlineData("bnVsbA==")]
    [InlineData("e30=")]
    public async Task InvalidPrincipalCannotAccessApplication(string encoded)
    {
        await using var fixture = await EndpointFixture.CreateAsync();
        fixture.Client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", encoded);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/app")).StatusCode);
    }

    [Fact]
    public async Task MachineTokensRemainSeparateFromInteractiveRoles()
    {
        await using var fixture = await EndpointFixture.CreateAsync();
        fixture.Client.DefaultRequestHeaders.Add("X-Test-Ingestion", "allowed");
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.PostAsync("/api/alerts", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/app")).StatusCode);
        fixture.Client.DefaultRequestHeaders.Remove("X-Test-Ingestion");
        fixture.Client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", EncodePrincipal("Admin"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.PostAsync("/api/alerts", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/app")).StatusCode);
    }

    [Theory]
    [InlineData(ApplicationAuthenticationOptions.Entra)]
    [InlineData(ApplicationAuthenticationOptions.Sql)]
    public async Task SqlAndEntraIdentitiesCannotBypassTheSelectedMode(string mode)
    {
        await using var fixture = await EndpointFixture.CreateAsync(mode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/test/sql-login")).StatusCode);
        Assert.Equal(mode == ApplicationAuthenticationOptions.Sql ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
            (await fixture.Client.GetAsync("/api/admin-action")).StatusCode);

        fixture.Client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", EncodePrincipal("Reader"));
        Assert.Equal(mode == ApplicationAuthenticationOptions.Sql ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await fixture.Client.GetAsync("/api/admin-action")).StatusCode);
    }

    [Fact]
    public async Task OpenModePreservesAnonymousAccess()
    {
        await using var fixture = await EndpointFixture.CreateAsync(ApplicationAuthenticationOptions.Open);
        foreach (var path in new[] { "/api/app", "/admin/rules", "/admin/inventory", "/admin/settings", "/api/admin-action" })
            Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("app", null)]
    [InlineData("app", "false")]
    [InlineData(null, "true")]
    public void EntraModeRequiresTrustedAppServiceHosting(string? site, string? enabled)
    {
        var configuration = HostConfiguration(site, enabled);
        Assert.Contains("Easy Auth", Assert.Throws<InvalidOperationException>(() =>
            AppServiceAuthenticationHandler.ValidateHosting(configuration)).Message);
    }

    [Fact]
    public async Task HeadersAreRejectedOutsideTrustedHostingEvenIfStartupValidationIsSkipped()
    {
        await using var fixture = await EndpointFixture.CreateAsync(trustedHosting: false);
        fixture.Client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", EncodePrincipal("Admin"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/app")).StatusCode);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData(ClaimTypes.Role)]
    [InlineData("custom-role")]
    public void MapsMultipleRolesAndRetainsIdentityClaims(string roleType)
    {
        var claims = new[]
        {
            new { typ = "custom-name", val = "operator@contoso.test" },
            new { typ = "oid", val = "11111111-2222-3333-4444-555555555555" },
            new { typ = roleType, val = "Reader" },
            new { typ = roleType, val = "Operator" }
        };
        var json = JsonSerializer.Serialize(new { auth_typ = "aad", name_typ = "custom-name", role_typ = roleType, claims });
        var principal = CurrentUserIdentity.CreateAppServicePrincipal(Headers(json));
        Assert.NotNull(principal);
        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal("operator@contoso.test", principal.Identity?.Name);
        Assert.Equal("11111111-2222-3333-4444-555555555555", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(principal.IsInRole("Reader"));
        Assert.True(principal.IsInRole("Operator"));
        Assert.False(principal.IsInRole("Admin"));
    }

    [Theory]
    [InlineData("""{"auth_typ":"github","claims":[{"typ":"name","val":"admin"},{"typ":"roles","val":"Admin"}]}""")]
    [InlineData("""{"auth_typ":"aad","claims":[{"typ":"name","val":"app"},{"typ":"idtyp","val":"app"},{"typ":"roles","val":"Admin"}]}""")]
    [InlineData("""{"auth_typ":"aad","claims":[{"typ":"roles","val":"Admin"}]}""")]
    [InlineData("""{"auth_typ":"aad","claims":[{"typ":"name","val":"admin"},null]}""")]
    [InlineData("""{"auth_typ":"aad","claims":[{"typ":"name","val":"admin"},{"typ":"roles","val":42}]}""")]
    public void RejectsMalformedAndNonUserPrincipals(string json) =>
        Assert.Null(CurrentUserIdentity.CreateAppServicePrincipal(Headers(json)));

    [Fact]
    public void RejectsOversizedPrincipal() =>
        Assert.Null(CurrentUserIdentity.CreateAppServicePrincipal(new HeaderDictionary
        {
            ["X-MS-CLIENT-PRINCIPAL"] = new string('A', 64 * 1024 + 1)
        }));

    [Theory]
    [InlineData(typeof(AdminRules), "Reader")]
    [InlineData(typeof(AdminInventory), "Reader")]
    [InlineData(typeof(AdminScheduledQueries), "Reader")]
    [InlineData(typeof(AdminSettings), "Operator")]
    [InlineData(typeof(AdminUsers), "Operator")]
    [InlineData(typeof(AdminAlertAuthentication), "Operator")]
    [InlineData(typeof(AdminAnalysisQueries), "Operator")]
    [InlineData(typeof(AdminLogAnalytics), "Operator")]
    public async Task BlazorRouteViewDeniesAccessBeforeCreatingProtectedPage(Type page, string role)
    {
        var collection = new ServiceCollection().AddLogging();
        collection.AddAuthorization(options => ApplicationAuthorization.Configure(options,
            new ApplicationAuthenticationOptions { Type = ApplicationAuthenticationOptions.Entra }));
        collection.AddSingleton<AuthenticationStateProvider>(new FixedAuthenticationState(
            CurrentUserIdentity.CreateAppServicePrincipal(new HeaderDictionary
            {
                ["X-MS-CLIENT-PRINCIPAL"] = EncodePrincipal(role)
            })!));
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AuthorizeRouteView>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(AuthorizeRouteView.RouteData)] = new RouteData(page, new Dictionary<string, object?>()),
                    [nameof(AuthorizeRouteView.NotAuthorized)] =
                        (RenderFragment<AuthenticationState>)(_ => builder => builder.AddContent(0, "Access denied"))
                }))).ToHtmlString());
        Assert.Equal("Access denied", html);
    }

    [Theory]
    [InlineData("/", null, "Access denied")]
    [InlineData("/", "", "Access denied")]
    [InlineData("/", "Unknown", "Access denied")]
    [InlineData("/admin/rules", "Reader", "Access denied")]
    [InlineData("/admin/inventory", "Reader", "Access denied")]
    [InlineData("/admin/settings", "Operator", "Access denied")]
    [InlineData("/login", "", "Sign in with Microsoft")]
    [InlineData("/login", null, "Sign in with Microsoft")]
    public async Task ApplicationRouterEnforcesAccessAndKeepsTheEntraLoginReachable(
        string path, string? role, string expected)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            await AssertApplicationRouterAccess(path, role, expected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private static async Task AssertApplicationRouterAccess(string path, string? role, string expected)
    {
        var authentication = new ApplicationAuthenticationOptions { Type = ApplicationAuthenticationOptions.Entra };
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddAuthorization(options => ApplicationAuthorization.Configure(options, authentication));
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddUiLocalization();
        builder.Services.AddSingleton(authentication);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<CurrentUserIdentity>();
        builder.Services.AddScoped<NavigationManager>(_ => new TestNavigationManager(path));
        builder.Services.AddScoped<AuthenticationStateProvider>(_ => new FixedAuthenticationState(
            role is null ? new ClaimsPrincipal(new ClaimsIdentity()) : CurrentUserIdentity.CreateAppServicePrincipal(new HeaderDictionary
            {
                ["X-MS-CLIENT-PRINCIPAL"] = EncodePrincipal(role)
            })!));
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Routes>()).ToHtmlString());
        Assert.Contains(expected, html);
        Assert.DoesNotContain("login-form", html);
        Assert.Single(Regex.Matches(html, "<header\\b"));
        if (expected == "Access denied")
            Assert.Contains("Sign out and use another account", html);
    }

    private static Type[] ProtectedPages() =>
        typeof(AdminRules).Assembly.GetTypes().Where(type => type.Namespace == typeof(AdminRules).Namespace &&
            type.Name.StartsWith("Admin", StringComparison.Ordinal) &&
            type.GetCustomAttributes<RouteAttribute>().Any()).ToArray();

    private static IConfiguration HostConfiguration(string? site, string? enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WEBSITE_SITE_NAME"] = site, ["WEBSITE_AUTH_ENABLED"] = enabled
        }).Build();

    private static HeaderDictionary Headers(string json) => new()
    {
        ["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
    };

    private static string EncodePrincipal(string roles)
    {
        var claims = new[] { new { typ = "preferred_username", val = "user@contoso.test" } }
            .Concat(roles.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(role => new { typ = "roles", val = role }));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { auth_typ = "aad", claims })));
    }

    private sealed class FixedAuthenticationState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string path) => Initialize("http://localhost/", "http://localhost" + path);
    }

    private sealed class EndpointFixture(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<EndpointFixture> CreateAsync(
            string mode = ApplicationAuthenticationOptions.Entra, bool trustedHosting = true)
        {
            var authentication = new ApplicationAuthenticationOptions { Type = mode };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddConfiguration(HostConfiguration(trustedHosting ? "test-app" : null,
                trustedHosting ? "true" : "false"));
            if (authentication.IsEntra && trustedHosting)
                AppServiceAuthenticationHandler.ValidateHosting(builder.Configuration);
            builder.Services.AddAuthentication(authentication.IsEntra
                    ? AppServiceAuthenticationHandler.SchemeName : ApplicationAuthorization.SqlCookieScheme)
                .AddCookie(ApplicationAuthorization.SqlCookieScheme, options =>
                {
                    options.Events.OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    };
                })
                .AddScheme<AuthenticationSchemeOptions, AppServiceAuthenticationHandler>(
                    AppServiceAuthenticationHandler.SchemeName, _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestIngestionHandler>("TestIngestion", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                ApplicationAuthorization.Configure(options, authentication);
                options.AddPolicy("TestIngestion", policy => policy.AddAuthenticationSchemes("TestIngestion")
                    .RequireAuthenticatedUser().RequireRole("Alerts.Write"));
            });
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/api/app", () => Results.Ok());
            app.MapMethods("/api/admin-action", ["GET", "POST"], () => Results.Ok())
                .RequireAuthorization(ApplicationAuthorization.AdminPolicy);
            app.MapPost("/api/operator-action", () => Results.Ok()).RequireAuthorization(ApplicationAuthorization.OperatorPolicy);
            app.MapPost("/api/alerts", () => Results.Ok()).RequireAuthorization("TestIngestion");
            app.MapGet("/login", () => Results.Ok()).AllowAnonymous();
            app.MapGet("/access-denied", () => Results.Ok()).AllowAnonymous();
            app.MapGet("/test/sql-login", async (HttpContext context) =>
            {
                await context.SignInAsync(ApplicationAuthorization.SqlCookieScheme, new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "sql-admin"), new Claim(ClaimTypes.Role, "Admin")],
                    ApplicationAuthorization.SqlCookieScheme)));
                return Results.Ok();
            }).AllowAnonymous();
            foreach (var page in ProtectedPages())
                foreach (var route in page.GetCustomAttributes<RouteAttribute>())
                    app.MapGet(route.Template, () => Results.Ok())
                        .WithMetadata(page.GetCustomAttributes<AuthorizeAttribute>().Cast<object>().ToArray());
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class TestIngestionHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers["X-Test-Ingestion"] == "allowed"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Role, "Alerts.Write")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
