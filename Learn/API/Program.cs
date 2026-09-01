using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using APILearning.Api;
using APILearning.Components;
using APILearning.Data;
using APILearning.GraphQL;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PayloadDatabase")
    ?? "Data Source=api-learning.db";
builder.Services.AddDbContextFactory<PayloadDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddSingleton<PayloadStore>();
builder.Services.AddGraphQLServer()
    .AddQueryType<PayloadQuery>()
    .AddFiltering()
    .AddSorting();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PayloadDbContext>();
    await db.Database.EnsureCreatedAsync();
    await PayloadDemoData.SeedAsync(db);
}

app.UseAntiforgery();

app.MapPost("/api/ingest", async Task<Results<Created<IngestResponse>, BadRequest<ApiError>, ProblemHttpResult>> (
    HttpRequest request,
    PayloadStore store,
    CancellationToken cancellationToken) =>
{
    if (request.ContentLength is > PayloadStore.MaximumPayloadBytes)
    {
        return TypedResults.BadRequest(new ApiError(
            $"JSON payloads may not exceed {PayloadStore.MaximumPayloadBytes / 1024} KB."));
    }

    try
    {
        var payload = await request.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return TypedResults.BadRequest(new ApiError("The request body must be a JSON object."));
        }

        var record = await store.AddAsync(payload, cancellationToken);
        return TypedResults.Created(
            $"/api/records/{record.Id}",
            new IngestResponse(record.Id, record.ReceivedAt, record.Name));
    }
    catch (JsonException)
    {
        return TypedResults.BadRequest(new ApiError("The request body contains invalid JSON."));
    }
    catch (InvalidOperationException exception)
    {
        return TypedResults.BadRequest(new ApiError(exception.Message));
    }
    catch (DbUpdateException exception)
    {
        app.Logger.LogError(exception, "The JSON payload could not be stored.");
        return TypedResults.Problem("The database is unavailable.", statusCode: 503);
    }
})
    .DisableAntiforgery()
    .WithName("IngestJson")
    .WithSummary("Stores an arbitrary JSON object and extracts searchable fields.")
    .Accepts<JsonElement>("application/json")
    .Produces<IngestResponse>(StatusCodes.Status201Created)
    .Produces<ApiError>(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/records", async (
    string? name,
    string? category,
    string? source,
    int? minimumSeverity,
    DateTimeOffset? receivedAfter,
    string? contains,
    int? limit,
    PayloadStore store,
    CancellationToken cancellationToken) =>
{
    var filter = new PayloadFilter(
        name,
        category,
        source,
        minimumSeverity,
        receivedAfter,
        contains,
        Math.Clamp(limit ?? 50, 1, 200));
    var records = await store.SearchAsync(filter, cancellationToken);
    return TypedResults.Ok(new QueryResponse(records.Count, filter, records));
})
    .WithName("QueryRecords")
    .WithSummary("Queries records with optional metadata, severity, time, and text filters.");

app.MapGet("/api/records/{id:guid}", async (
    Guid id,
    PayloadStore store,
    CancellationToken cancellationToken) =>
{
    var record = await store.GetAsync(id, cancellationToken);
    return record is null ? Results.NotFound() : Results.Ok(record);
}).WithName("GetRecord");

app.MapGraphQL("/graphql");
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
