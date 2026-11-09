using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonitoringApp;

public sealed record AgentAnalysisJobCreateRequest(
    IncidentAnalysisSnapshot Snapshot,
    AgentAnalysisExecutionState ExecutionState,
    string CreatedBy,
    DateTimeOffset ExpiresAt);

public sealed class AgentAnalysisJobConcurrencyException : InvalidOperationException
{
    public AgentAnalysisJobConcurrencyException(string message) : base(message)
    {
    }

    public AgentAnalysisJobConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class AgentAnalysisJobIdempotency
{
    public static string Compute(
        IncidentAnalysisSnapshot snapshot,
        string promptVersion,
        string createdBy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(promptVersion) || string.IsNullOrWhiteSpace(createdBy))
        {
            throw new InvalidOperationException("PromptVersion and CreatedBy are required for idempotency.");
        }

        var data = JsonNode.Parse(snapshot.ToCanonicalJson())
            ?? throw new InvalidOperationException("The analysis snapshot could not be canonicalized.");
        data.AsObject().Remove("generatedAt");
        RemoveDerivedAge(data);

        var input = string.Join('\n',
            IncidentAnalysisSnapshot.CurrentVersion,
            promptVersion.Trim(),
            createdBy.Trim().ToUpperInvariant(),
            data.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static void RemoveDerivedAge(JsonNode node)
    {
        if (node is JsonObject value)
        {
            value.Remove("ageAtSnapshot");
            foreach (var child in value.Select(property => property.Value).Where(child => child is not null))
            {
                RemoveDerivedAge(child!);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child is not null))
            {
                RemoveDerivedAge(child!);
            }
        }
    }
}

public static class AgentAnalysisJobResultPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void Store(
        AgentAnalysisJob job,
        AgentAnalysisResult result,
        DateTimeOffset storedAt)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);

        if (result.AnalysisId != job.AnalysisId || result.ResultVersion != job.ResultVersion)
        {
            throw new InvalidOperationException(
                "The analysis result identity and version must match the analysis job.");
        }

        if (result.ContractVersion != job.ResultContractVersion)
        {
            throw new InvalidOperationException(
                "The analysis result contract version must match the analysis job.");
        }

        if (string.IsNullOrWhiteSpace(result.ToCanonicalJson()))
        {
            throw new InvalidOperationException("The analysis result must not be empty.");
        }

        var sourceIds = result.DetailedAnalysis.ExecutiveSummary
            .SelectMany(statement => statement.SourceIds)
            .Concat(result.DetailedAnalysis.RecommendedChecks.SelectMany(check => check.SourceIds))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        job.ResultJson = result.ToCanonicalJson();
        job.TeamsSummaryJson = JsonSerializer.Serialize(result.TeamsSummary, JsonOptions);
        job.DetailedAnalysisJson = JsonSerializer.Serialize(result.DetailedAnalysis, JsonOptions);
        job.SourceIdsJson = JsonSerializer.Serialize(sourceIds, JsonOptions);
        job.ResultStoredAt = storedAt;
        job.TeamsDeliveryStatus = AgentAnalysisDeliveryStatuses.NotRequested;
        job.TeamsDeliveredResultVersion = null;
        job.TeamsDeliveredAt = null;

        AgentAnalysisJobStateMachine.Transition(
            job,
            AgentAnalysisJobStatuses.Completed,
            storedAt,
            "Validated result stored.");
    }
}