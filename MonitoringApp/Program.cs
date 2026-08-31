using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MonitoringApp;
using MonitoringApp.Components;

var builder = WebApplication.CreateBuilder(args);
const string SqlCookieScheme = "SqlAuthentication";
const string OperatorPolicy = "OperatorAccess";
const string AdminPolicy = "AdminAccess";

var ingestionAuthentication = builder.Configuration
    .GetSection(AlertIngestionAuthenticationOptions.SectionName)
    .Get<AlertIngestionAuthenticationOptions>() ?? new AlertIngestionAuthenticationOptions();
var ingestionAuthenticationErrors = ingestionAuthentication.Validate();
if (ingestionAuthentication.Enabled && ingestionAuthenticationErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid alert ingestion authentication configuration: {string.Join(" ", ingestionAuthenticationErrors)}");
}

var alertsConnectionString = builder.Configuration.GetConnectionString("AlertsDatabase");
var databaseConfigurationError = string.Empty;
SqlConnectionStringBuilder sqlConnection;

try
{
    sqlConnection = new SqlConnectionStringBuilder(alertsConnectionString);
}
catch (Exception exception) when (exception is ArgumentException or FormatException)
{
    databaseConfigurationError = $"Connection string AlertsDatabase is malformed: {exception.Message}";
    sqlConnection = new SqlConnectionStringBuilder();
}

if (string.IsNullOrWhiteSpace(alertsConnectionString))
{
    databaseConfigurationError = "Connection string AlertsDatabase is missing.";
}
else if (builder.Environment.IsProduction() &&
         (sqlConnection.Authentication != SqlAuthenticationMethod.ActiveDirectoryManagedIdentity ||
          string.IsNullOrWhiteSpace(sqlConnection.UserID)))
{
    databaseConfigurationError =
        "Production requires 'Authentication=Active Directory Managed Identity' and " +
        "'User Id=<managed-identity-client-id>'. User Id must be the client ID, not the object ID.";
}

var databaseConfiguration = new DatabaseConfigurationStatus(
    string.IsNullOrEmpty(databaseConfigurationError),
    string.IsNullOrEmpty(databaseConfigurationError) ? null : databaseConfigurationError,
    sqlConnection);
var effectiveConnectionString = databaseConfiguration.IsValid
    ? alertsConnectionString!
    : "Server=127.0.0.1,1;Database=Unavailable;Connect Timeout=1;Encrypt=False";

if (!databaseConfiguration.IsValid)
{
    throw new InvalidOperationException(
        $"A valid AlertsDatabase connection is required to load application settings: {databaseConfiguration.Error}");
}

var databaseSettings = DatabaseSettingsLoader.LoadRequired(effectiveConnectionString);
var applicationAuthentication = databaseSettings.Authentication;
var alertHistory = databaseSettings.AlertHistory;
var alertGraph = databaseSettings.AlertGraph;
var alertSeverityDisplay = databaseSettings.AlertSeverityDisplay;

var applicationAuthenticationErrors = applicationAuthentication.Validate();
if (applicationAuthenticationErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid database setting '{DatabaseSettingsLoader.Authentication}': {string.Join(" ", applicationAuthenticationErrors)}");
}

var alertHistoryErrors = alertHistory.Validate();
if (alertHistoryErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid database setting '{DatabaseSettingsLoader.AlertHistory}': {string.Join(" ", alertHistoryErrors)}");
}

var alertGraphErrors = alertGraph.Validate();
if (alertGraphErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid database setting '{DatabaseSettingsLoader.AlertGraph}': {string.Join(" ", alertGraphErrors)}");
}

    var alertSeverityDisplayErrors = alertSeverityDisplay.Validate();
    if (alertSeverityDisplayErrors.Count > 0)
    {
        throw new InvalidOperationException(
        $"Invalid database setting '{DatabaseSettingsLoader.AlertSeverityDisplay}': {string.Join(" ", alertSeverityDisplayErrors)}");
    }

builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("SqlLogin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddSingleton(applicationAuthentication);
builder.Services.AddSingleton(alertHistory);
builder.Services.AddSingleton(alertSeverityDisplay);
builder.Services.AddSingleton(alertGraph);
builder.Services.AddSingleton(ingestionAuthentication);
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = SqlCookieScheme;
        options.DefaultChallengeScheme = SqlCookieScheme;
        options.DefaultSignInScheme = SqlCookieScheme;
    })
    .AddCookie(SqlCookieScheme, options =>
    {
        options.Cookie.Name = ".MonitoringApp.SqlAuthentication";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddJwtBearer(options =>
    {
        options.Authority = "https://login.microsoftonline.com/organizations/v2.0";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = ingestionAuthentication.Audience,
            ValidateIssuer = true,
            ValidIssuers = ingestionAuthentication.Sources.Select(source =>
                $"https://login.microsoftonline.com/{source.TenantId}/v2.0")
        };
    });
var authorization = builder.Services.AddAuthorizationBuilder();
if (applicationAuthentication.IsSql)
{
    authorization.SetFallbackPolicy(new AuthorizationPolicyBuilder(SqlCookieScheme)
        .RequireAuthenticatedUser()
        .Build());
}

authorization
    .AddPolicy("LogicAppAlertWriter", policy =>
    {
        if (!applicationAuthentication.IsOpen && ingestionAuthentication.Enabled)
        {
            policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context =>
                AlertIngestionAuthorization.IsAuthorized(context.User, ingestionAuthentication));
        }
        else
        {
            policy.RequireAssertion(_ => true);
        }
    })
    .AddPolicy(OperatorPolicy, policy => policy.RequireAssertion(context =>
        applicationAuthentication.IsOpen ||
        context.User.IsInRole(SqlAuthenticationRoles.Operator) ||
        context.User.IsInRole(SqlAuthenticationRoles.Admin)))
    .AddPolicy(AdminPolicy, policy => policy.RequireAssertion(context =>
        applicationAuthentication.IsOpen ||
        context.User.IsInRole(SqlAuthenticationRoles.Admin)));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddDbContextFactory<AlertDbContext>(options =>
    options.UseSqlServer(effectiveConnectionString, sqlServerOptions =>
        sqlServerOptions.EnableRetryOnFailure()));
builder.Services.AddSingleton(databaseConfiguration);
builder.Services.AddSingleton<SqlPasswordHasher>();
builder.Services.AddSingleton<SqlAuthenticationService>();
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddSingleton<LogbookStore>();
builder.Services.AddSingleton(new QueryResultPresenter(
    Path.Combine(builder.Environment.ContentRootPath, "AlertDefinitions")));
builder.Services.AddSingleton<AlertRuleEvaluator>();
builder.Services.AddHostedService<DatabaseStartupCheck>();

var app = builder.Build();

if (databaseConfiguration.IsValid)
{
    app.Logger.LogInformation(
        "MonitoringApp starting. Environment={Environment}, Server={Server}, Database={Database}, Authentication={Authentication}",
        app.Environment.EnvironmentName,
        sqlConnection.DataSource,
        sqlConnection.InitialCatalog,
        sqlConnection.Authentication);
}
else
{
    app.Logger.LogCritical(
        "MonitoringApp started without a valid Azure SQL configuration. {ConfigurationError}",
        databaseConfiguration.Error);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();

app.MapPost("/auth/login", async (
    [FromForm] LoginRequest request,
    HttpContext httpContext,
    SqlAuthenticationService authenticationService,
    CancellationToken cancellationToken) =>
{
    if (!applicationAuthentication.IsSql)
    {
        return Results.Redirect("/");
    }

    var authenticatedUser = await authenticationService.ValidateCredentialsAsync(
        request.Username,
        request.Password,
        cancellationToken);
    if (authenticatedUser is null)
    {
        var returnUrl = Uri.EscapeDataString(SafeReturnUrl(request.ReturnUrl));
        return Results.Redirect($"/login?error=invalid&returnUrl={returnUrl}");
    }

    var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, authenticatedUser.Username),
            new Claim(ClaimTypes.Name, authenticatedUser.Username),
            new Claim(ClaimTypes.Role, authenticatedUser.Role)
        ],
        SqlCookieScheme);
    await httpContext.SignInAsync(
        SqlCookieScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = false });
    return Results.Redirect(SafeReturnUrl(request.ReturnUrl));
})
.AllowAnonymous()
.RequireRateLimiting("SqlLogin");

app.MapPost("/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(SqlCookieScheme);
    return Results.Redirect("/login");
});

app.MapPost("/api/alerts", async (JsonElement payload, AlertStore store, CancellationToken cancellationToken) =>
{
    if (payload.ValueKind != JsonValueKind.Object)
    {
        return Results.BadRequest(new { error = "The request body must be a JSON object." });
    }

    try
    {
        var result = await store.AddAsync(payload, cancellationToken);
        var response = new
        {
            result.Alert.Id,
            result.Alert.ReceivedAt,
            result.Created
        };

        return result.Created
            ? Results.Created($"/api/alerts/{result.Alert.Id}", response)
            : Results.Ok(response);
    }
    catch (Exception exception) when (exception is InvalidOperationException or SqlException)
    {
        app.Logger.LogError(exception, "Alert ingestion is unavailable because the database is not configured or reachable.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Alert storage is unavailable.");
    }
})
.WithName("IngestAlert")
.WithSummary("Receives an Azure Monitor alert payload")
.RequireAuthorization("LogicAppAlertWriter")
.Produces(StatusCodes.Status201Created)
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/query", async (
    int? activeMinutes,
    int? eventHours,
    AlertStore store,
    CancellationToken cancellationToken) =>
{
    if ((activeMinutes is null) == (eventHours is null))
    {
        return Results.BadRequest(new
        {
            error = "Set exactly one query parameter: activeMinutes or eventHours."
        });
    }

    if (activeMinutes is <= 0 or > 525600 || eventHours is <= 0 or > 8760)
    {
        return Results.BadRequest(new
        {
            error = "activeMinutes must be between 1 and 525600; eventHours must be between 1 and 8760."
        });
    }

    try
    {
        var now = DateTimeOffset.UtcNow;
        var mode = activeMinutes is not null ? "active" : "events";
        var from = activeMinutes is not null
            ? now.AddMinutes(-activeMinutes.Value)
            : now.AddHours(-eventHours!.Value);
        var alerts = await store.GetSinceRequiredAsync(from, cancellationToken);
        var matches = activeMinutes is not null
            ? AlertQuery.GetActiveSince(alerts, from)
            : AlertQuery.GetEventsSince(alerts, from);
        var items = matches.Select(AlertQueryItem.FromAlert).ToArray();

        return Results.Ok(new AlertQueryResponse(mode, from, now, items.Length, items));
    }
    catch (Exception exception) when (exception is InvalidOperationException or SqlException)
    {
        app.Logger.LogError(exception, "Alert query is unavailable because the database is not configured or reachable.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Alert storage is unavailable.");
    }
})
.WithName("QueryAlerts")
.WithSummary("Queries active alerts by minutes or all alert events by hours")
.Produces<AlertQueryResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string SafeReturnUrl(string? returnUrl) =>
    !string.IsNullOrWhiteSpace(returnUrl) &&
    Uri.TryCreate(returnUrl, UriKind.Relative, out _) &&
    returnUrl.StartsWith('/') &&
    !returnUrl.StartsWith("//", StringComparison.Ordinal)
        ? returnUrl
        : "/";
