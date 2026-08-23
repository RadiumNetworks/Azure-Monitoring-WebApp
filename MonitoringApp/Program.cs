using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MonitoringApp;
using MonitoringApp.Components;

var builder = WebApplication.CreateBuilder(args);
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

builder.Services.AddOpenApi();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddDbContextFactory<AlertDbContext>(options =>
    options.UseSqlServer(effectiveConnectionString, sqlServerOptions =>
        sqlServerOptions.EnableRetryOnFailure()));
builder.Services.AddSingleton(databaseConfiguration);
builder.Services.AddSingleton<AlertStore>();
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
app.UseAntiforgery();
app.MapStaticAssets();

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
.Produces(StatusCodes.Status201Created)
.Produces(StatusCodes.Status200OK)
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
