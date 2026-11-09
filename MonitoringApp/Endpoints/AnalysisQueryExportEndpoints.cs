using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;

namespace MonitoringApp;

public static class AnalysisQueryExportEndpoints
{
    public static void MapAnalysisQueryExport(this WebApplication app)
    {
        app.MapGet("/api/admin/analysis-queries/export", async Task<Results<FileContentHttpResult, ProblemHttpResult>> (
            HttpContext context,
            IAnalysisQueryExportService exporter,
            ILogger<AnalysisQueryExportService> logger,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var archive = await exporter.ExportAsync(cancellationToken);
                logger.LogInformation("Exported saved analysis query drafts for {Actor}.", context.User.Identity?.Name ?? "Unknown");
                return TypedResults.File(archive, "application/zip", $"AnalysisQueries-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
            }
            catch (AnalysisQueryExportException exception)
            {
                logger.LogWarning(exception, "Saved analysis query drafts could not be exported.");
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Query export is unavailable.",
                    detail: exception.Message);
            }
            catch (Exception exception) when (exception is InvalidOperationException or SqlException)
            {
                logger.LogError(exception, "Analysis query export failed because query storage is unavailable.");
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable, title: "Query storage is unavailable.",
                    detail: "The export failed. Check the server logs for details.");
            }
        })
        .RequireAuthorization("AdminAccess")
        .WithName("ExportAnalysisQueryDrafts")
        .WithSummary("Downloads all saved analysis query drafts as a ZIP of JSON templates")
        .WithDescription("Includes disabled definitions and their saved draft scope and visualization. Excludes unsaved editor changes, database IDs, workspace assignments and publication history. Files use the AD query template schema.")
        .Produces(StatusCodes.Status200OK, contentType: "application/zip")
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }
}
