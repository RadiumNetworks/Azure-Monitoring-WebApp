using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

public static class AgentAnalysisResultContract
{
    public const string CurrentVersion = "incident-analysis-result-v1";

    public const string JsonShape = """
        {
          "contractVersion": "incident-analysis-result-v1",
          "analysisId": "<new-guid>",
          "resultVersion": 1,
          "teamsSummary": [
            { "text": "- descriptive Markdown bullet", "detailReferences": ["executive-1"] }
          ],
          "detailedAnalysis": {
            "executiveSummary": [
              { "id": "executive-1", "kind": "ObservedFact|RuleAssessment|GraphCorrelation|HistoricalPattern|Contradiction|MissingEvidence", "text": "...", "evidenceIds": ["<event-guid>"], "ruleIds": ["<rule-guid>"], "sourceIds": ["<approved-source-id>"] }
            ],
            "hypotheses": [
              { "id": "hypothesis-1", "statement": "...", "confidence": "Low|Medium|High|Confirmed", "supportingEvidenceIds": ["<event-guid>"], "contradictingEvidenceIds": [], "confirmationOrRejectionEvidence": "..." }
            ],
            "recommendedChecks": [
              { "id": "check-1", "action": "...", "target": "...", "expectedSignal": "...", "impact": "...", "prerequisites": "...", "rollback": "...", "evidenceIds": ["<event-guid>"], "sourceIds": ["<approved-source-id>"] }
            ],
            "operatorHandoff": { "id": "operator-handoff", "currentDecision": "...", "unresolvedRisk": "...", "missingEvidence": "...", "audience": "...", "portalLink": "[PORTAL_LINK]" }
          }
        }
        """;
}

public sealed record AgentAnalysisResult(
    string ContractVersion,
    Guid AnalysisId,
    int ResultVersion,
    IReadOnlyList<AgentTeamsSummaryBullet> TeamsSummary,
    AgentDetailedAnalysis DetailedAnalysis)
{
    public string ToCanonicalJson() => JsonSerializer.Serialize(this, CanonicalJsonOptions);

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

public sealed record AgentTeamsSummaryBullet(
    string Text,
    IReadOnlyList<string> DetailReferences);

public sealed record AgentDetailedAnalysis(
    IReadOnlyList<AgentExecutiveStatement> ExecutiveSummary,
    IReadOnlyList<AgentHypothesis> Hypotheses,
    IReadOnlyList<AgentRecommendedCheck> RecommendedChecks,
    AgentOperatorHandoff OperatorHandoff);

public sealed record AgentExecutiveStatement(
    string Id,
    string Kind,
    string Text,
    IReadOnlyList<Guid> EvidenceIds,
    IReadOnlyList<Guid> RuleIds,
    IReadOnlyList<string> SourceIds);

public sealed record AgentHypothesis(
    string Id,
    string Statement,
    string Confidence,
    IReadOnlyList<Guid> SupportingEvidenceIds,
    IReadOnlyList<Guid> ContradictingEvidenceIds,
    string ConfirmationOrRejectionEvidence);

public sealed record AgentRecommendedCheck(
    string Id,
    string Action,
    string Target,
    string ExpectedSignal,
    string Impact,
    string Prerequisites,
    string Rollback,
    IReadOnlyList<Guid> EvidenceIds,
    IReadOnlyList<string> SourceIds);

public sealed record AgentOperatorHandoff(
    string Id,
    string CurrentDecision,
    string UnresolvedRisk,
    string MissingEvidence,
    string Audience,
    string PortalLink);

public static class AgentAnalysisResultValidator
{
    private static readonly IReadOnlySet<string> StatementKinds = new HashSet<string>(
        ["ObservedFact", "RuleAssessment", "GraphCorrelation", "HistoricalPattern", "Contradiction", "MissingEvidence"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ConfidenceLevels = new HashSet<string>(
        ["Low", "Medium", "High", "Confirmed"],
        StringComparer.Ordinal);

    public static IReadOnlyList<string> Validate(
        AgentAnalysisResult result,
        IncidentAnalysisSnapshot snapshot,
        MicrosoftSourceCatalog sourceCatalog)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sourceCatalog);

        var errors = new List<string>();
        if (result.ContractVersion != AgentAnalysisResultContract.CurrentVersion)
        {
            errors.Add($"ContractVersion must be '{AgentAnalysisResultContract.CurrentVersion}'.");
        }

        if (result.AnalysisId == Guid.Empty || result.ResultVersion < 1)
        {
            errors.Add("AnalysisId and a positive ResultVersion are required.");
        }

        if (result.TeamsSummary is null || result.DetailedAnalysis is null)
        {
            errors.Add("TeamsSummary and DetailedAnalysis are required.");
            return errors;
        }

        var details = result.DetailedAnalysis;
        if (details.ExecutiveSummary is null ||
            details.Hypotheses is null ||
            details.RecommendedChecks is null ||
            details.OperatorHandoff is null)
        {
            errors.Add("DetailedAnalysis requires ExecutiveSummary, Hypotheses, RecommendedChecks, and OperatorHandoff.");
            return errors;
        }
        var detailIds = details.ExecutiveSummary.Select(item => item.Id)
            .Concat(details.Hypotheses.Select(item => item.Id))
            .Concat(details.RecommendedChecks.Select(item => item.Id))
            .Append(details.OperatorHandoff.Id)
            .ToArray();
        AddDuplicateErrors(detailIds, "detail", errors);

        ValidateTeamsSummary(result.TeamsSummary, detailIds.ToHashSet(StringComparer.Ordinal), errors);

        var knownEvidenceIds = snapshot.PrimaryEvidence
            .Concat(snapshot.SystemHistory)
            .Concat(snapshot.Context24Hours)
            .Select(item => item.EventId)
            .Concat(snapshot.Lifecycles.SelectMany(item => item.Events).Select(item => item.EventId))
            .ToHashSet();
        var knownRuleIds = snapshot.Rules.Select(rule => rule.Id).ToHashSet();
        var approvedSourceIds = sourceCatalog.ApprovedSources
            .Select(source => source.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var statement in details.ExecutiveSummary)
        {
            ValidateIdAndText(statement.Id, statement.Text, "ExecutiveSummary statement", errors);
            if (!StatementKinds.Contains(statement.Kind))
            {
                errors.Add($"ExecutiveSummary statement '{statement.Id}' has unknown kind '{statement.Kind}'.");
            }

            ValidateKnownIds(statement.EvidenceIds, knownEvidenceIds, "evidence", statement.Id, errors);
            ValidateKnownIds(statement.RuleIds, knownRuleIds, "rule", statement.Id, errors);
            ValidateKnownSourceIds(statement.SourceIds, approvedSourceIds, statement.Id, errors);
        }

        foreach (var hypothesis in details.Hypotheses)
        {
            ValidateIdAndText(hypothesis.Id, hypothesis.Statement, "Hypothesis", errors);
            if (!ConfidenceLevels.Contains(hypothesis.Confidence))
            {
                errors.Add($"Hypothesis '{hypothesis.Id}' has unknown confidence '{hypothesis.Confidence}'.");
            }

            ValidateKnownIds(hypothesis.SupportingEvidenceIds, knownEvidenceIds, "supporting evidence", hypothesis.Id, errors);
            ValidateKnownIds(hypothesis.ContradictingEvidenceIds, knownEvidenceIds, "contradicting evidence", hypothesis.Id, errors);
            if (string.IsNullOrWhiteSpace(hypothesis.ConfirmationOrRejectionEvidence))
            {
                errors.Add($"Hypothesis '{hypothesis.Id}' must state what would confirm or reject it.");
            }

            if (hypothesis.Confidence == "Confirmed" &&
                (hypothesis.SupportingEvidenceIds.Count == 0 || hypothesis.ContradictingEvidenceIds.Count > 0))
            {
                errors.Add($"Hypothesis '{hypothesis.Id}' cannot be Confirmed without supporting evidence and no contradicting evidence.");
            }
        }

        foreach (var check in details.RecommendedChecks)
        {
            ValidateIdAndText(check.Id, check.Action, "RecommendedCheck", errors);
            if (string.IsNullOrWhiteSpace(check.Target) ||
                string.IsNullOrWhiteSpace(check.ExpectedSignal) ||
                string.IsNullOrWhiteSpace(check.Impact) ||
                string.IsNullOrWhiteSpace(check.Prerequisites) ||
                string.IsNullOrWhiteSpace(check.Rollback))
            {
                errors.Add($"RecommendedCheck '{check.Id}' requires target, expected signal, impact, prerequisites, and rollback.");
            }

            ValidateKnownIds(check.EvidenceIds, knownEvidenceIds, "evidence", check.Id, errors);
            ValidateKnownSourceIds(check.SourceIds, approvedSourceIds, check.Id, errors);
            if (check.SourceIds.Count == 0)
            {
                errors.Add($"RecommendedCheck '{check.Id}' requires an approved Microsoft source.");
            }
        }

        if (details.OperatorHandoff.Id != "operator-handoff" ||
            details.OperatorHandoff.PortalLink != "[PORTAL_LINK]")
        {
            errors.Add("OperatorHandoff must use id 'operator-handoff' and portalLink '[PORTAL_LINK]'.");
        }

        return errors;
    }

    private static void ValidateTeamsSummary(
        IReadOnlyList<AgentTeamsSummaryBullet> bullets,
        IReadOnlySet<string> detailIds,
        ICollection<string> errors)
    {
        if (bullets.Count is < 1 or > 7)
        {
            errors.Add("TeamsSummary must contain between 1 and 7 bullets and should normally contain 6.");
        }

        var characterCount = string.Join('\n', bullets.Select(item => item.Text)).Length;
        if (characterCount > 900)
        {
            errors.Add("TeamsSummary must not exceed 900 characters.");
        }

        if (bullets.Count > 0 && !bullets.Any(item => item.Text.Contains("**", StringComparison.Ordinal)))
        {
            errors.Add("TeamsSummary must highlight at least one operationally important name with bold Markdown.");
        }

        foreach (var bullet in bullets)
        {
            if (string.IsNullOrWhiteSpace(bullet.Text) || bullet.DetailReferences.Count == 0)
            {
                errors.Add("Every TeamsSummary bullet requires text and at least one detail reference.");
            }

            foreach (var reference in bullet.DetailReferences.Where(reference => !detailIds.Contains(reference)))
            {
                errors.Add($"TeamsSummary references unknown detail ID '{reference}'.");
            }
        }
    }

    private static void ValidateIdAndText(
        string id,
        string text,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{label} requires ID and text.");
        }
    }

    private static void ValidateKnownIds(
        IEnumerable<Guid> ids,
        IReadOnlySet<Guid> knownIds,
        string label,
        string ownerId,
        ICollection<string> errors)
    {
        foreach (var id in ids.Where(id => !knownIds.Contains(id)))
        {
            errors.Add($"'{ownerId}' references unknown {label} ID '{id}'.");
        }
    }

    private static void ValidateKnownSourceIds(
        IEnumerable<string> ids,
        IReadOnlySet<string> knownIds,
        string ownerId,
        ICollection<string> errors)
    {
        foreach (var id in ids.Where(id => !knownIds.Contains(id)))
        {
            errors.Add($"'{ownerId}' references unknown or blocked source ID '{id}'.");
        }
    }

    private static void AddDuplicateErrors(
        IEnumerable<string> ids,
        string label,
        ICollection<string> errors)
    {
        foreach (var duplicate in ids.Where(id => !string.IsNullOrWhiteSpace(id))
                     .GroupBy(id => id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add($"Duplicate {label} ID '{duplicate.Key}'.");
        }
    }
}

public static class AgentAnalysisResultParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AgentAnalysisResult ParseAndValidate(
        string json,
        IncidentAnalysisSnapshot snapshot,
        MicrosoftSourceCatalog sourceCatalog)
    {
        var result = ParseStored(json);

        var errors = AgentAnalysisResultValidator.Validate(result, snapshot, sourceCatalog);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Agent analysis response failed validation: {string.Join(" ", errors)}");
        }

        return result;
    }

    public static AgentAnalysisResult ParseStored(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentAnalysisResult>(json, SerializerOptions)
                ?? throw new InvalidOperationException("Agent analysis response contains JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Agent analysis response does not match the required JSON schema.",
                exception);
        }
    }
}