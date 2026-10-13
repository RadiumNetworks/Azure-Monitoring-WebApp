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
var inboxTargetOutputDisplay = databaseSettings.InboxTargetOutputDisplay;
var agentAnalysisContext = databaseSettings.AgentAnalysisContext;
var agentAnalysisExecution = databaseSettings.AgentAnalysisExecution;
var agentAnalysisProfiles = builder.Configuration
    .GetSection(AgentAnalysisProfilesOptions.SectionName)
    .Get<AgentAnalysisProfilesOptions>() ?? new AgentAnalysisProfilesOptions();
var agentAnalysisAccess = builder.Configuration
    .GetSection(AgentAnalysisAccessOptions.SectionName)
    .Get<AgentAnalysisAccessOptions>() ?? new AgentAnalysisAccessOptions();

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

var agentAnalysisContextErrors = agentAnalysisContext.Validate();
if (agentAnalysisContextErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid database setting '{DatabaseSettingsLoader.AgentAnalysisContext}': {string.Join(" ", agentAnalysisContextErrors)}");
}

    var alertSeverityDisplayErrors = alertSeverityDisplay.Validate();
    if (alertSeverityDisplayErrors.Count > 0)
    {
        throw new InvalidOperationException(
        $"Invalid database setting '{DatabaseSettingsLoader.AlertSeverityDisplay}': {string.Join(" ", alertSeverityDisplayErrors)}");
    }

var inboxTargetOutputDisplayErrors = inboxTargetOutputDisplay.Validate();
if (inboxTargetOutputDisplayErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid database setting '{DatabaseSettingsLoader.InboxTargetOutputDisplay}': {string.Join(" ", inboxTargetOutputDisplayErrors)}");
}

var agentAnalysisExecutionState = AgentAnalysisExecutionState.Create(
    agentAnalysisExecution,
    agentAnalysisProfiles,
    key => !string.IsNullOrWhiteSpace(builder.Configuration[key]));
var agentAnalysisAccessErrors = agentAnalysisAccess.Validate();
if (agentAnalysisAccessErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid agent analysis access configuration: {string.Join(" ", agentAnalysisAccessErrors)}");
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
builder.Services.AddSingleton(inboxTargetOutputDisplay);
builder.Services.AddSingleton(alertGraph);
builder.Services.AddSingleton(agentAnalysisContext);
builder.Services.AddSingleton(agentAnalysisProfiles);
builder.Services.AddSingleton(agentAnalysisExecutionState);
builder.Services.AddSingleton(agentAnalysisAccess);
builder.Services.AddSingleton(TimeProvider.System);
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
        if (AlertIngestionAuthorization.IsAuthenticationRequired(ingestionAuthentication))
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUserIdentity>();
builder.Services.AddSingleton<SqlCommandFailureLoggingInterceptor>();
builder.Services.AddSingleton<SqlConnectionFailureLoggingInterceptor>();
builder.Services.AddDbContextFactory<AlertDbContext>((services, options) =>
    options
        .UseSqlServer(effectiveConnectionString, sqlServerOptions =>
            sqlServerOptions.EnableRetryOnFailure())
        .AddInterceptors(
            services.GetRequiredService<SqlCommandFailureLoggingInterceptor>(),
            services.GetRequiredService<SqlConnectionFailureLoggingInterceptor>()));
builder.Services.AddSingleton(databaseConfiguration);
builder.Services.AddSingleton<SqlPasswordHasher>();
builder.Services.AddSingleton<SqlAuthenticationService>();
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddSingleton<LogbookStore>();
builder.Services.AddSingleton(new QueryResultPresenter(
    Path.Combine(builder.Environment.ContentRootPath, "AlertDefinitions")));
builder.Services.AddSingleton<AlertRuleEvaluator>();
builder.Services.AddSingleton<IncidentAnalysisSnapshotBuilder>();
builder.Services.AddSingleton<IncidentAnalysisSnapshotService>();
builder.Services.AddSingleton(_ => MicrosoftSourceCatalog.CreateDefault());
builder.Services.AddSingleton<AgentAnalysisOrchestrator>();
builder.Services.AddSingleton<AgentAnalysisJobStore>();
builder.Services.AddSingleton<AgentAnalysisReviewStore>();
builder.Services.AddSingleton<AgentAnalysisScopePolicy>();
builder.Services.AddSingleton<AgentAnalysisJobService>();
builder.Services.AddSingleton<IAgentAnalysisExecutionAdapter, DisabledTeamsWorkflowAdapter>();
builder.Services.AddSingleton<IAgentAnalysisExecutionAdapter, DisabledApiAnalysisAdapter>();
builder.Services.AddHttpClient<MicrosoftSourceLinkChecker>(client =>
    client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddHostedService<MicrosoftSourceCatalogVerificationService>();
builder.Services.AddScoped<InboxDashboardState>();
builder.Services.AddScoped<InboxLayoutRepository>();
builder.Services.AddHostedService<DatabaseStartupCheck>();
builder.Services.AddHostedService<AgentAnalysisRetentionService>();

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
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated != true &&
        CurrentUserIdentity.CreateAppServicePrincipal(context.Request.Headers) is { } entraPrincipal)
    {
        context.User = entraPrincipal;
    }

    await next();
});
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
            result.Created,
            result.Suppressed
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

app.MapGet("/api/alert-query-refresh-targets", async (
    string? type,
    AlertStore store,
    CancellationToken cancellationToken) =>
{
    if (!AlertQueryRefresh.TryGetDefinition(type, out var definition))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(type)] =
                [$"Type must be one of: {string.Join(", ", AlertQueryRefresh.SupportedTypes)}."]
        });
    }

    try
    {
        var targets = await store.GetActiveAlertQueryRefreshTargetsAsync(
            definition.Type,
            cancellationToken);
        return Results.Ok(targets.Select(target => new AlertQueryRefreshTargetResponse(
            target.Id,
            target.AlertId,
            target.Type,
            target.DimensionName,
            target.DimensionValue)));
    }
    catch (Exception exception) when (exception is InvalidOperationException or SqlException)
    {
        app.Logger.LogError(exception, "DCPort query-refresh targets are unavailable.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Alert query refresh is unavailable.");
    }
})
.WithName("GetAlertQueryRefreshTargets")
.WithSummary("Returns active alerts that require a scheduled query refresh")
.WithDescription("Returns supported stateful alerts whose current monitor condition is Fired.")
.RequireAuthorization("LogicAppAlertWriter")
.Produces<IReadOnlyList<AlertQueryRefreshTargetResponse>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.ProducesValidationProblem()
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPut("/api/alerts/{id:guid}/query-result", async (
    Guid id,
    UpdateAlertQueryResultRequest request,
    AlertStore store,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var validationErrors = AlertQueryRefresh.Validate(request, timeProvider.GetUtcNow());
    if (validationErrors.Count > 0)
    {
        return Results.ValidationProblem(validationErrors);
    }

    try
    {
        var result = await store.UpdateAlertQueryResultAsync(id, request, cancellationToken);
        return result.Status switch
        {
            AlertQueryRefreshUpdateStatus.Updated => Results.Ok(new UpdateAlertQueryResultResponse(
                id,
                true,
                result.QueryResultRefreshedAt!.Value)),
            AlertQueryRefreshUpdateStatus.Unchanged => Results.Ok(new UpdateAlertQueryResultResponse(
                id,
                false,
                result.QueryResultRefreshedAt!.Value)),
            AlertQueryRefreshUpdateStatus.NotFound => Results.NotFound(),
            AlertQueryRefreshUpdateStatus.NotActive => Results.Conflict(new
            {
                error = "The alert is no longer active and cannot be refreshed."
            }),
            AlertQueryRefreshUpdateStatus.TypeMismatch => Results.Conflict(new
            {
                error = "The stored alert query-result type does not match the refresh request."
            }),
            AlertQueryRefreshUpdateStatus.DimensionMismatch => Results.Conflict(new
            {
                error = "The dimension name or value does not match the stored alert."
            }),
            AlertQueryRefreshUpdateStatus.Stale => Results.Conflict(new
            {
                error = "A newer or different query result already exists for this query timestamp.",
                result.QueryResultRefreshedAt
            }),
            _ => throw new InvalidOperationException("Unsupported DCPort refresh result.")
        };
    }
    catch (Exception exception) when (exception is InvalidOperationException or SqlException)
    {
        app.Logger.LogError(exception, "DCPort query-result update is unavailable for alert record {AlertRecordId}.", id);
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Alert query refresh is unavailable.");
    }
})
.WithName("UpdateAlertQueryResult")
.WithSummary("Updates the query result of an active supported alert")
.WithDescription("Idempotently replaces only the query snapshot while preserving the stateful Azure alert identity and lifecycle.")
.RequireAuthorization("LogicAppAlertWriter")
.Produces<UpdateAlertQueryResultResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict)
.ProducesValidationProblem()
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/agent-analysis/jobs", async (
    AgentAnalysisJobStartRequest request,
    HttpContext httpContext,
    AgentAnalysisJobService jobService,
    CancellationToken cancellationToken) =>
{
    var validationErrors = AgentAnalysisJobApi.Validate(request);
    if (validationErrors.Count > 0)
    {
        return Results.ValidationProblem(validationErrors);
    }

    try
    {
        var response = await jobService.StartAsync(
            request,
            httpContext.User,
            cancellationToken);

        return Results.Accepted(
            $"/api/agent-analysis/jobs/{response.Job.Id}",
            response);
    }
    catch (AgentAnalysisRequestValidationException exception)
    {
        return Results.ValidationProblem(exception.Errors);
    }
    catch (AgentAnalysisScopeDeniedException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "The requested analysis scope is not allowed.",
            detail: exception.Message);
    }
    catch (AgentAnalysisUnavailableException exception)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Agent analysis is unavailable.",
            detail: exception.Message);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (SqlException exception)
    {
        app.Logger.LogError(exception, "Agent analysis job creation failed because storage is unavailable.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Agent analysis storage is unavailable.");
    }
})
.WithName("StartAgentAnalysisJob")
.WithSummary("Creates an internal agent analysis job and manual Copilot handoff")
.RequireAuthorization(OperatorPolicy)
.Produces<AgentAnalysisJobStartResponse>(StatusCodes.Status202Accepted)
.ProducesValidationProblem()
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/agent-analysis/jobs", async (
    HttpContext httpContext,
    AgentAnalysisJobService jobService,
    CancellationToken cancellationToken) =>
{
    var jobs = await jobService.ListAccessibleAsync(
        httpContext.User,
        cancellationToken: cancellationToken);
    return Results.Ok(jobs);
})
.WithName("ListAgentAnalysisJobs")
.WithSummary("Returns recent agent analysis jobs visible to the current operator")
.RequireAuthorization(OperatorPolicy)
.Produces<IReadOnlyList<AgentAnalysisJobResponse>>(StatusCodes.Status200OK);

app.MapGet("/api/agent-analysis/jobs/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    AgentAnalysisJobStore jobStore,
    CancellationToken cancellationToken) =>
{
    var job = await jobStore.GetAsync(id, cancellationToken);
    if (job is null || !AgentAnalysisJobApi.CanAccess(job, httpContext.User, applicationAuthentication.IsOpen))
    {
        return Results.NotFound();
    }

    return Results.Ok(AgentAnalysisJobResponse.FromJob(job));
})
.WithName("GetAgentAnalysisJob")
.WithSummary("Returns an internal agent analysis job status")
.RequireAuthorization(OperatorPolicy)
.Produces<AgentAnalysisJobResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound);

app.MapGet("/api/agent-analysis/jobs/{id:guid}/result", async (
    Guid id,
    HttpContext httpContext,
    AgentAnalysisJobStore jobStore,
    CancellationToken cancellationToken) =>
{
    var job = await jobStore.GetAsync(id, cancellationToken);
    if (job is null || !AgentAnalysisJobApi.CanAccess(job, httpContext.User, applicationAuthentication.IsOpen))
    {
        return Results.NotFound();
    }

    if (job.Status != AgentAnalysisJobStatuses.Completed || string.IsNullOrWhiteSpace(job.ResultJson))
    {
        return Results.Conflict(new { error = "The analysis result is not available." });
    }

    return Results.Content(job.ResultJson, "application/json", System.Text.Encoding.UTF8);
})
.WithName("GetAgentAnalysisResult")
.WithSummary("Returns the complete stored analysis result")
.RequireAuthorization(OperatorPolicy)
.Produces(StatusCodes.Status200OK, contentType: "application/json")
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict);

app.MapGet("/api/agent-analysis/jobs/{id:guid}/attachment", async (
    Guid id,
    HttpContext httpContext,
    AgentAnalysisJobStore jobStore,
    CancellationToken cancellationToken) =>
{
    var job = await jobStore.GetAsync(id, cancellationToken);
    if (job is null || !AgentAnalysisJobApi.CanAccess(job, httpContext.User, applicationAuthentication.IsOpen))
    {
        return Results.NotFound();
    }

    if (job.Status != AgentAnalysisJobStatuses.Completed || string.IsNullOrWhiteSpace(job.ResultJson))
    {
        return Results.Conflict(new { error = "The analysis attachment is not available." });
    }

    var result = AgentAnalysisResultParser.ParseStored(job.ResultJson);
    var markdown = AgentAnalysisMarkdownRenderer.Render(job, result);
    return Results.File(
        System.Text.Encoding.UTF8.GetBytes(markdown),
        "text/markdown; charset=utf-8",
        AgentAnalysisMarkdownRenderer.CreateFileName(job));
})
.WithName("DownloadAgentAnalysisAttachment")
.WithSummary("Downloads the complete stored analysis result as Markdown")
.RequireAuthorization(OperatorPolicy)
.Produces(StatusCodes.Status200OK, contentType: "text/markdown")
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict);

app.MapPost("/api/agent-analysis/jobs/{id:guid}/cancel", async (
    Guid id,
    AgentAnalysisJobCancelRequest request,
    HttpContext httpContext,
    AgentAnalysisJobStore jobStore,
    CancellationToken cancellationToken) =>
{
    if (request.ExpectedRevision < 1)
    {
        return Results.BadRequest(new { error = "ExpectedRevision must be positive." });
    }

    var job = await jobStore.GetAsync(id, cancellationToken);
    if (job is null || !AgentAnalysisJobApi.CanAccess(job, httpContext.User, applicationAuthentication.IsOpen))
    {
        return Results.NotFound();
    }

    try
    {
        var cancelled = await jobStore.TransitionAsync(
            id,
            request.ExpectedRevision,
            AgentAnalysisJobStatuses.Cancelled,
            request.Reason ?? "Cancelled by operator.",
            cancellationToken: cancellationToken);
        return Results.Ok(AgentAnalysisJobResponse.FromJob(cancelled));
    }
    catch (Exception exception) when (exception is AgentAnalysisJobConcurrencyException or InvalidOperationException)
    {
        return Results.Conflict(new { error = exception.Message });
    }
})
.WithName("CancelAgentAnalysisJob")
.WithSummary("Cancels a queued or running internal agent analysis job")
.RequireAuthorization(OperatorPolicy)
.Produces<AgentAnalysisJobResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict);

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
