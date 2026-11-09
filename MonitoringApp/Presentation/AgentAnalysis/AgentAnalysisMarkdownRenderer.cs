using System.Text;

namespace MonitoringApp;

public static class AgentAnalysisMarkdownRenderer
{
    public static string Render(AgentAnalysisJob job, AgentAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);

        if (job.Status != AgentAnalysisJobStatuses.Completed ||
            result.AnalysisId != job.AnalysisId ||
            result.ResultVersion != job.ResultVersion)
        {
            throw new InvalidOperationException(
                "Only the matching completed analysis result can be rendered.");
        }

        var markdown = new StringBuilder();
        markdown.AppendLine("# Agent analysis");
        markdown.AppendLine();
        markdown.AppendLine($"- Analysis ID: `{job.AnalysisId:D}`");
        markdown.AppendLine($"- Result version: `{job.ResultVersion}`");
        markdown.AppendLine($"- Created: `{job.CreatedAt:O}`");
        markdown.AppendLine($"- Result stored: `{job.ResultStoredAt:O}`");
        markdown.AppendLine($"- Scope: `{EscapeInline(job.ScopeType)}` / `{EscapeInline(job.ScopeValue)}`");
        markdown.AppendLine();

        AppendSection(markdown, "Teams summary", result.TeamsSummary.Select(item => item.Text));
        AppendExecutiveSummary(markdown, result.DetailedAnalysis.ExecutiveSummary);
        AppendHypotheses(markdown, result.DetailedAnalysis.Hypotheses);
        AppendRecommendedChecks(markdown, result.DetailedAnalysis.RecommendedChecks);
        AppendOperatorHandoff(markdown, result.DetailedAnalysis.OperatorHandoff);

        return markdown.ToString();
    }

    public static string CreateFileName(AgentAnalysisJob job) =>
        $"agent-analysis-{job.AnalysisId:D}-v{job.ResultVersion}.md";

    private static void AppendSection(
        StringBuilder markdown,
        string heading,
        IEnumerable<string> items)
    {
        markdown.AppendLine($"## {heading}");
        markdown.AppendLine();
        foreach (var item in items)
        {
            markdown.AppendLine(NormalizeBullet(item));
        }

        markdown.AppendLine();
    }

    private static void AppendExecutiveSummary(
        StringBuilder markdown,
        IEnumerable<AgentExecutiveStatement> statements)
    {
        markdown.AppendLine("## Executive summary");
        markdown.AppendLine();
        foreach (var statement in statements)
        {
            markdown.AppendLine($"### {EscapeHeading(statement.Id)}: {EscapeHeading(statement.Kind)}");
            markdown.AppendLine();
            markdown.AppendLine(statement.Text.Trim());
            AppendReferences(markdown, statement.EvidenceIds, statement.RuleIds, statement.SourceIds);
        }
    }

    private static void AppendHypotheses(
        StringBuilder markdown,
        IEnumerable<AgentHypothesis> hypotheses)
    {
        markdown.AppendLine("## Hypotheses");
        markdown.AppendLine();
        foreach (var hypothesis in hypotheses)
        {
            markdown.AppendLine($"### {EscapeHeading(hypothesis.Id)} ({EscapeHeading(hypothesis.Confidence)})");
            markdown.AppendLine();
            markdown.AppendLine(hypothesis.Statement.Trim());
            markdown.AppendLine();
            markdown.AppendLine($"Confirmation or rejection evidence: {hypothesis.ConfirmationOrRejectionEvidence.Trim()}");
            AppendReferences(
                markdown,
                hypothesis.SupportingEvidenceIds,
                [],
                [],
                hypothesis.ContradictingEvidenceIds);
        }
    }

    private static void AppendRecommendedChecks(
        StringBuilder markdown,
        IEnumerable<AgentRecommendedCheck> checks)
    {
        markdown.AppendLine("## Recommended checks");
        markdown.AppendLine();
        foreach (var check in checks)
        {
            markdown.AppendLine($"### {EscapeHeading(check.Id)}");
            markdown.AppendLine();
            markdown.AppendLine(check.Action.Trim());
            markdown.AppendLine();
            markdown.AppendLine($"- Target: {check.Target.Trim()}");
            markdown.AppendLine($"- Expected signal: {check.ExpectedSignal.Trim()}");
            markdown.AppendLine($"- Impact: {check.Impact.Trim()}");
            markdown.AppendLine($"- Prerequisites: {check.Prerequisites.Trim()}");
            markdown.AppendLine($"- Rollback: {check.Rollback.Trim()}");
            AppendReferences(markdown, check.EvidenceIds, [], check.SourceIds);
        }
    }

    private static void AppendOperatorHandoff(
        StringBuilder markdown,
        AgentOperatorHandoff handoff)
    {
        markdown.AppendLine("## Operator handoff");
        markdown.AppendLine();
        markdown.AppendLine($"- Current decision: {handoff.CurrentDecision.Trim()}");
        markdown.AppendLine($"- Unresolved risk: {handoff.UnresolvedRisk.Trim()}");
        markdown.AppendLine($"- Missing evidence: {handoff.MissingEvidence.Trim()}");
        markdown.AppendLine($"- Audience: {handoff.Audience.Trim()}");
        markdown.AppendLine($"- Portal link: {handoff.PortalLink.Trim()}");
    }

    private static void AppendReferences(
        StringBuilder markdown,
        IEnumerable<Guid> evidenceIds,
        IEnumerable<Guid> ruleIds,
        IEnumerable<string> sourceIds,
        IEnumerable<Guid>? contradictingEvidenceIds = null)
    {
        markdown.AppendLine();
        AppendReference(markdown, "Evidence", evidenceIds.Select(id => id.ToString("D")));
        AppendReference(markdown, "Contradicting evidence", contradictingEvidenceIds?.Select(id => id.ToString("D")) ?? []);
        AppendReference(markdown, "Rules", ruleIds.Select(id => id.ToString("D")));
        AppendReference(markdown, "Microsoft sources", sourceIds);
        markdown.AppendLine();
    }

    private static void AppendReference(
        StringBuilder markdown,
        string label,
        IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        if (materialized.Length > 0)
        {
            markdown.AppendLine($"- {label}: {string.Join(", ", materialized.Select(value => $"`{EscapeInline(value)}`"))}");
        }
    }

    private static string NormalizeBullet(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed : $"- {trimmed}";
    }

    private static string EscapeInline(string value) => value.Trim().Replace("`", "\\`", StringComparison.Ordinal);

    private static string EscapeHeading(string value) => value.Trim().Replace("#", "\\#", StringComparison.Ordinal);
}